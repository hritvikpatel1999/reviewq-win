import Foundation
@testable import ReviewQCore
import XCTest

@MainActor
final class AuthenticationTests: XCTestCase {
    func testCLITokensAcceptNumericAndStringEpochExpiry() throws {
        let future = Int64(Date().addingTimeInterval(3600).timeIntervalSince1970)
        for expiry in ["\(future)", "\"\(future)\""] {
            let token = try CLIToken.decode(Data("""
            {"accessToken":"synthetic-token","expires_on":\(expiry)}
            """.utf8))
            XCTAssertEqual(token.value, "synthetic-token")
            XCTAssertGreaterThan(token.expiresAt, Date())
        }
    }

    func testExpiredMalformedAndMissingExpiryTokensAreRejected() {
        for text in [
            #"{"accessToken":"synthetic-token","expires_on":1}"#,
            #"{"accessToken":"synthetic-token"}"#,
            #"{"accessToken":"","expires_on":9999999999}"#,
            #"{"accessToken":"two tokens","expires_on":9999999999}"#,
            #"{"accessToken":"synthetic-token","expires_on":"not-a-number"}"#,
            "<html>Sign in</html>"
        ] {
            XCTAssertThrowsError(try CLIToken.decode(Data(text.utf8)))
        }
    }

    func testLegacyLocalTimeExpiryIsSupported() throws {
        let future = Date().addingTimeInterval(3600)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSSSSS"
        let token = try CLIToken.decode(Data("""
        {"accessToken":"synthetic-token","expiresOn":"\(formatter.string(from: future))"}
        """.utf8))
        XCTAssertEqual(token.expiresAt.timeIntervalSince1970, future.timeIntervalSince1970, accuracy: 1)
    }

    func testAzureCLIUsesCorrectResourceCachesAndInvalidates() async throws {
        let future = Int64(Date().addingTimeInterval(3600).timeIntervalSince1970)
        let runner = StubCommandRunner(Data("""
        {"accessToken":"synthetic-token","expires_on":\(future)}
        """.utf8))
        let provider = AzureDevOpsCredentials(
            runner: runner, executable: URL(fileURLWithPath: "/synthetic/az")
        )
        let first = try await provider.authorization(for: configuration())
        let second = try await provider.authorization(for: configuration())
        XCTAssertEqual(first, "Bearer synthetic-token")
        XCTAssertEqual(second, first)
        var calls = await runner.calls
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0], [
            "account", "get-access-token", "--resource",
            "499b84ac-1321-427f-aa17-267ca6975798", "--output", "json"
        ])
        await provider.invalidate()
        _ = try await provider.authorization(for: configuration())
        calls = await runner.calls
        XCTAssertEqual(calls.count, 2)
    }

    func testTenantSwitchDoesNotReuseWrongTenantToken() async throws {
        let runner = StubCommandRunner(Data("""
        {"accessToken":"synthetic-token","expires_on":9999999999}
        """.utf8))
        let provider = AzureDevOpsCredentials(
            runner: runner, executable: URL(fileURLWithPath: "/synthetic/az")
        )
        var settings = configuration()
        _ = try await provider.authorization(for: settings)
        settings.tenantID = reviewerID
        _ = try await provider.authorization(for: settings)
        let calls = await runner.calls
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(Array(calls[1].suffix(2)), ["--tenant", reviewerID])
    }

    func testPATAuthorizationAndMissingPAT() async throws {
        var settings = configuration()
        settings.authentication = .personalAccessToken
        let provider = AzureDevOpsCredentials(keychain: StubPATStore())
        let header = try await provider.authorization(for: settings)
        XCTAssertEqual(header, "Basic " + Data(":synthetic-token".utf8).base64EncodedString())
        let missing = AzureDevOpsCredentials(keychain: StubPATStore(token: nil))
        do {
            _ = try await missing.authorization(for: settings)
            XCTFail("Missing PAT must fail")
        } catch let error as ReviewQError {
            guard case .missingPAT = error else { return XCTFail("Wrong error: \(error)") }
        }
    }

    func testSwitchingBackFromPATRechecksTheAzureCLIAccount() async throws {
        let runner = StubCommandRunner(Data("""
        {"accessToken":"synthetic-token","expires_on":9999999999}
        """.utf8))
        let provider = AzureDevOpsCredentials(
            keychain: StubPATStore(), runner: runner, executable: URL(fileURLWithPath: "/synthetic/az")
        )
        var settings = configuration()
        _ = try await provider.authorization(for: settings)
        settings.authentication = .personalAccessToken
        _ = try await provider.authorization(for: settings)
        settings.authentication = .azureCLI
        _ = try await provider.authorization(for: settings)
        let calls = await runner.calls
        XCTAssertEqual(calls.count, 2)
    }

    func testCommandRunnerCapturesOutputAndReportsFailure() async throws {
        let runner = AzureCLICommandRunner()
        let output = try await runner.run(
            executable: URL(fileURLWithPath: "/usr/bin/printf"), arguments: ["fixture"]
        )
        XCTAssertEqual(String(data: output, encoding: .utf8), "fixture")
        do {
            _ = try await runner.run(executable: URL(fileURLWithPath: "/usr/bin/false"), arguments: [])
            XCTFail("Nonzero exit must fail")
        } catch let error as ReviewQError {
            guard case .cliFailure = error else { return XCTFail("Wrong error: \(error)") }
        }
    }

    func testCommandRunnerTimeoutIsBounded() async throws {
        let runner = AzureCLICommandRunner(timeout: 0.1)
        let start = Date()
        do {
            _ = try await runner.run(
                executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["5"]
            )
            XCTFail("Timed-out process must fail")
        } catch let error as ReviewQError {
            guard case .cliTimeout = error else { return XCTFail("Wrong error: \(error)") }
        }
        XCTAssertLessThan(Date().timeIntervalSince(start), 2)
    }

    func testCommandRunnerCancellationTerminatesItsProcessPromptly() async throws {
        let start = Date()
        let task = Task {
            try await AzureCLICommandRunner().run(
                executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["5"]
            )
        }
        try await Task.sleep(for: .milliseconds(30))
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancelled process must not succeed")
        } catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertLessThan(Date().timeIntervalSince(start), 2)
    }
}
