import AppKit
@testable import ReviewQ
@testable import ReviewQCore
import XCTest

@MainActor
final class DiagnosticsWindowTests: XCTestCase {
    func testWindowIsReusedAndCanBeReopenedWithoutDiscardingSettings() async throws {
        _ = NSApplication.shared
        NSApplication.shared.setActivationPolicy(.regular)
        let app = AppDelegate()
        defer {
            for window in NSApplication.shared.windows where
                window.title == "ReviewQ Diagnostics" || window.title == "ReviewQ Settings" {
                window.close()
            }
        }
        app.showSettings()
        let settings = NSApplication.shared.windows.first { $0.title == "ReviewQ Settings" && $0.isVisible }
        app.showDiagnostics()
        try await Task.sleep(for: .milliseconds(100))
        let first = NSApplication.shared.windows.first { $0.title == "ReviewQ Diagnostics" && $0.isVisible }
        XCTAssertNotNil(first)
        XCTAssertTrue(first?.isVisible == true)
        XCTAssertTrue(first?.canBecomeKey == true)
        app.showDiagnostics()
        XCTAssertEqual(
            NSApplication.shared.windows.filter { $0.title == "ReviewQ Diagnostics" && $0.isVisible }.count, 1
        )
        first?.performClose(nil)
        app.showDiagnostics()
        try await Task.sleep(for: .milliseconds(100))
        let reopened = NSApplication.shared.windows.first { $0.title == "ReviewQ Diagnostics" && $0.isVisible }
        XCTAssertNotNil(reopened)
        XCTAssertFalse(first === reopened)
        XCTAssertTrue(reopened?.canBecomeKey == true)
        XCTAssertTrue(settings?.isVisible == true)
    }

    func testVerbosePersistsAndExportErrorsSurviveRefreshUntilRetrySucceeds() async throws {
        let (logger, store, defaults, directory, suite) = try fixture()
        defer {
            defaults.removePersistentDomain(forName: suite)
            cleanup(directory)
        }
        store.setVerboseEnabled(true)
        XCTAssertTrue(defaults.bool(forKey: DiagnosticLogger.verboseKey))
        logger.start()
        await store.export(to: directory.appendingPathComponent("missing/report.zip"))
        XCTAssertNotNil(store.errorMessage)
        await store.refresh()
        XCTAssertNotNil(store.errorMessage)
        await store.export(to: directory.appendingPathComponent("report.zip"))
        XCTAssertNil(store.errorMessage)
        XCTAssertNotNil(store.statusMessage)
        XCTAssertFalse(store.isWorking)
        XCTAssertEqual(DiagnosticsStore(logger: logger, defaults: defaults).verboseEnabled, true)
    }

    func testClearPreservesSpecificUnsafeLocationError() async throws {
        let (logger, store, defaults, directory, suite) = try fixture()
        defer {
            defaults.removePersistentDomain(forName: suite)
            cleanup(directory)
        }
        logger.start()
        logger.flush()
        let warning = directory.appendingPathComponent("warnings-errors.log")
        try FileManager.default.removeItem(at: warning)
        let target = directory.appendingPathComponent("target")
        try Data("do-not-modify".utf8).write(to: target)
        try FileManager.default.createSymbolicLink(at: warning, withDestinationURL: target)
        await store.clear()
        XCTAssertEqual(store.errorMessage, DiagnosticStorageError.unsafePath.localizedDescription)
        await store.refresh()
        XCTAssertEqual(store.errorMessage, DiagnosticStorageError.unsafePath.localizedDescription)
        XCTAssertEqual(try String(contentsOf: target, encoding: .utf8), "do-not-modify")
    }

    func testLifecycleRecordsUncleanSessionOnlyUntilAnOrderlyShutdown() throws {
        let (logger, store, defaults, directory, suite) = try fixture()
        defer {
            defaults.removePersistentDomain(forName: suite)
            cleanup(directory)
        }
        store.setVerboseEnabled(true)
        store.recordStartup()
        let second = DiagnosticsStore(logger: logger, defaults: defaults)
        second.recordStartup()
        second.recordShutdown()
        let third = DiagnosticsStore(logger: logger, defaults: defaults)
        third.recordStartup()
        third.recordShutdown()
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let records = try Data(contentsOf: directory.appendingPathComponent("verbose.log"))
            .split(separator: 0x0a).map { try decoder.decode(DiagnosticRecord.self, from: Data($0)) }
        XCTAssertEqual(records.filter { $0.event == .previousSessionUnclean }.count, 1)
        XCTAssertEqual(records.last?.event, .shutdown)
        XCTAssertFalse(defaults.bool(forKey: "diagnosticsSessionActive"))
    }

    private func fixture() throws -> (DiagnosticLogger, DiagnosticsStore, UserDefaults, URL, String) {
        let directory = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("ReviewQ-diagnostics-ui-tests-\(UUID())")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        let suite = "ReviewQ-diagnostics-ui-tests-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let logger = DiagnosticLogger(directory: directory)
        return (logger, DiagnosticsStore(logger: logger, defaults: defaults), defaults, directory, suite)
    }

    private func cleanup(_ directory: URL) {
        do { try FileManager.default.removeItem(at: directory) }
        catch { XCTFail("Could not remove the test-owned diagnostics directory.") }
    }
}
