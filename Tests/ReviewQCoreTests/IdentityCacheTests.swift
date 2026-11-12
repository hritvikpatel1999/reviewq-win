import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class IdentityCacheTests: XCTestCase {
    private let header = "Bearer synthetic-identity-cache-token"
    private let tenant = "44444444-4444-4444-4444-444444444444"
    private let differentID = "33333333-3333-3333-3333-333333333333"

    private func preferences() throws -> (UserDefaults, String) {
        let name = "ReviewQ-identity-tests-\(UUID())"
        return (try XCTUnwrap(UserDefaults(suiteName: name)), name)
    }

    private func following() -> ReviewConfiguration {
        var settings = configuration()
        settings.followPeople = true
        settings.followedAuthorEmails = "alex@example.com"
        return settings
    }

    private func scope(_ settings: ReviewConfiguration = configuration(), header: String? = nil) throws -> String {
        try IdentityCache.scope(
            configuration: settings, header: header ?? self.header,
            sources: settings.validatedSources(), followedEmails: settings.validatedFollowedAuthors()
        )
    }

    private func bearer(oid: String = reviewerID, tid: String? = nil, nonce: String) throws -> String {
        let data = try JSONEncoder().encode(["oid": oid, "tid": tid ?? tenant, "nonce": nonce])
        let payload = data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        return "Bearer e30.\(payload).signature-\(nonce)"
    }

    func testUserPositiveAndNegativeAuthorEntriesSurviveNewCacheInstances() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let cache = IdentityCache(defaults: defaults)
        let key = try scope()
        let context = try cache.begin(scope: key)
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        let author = try XCTUnwrap(UUID(uuidString: authorID))
        try cache.storeCurrentUser(user, organization: "example", context: context)
        try cache.storeFollowedAuthor(author, organization: "example", email: "alex@example.com", context: context)
        try cache.storeFollowedAuthor(nil, organization: "example", email: "sam@example.com", context: context)
        let restored = IdentityCache(defaults: defaults)
        let restoredContext = try restored.begin(scope: key)
        XCTAssertEqual(try restored.currentUser(organization: "example", context: restoredContext), user)
        XCTAssertEqual(
            try restored.followedAuthor(organization: "example", email: "alex@example.com", context: restoredContext),
            .identity(author)
        )
        XCTAssertEqual(
            try restored.followedAuthor(organization: "example", email: "sam@example.com", context: restoredContext),
            .notFound
        )
        let text = try XCTUnwrap(String(data: XCTUnwrap(defaults.data(forKey: IdentityCache.settingsKey)), encoding: .utf8))
        XCTAssertFalse(text.contains(header))
        XCTAssertNil(defaults.data(forKey: ReviewStore.settingsKey))
    }

    func testPositiveEntriesExpireAtExactlySevenDaysWithoutSlidingOnHits() throws {
        let clock = IdentityTestClock()
        let cache = IdentityCache(clock: { clock.now })
        let context = try cache.begin(scope: scope())
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        try cache.storeCurrentUser(user, organization: "example", context: context)
        try cache.storeFollowedAuthor(user, organization: "example", email: "alex@example.com", context: context)
        clock.advance(7 * 24 * 3600 - 1)
        XCTAssertEqual(try cache.currentUser(organization: "example", context: context), user)
        XCTAssertEqual(
            try cache.followedAuthor(organization: "example", email: "alex@example.com", context: context),
            .identity(user)
        )
        clock.advance(1)
        XCTAssertNil(try cache.currentUser(organization: "example", context: context))
        XCTAssertEqual(
            try cache.followedAuthor(organization: "example", email: "alex@example.com", context: context), .missing
        )
    }

    func testNegativeEntriesExpireAtExactlyFifteenMinutesWithoutSlidingOnHits() throws {
        let clock = IdentityTestClock()
        let cache = IdentityCache(clock: { clock.now })
        let context = try cache.begin(scope: scope())
        try cache.storeFollowedAuthor(nil, organization: "example", email: "alex@example.com", context: context)
        clock.advance(899)
        XCTAssertEqual(
            try cache.followedAuthor(organization: "example", email: "alex@example.com", context: context), .notFound
        )
        clock.advance(1)
        XCTAssertEqual(
            try cache.followedAuthor(organization: "example", email: "alex@example.com", context: context), .missing
        )
    }

    func testOrganizationsAndEmailsAreNormalizedButNeverMixed() throws {
        let cache = IdentityCache()
        let context = try cache.begin(scope: scope())
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        let author = try XCTUnwrap(UUID(uuidString: authorID))
        try cache.storeCurrentUser(user, organization: "EXAMPLE", context: context)
        try cache.storeCurrentUser(author, organization: "another", context: context)
        try cache.storeFollowedAuthor(author, organization: "EXAMPLE", email: "ALEX@example.com", context: context)
        XCTAssertEqual(try cache.currentUser(organization: "example", context: context), user)
        XCTAssertEqual(try cache.currentUser(organization: "ANOTHER", context: context), author)
        XCTAssertEqual(
            try cache.followedAuthor(organization: "example", email: "alex@EXAMPLE.com", context: context),
            .identity(author)
        )
        XCTAssertEqual(
            try cache.followedAuthor(organization: "another", email: "alex@example.com", context: context), .missing
        )
        XCTAssertEqual(
            try cache.followedAuthor(organization: "example", email: "sam@example.com", context: context), .missing
        )
    }

    func testPrincipalScopeSurvivesRenewalButSeparatesAccountsTenantsPATsAndAuthMethods() throws {
        let first = try bearer(nonce: "first")
        let renewed = try bearer(nonce: "renewed")
        XCTAssertEqual(try scope(header: first), try scope(header: renewed))
        XCTAssertNotEqual(try scope(header: first), try scope(header: bearer(oid: authorID, nonce: "other")))
        XCTAssertNotEqual(try scope(header: first), try scope(header: bearer(tid: differentID, nonce: "tenant")))
        var settings = configuration()
        settings.tenantID = "example.onmicrosoft.com"
        XCTAssertNotEqual(try scope(header: first), try scope(settings, header: first))
        settings = configuration()
        settings.authentication = .personalAccessToken
        XCTAssertNotEqual(try scope(header: first), try scope(settings, header: first))
        XCTAssertNotEqual(
            try scope(settings, header: "Basic synthetic-pat-one"),
            try scope(settings, header: "Basic synthetic-pat-two")
        )
        XCTAssertNotEqual(try scope(header: "Bearer opaque-one"), try scope(header: "Bearer opaque-two"))
    }

    func testInvalidOrAbsentPrincipalClaimsNeverUseASharedUnknownAccountScope() throws {
        let zero = "00000000-0000-0000-0000-000000000000"
        for claims in [
            "{}", #"{"oid":"not-a-guid","tid":"not-a-guid"}"#,
            #"{"oid":"\#(zero)","tid":"\#(tenant)"}"#,
            #"{"oid":"\#(reviewerID)","tid":"\#(zero)"}"#
        ] {
            let payload = Data(claims.utf8).base64EncodedString()
            XCTAssertNotEqual(
                try scope(header: "Bearer e30.\(payload).first"),
                try scope(header: "Bearer e30.\(payload).second")
            )
        }
    }

    func testOnlyIdentityAffectingSettingsChangeTheScope() throws {
        var settings = following()
        let original = try scope(settings)
        settings.followedAuthorEmails = "ALEX@example.com"
        settings.sourceURLs = "https://EXAMPLE.visualstudio.com/DefaultCollection/project"
        settings.refreshInterval = .fiveMinutes
        settings.includeDrafts = true
        settings.showCount = false
        settings.scoutAdditionalInstructions = "Fixture"
        XCTAssertEqual(try scope(settings), original)
        settings.followedAuthorEmails = "sam@example.com"
        XCTAssertNotEqual(try scope(settings), original)
        settings = following()
        settings.sourceURLs = "https://dev.azure.com/another/Project"
        XCTAssertNotEqual(try scope(settings), original)
        settings = following()
        settings.followPeople = false
        XCTAssertNotEqual(try scope(settings), original)
    }

    func testClearRemovesPersistentDataAndRejectsLateWritesAndReads() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let cache = IdentityCache(defaults: defaults)
        let context = try cache.begin(scope: scope())
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        try cache.storeCurrentUser(user, organization: "example", context: context)
        cache.clear()
        XCTAssertNil(defaults.object(forKey: IdentityCache.settingsKey))
        XCTAssertThrowsError(try cache.storeCurrentUser(user, organization: "example", context: context)) {
            XCTAssertTrue($0 is CancellationError)
        }
        XCTAssertThrowsError(try cache.currentUser(organization: "example", context: context)) {
            XCTAssertTrue($0 is CancellationError)
        }
        let fresh = try cache.begin(scope: scope())
        XCTAssertNil(try cache.currentUser(organization: "example", context: fresh))
    }

    func testSwitchingContextsRejectsOldWritesAndDoesNotRestoreOldAccountEntries() throws {
        let cache = IdentityCache()
        let context = try cache.begin(scope: scope(header: "Bearer first"))
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        try cache.storeCurrentUser(user, organization: "example", context: context)
        let other = try cache.begin(scope: scope(header: "Bearer other"))
        XCTAssertNil(try cache.currentUser(organization: "example", context: other))
        XCTAssertThrowsError(try cache.storeCurrentUser(user, organization: "example", context: context)) {
            XCTAssertTrue($0 is CancellationError)
        }
        let again = try cache.begin(scope: scope(header: "Bearer first"))
        XCTAssertNil(try cache.currentUser(organization: "example", context: again))
    }

    func testBoundEvictsTheOldestEntry() throws {
        let clock = IdentityTestClock()
        let cache = IdentityCache(clock: { clock.now })
        let context = try cache.begin(scope: scope())
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        for index in 0...IdentityCache.maximumEntries {
            try cache.storeCurrentUser(user, organization: "org-\(index)", context: context)
            clock.advance(1)
        }
        XCTAssertNil(try cache.currentUser(organization: "org-0", context: context))
        XCTAssertEqual(try cache.currentUser(organization: "org-1", context: context), user)
        XCTAssertEqual(
            try cache.currentUser(organization: "org-\(IdentityCache.maximumEntries)", context: context), user
        )
    }

    func testCorruptUnsupportedAndDuplicateStorageAreDiscarded() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let cache = IdentityCache(defaults: defaults)
        let key = try scope()
        let context = try cache.begin(scope: key)
        try cache.storeCurrentUser(XCTUnwrap(UUID(uuidString: reviewerID)), organization: "example", context: context)
        let original = try XCTUnwrap(
            JSONSerialization.jsonObject(with: XCTUnwrap(defaults.data(forKey: IdentityCache.settingsKey)))
                as? [String: Any]
        )
        var unsupported = original
        unsupported["version"] = 99
        var duplicate = original
        let entries = try XCTUnwrap(original["entries"] as? [[String: Any]])
        duplicate["entries"] = entries + entries
        for data in [
            Data("broken PRIVATE_CACHE_SENTINEL".utf8),
            try JSONSerialization.data(withJSONObject: unsupported),
            try JSONSerialization.data(withJSONObject: duplicate)
        ] {
            defaults.set(data, forKey: IdentityCache.settingsKey)
            let restored = IdentityCache(defaults: defaults)
            let fresh = try restored.begin(scope: key)
            XCTAssertNil(try restored.currentUser(organization: "example", context: fresh))
        }
    }

    func testClockRollbackAndExpiryAfterRestartDoNotResurrectEntries() throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let clock = IdentityTestClock()
        let cache = IdentityCache(defaults: defaults, clock: { clock.now })
        let key = try scope()
        let context = try cache.begin(scope: key)
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        try cache.storeCurrentUser(user, organization: "example", context: context)
        clock.advance(-1)
        XCTAssertNil(try cache.currentUser(organization: "example", context: context))
        clock.advance(1)
        try cache.storeCurrentUser(user, organization: "example", context: context)
        clock.advance(7 * 24 * 3600)
        let restored = IdentityCache(defaults: defaults, clock: { clock.now })
        let fresh = try restored.begin(scope: key)
        XCTAssertNil(try restored.currentUser(organization: "example", context: fresh))
    }

    func testWarmLoadAfterRestartSkipsBothIdentitiesButRefreshesListsProjectsAndHistory() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let authorization = StubAuthorization(header: header)
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([pullRequestJSON()]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(number: 2, hasReviewer: false)]),
            pullRequestsResponse([pullRequestJSON()]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(number: 3, hasReviewer: false)])
        ])
        let firstLoader = AzureDevOpsReviewLoader(
            authorization: authorization, transport: transport, identityCache: IdentityCache(defaults: defaults)
        )
        let first = try await firstLoader.load(configuration: following())
        XCTAssertEqual(Set(first.pullRequests.map(\.number)), [1, 2])
        await transport.setHistory(reviewThreadsResponse([reviewCommentThreadJSON()]), for: 1)
        let restoredLoader = AzureDevOpsReviewLoader(
            authorization: authorization, transport: transport, identityCache: IdentityCache(defaults: defaults)
        )
        let next = try await restoredLoader.load(configuration: following())
        XCTAssertEqual(Set(next.pullRequests.map(\.number)), [1, 3])
        XCTAssertEqual(next.pullRequests.first { $0.number == 1 }?.reviewStage, .commented)
        let requests = await transport.requests
        XCTAssertEqual(requests.count, 12)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/projects") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/threads") == true }.count, 4)
    }

    func testSevenDayExpiryResolvesBothIdentitiesAndUsesTheNewAuthorMapping() async throws {
        let clock = IdentityTestClock()
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), identitiesResponse([followedIdentityJSON()]),
            projectsResponse(["Project"]), pullRequestsResponse([]),
            connectionResponse(differentID), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON(id: differentID)]),
            projectsResponse(["Project"]), pullRequestsResponse([])
        ])
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(header: header), transport: transport,
            identityCache: IdentityCache(clock: { clock.now })
        )
        _ = try await loader.load(configuration: following())
        clock.advance(7 * 24 * 3600)
        _ = try await loader.load(configuration: following())
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 2)
        let creatorIDs = requests.compactMap(\.url).compactMap {
            URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems?
                .first { $0.name == "searchCriteria.creatorId" }?.value?.lowercased()
        }
        XCTAssertEqual(creatorIDs, [authorID, differentID])
    }

    func testCachedNoMatchStillFailsGloballyAndRetriesAtFifteenMinutes() async throws {
        let clock = IdentityTestClock()
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), identitiesResponse([]),
            pullRequestsResponse([]),
            pullRequestsResponse([]), identitiesResponse([followedIdentityJSON()]),
            projectsResponse(["Project"]), pullRequestsResponse([])
        ])
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(header: header), transport: transport,
            identityCache: IdentityCache(clock: { clock.now })
        )
        for advance in [0.0, 899.0] {
            clock.advance(advance)
            do {
                _ = try await loader.load(configuration: following())
                XCTFail("Unresolved authors must not become an empty successful queue")
            } catch { XCTAssertTrue(error is ReviewQError) }
        }
        clock.advance(1)
        _ = try await loader.load(configuration: following())
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
    }

    func testAuthorErrorsAreNeverCachedAsNoMatch() async throws {
        let badResponses = [
            HTTPResult(data: Data(), status: 401), HTTPResult(data: Data(), status: 403),
            HTTPResult(data: Data(), status: 429), HTTPResult(data: Data("broken".utf8), status: 200),
            identitiesResponse([followedIdentityJSON(), followedIdentityJSON(id: differentID)]),
            identitiesResponse([], continuation: "incomplete"),
            identitiesResponse([followedIdentityJSON(id: "00000000-0000-0000-0000-000000000000")])
        ]
        for response in badResponses {
            var settings = following()
            settings.authentication = .personalAccessToken
            let transport = StubTransport([
                connectionResponse(), pullRequestsResponse([]), response,
                pullRequestsResponse([]), identitiesResponse([followedIdentityJSON()]),
                projectsResponse(["Project"]), pullRequestsResponse([])
            ])
            let loader = AzureDevOpsReviewLoader(
                authorization: StubAuthorization(header: "Basic synthetic-cache-credential"), transport: transport
            )
            do {
                _ = try await loader.load(configuration: settings)
                XCTFail("Invalid author lookup must fail")
            } catch { XCTAssertTrue(error is ReviewQError) }
            _ = try await loader.load(configuration: settings)
            let requests = await transport.requests
            XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 2)
            XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
        }
    }

    func testNetworkFailureIsRetriedRatherThanNegativeCached() async throws {
        let base = StubTransport([
            connectionResponse(), pullRequestsResponse([]), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON()]), projectsResponse(["Project"]), pullRequestsResponse([])
        ])
        let transport = IdentityFailureTransport(base: base)
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(header: header), transport: transport)
        do {
            _ = try await loader.load(configuration: following())
            XCTFail("Network error must fail")
        } catch { XCTAssertTrue(error is ReviewQError) }
        _ = try await loader.load(configuration: following())
        let attempts = await transport.identityAttempts
        XCTAssertEqual(attempts, 2)
    }

    func testTokenRenewalUsesTheCachedUserButAnAccountSwitchResolvesAgain() async throws {
        let authorization = StubAuthorization(header: try bearer(nonce: "first"))
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), pullRequestsResponse([]),
            connectionResponse(differentID), pullRequestsResponse([])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: authorization, transport: transport)
        _ = try await loader.load(configuration: configuration())
        await authorization.setHeader(try bearer(nonce: "renewed"))
        _ = try await loader.load(configuration: configuration())
        await authorization.setHeader(try bearer(oid: differentID, nonce: "account"))
        _ = try await loader.load(configuration: configuration())
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 2)
        XCTAssertEqual(requests.count, 5)
    }

    func testUnauthorizedRetryForTheSamePrincipalDoesNotInvalidateTheUserIdentity() async throws {
        let authorization = StubAuthorization(header: try bearer(nonce: "same-account"))
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]),
            HTTPResult(data: Data(), status: 401), pullRequestsResponse([])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: authorization, transport: transport)
        _ = try await loader.load(configuration: configuration())
        _ = try await loader.load(configuration: configuration())
        let requests = await transport.requests
        let invalidations = await authorization.invalidations
        XCTAssertEqual(invalidations, 1)
        XCTAssertEqual(requests.count, 4)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 1)
    }

    func testExplicitInvalidationDispatchesThroughTheProtocolAndRechecksBothIdentities() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let authorization = StubAuthorization(header: header)
        let cycle = [
            connectionResponse(), pullRequestsResponse([]), identitiesResponse([followedIdentityJSON()]),
            projectsResponse(["Project"]), pullRequestsResponse([])
        ]
        let transport = StubTransport(cycle + cycle)
        let loader: any ReviewLoading = AzureDevOpsReviewLoader(
            authorization: authorization, transport: transport, identityCache: IdentityCache(defaults: defaults)
        )
        _ = try await loader.load(configuration: following())
        await loader.invalidateIdentityCache()
        XCTAssertNil(defaults.object(forKey: IdentityCache.settingsKey))
        _ = try await loader.load(configuration: following())
        let invalidations = await authorization.invalidations
        let requests = await transport.requests
        XCTAssertEqual(invalidations, 1)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 2)
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/identities") == true }.count, 2)
    }

    func testCurrentUserErrorsAreNeverCached() async throws {
        var settings = configuration()
        settings.authentication = .personalAccessToken
        let transport = StubTransport([
            HTTPResult(data: Data(), status: 403), connectionResponse(), pullRequestsResponse([])
        ])
        let loader = AzureDevOpsReviewLoader(authorization: StubAuthorization(header: header), transport: transport)
        do {
            _ = try await loader.load(configuration: settings)
            XCTFail("Current-user error must fail")
        } catch { XCTAssertTrue(error is ReviewQError) }
        _ = try await loader.load(configuration: settings)
        let requests = await transport.requests
        XCTAssertEqual(requests.filter { $0.url?.path.hasSuffix("/connectionData") == true }.count, 2)
    }

    func testInvalidatingDuringAuthenticationRejectsTheOldLoadBeforeItCanRepopulateTheCache() async throws {
        let (defaults, name) = try preferences()
        defer { defaults.removePersistentDomain(forName: name) }
        let authorization = DeferredIdentityAuthorization(header: header)
        let transport = StubTransport([])
        let loader = AzureDevOpsReviewLoader(
            authorization: authorization, transport: transport, identityCache: IdentityCache(defaults: defaults)
        )
        let pending = Task { try await loader.load(configuration: configuration()) }
        await authorization.waitUntilStarted()
        await loader.invalidateIdentityCache()
        await authorization.finish()
        do {
            _ = try await pending.value
            XCTFail("An invalidated lookup must not write new cache entries")
        } catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertNil(defaults.object(forKey: IdentityCache.settingsKey))
        let requests = await transport.requests
        XCTAssertTrue(requests.isEmpty)
    }
}

