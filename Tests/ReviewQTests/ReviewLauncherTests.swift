import AppKit
@testable import ReviewQ
@testable import ReviewQCore
import XCTest

@MainActor
final class ReviewLauncherTests: XCTestCase {
    func testPromptPreservesTheExactEncodedLink() throws {
        let link = "https://dev.azure.com/example/Project%20Name/_git/repo/pullrequest/42?_a=files&path=%2Fsrc%2F"
        XCTAssertEqual(try ReviewLauncher.prompt(for: XCTUnwrap(URL(string: link))), "Review PR: \(link)")
    }

    func testInstructionsFollowTheFixedLinkAndWhitespaceOnlyKeepsTheDefaultPrompt() throws {
        let url = try link()
        let base = "Review PR: \(url.absoluteString)"
        for suffix in ["", " \n\r\n "] {
            XCTAssertEqual(try ReviewLauncher.prompt(for: url, additionalInstructions: suffix), base)
        }
        XCTAssertEqual(
            try ReviewLauncher.prompt(
                for: url, additionalInstructions: " \nUse this skill.\r\nFocus on correctness.\u{2028}Keep it concise. \n"
            ),
            "\(base)\n\nUse this skill.\nFocus on correctness.\nKeep it concise."
        )
        XCTAssertEqual(
            try ReviewLauncher.prompt(for: url, additionalInstructions: "Review PR: another link"),
            "\(base)\n\nReview PR: another link"
        )
    }

    func testInstructionLimitsAreEnforcedBeforeAnyAppIsResolvedOrOpened() async throws {
        let launcher = ReviewLauncher(
            applicationURL: { _ in XCTFail("Invalid instructions must not open an app."); return nil },
            scoutLaunch: { _, _ in XCTFail("Invalid instructions must not type.") }
        )
        for assistant in ReviewAssistant.allCases {
            for invalid in [String(repeating: "x", count: 1_001), "unsafe\u{0}input"] {
                do {
                    try await launcher.launch(assistant, for: link(), additionalInstructions: invalid)
                    XCTFail("Expected invalid instructions.")
                } catch {
                    XCTAssertEqual(error as? ReviewLaunchError, .invalidInstructions)
                }
            }
        }
        let maximum = String(repeating: "x", count: 1_000)
        XCTAssertEqual(
            try ReviewLauncher.prompt(for: link(), additionalInstructions: maximum),
            "Review PR: \(try link().absoluteString)\n\n\(maximum)"
        )
    }

    func testScoutInputReconstructsMultilineUnicodeWithoutSplittingSurrogatePairsOrTypingReturnAsText() throws {
        let suffix = "Use this skill.\n" + String(repeating: "\u{1F469}\u{1F3FD}\u{200D}\u{1F4BB}", count: 200)
        let prompt = try ReviewLauncher.prompt(for: link(), additionalInstructions: suffix)
        let steps = ScoutPromptInput.steps(for: prompt)
        XCTAssertEqual(steps.map(\.content).joined(), prompt)
        XCTAssertEqual(steps.filter { $0 == .lineBreak }.count, 3)
        for step in steps {
            if case .text(let text) = step {
                XCTAssertFalse(text.isEmpty)
                XCTAssertLessThanOrEqual(text.utf16.count, 16)
                XCTAssertFalse(text.contains("\n"))
                XCTAssertFalse(text.contains("\u{fffd}"))
            }
        }
        XCTAssertEqual(ScoutPromptInput.steps(for: ""), [])
    }

