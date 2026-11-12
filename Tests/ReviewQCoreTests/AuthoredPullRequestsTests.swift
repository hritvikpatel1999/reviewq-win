import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class AuthoredPullRequestsTests: XCTestCase {
    private let requiredReviewersType = "fd2167ab-b0be-447a-8ec8-39368250530e"
    private let buildType = "0609b952-1397-4640-95ec-e00a01b2c241"

    func testSettingDefaultsOffMigratesMissingKeyAndRoundTripsWithoutChangingOtherValues() throws {
        var settings = configuration()
        settings.refreshInterval = .fiveMinutes
        settings.includeDrafts = true
        settings.scoutAdditionalInstructions = "Keep this."
        XCTAssertFalse(settings.showAuthoredPullRequests)
        settings.showAuthoredPullRequests = true
        let data = try JSONEncoder().encode(settings)
        XCTAssertEqual(try JSONDecoder().decode(ReviewConfiguration.self, from: data), settings)
        var legacy = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        legacy.removeValue(forKey: "showAuthoredPullRequests")
        settings.showAuthoredPullRequests = false
        XCTAssertEqual(
            try JSONDecoder().decode(ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: legacy)),
            settings
        )
        for invalid: Any in [NSNull(), 1, "true"] {
            legacy["showAuthoredPullRequests"] = invalid
            XCTAssertThrowsError(
                try JSONDecoder().decode(
                    ReviewConfiguration.self, from: JSONSerialization.data(withJSONObject: legacy)
                )
            )
        }
    }

    func testOptOutMakesOnlyOriginalAssignedReviewRequests() async throws {
        let transport = StubTransport([connectionResponse(), pullRequestsResponse([pullRequestJSON()])])
        let snapshot = try await load(configuration(), transport: transport)
        XCTAssertEqual(snapshot.pendingReviewCount, 1)
        XCTAssertNil(try XCTUnwrap(snapshot.pullRequests.first).authoredStatus)
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 3)
        XCTAssertFalse(requests.contains { query($0)["searchCriteria.creatorId"] != nil })
        XCTAssertFalse(requests.contains { $0.url?.path.hasSuffix("/policy/evaluations") == true })
        XCTAssertFalse(requests.contains { $0.url?.path.hasSuffix("/projects") == true })
    }

    func testAuthorSectionIncludesOnlyOwnActivePRsAcrossOrganizationAndKeepsReviewCountSeparate() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON(number: 10)]),
            projectsResponse(["Project", "Outside"]),
            pullRequestsResponse([
                ownPR(), ownPR(number: 2, draft: true), ownPR(number: 3, status: "completed"),
                ownPR(number: 4, status: "abandoned"), pullRequestJSON(number: 5, hasReviewer: false),
                ownPR()
            ]),
            pullRequestsResponse([ownPR(number: 6, project: "Outside", date: "2026-10-02T00:00:00Z")])
        ], policies: [1: [policyEvaluationsResponse()], 6: [policyEvaluationsResponse()]])
        let snapshot = try await load(authoring(), transport: transport)
        XCTAssertEqual(snapshot.pullRequests.map(\.number), [6, 1, 10])
        let authored = snapshot.pullRequests.filter { $0.authoredStatus != nil }
        XCTAssertEqual(authored.map(\.number), [6, 1])
        XCTAssertTrue(authored.allSatisfy { !$0.isFollowedOnly && !$0.isRequired && $0.isMergeReady })
        XCTAssertEqual(authored.first?.authorLabel, "Fixture author in Repository")
        XCTAssertEqual(snapshot.pendingReviewCount, 1)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 1)
        let creators = requests.filter { query($0)["searchCriteria.creatorId"] != nil }
        XCTAssertEqual(creators.count, 2)
        XCTAssertTrue(creators.allSatisfy {
            query($0)["searchCriteria.creatorId"]?.lowercased() == reviewerID
                && query($0)["searchCriteria.reviewerId"] == nil
        })
        let policies = requests.filter { $0.url?.path.hasSuffix("/policy/evaluations") == true }
        XCTAssertEqual(policies.count, 2)
        XCTAssertEqual(Set(policies.compactMap { query($0)["artifactId"] }), [
            "vstfs:///CodeReview/CodeReviewId/project-id/1", "vstfs:///CodeReview/CodeReviewId/project-id/6"
        ])
        XCTAssertTrue(policies.allSatisfy {
            query($0)["api-version"] == "7.1-preview.1" && query($0)["includeNotApplicable"] == "true"
        })
        XCTAssertFalse(requests.contains { $0.url?.path.lowercased().contains("build") == true })
        XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "GET" })
    }

    func testDraftPreferenceAppliesButDraftIsNeverMergeReady() async throws {
        var settings = authoring("https://dev.azure.com/example")
        settings.includeDrafts = true
        let transport = StubTransport([
            connectionResponse(), projectsResponse(["Project"]), pullRequestsResponse([]),
            pullRequestsResponse([ownPR(draft: true)])
        ], policies: [1: [policyEvaluationsResponse()]])
        let snapshot = try await load(settings, transport: transport)
        let request = try XCTUnwrap(snapshot.pullRequests.first)
        XCTAssertTrue(request.isDraft)
        XCTAssertEqual(request.authoredStatus?.activeCommentCount, 0)
        XCTAssertFalse(request.isMergeReady)
        XCTAssertNil(request.authoredStatusLabel)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 1)
    }

    func testIdentityIsResolvedPerOrganizationAndRepeatedSourcesDoNotDuplicateAuthorQueries() async throws {
        let otherID = "33333333-3333-3333-3333-333333333333"
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), pullRequestsResponse([]),
            connectionResponse(otherID), pullRequestsResponse([]),
            projectsResponse(["Project"]), pullRequestsResponse([ownPR()]),
            projectsResponse(["Project"]), pullRequestsResponse([
                pullRequestJSON(number: 2, author: otherID, hasReviewer: false)
            ])
        ], policies: [1: [policyEvaluationsResponse()], 2: [policyEvaluationsResponse()]])
        let snapshot = try await load(authoring("""
        https://dev.azure.com/example/Project
        https://dev.azure.com/EXAMPLE/Other
        https://dev.azure.com/another/Project
        """), transport: transport)
        XCTAssertEqual(snapshot.pullRequests.count, 2)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 2)
        let creatorIDs = requests.compactMap { query($0)["searchCriteria.creatorId"]?.lowercased() }
        XCTAssertEqual(creatorIDs, [reviewerID, otherID])
    }

    func testFollowingSelfReusesAuthorQueueWithoutDuplicateQueriesOrHistory() async throws {
        var settings = authoring("https://dev.azure.com/example")
        settings.followPeople = true
        settings.followedAuthorEmails = "alex@example.com"
        let transport = StubTransport([
            connectionResponse(), projectsResponse(["Project"]), pullRequestsResponse([]),
            pullRequestsResponse([ownPR()]),
            identitiesResponse([followedIdentityJSON(id: reviewerID)])
        ], policies: [1: [policyEvaluationsResponse()]])
        let snapshot = try await load(settings, transport: transport)
        XCTAssertEqual(snapshot.pullRequests.count, 1)
        XCTAssertNotNil(snapshot.pullRequests.first?.authoredStatus)
        XCTAssertFalse(try XCTUnwrap(snapshot.pullRequests.first).isFollowedOnly)
        XCTAssertEqual(snapshot.pendingReviewCount, 0)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { query($0)["searchCriteria.creatorId"] != nil }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/threads") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 1)
    }

    func testAuthoredPRPaginationIncludesAllPagesAndEnrichesEachPRExactlyOnce() async throws {
        let first = (1...100).map { ownPR(number: $0) }
        let policies = Dictionary(uniqueKeysWithValues:
            (1...101).map { ($0, [policyEvaluationsResponse()]) }
        )
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]),
            pullRequestsResponse(first), pullRequestsResponse([ownPR(number: 101)])
        ], policies: policies)
        let snapshot = try await load(authoring(), transport: transport)
        XCTAssertEqual(snapshot.pullRequests.count, 101)
        XCTAssertEqual(snapshot.pendingReviewCount, 0)
        let requests = await transport.requests
        XCTAssertEqual(
            requests.filter { query($0)["searchCriteria.creatorId"] != nil }.map { query($0)["$skip"] },
            ["0", "100"]
        )
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/threads") == true }.count, 101)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/policy/evaluations") == true }.count, 101)
    }

    func testAssignedFollowedAndAuthoredQueuesShareDiscoveryAndRemainDeduplicated() async throws {
        var settings = authoring()
        settings.followPeople = true
        settings.followedAuthorEmails = "alex@example.com"
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON(number: 10)]),
            projectsResponse(["Project"]), pullRequestsResponse([ownPR()]),
            identitiesResponse([followedIdentityJSON()]),
            pullRequestsResponse([pullRequestJSON(number: 10), pullRequestJSON(number: 2, hasReviewer: false)])
        ], policies: [1: [policyEvaluationsResponse()]])
        let snapshot = try await load(settings, transport: transport)
        XCTAssertEqual(snapshot.pullRequests.count, 3)
        XCTAssertEqual(snapshot.pendingReviewCount, 2)
        XCTAssertEqual(snapshot.pullRequests.filter { $0.authoredStatus != nil }.map(\.number), [1])
        XCTAssertEqual(snapshot.pullRequests.filter(\.isFollowedOnly).map(\.number), [2])
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/10/threads") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/policy/evaluations") == true }.count, 1)
    }

    func testDisablingAuthorSectionStopsExtraQueriesAndReusesCurrentUserIdentity() async throws {
        var settings = authoring()
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]),
            pullRequestsResponse([ownPR()]), pullRequestsResponse([])
        ], policies: [1: [policyEvaluationsResponse()]])
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(header: "synthetic-fixed-account"), transport: transport
        )
        let initial = try await loader.load(configuration: settings)
        XCTAssertEqual(initial.pullRequests.count, 1)
        let previousCount = await transport.requests.count
        settings.showAuthoredPullRequests = false
        let next = try await loader.load(configuration: settings)
        XCTAssertTrue(next.pullRequests.isEmpty)
        let newRequests = Array((await transport.requests).dropFirst(previousCount))
        XCTAssertEqual(newRequests.count, 1)
        XCTAssertEqual(query(try XCTUnwrap(newRequests.first))["searchCriteria.reviewerId"]?.lowercased(), reviewerID)
    }

    func testCountsUnresolvedDiscussionsOnceRegardlessOfAuthorAndExcludesDeletedAndSystemEvents() throws {
        let replies = """
        {"status":"active","comments":[
          {"author":{"id":"\(authorID)"},"commentType":"text","content":"Please fix."},
          {"author":{"id":"\(reviewerID)"},"commentType":"text","content":"Reply."}]}
        """
        let status = try summary(threads: [
            replies, reviewCommentThreadJSON(statusJSON: "6"),
            reviewCommentThreadJSON(statusJSON: #""fixed""#),
            reviewCommentThreadJSON(statusJSON: #""wontFix""#),
            reviewCommentThreadJSON(statusJSON: #""closed""#),
            reviewCommentThreadJSON(statusJSON: #""byDesign""#),
            reviewCommentThreadJSON(deleted: true, statusJSON: #""active""#),
            reviewCommentThreadJSON(statusJSON: #""active""#, threadDeleted: true),
            reviewCommentThreadJSON(type: "system", statusJSON: #""active""#),
            reviewCommentThreadJSON(threadType: "PullRequestStatusUpdate", statusJSON: #""active""#),
            voteThreadJSON()
        ])
        XCTAssertEqual(status.activeCommentCount, 2)
        XCTAssertTrue(status.hasRequiredApprovals)
        XCTAssertThrowsError(try summary(threads: [reviewCommentThreadJSON()]))
        XCTAssertThrowsError(try summary(threads: [reviewCommentThreadJSON(statusJSON: #""future-status""#)]))
    }

    func testRequiredReviewerVotesAndGroupsAreHonoredWithoutRequiringOptionalVotes() throws {
        for vote in [-10, -5, 0, 5, 10] {
            let status = try summary(reviewers: [reviewer(vote: vote, required: true)])
            XCTAssertEqual(status.hasRequiredApprovals, vote == 5 || vote == 10)
        }
        XCTAssertFalse(try summary(reviewers: [reviewer(vote: 10, required: true, declined: true)]).hasRequiredApprovals)
        XCTAssertTrue(try summary(reviewers: [reviewer(vote: -10, required: false)]).hasRequiredApprovals)
        XCTAssertFalse(try summary(reviewers: [
            reviewer(vote: 10, required: true), reviewer(vote: 0, required: true)
        ]).hasRequiredApprovals)
        XCTAssertThrowsError(try summary(reviewers: [reviewer(vote: 42, required: true)]))
    }

    func testRequiredPoliciesDetermineApprovalReadinessAndBuildStatusesAreIgnored() throws {
        for type in ["fa4e907d-c16b-4a4c-9dfa-4906e5d171dd", requiredReviewersType] {
            for state in ["approved", "notApplicable", "queued", "running", "rejected", "broken"] {
                let status = try summary(policies: [policyEvaluationJSON(typeID: type, status: state)])
                XCTAssertEqual(status.hasRequiredApprovals, state == "approved" || state == "notApplicable")
            }
        }
        XCTAssertFalse(try summary(
            policies: [policyEvaluationJSON(status: "rejected")],
            reviewers: [reviewer(vote: 10, required: true)]
        ).hasRequiredApprovals, "A self-approval or stale vote must not override Azure DevOps' minimum-review policy.")
        XCTAssertTrue(try summary(policies: [
            policyEvaluationJSON(status: "rejected", enabled: false),
            policyEvaluationJSON(status: "rejected", blocking: false),
            policyEvaluationJSON(status: "rejected", deleted: true),
            policyEvaluationJSON(typeID: buildType, status: "rejected"),
            policyEvaluationJSON(typeID: "11111111-1111-1111-1111-111111111111", status: "future-status")
        ]).hasRequiredApprovals)
        XCTAssertThrowsError(try summary(policies: [policyEvaluationJSON(status: "future-status")]))
    }

    func testLabelsRequireNonzeroCommentsOrCompleteApprovalsAndNeverShowZeroComments() {
        for comments in [0, 1, 5] {
            for approved in [false, true] {
                for draft in [false, true] {
                    let request = model(
                        status: AuthoredPullRequestStatus(activeCommentCount: comments, hasRequiredApprovals: approved),
                        draft: draft
                    )
                    XCTAssertEqual(request.isMergeReady, comments == 0 && approved && !draft)
                    XCTAssertEqual(
                        request.authoredStatusLabel,
                        comments > 0 ? "\(comments) Active Comments" : (approved && !draft ? "Merge Ready" : nil)
                    )
                    XCTAssertFalse(request.authoredStatusLabel?.contains("0 Active") == true)
                }
            }
        }
    }

    func testPolicyPaginationReadsLaterBlockingApprovalAndRejectsRepeatingPages() async throws {
        let first = (1...100).map { _ in policyEvaluationJSON() }
        let settings = authoring()
        let transport = StubTransport(
            [connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]), pullRequestsResponse([ownPR()])],
            policies: [1: [
                policyEvaluationsResponse(first), policyEvaluationsResponse([policyEvaluationJSON(status: "rejected")])
            ]]
        )
        let snapshot = try await load(settings, transport: transport)
        XCTAssertFalse(try XCTUnwrap(snapshot.pullRequests.first).isMergeReady)
        let requests = await transport.requests
        XCTAssertEqual(
            requests.filter { $0.url?.path.hasSuffix("/policy/evaluations") == true }.map { query($0)["$skip"] },
            ["0", "100"]
        )
        let repeated = StubTransport(
            [connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]), pullRequestsResponse([ownPR()])],
            policies: [1: [policyEvaluationsResponse(first), policyEvaluationsResponse(first)]]
        )
        do {
            _ = try await load(settings, transport: repeated)
            XCTFail("Repeated pages must not turn into a complete approval result.")
        } catch let error as ReviewQError {
            guard case .invalidPagination = error else { return XCTFail("Unexpected error: \(error)") }
        }
    }

    func testPolicyFailuresWrongArtifactsAndIncompleteResponsesNeverBecomeMergeReady() async throws {
        for response in [
            policyEvaluationsResponse(status: 403), policyEvaluationsResponse(status: 429),
            policyEvaluationsResponse(status: 500), policyEvaluationsResponse(continuation: "unexpected"),
            policyEvaluationsResponse([policyEvaluationJSON(number: 99)]),
            policyEvaluationsResponse([
                policyEvaluationJSON(number: 99)
                    .replacingOccurrences(of: "project-id/99", with: "project-id%2F99")
            ]),
            policyEvaluationsResponse([
                policyEvaluationJSON().replacingOccurrences(of: "project-id/1", with: "project-id%ZZ1")
            ]),
            policyEvaluationsResponse([
                policyEvaluationJSON().replacingOccurrences(of: "project-id/1", with: "project-id%252F1")
            ]),
            HTTPResult(data: Data(#"{"value":[{"status":"approved"}]}"#.utf8), status: 200)
        ] {
            let transport = StubTransport(
                [connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]), pullRequestsResponse([ownPR()])],
                policies: [1: [response]]
            )
            do {
                _ = try await load(authoring(), transport: transport)
                XCTFail("Incomplete approvals must be reported, not guessed.")
            } catch {
                XCTAssertTrue(error is ReviewQError)
            }
        }
    }

    func testPolicyArtifactCorrelationAcceptsEncodedSeparatorsForTheSamePR() async throws {
        for separator in ["/", "%2f", "%2F"] {
            let policy = policyEvaluationJSON()
                .replacingOccurrences(of: "project-id/1", with: "project-id\(separator)1")
            let transport = StubTransport([
                connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]),
                pullRequestsResponse([ownPR()])
            ], policies: [1: [policyEvaluationsResponse([policy])]])
            let snapshot = try await load(authoring(), transport: transport)
            XCTAssertTrue(try XCTUnwrap(snapshot.pullRequests.first).isMergeReady)
        }
    }

    func testRefreshUpdatesAuthoredCommentsAndApprovalsWithoutCachingPRState() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]), pullRequestsResponse([ownPR()]),
            pullRequestsResponse([]), projectsResponse(["Project"]), pullRequestsResponse([ownPR()])
        ], policies: [1: [policyEvaluationsResponse(), policyEvaluationsResponse([policyEvaluationJSON(status: "rejected")])]])
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(header: "synthetic-authored-account"), transport: transport
        )
        let first = try await loader.load(configuration: authoring())
        XCTAssertTrue(try XCTUnwrap(first.pullRequests.first).isMergeReady)
        await transport.setHistory(reviewThreadsResponse([reviewCommentThreadJSON(statusJSON: #""active""#)]), for: 1)
        let next = try await loader.load(configuration: authoring())
        let request = try XCTUnwrap(next.pullRequests.first)
        XCTAssertEqual(request.authoredStatusLabel, "1 Active Comments")
        XCTAssertEqual(request.authoredStatus?.hasRequiredApprovals, false)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/policy/evaluations") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/threads") == true }.count, 2)
    }

    func testStoreKeepsReviewCountsFailureSnapshotAndPersistsOptOut() async throws {
        let suite = "ReviewQ-authored-store-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let authored = model(status: AuthoredPullRequestStatus(activeCommentCount: 0, hasRequiredApprovals: true))
        let snapshot = ReviewSnapshot(pullRequests: [authored])
        let loader = StubReviewLoader([.success(snapshot), .failure(.http(403, "approvals")), .success(ReviewSnapshot(pullRequests: []))])
        let store = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader)
        var settings = authoring()
        try store.save(settings)
        let restored = ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader)
        XCTAssertTrue(restored.configuration.showAuthoredPullRequests)
        await store.refresh()
        XCTAssertEqual(store.menuBarTitle, " 0")
        await store.refresh()
        XCTAssertEqual(store.snapshot, snapshot)
        XCTAssertNotNil(store.errorMessage)
        XCTAssertEqual(store.menuBarTitle, " 0 !")
        settings.showAuthoredPullRequests = false
        try store.save(settings)
        XCTAssertNil(store.snapshot)
        await store.refresh()
        XCTAssertEqual(store.snapshot?.pullRequests, [])
        XCTAssertFalse(ReviewStore(defaults: defaults, keychain: StubPATStore(), loader: loader).configuration.showAuthoredPullRequests)
    }

    private func authoring(_ urls: String = "https://dev.azure.com/example/Project") -> ReviewConfiguration {
        var settings = configuration(urls)
        settings.showAuthoredPullRequests = true
        return settings
    }

    private func ownPR(
        number: Int = 1, draft: Bool = false, status: String = "active", project: String = "Project",
        date: String = "2026-10-01T12:30:00.1234567Z"
    ) -> String {
        pullRequestJSON(
            number: number, draft: draft, status: status, author: reviewerID, date: date,
            hasReviewer: false, project: project
        )
    }

    private func load(_ settings: ReviewConfiguration, transport: StubTransport) async throws -> ReviewSnapshot {
        try await AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: transport
        ).load(configuration: settings)
    }

    private func summary(
        threads: [String] = [], policies: [String] = [], reviewers: [String] = []
    ) throws -> AuthoredPullRequestStatus {
        let decoder = JSONDecoder()
        return try AuthoredPullRequestStatus.from(
            threads: decoder.decode(APIList<APIReviewThread>.self, from: reviewThreadsResponse(threads).data).value,
            reviewers: decoder.decode([APIReviewer].self, from: Data("[\(reviewers.joined(separator: ","))]".utf8)),
            evaluations: decoder.decode(
                APIList<APIPolicyEvaluation>.self, from: policyEvaluationsResponse(policies).data
            ).value
        )
    }

    private func reviewer(vote: Int, required: Bool, declined: Bool = false) -> String {
        #"{"id":"\#(authorID)","vote":\#(vote),"isRequired":\#(required),"hasDeclined":\#(declined)}"#
    }

    private func model(status: AuthoredPullRequestStatus, draft: Bool = false) -> ReviewPullRequest {
        ReviewPullRequest(
            number: 1, title: "Authored fixture", author: "Fixture author", organization: "example",
            project: "Project", repository: "Repository", repositoryID: "repo-id", createdAt: .distantPast,
            url: URL(string: "https://dev.azure.com/example/Project/_git/repo/pullrequest/1")!,
            isRequired: false, isDraft: draft, reviewStage: .firstReview, authoredStatus: status
        )
    }

    private func query(_ request: URLRequest) -> [String: String] {
        guard let url = request.url else { return [:] }
        return Dictionary(uniqueKeysWithValues:
            (URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? [])
                .compactMap { item in item.value.map { (item.name, $0) } }
        )
    }
}
