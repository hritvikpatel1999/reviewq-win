import AppKit
@testable import ReviewQ
@testable import ReviewQCore
import SwiftUI
import XCTest

@MainActor
final class RepositoryDropdownTests: XCTestCase {
    func testRepositoryGroupsHaveSeparateCountsAndLocations() throws {
        let first = try request(number: 1)
        let second = try request(number: 2)
        let otherOrganization = try request(number: 3, organization: "another")
        let otherRepository = try request(number: 4, repository: "Other")
        let groups = RepositoryGroup.grouping([first, second, otherOrganization, otherRepository])
        XCTAssertEqual(groups.count, 3)
        let service = try XCTUnwrap(groups.first { $0.id == first.groupID })
        XCTAssertEqual(service.repository, "Service")
        XCTAssertEqual(service.location, "example / Project")
        XCTAssertEqual(service.requests.map(\.number), [2, 1])
        XCTAssertEqual(groups.map(\.id), groups.map(\.id).sorted())
        XCTAssertTrue(RepositoryGroup.grouping([]).isEmpty)
    }

    func testDropdownStartsCollapsedAndHeaderTogglesItsReviews() async throws {
        let state = DropdownState()
        let group = try XCTUnwrap(RepositoryGroup.grouping([
            request(number: 1), request(number: 2)
        ]).first)
        let (host, window) = makeHost(group: group, state: state)
        defer { window.close() }
        try await settle(host)
        let collapsedHeight = host.intrinsicContentSize.height
        XCTAssertGreaterThan(collapsedHeight, 0)
        XCTAssertFalse(state.isExpanded)

        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        let expandedHeight = host.intrinsicContentSize.height
        XCTAssertGreaterThan(expandedHeight, collapsedHeight + 100)
        let rowHeight = (expandedHeight - collapsedHeight - 10 - 6) / 2
        let header = headerCenter(in: host, collapsedHeight: collapsedHeight)
        let direction: CGFloat = host.isFlipped ? 1 : -1
        let firstReview = NSPoint(
            x: header.x, y: header.y + direction * (collapsedHeight / 2 - 12 + 10 + rowHeight / 2)
        )
        try click(firstReview, in: host, window: window)
        try await settle(host)
        XCTAssertEqual(state.openedRequest, group.requests.first?.id)

        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertFalse(state.isExpanded)
        XCTAssertEqual(host.intrinsicContentSize.height, collapsedHeight, accuracy: 1)
    }

    func testExpandedRepositoryRetainsStateAndUpdatesItsCountAfterRefresh() async throws {
        let state = DropdownState()
        let original = try XCTUnwrap(RepositoryGroup.grouping([request(number: 1)]).first)
        let (host, window) = makeHost(group: original, state: state)
        defer { window.close() }
        try await settle(host)
        let collapsedHeight = host.intrinsicContentSize.height
        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        let originalHeight = host.intrinsicContentSize.height

        let refreshed = try XCTUnwrap(RepositoryGroup.grouping([
            request(number: 1), request(number: 2), request(number: 3)
        ]).first)
        host.rootView = DropdownHarness(group: refreshed, state: state)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        XCTAssertEqual(refreshed.requests.count, 3)
        XCTAssertGreaterThan(host.intrinsicContentSize.height, originalHeight + 100)
    }