    func testInvalidLinksAreRejectedBeforeOpeningAnApp() async throws {
        let runner = RecordingCommandRunner()
        let launcher = ReviewLauncher(
            applicationURL: { _ in XCTFail("Invalid links must not resolve or launch an app."); return nil },
            runner: runner, scoutLaunch: { _, _ in XCTFail("Invalid links must not launch Scout.") }
        )
        for link in [
            "http://example.com/pr/1", "file:///tmp/pr", "javascript:alert(1)",
            "https://user:password@example.com/pr/1", "https://example.com/" + String(repeating: "a", count: 2_049)
        ] {
            do {
                try await launcher.launch(.scout, for: XCTUnwrap(URL(string: link)))
                XCTFail("Expected invalid URL.")
            } catch {
                XCTAssertEqual(error as? ReviewLaunchError, .invalidURL)
            }
        }
        let commands = await runner.commands
        XCTAssertTrue(commands.isEmpty)
    }

    func testMissingAppsFailExplicitlyWithoutRunningAnything() async throws {
        let runner = RecordingCommandRunner()
        let launcher = ReviewLauncher(
            applicationURL: { _ in nil }, runner: runner,
            scoutLaunch: { _, _ in XCTFail("Missing Scout must not be launched.") }
        )
        for assistant in ReviewAssistant.allCases {
            do {
                try await launcher.launch(assistant, for: link())
                XCTFail("Expected missing app.")
            } catch {
                XCTAssertEqual(error as? ReviewLaunchError, .appUnavailable(assistant))
            }
        }
        let commands = await runner.commands
        XCTAssertTrue(commands.isEmpty)
    }

    func testScoutReceivesOnlyTheExactPromptAndItsResolvedApp() async throws {
        let runner = RecordingCommandRunner()
        let appURL = URL(fileURLWithPath: "/Applications/Synthetic Scout.app")
        var received: (URL, String)?
        let launcher = ReviewLauncher(
            applicationURL: { _ in appURL }, runner: runner,
            scoutLaunch: { received = ($0, $1) }
        )
        let url = try link()
        try await launcher.launch(.scout, for: url)
        XCTAssertEqual(received?.0, appURL)
        XCTAssertEqual(received?.1, "Review PR: \(url.absoluteString)")
        let commands = await runner.commands
        XCTAssertTrue(commands.isEmpty)
    }

    func testEachLauncherReceivesItsEntireAdditionalInstructionsLiterally() async throws {
        let appURL = try fakeCodeApp()
        defer { cleanup(appURL) }
        let runner = RecordingCommandRunner()
        var received: String?
        let launcher = ReviewLauncher(
            applicationURL: { _ in appURL }, runner: runner, scoutLaunch: { _, prompt in received = prompt }
        )
        let url = try link()
        let instructions = "Use a 'review' skill.\nKeep $(printf literal) and & characters as text."
        let expected = "Review PR: \(url.absoluteString)\n\n\(instructions)"
        try await launcher.launch(.scout, for: url, additionalInstructions: instructions)
        XCTAssertEqual(received, expected)
        try await launcher.launch(.copilot, for: url, additionalInstructions: instructions)
        let commands = await runner.commands
        XCTAssertEqual(commands.count, 2)
        XCTAssertEqual(commands[1].1, ["chat", "--mode", "agent", "--new-window", "--maximize", expected])
    }

    func testCopilotUsesABundledCLIAndANewAgentWindowWithOnePromptArgument() async throws {
        let appURL = try fakeCodeApp()
        defer { cleanup(appURL) }
        let runner = RecordingCommandRunner()
        let launcher = ReviewLauncher(applicationURL: { _ in appURL }, runner: runner)
        let url = try link()
        try await launcher.launch(.copilot, for: url)
        let commands = await runner.commands
        XCTAssertEqual(commands.count, 2)
        XCTAssertEqual(commands[0].0, appURL.appendingPathComponent("Contents/Resources/app/bin/code"))
        XCTAssertEqual(commands[0].1, ["chat", "--help"])
        XCTAssertEqual(commands[1].1, [
            "chat", "--mode", "agent", "--new-window", "--maximize", "Review PR: \(url.absoluteString)"
        ])
        XCTAssertFalse(commands[1].1.contains("--reuse-window"))
    }

