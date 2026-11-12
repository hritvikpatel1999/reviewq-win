import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class FollowedPeopleTests: XCTestCase {
    private let otherAuthorID = "33333333-3333-3333-3333-333333333333"

    func testOptOutMakesExactlyTheOriginalRequestsEvenWithSavedEmails() async throws {
        var settings = configuration()
        settings.followedAuthorEmails = "alex@example.com;sam@example.com"
        let transport = StubTransport([connectionResponse(), pullRequestsResponse([pullRequestJSON()])])
        let result = try await load(settings, transport: transport)
        XCTAssertEqual(result.pullRequests.map(\.number), [1])
        XCTAssertFalse(try XCTUnwrap(result.pullRequests.first).isFollowedOnly)
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 3)
        XCTAssertEqual(requests.compactMap { $0.url?.path }, [
            "/example/_apis/connectionData", "/example/Project/_apis/git/pullrequests",
            "/example/Project/_apis/git/repositories/repo-id/pullRequests/1/threads"
        ])
    }

    func testFollowedPRsNeedNoReviewerAndAssignedPRsAreNeverDuplicatedOrReenriched() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON()]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project"]),
            pullRequestsResponse([
                pullRequestJSON(), pullRequestJSON(number: 2, hasReviewer: false),
                pullRequestJSON(number: 3, vote: 10),
                pullRequestJSON(number: 4, draft: true, hasReviewer: false),
                pullRequestJSON(number: 5, status: "completed", hasReviewer: false),
                pullRequestJSON(number: 6, author: otherAuthorID, hasReviewer: false)
            ])
        ], histories: [
            2: reviewThreadsResponse([reviewCommentThreadJSON(statusJSON: #""fixed""#)]),
            3: reviewThreadsResponse([voteThreadJSON()])
        ])
        let result = try await load(following(), transport: transport)
        XCTAssertEqual(result.pullRequests.count, 3)
        let assigned = try XCTUnwrap(result.pullRequests.first { $0.number == 1 })
        XCTAssertFalse(assigned.isFollowedOnly)
        XCTAssertEqual(assigned.authorLabel, "Fixture author")
        let followed = result.pullRequests.filter(\.isFollowedOnly)
        XCTAssertEqual(followed.map(\.number), [2, 3])
        XCTAssertFalse(followed[0].isRequired)
        XCTAssertEqual(followed[0].reviewStage, .commentsResolved)
        XCTAssertEqual(followed[0].authorLabel, "Fixture author in Repository")
        XCTAssertEqual(followed[1].reviewStage, .approved)
        XCTAssertFalse(followed[1].reviewStageExplanation.contains("needs your vote again"))
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 8)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/1/threads") == true }.count, 1)
        let identity = try XCTUnwrap(requests.first { $0.url?.host == "vssps.dev.azure.com" })
        XCTAssertEqual(query(identity)["filterValue"], "alex@example.com")
        XCTAssertEqual(query(identity)["queryMembership"], "None")
        let creatorQuery = try XCTUnwrap(requests.last { query($0)["searchCriteria.creatorId"] != nil })
        XCTAssertEqual(query(creatorQuery)["searchCriteria.creatorId"]?.lowercased(), authorID)
        XCTAssertEqual(query(creatorQuery)["searchCriteria.status"], "active")
        XCTAssertNil(query(creatorQuery)["searchCriteria.reviewerId"])
    }

    func testFollowingSearchesEveryAccessibleProjectInEachConfiguredOrganizationOnlyOnce() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), pullRequestsResponse([]),
            connectionResponse(), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project", "Outside"]),
            pullRequestsResponse([pullRequestJSON(number: 1, hasReviewer: false)]),
            pullRequestsResponse([pullRequestJSON(number: 2, hasReviewer: false, project: "Outside")]),
            identitiesResponse([followedIdentityJSON(id: otherAuthorID)]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(number: 3, author: otherAuthorID, hasReviewer: false)])
        ])
        let settings = following("""
        https://dev.azure.com/example/Project
        https://dev.azure.com/EXAMPLE/Other
        https://dev.azure.com/another/Project
        """)
        let result = try await load(settings, transport: transport)
        XCTAssertEqual(result.pullRequests.count, 3)
        XCTAssertTrue(result.pullRequests.allSatisfy(\.isFollowedOnly))
        XCTAssertTrue(result.pullRequests.contains { $0.project == "Outside" })
        XCTAssertEqual(Set(result.pullRequests.map(\.organization)), ["example", "another"])
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 2)
    }

    func testOrganizationProjectDiscoveryIsReusedAndEmailAliasesResolveOneAuthorQuery() async throws {
        var settings = following("https://dev.azure.com/example")
        settings.followedAuthorEmails = "alex@example.com;alias@example.com;ALEX@example.com"
        let transport = StubTransport([
            connectionResponse(), projectsResponse(["Project"]), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON()]),
            identitiesResponse([followedIdentityJSON(email: "alex@example.com", account: "alias@example.com")]),
            pullRequestsResponse([pullRequestJSON(hasReviewer: false)])
        ])
        let result = try await load(settings, transport: transport)
        XCTAssertEqual(result.pullRequests.count, 1)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 2)
        XCTAssertEqual(requests.filter { query($0)["searchCriteria.creatorId"] != nil }.count, 1)
    }

    func testMultipleFollowedPeopleUseSeparateCreatorFiltersAndOneSharedPeopleQueue() async throws {
        var settings = following()
        settings.followedAuthorEmails = "alex@example.com;sam@example.com"
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON()]),
            identitiesResponse([followedIdentityJSON(email: "SAM@example.com", id: otherAuthorID)]),
            projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(number: 1, date: "2026-09-01T00:00:00Z", hasReviewer: false)]),
            pullRequestsResponse([
                pullRequestJSON(number: 2, author: otherAuthorID, date: "2026-10-01T00:00:00Z", hasReviewer: false)
            ])
        ])
        let result = try await load(settings, transport: transport)
        XCTAssertEqual(result.pullRequests.map(\.number), [2, 1])
        XCTAssertTrue(result.pullRequests.allSatisfy(\.isFollowedOnly))
        let requests = await transport.requests
        XCTAssertEqual(
            Set(requests.compactMap { query($0)["searchCriteria.creatorId"]?.lowercased() }),
            [authorID, otherAuthorID]
        )
    }

    func testInactiveAuthorsCanStillHaveActivePullRequests() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON(active: false)]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(hasReviewer: false)])
        ])
        let result = try await load(following(), transport: transport)
        XCTAssertEqual(result.pullRequests.count, 1)
        XCTAssertTrue(try XCTUnwrap(result.pullRequests.first).isFollowedOnly)
    }

    func testFollowedIdentitiesCanExistInOnlyOneOfTheConfiguredOrganizations() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), connectionResponse(), pullRequestsResponse([]),
            identitiesResponse([]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(hasReviewer: false)])
        ])
        let result = try await load(following("""
        https://dev.azure.com/example/Project
        https://dev.azure.com/another/Project
        """), transport: transport)
        XCTAssertEqual(result.pullRequests.map(\.organization), ["another"])
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path == "/example/_apis/projects" }.count, 0)
    }

    func testCreatorPaginationDraftPreferenceAndNewestFirstOrdering() async throws {
        var settings = following()
        settings.includeDrafts = true
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project"]),
            pullRequestsResponse((1...100).map {
                pullRequestJSON(number: $0, draft: true, date: "2026-09-01T00:00:00Z", hasReviewer: false)
            }),
            pullRequestsResponse([
                pullRequestJSON(number: 101, date: "2026-10-01T00:00:00Z", hasReviewer: false),
                pullRequestJSON(number: 1, draft: true, hasReviewer: false)
            ])
        ])
        let result = try await load(settings, transport: transport)
        XCTAssertEqual(result.pullRequests.count, 101)
        XCTAssertEqual(result.pullRequests.first?.number, 101)
        XCTAssertEqual(result.pullRequests.dropFirst().map(\.id), result.pullRequests.dropFirst().map(\.id).sorted())
        XCTAssertEqual(result.pullRequests.filter(\.isDraft).count, 100)
        let requests = await transport.requests
        XCTAssertEqual(
            requests.filter { query($0)["searchCriteria.creatorId"] != nil }.map { query($0)["$skip"] },
            ["0", "100"]
        )
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/threads") == true }.count, 101)
    }

    func testUnresolvedOrFuzzyEmailsNeverSilentlyBecomeAnEmptySuccess() async throws {
        for identities in [
            [], [followedIdentityJSON(email: "not-alex@example.com")],
            [followedIdentityJSON(group: true)]
        ] {
            let transport = StubTransport([
                connectionResponse(), pullRequestsResponse([]), identitiesResponse(identities)
            ])
            do {
                _ = try await load(following(), transport: transport)
                XCTFail("An unresolved followed email must be reported.")
            } catch let error as ReviewQError {
                guard case .unresolvedFollowedAuthors(let emails) = error else {
                    return XCTFail("Unexpected error: \(error)")
                }
                XCTAssertEqual(emails, ["alex@example.com"])
            }
        }
    }

    func testAmbiguousOrTruncatedIdentityResultsFailExplicitly() async throws {
        for response in [
            identitiesResponse([followedIdentityJSON(), followedIdentityJSON(id: otherAuthorID)]),
            identitiesResponse([followedIdentityJSON()], continuation: "more"),
            identitiesResponse([followedIdentityJSON(id: "00000000-0000-0000-0000-000000000000")])
        ] {
            do {
                _ = try await load(following(), transport: StubTransport([
                    connectionResponse(), pullRequestsResponse([]), response
                ]))
                XCTFail("Incomplete or ambiguous identity data must not replace the queue.")
            } catch {
                XCTAssertTrue(error is ReviewQError)
            }
        }
    }

    func testFollowedLookupAndProjectFailuresNeverBecomeSuccess() async throws {
        for status in [403, 429, 500] {
            for failProjects in [false, true] {
                var responses = [connectionResponse(), pullRequestsResponse([])]
                if failProjects { responses.append(identitiesResponse([followedIdentityJSON()])) }
                responses.append(HTTPResult(data: Data(), status: status))
                do {
                    _ = try await load(following(), transport: StubTransport(responses))
                    XCTFail("Followed query failures must preserve the previous snapshot.")
                } catch let error as ReviewQError {
                    guard case .http(let code, _) = error else { return XCTFail("Unexpected error: \(error)") }
                    XCTAssertEqual(code, status)
                }
            }
        }
    }

    func testCountsIncludeFollowedPRsFailuresPreserveBothSectionsAndOptingOutClearsThem() async throws {
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON()]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(), pullRequestJSON(number: 2, hasReviewer: false)]),
            HTTPResult(data: Data(), status: 403),
            connectionResponse(), pullRequestsResponse([pullRequestJSON()])
        ])
        let suite = "ReviewQ-followed-store-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ReviewStore(
            defaults: defaults, keychain: StubPATStore(),
            loader: AzureDevOpsReviewLoader(authorization: StubAuthorization(), transport: transport)
        )
        var settings = following()
        try store.save(settings)
        await store.refresh()
        let successful = try XCTUnwrap(store.snapshot)
        XCTAssertEqual(successful.pullRequests.filter(\.isFollowedOnly).count, 1)
        XCTAssertEqual(store.menuBarTitle, " 2")
        await store.refresh()
        XCTAssertEqual(store.snapshot, successful)
        XCTAssertEqual(store.menuBarTitle, " 2 !")
        settings.followPeople = false
        try store.save(settings)
        XCTAssertNil(store.snapshot)
        XCTAssertEqual(store.configuration.followedAuthorEmails, "alex@example.com")
        await store.refresh()
        XCTAssertEqual(store.menuBarTitle, " 1")
        XCTAssertTrue(try XCTUnwrap(store.snapshot).pullRequests.allSatisfy { !$0.isFollowedOnly })
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 1)
    }

    private func following(_ urls: String = "https://dev.azure.com/example/Project") -> ReviewConfiguration {
        var settings = configuration(urls)
        settings.followPeople = true
        settings.followedAuthorEmails = "alex@example.com"
        return settings
    }

    private func load(_ settings: ReviewConfiguration, transport: StubTransport) async throws -> ReviewSnapshot {
        try await AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: transport
        ).load(configuration: settings)
    }

    private func query(_ request: URLRequest) -> [String: String] {
        guard let url = request.url else { return [:] }
        return Dictionary(
            uniqueKeysWithValues: (URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? [])
                .compactMap { item in item.value.map { (item.name, $0) } }
        )
    }
}