    func testPeopleSectionStartsCollapsedAndHeaderTogglesItsReviews() async throws {
        let state = DropdownState()
        let requests = try [request(number: 2, isFollowedOnly: true), request(number: 1, isFollowedOnly: true)]
        let (host, window) = makeHost(PeopleDropdownHarness(requests: requests, state: state))
        defer { window.close() }
        try await settle(host)
        let collapsedHeight = host.intrinsicContentSize.height
        XCTAssertGreaterThan(collapsedHeight, 0)
        XCTAssertFalse(state.isExpanded)

        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        let expandedHeight = host.intrinsicContentSize.height
        XCTAssertGreaterThan(expandedHeight, collapsedHeight + 100)
        let rowHeight = (expandedHeight - collapsedHeight - 10 - 6) / 2
        let header = headerCenter(in: host, collapsedHeight: collapsedHeight)
        let direction: CGFloat = host.isFlipped ? 1 : -1
        try click(NSPoint(
            x: header.x, y: header.y + direction * (collapsedHeight / 2 - 12 + 10 + rowHeight / 2)
        ), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(state.openedRequest, requests.first?.id)

        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertFalse(state.isExpanded)
        XCTAssertEqual(host.intrinsicContentSize.height, collapsedHeight, accuracy: 1)
    }

    func testPeopleExpansionSurvivesRefreshAndReopeningAndCollapsedStateSurvivesRefresh() async throws {
        let state = DropdownState()
        let original = try [request(number: 1, isFollowedOnly: true)]
        let (host, window) = makeHost(PeopleDropdownHarness(requests: original, state: state))
        defer { window.close() }
        try await settle(host)
        let collapsedHeight = host.intrinsicContentSize.height
        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        let originalHeight = host.intrinsicContentSize.height
        let refreshed = try [
            request(number: 3, isFollowedOnly: true), request(number: 2, isFollowedOnly: true),
            request(number: 1, isFollowedOnly: true)
        ]
        host.rootView = PeopleDropdownHarness(requests: refreshed, state: state)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        XCTAssertGreaterThan(host.intrinsicContentSize.height, originalHeight + 100)
        window.orderOut(nil)
        window.makeKeyAndOrderFront(nil)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)

        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertFalse(state.isExpanded)
        host.rootView = PeopleDropdownHarness(requests: original, state: state)
        try await settle(host)
        XCTAssertFalse(state.isExpanded)
        XCTAssertEqual(host.intrinsicContentSize.height, collapsedHeight, accuracy: 1)
    }

    func testPRIconsDispatchIndependentlyAndDoNotOpenTheBrowserForAIReviews() async throws {
        for isFollowedOnly in [false, true] {
            try await exercisePRCardActions(isFollowedOnly: isFollowedOnly)
        }
    }

    func testAuthorSectionTogglesKeepsExpansionAcrossRefreshAndOpensItsCard() async throws {
        let state = DropdownState()
        let status = AuthoredPullRequestStatus(activeCommentCount: 2, hasRequiredApprovals: false)
        let original = try [request(number: 1, authoredStatus: status)]
        let (host, window) = makeHost(AuthorDropdownHarness(requests: original, state: state))
        defer { window.close() }
        try await settle(host)
        let collapsedHeight = host.intrinsicContentSize.height
        XCTAssertFalse(state.isExpanded)
        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        let originalHeight = host.intrinsicContentSize.height
        XCTAssertGreaterThan(originalHeight, collapsedHeight + 50)

        let refreshed = try [
            request(number: 2, authoredStatus: status), request(number: 1, authoredStatus: status)
        ]
        host.rootView = AuthorDropdownHarness(requests: refreshed, state: state)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)
        XCTAssertGreaterThan(host.intrinsicContentSize.height, originalHeight + 50)
        window.orderOut(nil)
        window.makeKeyAndOrderFront(nil)
        try await settle(host)
        XCTAssertTrue(state.isExpanded)

        let expandedHeight = host.intrinsicContentSize.height
        let rowHeight = (expandedHeight - collapsedHeight - 10 - 6) / 2
        let header = headerCenter(in: host, collapsedHeight: collapsedHeight)
        let direction: CGFloat = host.isFlipped ? 1 : -1
        try click(NSPoint(
            x: header.x, y: header.y + direction * (collapsedHeight / 2 - 12 + 10 + rowHeight / 2)
        ), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(state.openedRequest, refreshed.first?.id)
        try click(headerCenter(in: host, collapsedHeight: collapsedHeight), in: host, window: window)
        try await settle(host)
        XCTAssertFalse(state.isExpanded)
        host.rootView = AuthorDropdownHarness(requests: original, state: state)
        try await settle(host)
        XCTAssertFalse(state.isExpanded)
        XCTAssertEqual(host.intrinsicContentSize.height, collapsedHeight, accuracy: 1)
    }

