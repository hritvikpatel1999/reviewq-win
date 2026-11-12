import AppKit
import Combine
import ReviewQCore
import UniformTypeIdentifiers

@MainActor
final class DiagnosticsStore: ObservableObject {
    @Published private(set) var verboseEnabled: Bool
    @Published private(set) var isWorking = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    @Published private(set) var storageBytes = 0

    let logger: DiagnosticLogger
    private let defaults: UserDefaults
    private var operationError: String?
    private static let activeSessionKey = "diagnosticsSessionActive"

    init(logger: DiagnosticLogger = .disabled, defaults: UserDefaults = .standard) {
        self.logger = logger
        self.defaults = defaults
        verboseEnabled = defaults.bool(forKey: DiagnosticLogger.verboseKey)
    }

    func refresh() async {
        let logger = logger
        let state = await Task.detached(priority: .utility) {
            logger.inspect()
        }.value
        storageBytes = state.bytes
        errorMessage = operationError ?? state.error?.localizedDescription
    }

    func setVerboseEnabled(_ value: Bool) {
        verboseEnabled = value
        defaults.set(value, forKey: DiagnosticLogger.verboseKey)
        logger.setVerboseEnabled(value)
        statusMessage = nil
        Task { await refresh() }
    }

    func showInFinder() async {
        guard !isWorking else { return }
        isWorking = true
        defer { isWorking = false }
        do {
            let logger = logger
            let directory = try await Task.detached(priority: .utility) {
                try logger.prepareFolder()
            }.value
            guard NSWorkspace.shared.open(directory) else {
                operationError = "Could not open the diagnostic folder in Finder."
                logger.log(.warning, .logsFolderFailed, reason: .system)
                await refresh()
                return
            }
            operationError = nil
            statusMessage = nil
        } catch {
            operationError = (error as? DiagnosticStorageError ?? .unavailable).localizedDescription
            logger.logFailure(.logsFolderFailed, error: error)
        }
        await refresh()
    }

    func clear() async {
        guard !isWorking else { return }
        isWorking = true
        defer { isWorking = false }
        do {
            let logger = logger
            try await Task.detached(priority: .utility) { try logger.clear() }.value
            operationError = nil
            statusMessage = "Logs cleared. Current startup information is retained."
        } catch {
            operationError = (error as? DiagnosticStorageError ?? .unavailable).localizedDescription
            logger.logFailure(.logsClearFailed, error: error)
        }
        await refresh()
    }

    func export() {
        guard !isWorking else { return }
        isWorking = true
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.zip]
        panel.nameFieldStringValue = "ReviewQ-Logs.zip"
        panel.title = "Export ReviewQ Logs"
        panel.message = "Logs include timestamps and system versions. Review the ZIP before sharing. Nothing is uploaded."
        panel.begin { [weak self] response in
            guard let self else { return }
            guard response == .OK, let destination = panel.url else {
                isWorking = false
                return
            }
            Task { await completeExport(to: destination) }
        }
    }

    func export(to destination: URL) async {
        guard !isWorking else { return }
        isWorking = true
        await completeExport(to: destination)
    }

    private func completeExport(to destination: URL) async {
        defer { isWorking = false }
        do {
            let logger = logger
            try await Task.detached(priority: .utility) { try logger.export(to: destination) }.value
            operationError = nil
            statusMessage = "Logs exported. Nothing was uploaded or sent."
        } catch {
            operationError = (error as? DiagnosticStorageError ?? .exportFailed).localizedDescription
        }
        await refresh()
    }

    func recordStartup() {
        guard logger.directory != nil else { return }
        logger.start(previousSessionUnclean: defaults.bool(forKey: Self.activeSessionKey))
        defaults.set(true, forKey: Self.activeSessionKey)
    }

    func recordShutdown() {
        guard logger.directory != nil else { return }
        logger.log(.info, .shutdown)
        logger.flush()
        defaults.set(false, forKey: Self.activeSessionKey)
    }
}