    func testUnsupportedCodeChatDoesNotPretendToLaunchAReview() async throws {
        let appURL = try fakeCodeApp()
        defer { cleanup(appURL) }
        let runner = RecordingCommandRunner(help: Data("Old CLI without chat".utf8))
        let launcher = ReviewLauncher(applicationURL: { _ in appURL }, runner: runner)
        do {
            try await launcher.launch(.copilot, for: link())
            XCTFail("Expected unsupported CLI.")
        } catch {
            XCTAssertEqual(error as? ReviewLaunchError, .unsupportedCopilotCLI)
        }
        let commands = await runner.commands
        XCTAssertEqual(commands.count, 1)
    }

    func testCodeEnvironmentCannotRedirectTheCommandIntoARemoteTerminal() {
        let environment = ReviewLauncher.copilotEnvironment([
            "VSCODE_IPC_HOOK_CLI": "/remote/old-session", "NODE_OPTIONS": "old-options",
            "NODE_REPL_EXTERNAL_MODULE": "old-module", "PATH": "/fixture/bin", "HOME": "/fixture/home"
        ])
        XCTAssertNil(environment["VSCODE_IPC_HOOK_CLI"])
        XCTAssertNil(environment["NODE_OPTIONS"])
        XCTAssertNil(environment["NODE_REPL_EXTERNAL_MODULE"])
        XCTAssertEqual(environment["HOME"], "/fixture/home")
        XCTAssertTrue(environment["PATH"]?.hasPrefix("/usr/bin:/bin:") == true)
    }

    func testSharedCommandRunnerPassesArgumentsLiterally() async throws {
        let value = "A quoted 'PR' & query with spaces"
        let data = try await LocalCommandRunner().run(
            executable: URL(fileURLWithPath: "/usr/bin/printf"), arguments: ["%s", value]
        )
        XCTAssertEqual(String(data: data, encoding: .utf8), value)
    }

    func testLaunchFailureIsVisibleAndDiagnosticsNeverIncludeTheLinkOrTitle() async throws {
        let directory = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("ReviewQ-launch-tests-\(UUID())")
        let suite = "ReviewQ-launch-tests-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite); cleanup(directory) }
        let logger = DiagnosticLogger(directory: directory, verboseEnabled: true)
        logger.start()
        var shownError: ReviewLaunchError?
        let launcher = StubReviewLauncher(error: ReviewLaunchError.accessibilityRequired)
        let store = ReviewStore(defaults: defaults)
        var settings = ReviewConfiguration()
        settings.sourceURLs = "https://dev.azure.com/example/Project"
        settings.scoutAdditionalInstructions = "Synthetic private instructions never logged"
        try store.save(settings)
        let app = AppDelegate(
            store: store, diagnostics: DiagnosticsStore(logger: logger, defaults: defaults),
            reviewLauncher: launcher, presentReviewError: { shownError = $0 }
        )
        let request = try fixture()
        await app.review(request, with: .scout)
        XCTAssertFalse(app.isLaunchingReview)
        XCTAssertEqual(app.actionError, ReviewLaunchError.accessibilityRequired.localizedDescription)
        XCTAssertEqual(shownError, .accessibilityRequired)
        logger.flush()
        let data = try Data(contentsOf: directory.appendingPathComponent("verbose.log"))
        let text = try XCTUnwrap(String(data: data, encoding: .utf8))
        XCTAssertFalse(text.contains(request.url.absoluteString))
        XCTAssertFalse(text.contains(request.title))
        XCTAssertFalse(text.contains("Review PR:"))
        XCTAssertFalse(text.contains(settings.scoutAdditionalInstructions))
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let records = try data.split(separator: 0x0a).map { try decoder.decode(DiagnosticRecord.self, from: Data($0)) }
        let started = try XCTUnwrap(records.first { $0.event == .reviewLaunchStarted })
        let failed = try XCTUnwrap(records.first { $0.event == .reviewLaunchFailed })
        XCTAssertEqual(started.requestID, failed.requestID)
        XCTAssertEqual(failed.operation, .scout)
        XCTAssertEqual(failed.reason, .approvalPending)
    }

