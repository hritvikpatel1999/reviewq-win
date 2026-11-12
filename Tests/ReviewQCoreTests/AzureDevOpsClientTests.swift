import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class AzureDevOpsClientTests: XCTestCase {
    func testOnlyUnvotedActiveAssignedReviewsAreIncluded() async throws {
        let responses = [
            pullRequestJSON(number: 1),
            pullRequestJSON(number: 2, vote: 10),
            pullRequestJSON(number: 3, vote: 5),
            pullRequestJSON(number: 4, vote: -5),
            pullRequestJSON(number: 5, vote: -10),
            pullRequestJSON(number: 6, draft: true),
            pullRequestJSON(number: 7, status: "completed"),
            pullRequestJSON(number: 8, reviewer: authorID),
            pullRequestJSON(number: 9, author: reviewerID),
            pullRequestJSON(number: 10, declined: true),
            pullRequestJSON(number: 11, reviewer: reviewerID.uppercased(), date: "2026-09-01T12:30:00Z")
        ]
        let transport = StubTransport([connectionResponse(), pullRequestsResponse(responses)])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        let result = try await loader.load(configuration: configuration())
        XCTAssertEqual(result.pullRequests.map(\.number), [1, 11])
        XCTAssertTrue(result.pullRequests.allSatisfy(\.isRequired))
        XCTAssertEqual(result.pullRequests[0].url.absoluteString,
                       "https://dev.azure.com/example/Project/_git/repo-id/pullrequest/1")
        let requests = await transport.requests.filter { $0.url?.path.hasSuffix("/git/pullrequests") == true }
        let url = try XCTUnwrap(requests.last?.url)
        let query = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        XCTAssertEqual(query.first { $0.name == "searchCriteria.reviewerId" }?.value?.lowercased(), reviewerID)
        XCTAssertEqual(query.first { $0.name == "searchCriteria.status" }?.value, "active")
        XCTAssertEqual(requests.last?.value(forHTTPHeaderField: "Authorization"), "Bearer fixture-1")
    }

    func testDraftPreferenceIsRespected() async throws {
        var settings = configuration()
        settings.includeDrafts = true
        let transport = StubTransport([connectionResponse(), pullRequestsResponse([pullRequestJSON(draft: true)])])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        let result = try await loader.load(configuration: settings)
        XCTAssertEqual(result.pullRequests.count, 1)
        XCTAssertTrue(result.pullRequests[0].isDraft)
    }

    func testPullRequestPaginationHasNoHundredItemTruncation() async throws {
        let firstPage = (1...100).map { pullRequestJSON(number: $0) }
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse(firstPage),
            pullRequestsResponse([pullRequestJSON(number: 101)])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        let result = try await loader.load(configuration: configuration())
        XCTAssertEqual(result.pullRequests.count, 101)
        let requests = await transport.requests
        let skips = requests.dropFirst().compactMap { request in
            URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?
                .queryItems?.first { $0.name == "$skip" }?.value
        }
        XCTAssertEqual(skips, ["0", "100"])
    }

    func testOrganizationProjectPaginationAndOverlappingScopesDeduplicatePRs() async throws {
        let projects1 = HTTPResult(
            data: Data(#"{"value":[{"id":"p1","name":"Project"}]}"#.utf8),
            status: 200, continuationToken: "next-page"
        )
        let projects2 = HTTPResult(
            data: Data(#"{"value":[{"id":"p2","name":"Other"}]}"#.utf8), status: 200
        )
        let transport = StubTransport([
            connectionResponse(), projects1, projects2,
            pullRequestsResponse([pullRequestJSON()]), pullRequestsResponse([]),
            pullRequestsResponse([pullRequestJSON()])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        let result = try await loader.load(configuration: configuration("""
        https://dev.azure.com/example
        https://dev.azure.com/example/Project
        """))
        XCTAssertEqual(result.pullRequests.count, 1)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
        let continuationURL = try XCTUnwrap(requests[2].url)
        XCTAssertEqual(
            URLComponents(url: continuationURL, resolvingAgainstBaseURL: false)?
                .queryItems?.first { $0.name == "continuationToken" }?.value, "next-page"
        )
    }

    func testIdentityIsResolvedPerOrganization() async throws {
        let otherReviewer = "33333333-3333-3333-3333-333333333333"
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON()]),
            connectionResponse(otherReviewer),
            pullRequestsResponse([pullRequestJSON(reviewer: otherReviewer)])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        let result = try await loader.load(configuration: configuration("""
        https://dev.azure.com/example/Project
        https://dev.azure.com/another/Project
        """))
        XCTAssertEqual(result.pullRequests.count, 2)
    }

    func testUnauthorizedAzureCLIRequestRenewsOnce() async throws {
        let authorization = StubAuthorization()
        let transport = StubTransport([
            HTTPResult(data: Data(), status: 401),
            connectionResponse(), pullRequestsResponse([])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: authorization, transport: transport)
        let result = try await loader.load(configuration: configuration())
        XCTAssertTrue(result.pullRequests.isEmpty)
        let calls = await authorization.calls
        let invalidations = await authorization.invalidations
        XCTAssertEqual(calls, 2)
        XCTAssertEqual(invalidations, 1)
    }

    func testPATUnauthorizedAndOtherHTTPFailuresNeverBecomeEmptySuccess() async throws {
        for status in [401, 403, 404, 429, 500, 302] {
            var settings = configuration()
            settings.authentication = .personalAccessToken
            let authorization = StubAuthorization()
            let loader = AzureDevOpsReviewLoader(
                authorization: authorization,
                transport: StubTransport([HTTPResult(data: Data(), status: status)])
            )
            do {
                _ = try await loader.load(configuration: settings)
                XCTFail("HTTP \(status) must fail")
            } catch let error as ReviewQError {
                guard case .http(let actualStatus, _) = error else { return XCTFail("Wrong error: \(error)") }
                XCTAssertEqual(actualStatus, status)
            }
            let calls = await authorization.calls
            XCTAssertEqual(calls, 1)
        }
    }

    func testMalformedResponsesAndAnonymousIdentityFail() async throws {
        for response in [
            HTTPResult(data: Data("<html>Sign in</html>".utf8), status: 200),
            connectionResponse("00000000-0000-0000-0000-000000000000")
        ] {
            var settings = configuration()
            settings.authentication = .personalAccessToken
            let loader = AzureDevOpsReviewLoader(
                authorization: StubAuthorization(), transport: StubTransport([response])
            )
            do {
                _ = try await loader.load(configuration: settings)
                XCTFail("Malformed or anonymous response must fail")
            } catch { XCTAssertTrue(error is ReviewQError) }
        }
    }

    func testRepeatedPagesFailInsteadOfLooping() async throws {
        let page = pullRequestsResponse((1...100).map { pullRequestJSON(number: $0) })
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: StubTransport([connectionResponse(), page, page])
        )
        do {
            _ = try await loader.load(configuration: configuration())
            XCTFail("Repeated pages must fail")
        } catch let error as ReviewQError {
            guard case .invalidPagination = error else { return XCTFail("Wrong error: \(error)") }
        }
    }

    func testRepeatedProjectContinuationTokensFail() async throws {
        let page = HTTPResult(
            data: Data(#"{"value":[{"id":"p1","name":"Project"}]}"#.utf8),
            status: 200, continuationToken: "repeated"
        )
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: StubTransport([connectionResponse(), page, page])
        )
        do {
            _ = try await loader.load(configuration: configuration("https://dev.azure.com/example"))
            XCTFail("Repeated continuation tokens must fail")
        } catch let error as ReviewQError {
            guard case .invalidPagination = error else { return XCTFail("Wrong error: \(error)") }
        }
    }

    func testAnySourceFailureRejectsThePartialQueue() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON()]),
            HTTPResult(data: Data(), status: 403)
        ])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        do {
            _ = try await loader.load(configuration: configuration("""
            https://dev.azure.com/example/Project
            https://dev.azure.com/another/Project
            """))
            XCTFail("A partial queue must not be presented as a successful refresh")
        } catch let error as ReviewQError {
            guard case .http(403, let scope) = error else { return XCTFail("Wrong error: \(error)") }
            XCTAssertEqual(scope, "another/Project")
        }
    }

    func testRedirectDelegateDoesNotForwardAuthenticatedRequests() async throws {
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let original = try XCTUnwrap(URL(string: "https://dev.azure.com/example/_apis/projects"))
        let destination = try XCTUnwrap(URL(string: "https://other.example.test/"))
        let response = try XCTUnwrap(HTTPURLResponse(
            url: original, statusCode: 302, httpVersion: nil,
            headerFields: ["Location": destination.absoluteString]
        ))
        let task = session.dataTask(with: original)
        let redirected: URLRequest? = await withCheckedContinuation { continuation in
            RedirectBlocker().urlSession(
                session, task: task, willPerformHTTPRedirection: response,
                newRequest: URLRequest(url: destination)
            ) { continuation.resume(returning: $0) }
        }
        XCTAssertNil(redirected)
        task.cancel()
    }
}