    func testAuthoredCardsHaveOnlyTheBrowserActionEvenWhileAnAIReviewIsLaunching() async throws {
        _ = NSApplication.shared
        let request = try request(
            number: 42, authoredStatus: AuthoredPullRequestStatus(activeCommentCount: 0, hasRequiredApprovals: true)
        )
        var opened = 0
        var edgeOpened = 0
        var assistants: [ReviewAssistant] = []
        let (host, window) = makeHost(PullRequestRow(
            request: request, open: { opened += 1 }, openInEdge: { edgeOpened += 1 },
            review: { assistants.append($0) }, isLaunchingReview: true
        ).frame(width: 392))
        defer { window.close() }
        try await settle(host)
        let top = host.isFlipped
            ? host.bounds.midY - host.intrinsicContentSize.height / 2
            : host.bounds.midY + host.intrinsicContentSize.height / 2
        let direction: CGFloat = host.isFlipped ? 1 : -1
        let left = host.bounds.midX - 196
        try click(NSPoint(x: left + 371, y: top + direction * 21), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(opened, 1)
        try click(NSPoint(x: left + 100, y: top + direction * 48), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(opened, 2)
        for x in [315.0, 343.0] {
            try click(NSPoint(x: left + x, y: top + direction * 21), in: host, window: window)
        }
        try await settle(host)
        XCTAssertTrue(assistants.isEmpty)
        XCTAssertEqual(edgeOpened, 0)
    }

    private func exercisePRCardActions(isFollowedOnly: Bool) async throws {
        _ = NSApplication.shared
        let request = try request(number: 42, isFollowedOnly: isFollowedOnly)
        var opened = 0
        var edgeOpened = 0
        var assistants: [ReviewAssistant] = []
        let host = NSHostingView(rootView: PullRequestRow(
            request: request, open: { opened += 1 }, openInEdge: { edgeOpened += 1 },
            review: { assistants.append($0) },
            isLaunchingReview: false
        ).frame(width: 392))
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 392, height: 120),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        try await settle(host)
        let top = host.isFlipped
            ? host.bounds.midY - host.intrinsicContentSize.height / 2
            : host.bounds.midY + host.intrinsicContentSize.height / 2
        let direction: CGFloat = host.isFlipped ? 1 : -1
        let y = top + direction * 21
        try click(NSPoint(x: 371, y: y), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(assistants, [.copilot])
        XCTAssertEqual(opened, 0)
        try click(NSPoint(x: 343, y: y), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(assistants, [.copilot, .scout])
        XCTAssertEqual(opened, 0)
        try click(NSPoint(x: 315, y: y), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(edgeOpened, 1)
        XCTAssertEqual(opened, 0, "The Edge shortcut must not invoke the default-browser action.")
        try click(NSPoint(x: 100, y: top + direction * 48), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(opened, 1, "Clicking the row should still open the default browser.")

        host.rootView = PullRequestRow(
            request: request, open: { opened += 1 }, openInEdge: { edgeOpened += 1 },
            review: { assistants.append($0) },
            isLaunchingReview: true
        ).frame(width: 392)
        try await settle(host)
        try click(NSPoint(x: 371, y: y), in: host, window: window)
        try click(NSPoint(x: 343, y: y), in: host, window: window)
        try await settle(host)
        XCTAssertEqual(assistants, [.copilot, .scout], "AI buttons must be disabled during a launch.")
        XCTAssertEqual(opened, 1, "Disabled AI buttons must not fall through to the underlying row button.")
        XCTAssertEqual(edgeOpened, 1)
    }

    private func request(
        number: Int, organization: String = "example", repository: String = "Service",
        isFollowedOnly: Bool = false, authoredStatus: AuthoredPullRequestStatus? = nil
    ) throws -> ReviewPullRequest {
        ReviewPullRequest(
            number: number, title: "Synthetic review \(number)", author: "Fixture author",
            organization: organization, project: "Project",
            repository: repository, repositoryID: repository.lowercased(),
            createdAt: Date(timeIntervalSince1970: TimeInterval(number)),
            url: try XCTUnwrap(URL(string: "https://dev.azure.com/\(organization)/Project/_git/repo/pullrequest/\(number)")),
            isRequired: false, isDraft: false, reviewStage: .firstReview, isFollowedOnly: isFollowedOnly,
            authoredStatus: authoredStatus
        )
    }

    func testIconHoverEnlargesTheGlyphWithoutChangingLayoutAndDisabledIconsStayStill() async throws {
        _ = NSApplication.shared
        let host = NSHostingView(rootView: hoverGlyph(hovered: false, disabled: false))
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 100, height: 60),
            styleMask: [.titled], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        try await settle(host)
        let layout = host.intrinsicContentSize
        let before = try redGlyphWidth(host)
        host.rootView = hoverGlyph(hovered: true, disabled: false)
        try await settle(host)
        let hovered = try redGlyphWidth(host)
        XCTAssertGreaterThanOrEqual(hovered, before * 1.1, "Hover should visibly enlarge the icon.")
        XCTAssertLessThanOrEqual(hovered, before * 1.25, "Hover enlargement should stay subtle.")
        XCTAssertEqual(host.intrinsicContentSize, layout, "Hover must not reflow the row.")
        host.rootView = hoverGlyph(hovered: true, disabled: true)
        try await settle(host)
        XCTAssertEqual(try redGlyphWidth(host), before, accuracy: 1, "Disabled icons must not enlarge.")
    }

    private func hoverGlyph(hovered: Bool, disabled: Bool) -> some View {
        Rectangle().fill(.red).frame(width: 12, height: 12)
            .modifier(PRIconAffordance(isHovered: hovered, isPressed: false))
            .disabled(disabled)
            .frame(width: 100, height: 60)
            .background(.white)
    }

    private func redGlyphWidth(_ host: NSView) throws -> CGFloat {
        host.displayIfNeeded()
        let image = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: image)
        var columns: Set<Int> = []
        for x in 0..<image.pixelsWide {
            for y in 0..<image.pixelsHigh {
                if let color = image.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB),
                   color.redComponent > 0.5,
                   color.redComponent - max(color.greenComponent, color.blueComponent) > 0.15 {
                    columns.insert(x)
                }
            }
        }
        XCTAssertFalse(columns.isEmpty)
        return CGFloat(columns.count)
    }

    private func makeHost(
        group: RepositoryGroup, state: DropdownState
    ) -> (NSHostingView<DropdownHarness>, NSWindow) {
        makeHost(DropdownHarness(group: group, state: state))
    }

    private func makeHost<Content: View>(_ view: Content) -> (NSHostingView<Content>, NSWindow) {
        _ = NSApplication.shared
        let host = NSHostingView(rootView: view)
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 440, height: 640),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        return (host, window)
    }

    private func settle(_ host: NSView) async throws {
        await Task.yield()
        host.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(200))
        host.layoutSubtreeIfNeeded()
    }

