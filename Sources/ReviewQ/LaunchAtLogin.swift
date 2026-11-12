import Combine
import OSLog
import ReviewQCore
import ServiceManagement

@MainActor
final class LoginItemStore: ObservableObject {
    @Published private(set) var status = SMAppService.mainApp.status
    @Published private(set) var isWorking = false
    @Published private(set) var errorMessage: String?
    private let logger = Logger(subsystem: "dev.prbar.app", category: "login-item")
    private let diagnostics: DiagnosticLogger

    init(diagnostics: DiagnosticLogger = .disabled) {
        self.diagnostics = diagnostics
    }

    var isOn: Bool { status == .enabled || status == .requiresApproval }

    var statusMessage: String {
        switch status {
        case .enabled: "ReviewQ starts quietly in the menu bar when you sign in."
        case .requiresApproval: "Approve ReviewQ in System Settings > General > Login Items."
        case .notRegistered: "ReviewQ will not launch automatically."
        case .notFound: "Run the built ReviewQ app from Applications to use launch at login."
        @unknown default: "Check ReviewQ in System Settings > General > Login Items."
        }
    }

    func refresh() { status = SMAppService.mainApp.status }

    func setEnabled(_ enabled: Bool) async {
        guard !isWorking else { return }
        refresh()
        errorMessage = nil
        guard enabled != isOn else { return }
        isWorking = true
        defer {
            refresh()
            isWorking = false
        }
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try await SMAppService.mainApp.unregister()
            }
            diagnostics.log(
                .info, .loginItemChanged, operation: .loginItem,
                reason: SMAppService.mainApp.status == .requiresApproval ? .approvalPending : nil
            )
        } catch {
            errorMessage = "Could not change launch at login: \(error.localizedDescription)"
            diagnostics.logFailure(.loginItemFailed, error: error, operation: .loginItem)
            logger.error("Login-item change failed. Check ReviewQ Diagnostics.")
        }
    }

    func openSystemSettings() { SMAppService.openSystemSettingsLoginItems() }
}
