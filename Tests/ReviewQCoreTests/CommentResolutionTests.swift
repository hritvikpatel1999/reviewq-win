import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class CommentResolutionTests: XCTestCase {
    func testAllOwnFixedThreadsShowResolvedWithoutCountingOtherReviewersOrSystemThreads() async throws {
        let request = try await load([
            reviewCommentThreadJSON(author: reviewerID.uppercased(), statusJSON: "\"fixed\""),
            reviewCommentThreadJSON(statusJSON: "2"),
            reviewCommentThreadJSON(author: authorID, statusJSON: "\"active\""),
            reviewCommentThreadJSON(type: "system", statusJSON: "\"active\""),
            reviewCommentThreadJSON(threadType: "RefUpdate", statusJSON: "\"active\"")
        ])
        XCTAssertEqual(request.reviewStage, .commentsResolved)
        XCTAssertEqual(request.reviewStageLabel, "Your comments are resolved")
        XCTAssertEqual(request.commentResolution, ReviewCommentResolution(resolvedCount: 2, openCount: 0))
    }

    func testMixedCountsDescribeThreadsRatherThanReplies() async throws {
        let replies = """
        {"status":"fixed","comments":[
          {"author":{"id":"\(authorID)"},"commentType":"text"},
          {"author":{"id":"\(reviewerID)"},"commentType":"text"},
          {"author":{"id":"\(reviewerID)"},"commentType":"text"}
        ]}
        """
        let request = try await load([
            replies, reviewCommentThreadJSON(statusJSON: "\"FIXED\""),
            reviewCommentThreadJSON(statusJSON: "\"pending\"")
        ])
        XCTAssertEqual(request.reviewStage, .commented)
        XCTAssertEqual(request.reviewStageLabel, "You commented [2 resolved, 1 open]")
        XCTAssertEqual(request.commentResolution, ReviewCommentResolution(resolvedCount: 2, openCount: 1))
        XCTAssertTrue(request.reviewStageExplanation.contains("discussion threads"))
    }

    func testLegacyDiscussionCommentsRetainResolutionWithoutACommentType() async throws {
        for type in [nil, "unknown"] {
            let request = try await load([
                reviewCommentThreadJSON(type: type, content: "Fixture feedback", statusJSON: "\"fixed\"")
            ])
            XCTAssertEqual(request.reviewStageLabel, "Your comments are resolved")
            XCTAssertEqual(request.commentResolution?.resolvedCount, 1)
        }
    }

    func testKnownOpenStatesShowZeroResolvedAndIncludePending() async throws {
        let request = try await load([
            reviewCommentThreadJSON(statusJSON: "1"),
            reviewCommentThreadJSON(statusJSON: "6")
        ])
        XCTAssertEqual(request.reviewStageLabel, "You commented [0 resolved, 2 open]")
    }

    func testMissingUnknownAndOtherClosureStatesDoNotClaimResolutionOrIncompleteTotals() async throws {
        for status in [nil, "null", "\"unknown\"", "0", "99", "\"futureStatus\"",
                       "\"wontFix\"", "\"closed\"", "\"byDesign\"", "3", "4", "5"] {
            let request = try await load([
                reviewCommentThreadJSON(statusJSON: "\"fixed\""),
                reviewCommentThreadJSON(statusJSON: status)
            ])
            XCTAssertEqual(request.reviewStageLabel, "You commented", "\(String(describing: status))")
            XCTAssertNil(request.commentResolution)
            XCTAssertTrue(request.reviewStageExplanation.contains("unavailable"))
        }
    }

    func testDeletedThreadsAndDeletedOwnRepliesAreExcludedButHistoryIsPreserved() async throws {
        let request = try await load([
            reviewCommentThreadJSON(statusJSON: "\"fixed\""),
            reviewCommentThreadJSON(deleted: true, statusJSON: "\"active\""),
            reviewCommentThreadJSON(statusJSON: "\"active\"", threadDeleted: true)
        ])
        XCTAssertEqual(request.reviewStageLabel, "Your comments are resolved")
        XCTAssertEqual(request.commentResolution?.resolvedCount, 1)
        for thread in [
            reviewCommentThreadJSON(deleted: true, statusJSON: "\"fixed\""),
            reviewCommentThreadJSON(statusJSON: "\"fixed\"", threadDeleted: true)
        ] {
            let deleted = try await load([thread])
            XCTAssertEqual(deleted.reviewStageLabel, "You commented")
        }
        let empty = try await load([])
        XCTAssertEqual(empty.reviewStage, .firstReview)
        let otherReviewer = try await load([reviewCommentThreadJSON(author: authorID, statusJSON: "\"fixed\"")])
        XCTAssertEqual(otherReviewer.reviewStage, .firstReview)
    }

    func testNonzeroVoteHistoryKeepsPrecedenceOverResolvedComments() async throws {
        for (vote, stage) in [(10, ReviewStage.approved), (5, .approvedWithSuggestions),
                              (-5, .waitedForAuthor), (-10, .rejected)] {
            let request = try await load([
                reviewCommentThreadJSON(statusJSON: "\"fixed\""),
                voteThreadJSON(vote: vote), voteThreadJSON(vote: 0)
            ])
            XCTAssertEqual(request.reviewStage, stage)
            XCTAssertEqual(request.reviewStageLabel, stage.label)
            XCTAssertNil(request.commentResolution)
        }
    }

    func testCountersUpdateDuringTheExistingRefreshWithoutChangingPRIdentity() async throws {
        let transport = StubTransport(
            [connectionResponse(), pullRequestsResponse([pullRequestJSON()]),
             pullRequestsResponse([pullRequestJSON()])],
            histories: [1: reviewThreadsResponse([reviewCommentThreadJSON(statusJSON: "\"active\"")])]
        )
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(header: "Bearer synthetic-cache-fixture"), transport: transport
        )
        let firstResult = try await loader.load(configuration: configuration())
        let first = try XCTUnwrap(firstResult.pullRequests.first)
        await transport.setHistory(
            reviewThreadsResponse([reviewCommentThreadJSON(statusJSON: "\"fixed\"")]), for: 1
        )
        let nextResult = try await loader.load(configuration: configuration())
        let next = try XCTUnwrap(nextResult.pullRequests.first)
        XCTAssertEqual(first.id, next.id)
        XCTAssertEqual(first.reviewStageLabel, "You commented [0 resolved, 1 open]")
        XCTAssertEqual(next.reviewStageLabel, "Your comments are resolved")
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 5)
    }

    func testAllThreeStatesUseExactlyTheExistingHTTPRequestsAndQueryParameters() async throws {
        let authorization = StubAuthorization()
        let transport = StubTransport(
            [connectionResponse(), pullRequestsResponse((1...3).map { pullRequestJSON(number: $0) })],
            histories: [
                1: reviewThreadsResponse([reviewCommentThreadJSON(statusJSON: "\"fixed\"")]),
                2: reviewThreadsResponse([
                    reviewCommentThreadJSON(statusJSON: "\"fixed\""),
                    reviewCommentThreadJSON(statusJSON: "\"fixed\""),
                    reviewCommentThreadJSON(statusJSON: "\"active\"")
                ]),
                3: reviewThreadsResponse([reviewCommentThreadJSON()])
            ]
        )
        let result = try await AzureDevOpsReviewLoader(
            authorization: authorization, transport: transport
        ).load(configuration: configuration())
        let labels = Dictionary(uniqueKeysWithValues: result.pullRequests.map { ($0.number, $0.reviewStageLabel) })
        XCTAssertEqual(labels, [
            1: "Your comments are resolved",
            2: "You commented [2 resolved, 1 open]",
            3: "You commented"
        ])
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 5, "One identity request, one PR list, and one existing thread request per PR.")
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/git/pullrequests") == true }.count, 1)
        let histories = requests.filter { $0.url?.path.hasSuffix("/threads") == true }
        XCTAssertEqual(histories.count, 3)
        XCTAssertEqual(
            Set(histories.compactMap { $0.url?.path }),
            Set((1...3).map { "/example/Project/_apis/git/repositories/repo-id/pullRequests/\($0)/threads" })
        )
        for request in histories {
            XCTAssertEqual(request.httpMethod, "GET")
            let url = try XCTUnwrap(request.url)
            XCTAssertEqual(
                URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
                [URLQueryItem(name: "api-version", value: "7.1")]
            )
        }
        let calls = await authorization.calls
        XCTAssertEqual(calls, 1)
    }

    func testMalformedStatusTypesFailVisiblyRatherThanClaimingResolved() async throws {
        for status in ["true", "{}", "[]"] {
            do {
                _ = try await load([reviewCommentThreadJSON(statusJSON: status)])
                XCTFail("Malformed resolution data must fail.")
            } catch {
                XCTAssertTrue(error.localizedDescription.contains("unexpected Azure DevOps response"))
            }
        }
    }

    private func load(_ threads: [String]) async throws -> ReviewPullRequest {
        let result = try await AzureDevOpsReviewLoader(
            authorization: StubAuthorization(),
            transport: StubTransport(
                [connectionResponse(), pullRequestsResponse([pullRequestJSON()])],
                histories: [1: reviewThreadsResponse(threads)]
            )
        ).load(configuration: configuration())
        return try XCTUnwrap(result.pullRequests.first)
    }
}
