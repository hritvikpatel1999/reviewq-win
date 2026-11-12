import Foundation
@testable import ReviewQCore
import XCTest

final class DiagnosticsTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("ReviewQ-diagnostics-tests-\(UUID())")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
    }

    override func tearDownWithError() throws {
        try FileManager.default.removeItem(at: directory)
    }

    func testIdentityCacheEventsAndCorruptionNeverLogPrivateKeysOrPayloads() throws {
        let name = "ReviewQ-identity-diagnostics-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set(Data("PRIVATE_CACHE_PAYLOAD".utf8), forKey: IdentityCache.settingsKey)
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        let cache = IdentityCache(defaults: defaults, diagnostics: logger)
        let settings = configuration("https://dev.azure.com/private-cache-org/Project")
        let scope = try IdentityCache.scope(
            configuration: settings, header: "Bearer PRIVATE_CACHE_CREDENTIAL",
            sources: settings.validatedSources(), followedEmails: []
        )
        let context = try cache.begin(scope: scope)
        let user = try XCTUnwrap(UUID(uuidString: reviewerID))
        XCTAssertNil(try cache.currentUser(organization: "private-cache-org", context: context))
        try cache.storeCurrentUser(user, organization: "private-cache-org", context: context)
        _ = try cache.currentUser(organization: "private-cache-org", context: context)
        try cache.storeFollowedAuthor(
            nil, organization: "private-cache-org", email: "private-cache@example.com", context: context
        )
        _ = try cache.followedAuthor(
            organization: "private-cache-org", email: "private-cache@example.com", context: context
        )
        cache.clear()
        logger.flush()
        XCTAssertTrue(try records().contains { $0.event == .identityCacheInvalidated && $0.reason == .decoding })
        let events = try records("verbose.log").map(\.event)
        XCTAssertTrue(events.contains(.identityCacheHit))
        XCTAssertTrue(events.contains(.identityCacheMiss))
        XCTAssertTrue(events.contains(.identityCacheCleared))
        let text = try logText()
        for sentinel in [
            "PRIVATE_CACHE_PAYLOAD", "PRIVATE_CACHE_CREDENTIAL", "private-cache-org",
            "private-cache@example.com", reviewerID, scope
        ] {
            XCTAssertFalse(text.contains(sentinel))
        }
    }

    func testDefaultCollectionIsProblemsAndStartupOnly() throws {
        let logger = DiagnosticLogger(directory: directory)
        logger.start()
        logger.log(.debug, .requestStarted)
        logger.log(.info, .refreshFinished)
        logger.log(.warning, .refreshFailed, reason: .network)
        logger.log(.error, .settingsFailed, reason: .corruptSettings)
        logger.flush()
        XCTAssertEqual(try records().map(\.event), [.startup, .refreshFailed, .settingsFailed])
        XCTAssertEqual(try records("verbose.log").map(\.event), [.startup])
        XCTAssertNotNil(try records().first?.metadata)
        XCTAssertNil(logger.error)
    }

    func testVerboseOptOutStopsFutureOptionalCollectionButRetainsExistingEvents() throws {
        let logger = DiagnosticLogger(directory: directory)
        logger.start()
        logger.setVerboseEnabled(true)
        logger.log(.debug, .requestStarted)
        logger.log(.warning, .requestFailed)
        logger.setVerboseEnabled(false)
        logger.log(.info, .requestFinished)
        logger.log(.warning, .settingsFailed)
        logger.flush()
        let verbose = try records("verbose.log").map(\.event)
        XCTAssertTrue(verbose.contains(.requestStarted))
        XCTAssertTrue(verbose.contains(.requestFailed))
        XCTAssertTrue(verbose.contains(.verboseDisabled))
        XCTAssertFalse(verbose.contains(.requestFinished))
        XCTAssertFalse(verbose.contains(.settingsFailed))
        XCTAssertTrue(try records().contains { $0.event == .settingsFailed })
    }

    func testConcurrentWritesRemainCompleteAndOwnerOnly() throws {
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        DispatchQueue.concurrentPerform(iterations: 100) { index in
            logger.log(.warning, .requestFailed, count: index)
        }
        logger.flush()
        for name in DiagnosticLogger.filenames {
            let entries = try records(name)
            XCTAssertEqual(entries.count, 100)
            XCTAssertEqual(Set(entries.compactMap(\.count)), Set(0..<100))
            XCTAssertEqual(try permissions(directory.appendingPathComponent(name)), 0o600)
        }
        XCTAssertEqual(try permissions(directory), 0o700)
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted(),
            DiagnosticLogger.filenames.sorted()
        )
    }

    func testRotationAndOversizedExistingFilesStayInsideTheExactBudget() throws {
        let first = DiagnosticLogger(directory: directory, verboseEnabled: true)
        for index in 0..<100 { first.log(.warning, .requestFailed, count: index) }
        first.flush()
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true, maxFileBytes: 2048)
        logger.start()
        for index in 0..<80 { logger.log(.warning, .requestFailed, count: index) }
        logger.flush()
        for name in DiagnosticLogger.filenames {
            XCTAssertLessThanOrEqual(try Data(contentsOf: directory.appendingPathComponent(name)).count, 2048)
            XCTAssertEqual(try records(name).last?.count, 79)
            XCTAssertTrue(try records(name).contains { $0.event == .logsEvicted && $0.reason == .sizeLimit })
        }
        XCTAssertLessThanOrEqual(logger.storedBytes, 4096)
        XCTAssertNil(logger.error)
    }

    func testOversizedEntryFailsExplicitlyRatherThanExceedingTheBudget() throws {
        let logger = DiagnosticLogger(directory: directory, maxFileBytes: 2048)
        logger.start(metadata: DiagnosticMetadata(
            version: String(repeating: "9", count: 3000), build: "1", macOS: "14.0", architecture: "arm64"
        ))
        logger.flush()
        XCTAssertEqual(logger.error, .entryTooLarge)
        XCTAssertLessThanOrEqual(logger.storedBytes, 4096)
    }

    func testRetentionAndMalformedInputArePrunedWithAFakeClock() throws {
        let clock = DiagnosticTestClock(Date(timeIntervalSince1970: 100_000))
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true, clock: { clock.now })
        logger.start()
        logger.log(.warning, .refreshFailed, reason: .network)
        logger.flush()
        let url = directory.appendingPathComponent("warnings-errors.log")
        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data("malformed-private-sentinel\n".utf8))
        try handle.close()
        clock.advance(8 * 24 * 3600)
        logger.log(.warning, .settingsFailed, reason: .corruptSettings)
        logger.flush()
        XCTAssertFalse(try records().contains { $0.event == .refreshFailed })
        XCTAssertTrue(try records().contains { $0.reason == .retention })
        XCTAssertTrue(try records().contains { $0.reason == .malformedRecord })
        XCTAssertFalse(try logText().contains("malformed-private-sentinel"))
        XCTAssertTrue(try records().allSatisfy { $0.timestamp >= clock.now.addingTimeInterval(-7 * 24 * 3600) })
    }

    func testClearRetainsOnlyStartupWhenVerboseIsOff() throws {
        let logger = DiagnosticLogger(directory: directory)
        logger.start()
        logger.log(.warning, .refreshFailed)
        logger.flush()
        try logger.clear()
        XCTAssertEqual(try records().map(\.event), [.startup])
        XCTAssertEqual(try records("verbose.log").map(\.event), [.startup])
        XCTAssertNil(logger.error)
    }

    func testUnsafeFilesAndAncestorLinksDoNotModifyTheTarget() throws {
        let target = directory.appendingPathComponent("outside.txt")
        let sentinel = Data("untouched".utf8)
        try sentinel.write(to: target)
        let logs = directory.appendingPathComponent("Logs")
        try FileManager.default.createDirectory(at: logs, withIntermediateDirectories: false)
        try FileManager.default.createSymbolicLink(
            at: logs.appendingPathComponent("warnings-errors.log"), withDestinationURL: target
        )
        let logger = DiagnosticLogger(directory: logs)
        logger.start()
        logger.flush()
        XCTAssertEqual(logger.error, .unsafePath)
        XCTAssertThrowsError(try logger.clear()) { XCTAssertEqual($0 as? DiagnosticStorageError, .unsafePath) }
        XCTAssertEqual(try Data(contentsOf: target), sentinel)
        let alias = directory.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: logs)
        let nested = DiagnosticLogger(directory: alias.appendingPathComponent("Nested"))
        nested.start()
        nested.flush()
        XCTAssertEqual(nested.error, .unsafePath)
        XCTAssertFalse(FileManager.default.fileExists(atPath: logs.appendingPathComponent("Nested").path))
    }

    func testExportIsAllowlistedPrivateAndSanitizesPreexistingUnknownFields() throws {
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        logger.start()
        logger.logFailure(.refreshFailed, error: ReviewQError.network("private-source", "SECRET_SENTINEL"))
        logger.flush()
        let url = directory.appendingPathComponent("warnings-errors.log")
        var objects = try Data(contentsOf: url).split(separator: 0x0a).map {
            try XCTUnwrap(JSONSerialization.jsonObject(with: Data($0)) as? [String: Any])
        }
        objects[0]["responseBody"] = "SECRET_SENTINEL"
        var modified = Data()
        for object in objects {
            modified.append(try JSONSerialization.data(withJSONObject: object))
            modified.append(0x0a)
        }
        try modified.write(to: url)
        try Data("SECRET_SENTINEL".utf8).write(to: directory.appendingPathComponent("unrelated.log"))
        let archive = directory.appendingPathComponent("export.zip")
        try logger.export(to: archive)
        XCTAssertEqual(try permissions(archive), 0o600)
        let extracted = directory.appendingPathComponent("extracted")
        try extract(archive, to: extracted)
        let contents = extracted.appendingPathComponent("ReviewQ-Logs")
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(atPath: contents.path).sorted(),
            (DiagnosticLogger.filenames + ["diagnostic-summary.json"]).sorted()
        )
        for name in DiagnosticLogger.filenames + ["diagnostic-summary.json"] {
            let file = contents.appendingPathComponent(name)
            XCTAssertFalse(try String(contentsOf: file, encoding: .utf8).contains("SECRET_SENTINEL"))
            XCTAssertEqual(try permissions(file), 0o600)
        }
        XCTAssertFalse(try logText().contains("SECRET_SENTINEL"))
    }

    func testExportCleansTemporaryFilesOnFailureAndDoesNotFollowDestinationLinks() throws {
        let logger = DiagnosticLogger(directory: directory)
        logger.start()
        logger.flush()
        let before = try exportTemporaryNames()
        XCTAssertThrowsError(try logger.export(to: directory.appendingPathComponent("missing/export.zip")))
        XCTAssertEqual(try exportTemporaryNames(), before)
        let target = directory.appendingPathComponent("target.zip")
        let sentinel = Data("unchanged".utf8)
        try sentinel.write(to: target)
        let link = directory.appendingPathComponent("link.zip")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)
        XCTAssertThrowsError(try logger.export(to: link)) {
            XCTAssertEqual($0 as? DiagnosticStorageError, .unsafePath)
        }
        XCTAssertEqual(try Data(contentsOf: target), sentinel)
        XCTAssertEqual(try exportTemporaryNames(), before)
    }

    @MainActor
    func testRefreshAndEveryHTTPOutcomeShareCorrelationWithoutPrivateData() async throws {
        let sentinel = "PRIVATE_CONTENT_SENTINEL"
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        logger.start()
        let transport = StubTransport([
            connectionResponse(),
            pullRequestsResponse([pullRequestJSON().replacingOccurrences(of: "Fixture author", with: sentinel)])
        ])
        let loader = AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: transport, diagnostics: logger
        )
        let suite = "ReviewQ-diagnostics-store-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ReviewStore(defaults: defaults, loader: loader, diagnostics: logger)
        try store.save(configuration("https://dev.azure.com/private-content-sentinel/Project"))
        await store.refresh()
        logger.flush()
        XCTAssertEqual(store.snapshot?.pullRequests.count, 1)
        let events = try records("verbose.log")
        let cycle = try XCTUnwrap(events.first { $0.event == .refreshStarted }?.cycleID)
        let starts = events.filter { $0.event == .requestStarted }
        XCTAssertEqual(starts.count, 3)
        XCTAssertEqual(Set(starts.compactMap(\.operation)), [.identity, .pullRequests, .reviewHistory])
        for start in starts {
            let outcomes = events.filter {
                $0.requestID == start.requestID && [.requestFinished, .requestFailed, .requestCancelled].contains($0.event)
            }
            XCTAssertEqual(outcomes.count, 1)
            XCTAssertEqual(start.cycleID, cycle)
            XCTAssertEqual(outcomes.first?.cycleID, cycle)
            XCTAssertEqual(outcomes.first?.status, 200)
            XCTAssertGreaterThanOrEqual(try XCTUnwrap(outcomes.first?.durationMilliseconds), 0)
            XCTAssertGreaterThan(try XCTUnwrap(outcomes.first?.responseBytes), 0)
        }
        XCTAssertEqual(events.first { $0.event == .refreshFinished }?.count, 1)
        let text = try logText()
        for value in [sentinel, "private-content-sentinel", reviewerID, authorID, "dev.azure.com", "Fixture author"] {
            XCTAssertFalse(text.contains(value))
        }
    }

    @MainActor
    func testFollowedIdentityRequestsNeverLogEmailQueriesOrIdentityResponses() async throws {
        let email = "private-follow-sentinel@example.com"
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]),
            identitiesResponse([followedIdentityJSON(email: email)]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(hasReviewer: false)])
        ])
        var settings = configuration()
        settings.followPeople = true
        settings.followedAuthorEmails = email
        let result = try await AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: transport, diagnostics: logger
        ).load(configuration: settings)
        XCTAssertEqual(result.pullRequests.count, 1)
        logger.flush()
        XCTAssertTrue(try records("verbose.log").contains { $0.operation == .followedIdentity })
        let text = try logText()
        for value in [email, "filterValue", "vssps.dev.azure.com", authorID, "Fixture author"] {
            XCTAssertFalse(text.contains(value), value)
        }
        logger.logFailure(.refreshFailed, error: ReviewQError.unresolvedFollowedAuthors([email]))
        logger.flush()
        XCTAssertFalse(try logText().contains(email))
        XCTAssertEqual(try records().last?.reason, .invalidConfiguration)
    }

    @MainActor
    func testHTTPFailureAndDecodeCategoriesDoNotIncludeResponseContent() async throws {
        for (status, reason) in [(403, DiagnosticReason.forbidden), (503, .serviceUnavailable), (200, .decoding)] {
            let logs = directory.appendingPathComponent("http-\(status)")
            let logger = DiagnosticLogger(directory: logs, verboseEnabled: true)
            let loader = AzureDevOpsReviewLoader(
                authorization: StubAuthorization(),
                transport: StubTransport([HTTPResult(data: Data("PRIVATE_RESPONSE".utf8), status: status)]),
                diagnostics: logger
            )
            do {
                _ = try await loader.load(configuration: configuration())
                XCTFail("The failing response must remain a failure")
            } catch { XCTAssertTrue(error is ReviewQError) }
            logger.flush()
            let events = try decode(logs.appendingPathComponent("verbose.log"))
            XCTAssertEqual(events.filter { $0.event == .requestStarted }.count, 1)
            let failure = try XCTUnwrap(events.first { $0.event == .requestFailed })
            XCTAssertEqual(failure.reason, reason)
            XCTAssertEqual(failure.status, status)
            XCTAssertFalse(try String(contentsOf: logs.appendingPathComponent("verbose.log"), encoding: .utf8)
                .contains("PRIVATE_RESPONSE"))
        }
    }

    @MainActor
    func testAuthoredReviewerPoliciesLogOnlySafeCategoriesAndNeverArtifactOrPolicyPayloads() async throws {
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        let transport = StubTransport([
            connectionResponse(), pullRequestsResponse([]), projectsResponse(["Project"]),
            pullRequestsResponse([pullRequestJSON(author: reviewerID, hasReviewer: false)])
        ], policies: [1: [policyEvaluationsResponse([
            policyEvaluationJSON(status: "approved")
                .replacingOccurrences(
                    of: "\"status\":\"approved\"",
                    with: "\"status\":\"approved\",\"privatePayload\":\"PRIVATE_APPROVAL_SENTINEL\""
                )
        ])]])
        var settings = configuration()
        settings.showAuthoredPullRequests = true
        let result = try await AzureDevOpsReviewLoader(
            authorization: StubAuthorization(), transport: transport, diagnostics: logger
        ).load(configuration: settings)
        XCTAssertTrue(try XCTUnwrap(result.pullRequests.first).isMergeReady)
        logger.flush()
        XCTAssertTrue(try records("verbose.log").contains { $0.operation == .reviewerPolicies && $0.event == .requestFinished })
        let text = try logText()
        for sentinel in ["PRIVATE_APPROVAL_SENTINEL", "artifactId", "CodeReviewId", "project-id", reviewerID, "Fixture author"] {
            XCTAssertFalse(text.contains(sentinel), sentinel)
        }
    }

    @MainActor
    func testTransportCodesAndCancellationSurviveNormalization() async throws {
        for (code, reason) in [
            (URLError.Code.timedOut, DiagnosticReason.timeout), (.notConnectedToInternet, .offline), (.cancelled, .cancelled)
        ] {
            let logs = directory.appendingPathComponent("transport-\(code.rawValue)")
            let logger = DiagnosticLogger(directory: logs, verboseEnabled: true)
            let loader = AzureDevOpsReviewLoader(
                authorization: StubAuthorization(),
                transport: DiagnosticFailingTransport(code: code), diagnostics: logger
            )
            do {
                _ = try await loader.load(configuration: configuration())
                XCTFail("A transport failure must not publish a successful queue")
            } catch {
                XCTAssertEqual(DiagnosticFailure(error).reason, reason)
                if code == .cancelled { XCTAssertTrue(error is CancellationError) }
            }
            logger.flush()
            let events = try decode(logs.appendingPathComponent("verbose.log"))
            let terminal = events.filter { $0.event == .requestFailed || $0.event == .requestCancelled }
            XCTAssertEqual(terminal.count, 1)
            XCTAssertEqual(terminal.first?.reason, reason)
            XCTAssertEqual(terminal.first?.systemCode, code.rawValue)
            if code == .cancelled {
                XCTAssertEqual(terminal.first?.event, .requestCancelled)
                XCTAssertTrue(try decode(logs.appendingPathComponent("warnings-errors.log")).isEmpty)
            }
            XCTAssertFalse(try String(contentsOf: logs.appendingPathComponent("verbose.log"), encoding: .utf8)
                .contains("PRIVATE_TRANSPORT_DESCRIPTION"))
        }
    }

    @MainActor
    func testAuthenticationDoesNotRecordPATOrCLISecrets() async throws {
        let sentinel = "SYNTHETIC_CREDENTIAL_SENTINEL"
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        let cli = AzureDevOpsCredentials(
            runner: StubCommandRunner(Data("""
            {"accessToken":"\(sentinel)","expires_on":\(Int(Date().addingTimeInterval(3600).timeIntervalSince1970))}
            """.utf8)), executable: URL(fileURLWithPath: "/synthetic/az"), diagnostics: logger
        )
        _ = try await cli.authorization(for: configuration())
        let pat = AzureDevOpsCredentials(keychain: StubPATStore(token: sentinel), diagnostics: logger)
        var settings = configuration()
        settings.authentication = .personalAccessToken
        _ = try await pat.authorization(for: settings)
        logger.flush()
        XCTAssertEqual(try records("verbose.log").filter { $0.event == .authenticationFinished }.count, 2)
        let text = try logText()
        XCTAssertFalse(text.contains(sentinel))
        XCTAssertFalse(text.contains(Data(":\(sentinel)".utf8).base64EncodedString()))
    }

    private func records(_ name: String = "warnings-errors.log") throws -> [DiagnosticRecord] {
        try decode(directory.appendingPathComponent(name))
    }

    private func decode(_ url: URL) throws -> [DiagnosticRecord] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try Data(contentsOf: url).split(separator: 0x0a).map {
            try decoder.decode(DiagnosticRecord.self, from: Data($0))
        }
    }

    private func logText() throws -> String {
        try DiagnosticLogger.filenames.map {
            try String(contentsOf: directory.appendingPathComponent($0), encoding: .utf8)
        }.joined()
    }

    private func permissions(_ url: URL) throws -> Int {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        return try XCTUnwrap(attributes[.posixPermissions] as? NSNumber).intValue & 0o777
    }

    private func exportTemporaryNames() throws -> Set<String> {
        Set(try FileManager.default.contentsOfDirectory(
            atPath: FileManager.default.temporaryDirectory.resolvingSymlinksInPath().path
        ).filter { $0.hasPrefix("ReviewQ-diagnostics-") && !$0.hasPrefix("ReviewQ-diagnostics-tests-") })
    }

    private func extract(_ archive: URL, to destination: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        process.arguments = ["-x", "-k", archive.path, destination.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
    }
}

private struct DiagnosticFailingTransport: HTTPTransport {
    let code: URLError.Code
    func send(_ request: URLRequest) throws -> HTTPResult {
        throw URLError(code, userInfo: [NSLocalizedDescriptionKey: "PRIVATE_TRANSPORT_DESCRIPTION"])
    }
}

private final class DiagnosticTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Date
    init(_ value: Date) { self.value = value }
    var now: Date { lock.withLock { value } }
    func advance(_ interval: TimeInterval) { lock.withLock { value.addTimeInterval(interval) } }
}
