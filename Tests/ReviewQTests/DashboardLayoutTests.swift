import AppKit
@testable import ReviewQ
@testable import ReviewQCore
import SwiftUI
import Vision
import XCTest

@MainActor
final class DashboardLayoutTests: XCTestCase {
    func testReviewStagesAreBesideDraftWithoutNumbersOrAnotherRow() throws {
        var heights: Set<Int> = []
        for stage in ReviewStage.allCases {
            for draft in [false, true] {
                let request = fixture(stage: stage, draft: draft, required: true)
                let image = try render(
                    PullRequestRow(
                        request: request, open: {}, openInEdge: {}, review: { _ in }, isLaunchingReview: false
                    )
                        .frame(width: 392)
                )
                heights.insert(image.height)
                let lines = try recognize(image)
                XCTAssertTrue(lines.contains { $0.contains(stage.label) }, "\(lines)")
                if draft {
                    XCTAssertTrue(lines.contains {
                        $0.contains("Draft") && $0.contains(stage.label)
                    }, "Draft and review stage must be on the same line: \(lines)")
                }
                XCTAssertFalse(lines.joined().contains(String(request.number)))
                XCTAssertFalse(lines.joined().contains("#"))
                XCTAssertFalse(lines.contains { $0.split(separator: " ").contains("old") })
            }
        }
        XCTAssertEqual(heights.count, 1, "Review labels must not add height to the PR row")
    }

    func testNewestFirstOrderingHasADeterministicTieBreak() {
        let newest = fixture(number: 20, createdAt: 300)
        let tieA = fixture(number: 7, createdAt: 200)
        let tieB = fixture(number: 8, createdAt: 200)
        for requests in [[tieB, newest, tieA], [tieA, tieB, newest]] {
            let groups = RepositoryGroup.grouping(requests)
            XCTAssertEqual(groups.first?.requests.map(\.number), [20, 7, 8])
        }
        XCTAssertFalse(ReviewPullRequest.newestFirst(tieA, tieA))
    }

    func testRepositoryGroupingExcludesFollowedOnlyRequests() {
        let assigned = fixture(number: 1)
        let followed = fixture(number: 2, isFollowedOnly: true, repository: "Another repository")
        let groups = RepositoryGroup.grouping([followed, assigned])
        XCTAssertEqual(groups.count, 1)
        XCTAssertEqual(groups.first?.requests, [assigned])
        XCTAssertTrue(RepositoryGroup.grouping([followed]).isEmpty)
    }

    func testAuthoredRequestsAreNotGroupedAsAssignedReviews() {
        let assigned = fixture(number: 1)
        let authored = fixture(
            number: 2, authoredStatus: AuthoredPullRequestStatus(activeCommentCount: 0, hasRequiredApprovals: true)
        )
        XCTAssertEqual(RepositoryGroup.grouping([assigned, authored]).first?.requests, [assigned])
        XCTAssertTrue(RepositoryGroup.grouping([authored]).isEmpty)
        XCTAssertEqual(ReviewSnapshot(pullRequests: [assigned, authored]).pendingReviewCount, 1)
    }

    func testAuthoredSectionIsOptInAndBelowRepositoryAndPeopleSections() async throws {
        for enabled in [false, true] {
            let name = "ReviewQ-authored-layout-\(UUID().uuidString)"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
            defer { defaults.removePersistentDomain(forName: name) }
            let store = try makeStore(
                defaults: defaults, followPeople: true, showAuthored: enabled, requests: [
                    fixture(number: 1, repository: "Alpha"),
                    fixture(number: 2, isFollowedOnly: true),
                    fixture(number: 3, authoredStatus: AuthoredPullRequestStatus(activeCommentCount: 2, hasRequiredApprovals: false))
                ]
            )
            await store.refresh()
            let lines = try recognize(
                await capture(DashboardView(store: store, app: AppDelegate(store: store))), level: .fast
            )
            XCTAssertEqual(lines.joined().contains("I'm the Author"), enabled, "\(lines)")
            if enabled {
                let author = try XCTUnwrap(lines.firstIndex { $0.contains("I'm the Author") }, "\(lines)")
                for label in ["Alpha", "People you follow"] {
                    XCTAssertLessThan(try XCTUnwrap(lines.firstIndex { $0.contains(label) }, "\(lines)"), author)
                }
                XCTAssertFalse(lines.joined().contains("Active Comments"), "The author section starts collapsed.")
            }
        }
    }