private final class IdentityTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Date(timeIntervalSince1970: 1_800_000_000)
    var now: Date { lock.withLock { value } }
    func advance(_ seconds: TimeInterval) { lock.withLock { value.addTimeInterval(seconds) } }
}

private actor IdentityFailureTransport: HTTPTransport {
    let base: StubTransport
    private(set) var identityAttempts = 0

    init(base: StubTransport) { self.base = base }

    func send(_ request: URLRequest) async throws -> HTTPResult {
        if request.url?.path.hasSuffix("/identities") == true {
            identityAttempts += 1
            if identityAttempts == 1 { throw URLError(.timedOut) }
        }
        return try await base.send(request)
    }
}

private actor DeferredIdentityAuthorization: AuthorizationProviding {
    let header: String
    private var pending: CheckedContinuation<String, Never>?
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(header: String) { self.header = header }

    func authorization(for configuration: ReviewConfiguration) async -> String {
        await withCheckedContinuation {
            pending = $0
            for waiter in waiters { waiter.resume() }
            waiters.removeAll()
        }
    }

    func invalidate() {}

    func waitUntilStarted() async {
        if pending != nil { return }
        await withCheckedContinuation { waiters.append($0) }
    }

    func finish() {
        precondition(pending != nil)
        pending?.resume(returning: header)
        pending = nil
    }
}