    func testSuccessfulLaunchClearsAnOldErrorAndDoesNotPresentAnAlert() async throws {
        let suite = "ReviewQ-launch-tests-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let launcher = StubReviewLauncher()
        let app = AppDelegate(
            store: ReviewStore(defaults: defaults), reviewLauncher: launcher,
            presentReviewError: { _ in XCTFail("Success must not show an error.") }
        )
        app.actionError = "Previous action failed"
        let request = try fixture()
        await app.review(request, with: .copilot)
        XCTAssertNil(app.actionError)
        XCTAssertFalse(app.isLaunchingReview)
        XCTAssertEqual(launcher.requests.count, 1)
        XCTAssertEqual(launcher.requests.first?.0, .copilot)
        XCTAssertEqual(launcher.requests.first?.1, request.url)
    }

    private func link() throws -> URL {
        try XCTUnwrap(URL(string: "https://dev.azure.com/example/Project/_git/repo/pullrequest/42"))
    }

    func testAppSelectsOnlyTheSelectedAssistantsLatestSavedInstructions() async throws {
        let suite = "ReviewQ-launch-tests-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ReviewStore(defaults: defaults)
        let launcher = StubReviewLauncher()
        let app = AppDelegate(
            store: store, reviewLauncher: launcher,
            presentReviewError: { _ in XCTFail("Valid instructions must not show an error.") }
        )
        var settings = ReviewConfiguration()
        settings.sourceURLs = "https://dev.azure.com/example/Project"
        settings.scoutAdditionalInstructions = "Use the Scout skill."
        settings.copilotAdditionalInstructions = "Use the Copilot skill."
        try store.save(settings)
        let request = try fixture()
        await app.review(request, with: .scout)
        await app.review(request, with: .copilot)
        settings.scoutAdditionalInstructions = "Use the updated Scout skill."
        try store.save(settings)
        await app.review(request, with: .scout)
        XCTAssertEqual(launcher.requests.map(\.0), [.scout, .copilot, .scout])
        XCTAssertEqual(launcher.requests.map(\.1), Array(repeating: request.url, count: 3))
        XCTAssertEqual(launcher.requests.map(\.2), [
            "Use the Scout skill.", "Use the Copilot skill.", "Use the updated Scout skill."
        ])
    }

    private func fixture() throws -> ReviewPullRequest {
        ReviewPullRequest(
            number: 42, title: "Synthetic private title never logged", author: "Fixture author",
            organization: "example", project: "Project", repository: "Repo", repositoryID: "repo",
            createdAt: Date(), url: try link(), isRequired: false, isDraft: false, reviewStage: .firstReview
        )
    }

    private func fakeCodeApp() throws -> URL {
        let url = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("ReviewQ-fake-code-\(UUID()).app")
        let bin = url.appendingPathComponent("Contents/Resources/app/bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let executable = bin.appendingPathComponent("code")
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
        return url
    }

    private func cleanup(_ url: URL) {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do { try FileManager.default.removeItem(at: url) }
        catch { XCTFail("Could not remove task-owned test fixture.") }
    }
}

private actor RecordingCommandRunner: CommandRunning {
    let help: Data
    var commands: [(URL, [String])] = []

    init(help: Data = Data("Usage: code chat [prompt] --new-window --mode".utf8)) {
        self.help = help
    }

    func run(executable: URL, arguments: [String]) async throws -> Data {
        commands.append((executable, arguments))
        return arguments == ["chat", "--help"] ? help : Data()
    }
}

@MainActor
private final class StubReviewLauncher: ReviewLaunching {
    let error: (any Error)?
    var requests: [(ReviewAssistant, URL, String)] = []

    init(error: (any Error)? = nil) { self.error = error }

    func launch(_ assistant: ReviewAssistant, for url: URL, additionalInstructions: String) async throws {
        requests.append((assistant, url, additionalInstructions))
        if let error { throw error }
    }
}