    func testEmptyAuthorSectionIsVisibleWhenEnabledWithoutShowingCaughtUp() async throws {
        let name = "ReviewQ-empty-authored-layout-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = try makeStore(defaults: defaults, showAuthored: true)
        await store.refresh()
        let lines = try recognize(
            await capture(DashboardView(store: store, app: AppDelegate(store: store))), level: .fast
        )
        XCTAssertTrue(lines.joined().contains("I'm the Author"), "\(lines)")
        XCTAssertFalse(lines.joined().contains("caught up"), "\(lines)")
        let expanded = try recognize(render(
            AuthoredPullRequestsSection(requests: [], isExpanded: .constant(true), open: { _ in })
                .frame(width: 416)
        ), level: .fast)
        XCTAssertTrue(expanded.joined().localizedCaseInsensitiveContains("No active PRs authored by you"), "\(expanded)")
    }

    func testAuthoredCardsShowOnlyTheirOwnStatusAndHideZeroCommentsWithoutChangingHeight() throws {
        var heights: Set<Int> = []
        for (comments, approvals, label) in [
            (2, true, "2 Active Comments"), (0, true, "Merge Ready"), (0, false, "")
        ] {
            let request = fixture(
                authoredStatus: AuthoredPullRequestStatus(activeCommentCount: comments, hasRequiredApprovals: approvals)
            )
            let image = try render(PullRequestRow(request: request, open: {}).frame(width: 392))
            heights.insert(image.height)
            let text = try recognize(image, level: .fast).joined(separator: " ")
            if !label.isEmpty { XCTAssertTrue(text.contains(label), text) }
            XCTAssertFalse(text.contains("First Review"), text)
            XCTAssertFalse(text.contains("0 Active Comments"), text)
            XCTAssertEqual(text.contains("Merge Ready"), comments == 0 && approvals, text)
            XCTAssertTrue(text.contains("A reviewer in Service"), text)
        }
        XCTAssertEqual(heights.count, 1)
    }

    func testFollowedAuthorShowsOnlyRepositoryOnTheSameLineWithoutChangingCardHeight() throws {
        var heights: Set<Int> = []
        for followed in [false, true] {
            let request = fixture(draft: true, isFollowedOnly: followed)
            let image = try render(
                PullRequestRow(
                    request: request, open: {}, openInEdge: {}, review: { _ in }, isLaunchingReview: false
                ).frame(width: 392)
            )
            heights.insert(image.height)
            let lines = try recognize(image, level: .fast)
            XCTAssertTrue(lines.contains { $0.contains(request.authorLabel) }, "\(lines)")
            XCTAssertFalse(lines.joined().contains("example"))
            XCTAssertFalse(lines.joined().contains("Project"))
            XCTAssertEqual(lines.joined().contains("Service"), followed)
            XCTAssertTrue(lines.contains { $0.contains("Draft") && $0.contains("First Review") }, "\(lines)")
        }
        XCTAssertEqual(heights.count, 1, "The repository belongs on the existing author line, not a new row.")
    }