    private func headerCenter(in host: NSView, collapsedHeight: CGFloat) -> NSPoint {
        let direction: CGFloat = host.isFlipped ? -1 : 1
        return NSPoint(
            x: host.bounds.midX,
            y: host.bounds.midY + direction * (host.intrinsicContentSize.height - collapsedHeight) / 2
        )
    }

    private func click(_ point: NSPoint, in host: NSView, window: NSWindow) throws {
        let location = host.convert(point, to: nil)
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            let event = try XCTUnwrap(NSEvent.mouseEvent(
                with: type, location: location, modifierFlags: [],
                timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0
            ))
            NSApplication.shared.postEvent(event, atStart: false)
        }
        while let event = NSApplication.shared.nextEvent(
            matching: [.leftMouseDown, .leftMouseUp], until: Date().addingTimeInterval(0.05),
            inMode: .default, dequeue: true
        ) {
            window.sendEvent(event)
        }
    }
}

@MainActor
private final class DropdownState: ObservableObject {
    @Published var isExpanded = false
    var openedRequest: String?
}

private struct DropdownHarness: View {
    let group: RepositoryGroup
    @ObservedObject var state: DropdownState

    var body: some View {
        RepositoryDropdown(
            group: group, isExpanded: $state.isExpanded,
            open: { state.openedRequest = $0.id }, openInEdge: { _ in },
            review: { _, _ in }, isLaunchingReview: false
        )
        .frame(width: 416)
    }

}

private struct PeopleDropdownHarness: View {
    let requests: [ReviewPullRequest]
    @ObservedObject var state: DropdownState

    var body: some View {
        FollowedPeopleSection(
            requests: requests, isExpanded: $state.isExpanded,
            open: { state.openedRequest = $0.id }, openInEdge: { _ in },
            review: { _, _ in }, isLaunchingReview: false
        )
        .frame(width: 416)
    }

}

private struct AuthorDropdownHarness: View {
    let requests: [ReviewPullRequest]
    @ObservedObject var state: DropdownState

    var body: some View {
        AuthoredPullRequestsSection(
            requests: requests, isExpanded: $state.isExpanded, open: { state.openedRequest = $0.id }
        )
        .frame(width: 416)
    }
}