    func testPeopleSectionIsBelowAllRepositoryHeaders() async throws {
        let name = "ReviewQ-followed-layout-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = try makeStore(defaults: defaults, followPeople: true, requests: [
            fixture(number: 1, repository: "Alpha"),
            fixture(number: 2, repository: "Beta"),
            fixture(number: 3, isFollowedOnly: true, repository: "Gamma")
        ])
        await store.refresh()
        let lines = try recognize(
            await capture(DashboardView(store: store, app: AppDelegate(store: store))), level: .fast
        )
        let peopleIndex = try XCTUnwrap(lines.firstIndex { $0.contains("People you follow") }, "\(lines)")
        for name in ["Alpha", "Beta"] {
            XCTAssertLessThan(try XCTUnwrap(lines.firstIndex { $0 == name }, "\(lines)"), peopleIndex)
        }
        XCTAssertFalse(lines.contains { $0.contains("A reviewer in Gamma") }, "People you follow starts collapsed: \(lines)")
    }

    func testFollowedOnlyQueueStartsCollapsedAndNeverShowsCaughtUp() async throws {
        let name = "ReviewQ-followed-only-layout-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = try makeStore(defaults: defaults, followPeople: true, requests: [
            fixture(number: 1, createdAt: 100, isFollowedOnly: true, title: "Older followed PR"),
            fixture(number: 2, createdAt: 200, isFollowedOnly: true, title: "Newest followed PR")
        ])
        await store.refresh()
        let lines = try recognize(
            await capture(DashboardView(store: store, app: AppDelegate(store: store))), level: .fast
        )
        XCTAssertTrue(lines.contains { $0.contains("People you follow") }, "\(lines)")
        XCTAssertFalse(lines.joined().contains("caught up"))
        XCTAssertFalse(lines.contains { $0.contains("Newest followed PR") || $0.contains("Older followed PR") }, "\(lines)")
    }

    func testEmptyPeopleSectionIsVisibleOnlyWhenOptedIn() async throws {
        for enabled in [false, true] {
            let name = "ReviewQ-empty-followed-layout-\(UUID().uuidString)"
            let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
            defer { defaults.removePersistentDomain(forName: name) }
            let store = try makeStore(defaults: defaults, followPeople: enabled)
            await store.refresh()
            let lines = try recognize(
                await capture(DashboardView(store: store, app: AppDelegate(store: store))), level: .fast
            )
            XCTAssertEqual(lines.joined().contains("People you follow"), enabled, "\(lines)")
            XCTAssertFalse(lines.joined().localizedCaseInsensitiveContains("No other active PRs"), "\(lines)")
        }
    }

    func testExpandedEmptyPeopleSectionShowsItsEmptyMessage() throws {
        let image = try render(
            FollowedPeopleSection(
                requests: [], isExpanded: .constant(true), open: { _ in }, openInEdge: { _ in },
                review: { _, _ in }, isLaunchingReview: false
            ).frame(width: 416)
        )
        let lines = try recognize(image, level: .fast)
        XCTAssertTrue(lines.joined().localizedCaseInsensitiveContains("No other active PRs"), "\(lines)")
    }

    func testCommentResolutionLabelsStayBesideDraftWithoutAddingRowHeight() throws {
        let counted = fixture(
            stage: .commented, draft: true, required: true,
            commentResolution: ReviewCommentResolution(resolvedCount: 2, openCount: 1)
        )
        let resolved = fixture(stage: .commentsResolved, draft: true, required: true)
        let plain = fixture(stage: .commented, draft: true, required: true)
        var heights: Set<Int> = []
        for request in [counted, resolved, plain] {
            let image = try render(
                PullRequestRow(
                    request: request, open: {}, openInEdge: {}, review: { _ in }, isLaunchingReview: false
                ).frame(width: 392)
            )
            heights.insert(image.height)
            let lines = try recognize(image, level: .fast)
            if request.commentResolution != nil {
                XCTAssertTrue(lines.contains {
                    $0.contains("Draft") && $0.contains("You commented")
                        && $0.contains("2 resolved") && $0.contains("1 open")
                }, "\(lines)")
            } else {
                XCTAssertTrue(lines.contains {
                    $0.contains("Draft") && $0.contains(request.reviewStageLabel)
                }, "\(lines)")
            }
        }
        XCTAssertEqual(heights.count, 1)
    }

    func testDashboardRemovesFooterActionsButKeepsQuitAndRefreshTime() async throws {
        let name = "ReviewQ-layout-tests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = try makeStore(defaults: defaults)
        await store.refresh()
        let view = DashboardView(store: store, app: AppDelegate(store: store)).frame(width: 440, height: 540)
        let image = try await capture(view)
        let text = try recognize(image).joined(separator: "\n")
        XCTAssertEqual(text.components(separatedBy: "Quit").count - 1, 1, text)
        XCTAssertTrue(text.contains("Updated"))
        XCTAssertFalse(text.contains("Settings..."))
        XCTAssertFalse(text.contains("Azure DevOps"))
    }

    func testDashboardStillIdentifiesTheLastSuccessfulUpdateAfterAnError() async throws {
        let name = "ReviewQ-layout-tests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = try makeStore(defaults: defaults)
        await store.refresh()
        await store.refresh()
        XCTAssertNotNil(store.errorMessage)
        let view = DashboardView(store: store, app: AppDelegate(store: store)).frame(width: 440, height: 540)
        let image = try await capture(view)
        let text = try recognize(image).joined(separator: "\n")
        XCTAssertTrue(text.contains("Last successful update"), text)
    }

    private func fixture(
        number: Int = 8675309, createdAt: TimeInterval = 1,
        stage: ReviewStage = .firstReview, draft: Bool = false, required: Bool = false,
        commentResolution: ReviewCommentResolution? = nil, isFollowedOnly: Bool = false,
        repository: String = "Service", title: String = "A compact pull request row",
        authoredStatus: AuthoredPullRequestStatus? = nil
    ) -> ReviewPullRequest {
        ReviewPullRequest(
            number: number, title: title, author: "A reviewer",
            organization: "example", project: "Project", repository: repository, repositoryID: "service-id",
            createdAt: Date(timeIntervalSince1970: createdAt),
            url: URL(string: "https://dev.azure.com/example/Project/_git/service/pullrequest/\(number)")!,
            isRequired: required, isDraft: draft, reviewStage: stage, commentResolution: commentResolution,
            isFollowedOnly: isFollowedOnly, authoredStatus: authoredStatus
        )
    }

    private func makeStore(
        defaults: UserDefaults, followPeople: Bool = false, showAuthored: Bool = false,
        requests: [ReviewPullRequest] = []
    ) throws -> ReviewStore {
        let store = ReviewStore(
            defaults: defaults, keychain: NoPATStore(), loader: LayoutReviewLoader(requests: requests)
        )
        var settings = ReviewConfiguration()
        settings.sourceURLs = "https://dev.azure.com/example/Project"
        settings.followPeople = followPeople
        settings.showAuthoredPullRequests = showAuthored
        settings.followedAuthorEmails = followPeople ? "alex@example.com" : ""
        try store.save(settings)
        return store
    }

    private func render(_ view: some View) throws -> CGImage {
        _ = NSApplication.shared
        let renderer = ImageRenderer(
            content: view
                .environment(\.colorScheme, .light)
                .environment(\.locale, Locale(identifier: "en_US"))
                .background(Color(nsColor: .windowBackgroundColor))
        )
        renderer.scale = 3
        return try XCTUnwrap(renderer.cgImage)
    }

    private func capture(_ view: some View) async throws -> CGImage {
        _ = NSApplication.shared
        let host = NSHostingView(rootView: view
            .environment(\.colorScheme, .light)
            .environment(\.locale, Locale(identifier: "en_US"))
        )
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 440, height: 540),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        await Task.yield()
        host.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(200))
        host.displayIfNeeded()
        // ImageRenderer omits AppKit-backed borderless toolbar buttons.
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        return try XCTUnwrap(bitmap.cgImage)
    }

    private func recognize(
        _ image: CGImage, level: VNRequestTextRecognitionLevel = .accurate
    ) throws -> [String] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = level
        request.recognitionLanguages = ["en-US"]
        try VNImageRequestHandler(cgImage: image).perform([request])
        return try XCTUnwrap(request.results).map {
            try XCTUnwrap($0.topCandidates(1).first?.string)
        }
    }
}

private struct NoPATStore: PATStoring {
    func load() throws -> String? { throw ReviewQError.missingPAT }
    func save(_ token: String) throws { throw ReviewQError.missingPAT }
    func remove() throws { throw ReviewQError.missingPAT }
}

private actor LayoutReviewLoader: ReviewLoading {
    func invalidateIdentityCache() {}

    private var calls = 0
    private let requests: [ReviewPullRequest]

    init(requests: [ReviewPullRequest]) {
        self.requests = requests
    }

    func load(configuration: ReviewConfiguration) async throws -> ReviewSnapshot {
        calls += 1
        if calls == 1 { return ReviewSnapshot(pullRequests: requests, updatedAt: Date().addingTimeInterval(-180)) }
        throw ReviewQError.http(403, "example/Project")
    }
}
