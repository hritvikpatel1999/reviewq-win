import AppKit
import Combine
import OSLog
import ReviewQCore
import SwiftUI

@main
enum ReviewQApp {
    @MainActor
    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        application.mainMenu = makeMainMenu()
        let logger = DiagnosticLogger(
            directory: DiagnosticLogger.defaultDirectory,
            verboseEnabled: UserDefaults.standard.bool(forKey: DiagnosticLogger.verboseKey)
        )
        let diagnostics = DiagnosticsStore(logger: logger)
        let delegate = AppDelegate(store: ReviewStore(diagnostics: logger), diagnostics: diagnostics)
        application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
    }

    @MainActor
    static func makeMainMenu() -> NSMenu {
        let menu = NSMenu()
        let applicationMenu = NSMenu(title: "ReviewQ")
        applicationMenu.addItem(
            withTitle: "Quit ReviewQ", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"
        )
        let applicationItem = menu.addItem(withTitle: "ReviewQ", action: nil, keyEquivalent: "")
        applicationItem.submenu = applicationMenu

        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Undo", action: NSSelectorFromString("undo:"), keyEquivalent: "z")
        let redo = editMenu.addItem(withTitle: "Redo", action: NSSelectorFromString("redo:"), keyEquivalent: "Z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let editItem = menu.addItem(withTitle: "Edit", action: nil, keyEquivalent: "")
        editItem.submenu = editMenu
        return menu
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSPopoverDelegate, NSWindowDelegate, ObservableObject {
    let store: ReviewStore
    let loginItem: LoginItemStore
    let diagnostics: DiagnosticsStore
    let menuShortcut: GlobalShortcutStore
    @Published var actionError: String?
    @Published private(set) var isLaunchingReview = false
    private let reviewLauncher: any ReviewLaunching
    private let presentReviewError: @MainActor (ReviewLaunchError) -> Void
    private let popover = NSPopover()
    private var statusItem: NSStatusItem?
    private var settingsWindow: NSWindow?
    private var diagnosticsWindow: NSWindow?
    private var subscriptions: Set<AnyCancellable> = []
    private let logger = Logger(subsystem: "dev.prbar.app", category: "application")

    init(
        store: ReviewStore = ReviewStore(), diagnostics: DiagnosticsStore = DiagnosticsStore(),
        reviewLauncher: any ReviewLaunching = ReviewLauncher(),
        shortcutRegistrar: (any GlobalShortcutRegistering)? = nil,
        presentReviewError: @escaping @MainActor (ReviewLaunchError) -> Void = AppDelegate.showReviewError
    ) {
        self.store = store
        self.diagnostics = diagnostics
        self.reviewLauncher = reviewLauncher
        self.presentReviewError = presentReviewError
        menuShortcut = GlobalShortcutStore(
            registrar: shortcutRegistrar ?? CarbonShortcutRegistrar(diagnostics: diagnostics.logger),
            diagnostics: diagnostics.logger
        )
        loginItem = LoginItemStore(diagnostics: diagnostics.logger)
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        diagnostics.recordStartup()
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem = item
        guard let button = item.button,
              let image = NSImage(systemSymbolName: "arrow.triangle.pull", accessibilityDescription: "Pull requests")
        else { preconditionFailure("The native ReviewQ status icon is unavailable.") }
        image.isTemplate = true
        button.image = image
        button.imagePosition = .imageLeading
        button.target = self
        button.action = #selector(togglePopover)
        popover.behavior = .transient
        popover.animates = false
        popover.delegate = self
        let host = NSHostingController(
            rootView: DashboardView(store: store, app: self)
                .onExitCommand { [weak self] in self?.closePopover() }
        )
        popover.contentViewController = host
        popover.contentSize = NSSize(width: 440, height: 540)
        store.objectWillChange
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.updateStatusItem() }
            .store(in: &subscriptions)
        NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.didWakeNotification)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in
                self?.diagnostics.logger.log(.info, .wake)
                self?.store.requestRefresh()
            }
            .store(in: &subscriptions)
        NSWorkspace.shared.notificationCenter.publisher(for: NSWorkspace.willSleepNotification)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.diagnostics.logger.log(.info, .sleep) }
            .store(in: &subscriptions)
        updateStatusItem()
        startMenuShortcut()
        store.start()
        if CommandLine.arguments.contains("--diagnostics") {
            showDiagnostics()
        } else if !store.configuration.hasSources || store.errorMessage != nil || menuShortcut.errorMessage != nil {
            showSettings()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        diagnostics.logger.log(.info, .appReopened)
        showSettings()
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        menuShortcut.stop()
        store.stop()
        diagnostics.recordShutdown()
        subscriptions.removeAll()
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
    }

    func startMenuShortcut() {
        menuShortcut.start { [weak self] in self?.togglePopover() }
    }

    @objc private func togglePopover() {
        if popover.isShown {
            closePopover()
        } else {
            guard let button = statusItem?.button else {
                reportActionFailure("ReviewQ's menu-bar button is unavailable. Restart the app.")
                return
            }
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            NSApplication.shared.activate(ignoringOtherApps: true)
            popover.contentViewController?.view.window?.makeKey()
            button.highlight(true)
        }
    }

    func closePopover() { popover.performClose(nil) }

    func popoverDidClose(_ notification: Notification) {
        statusItem?.button?.highlight(false)
    }

    func showSettings() {
        closePopover()
        loginItem.refresh()
        if settingsWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 560, height: 640),
                styleMask: [.titled, .closable, .miniaturizable, .resizable],
                backing: .buffered, defer: false
            )
            window.title = "ReviewQ Settings"
            window.contentMinSize = NSSize(width: 520, height: 540)
            window.isReleasedWhenClosed = false
            window.contentViewController = NSHostingController(
                rootView: SettingsView(store: store, app: self, loginItem: loginItem)
            )
            window.delegate = self
            window.center()
            settingsWindow = window
        }
        NSApplication.shared.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    func showDiagnostics() {
        closePopover()
        if diagnosticsWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 560, height: 500),
                styleMask: [.titled, .closable, .miniaturizable, .resizable],
                backing: .buffered, defer: false
            )
            window.title = "ReviewQ Diagnostics"
            window.contentMinSize = NSSize(width: 520, height: 420)
            window.isReleasedWhenClosed = false
            window.contentViewController = NSHostingController(rootView: DiagnosticsView(diagnostics: diagnostics))
            window.delegate = self
            window.center()
            diagnosticsWindow = window
        }
        NSApplication.shared.activate(ignoringOtherApps: true)
        diagnosticsWindow?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        if let window = notification.object as? NSWindow, window === settingsWindow {
            settingsWindow = nil
        } else if let window = notification.object as? NSWindow, window === diagnosticsWindow {
            diagnosticsWindow = nil
        }
    }

    func windowDidBecomeKey(_ notification: Notification) {
        if let window = notification.object as? NSWindow, window === diagnosticsWindow {
            diagnostics.logger.log(.info, .diagnosticsOpened, operation: .application)
            logger.notice("Diagnostics window became key.")
        }
    }

    func open(_ url: URL) {
        if NSWorkspace.shared.open(url) {
            actionError = nil
            closePopover()
        } else {
            reportActionFailure(
                "macOS could not open this link. Check your default browser.",
                event: .browserOpenFailed, operation: .browser
            )
        }
    }

    func review(_ request: ReviewPullRequest, with assistant: ReviewAssistant) async {
        guard !isLaunchingReview else {
            reportActionFailure(
                "Wait for the current review chat to open before starting another.",
                event: .reviewLaunchFailed, operation: assistant.diagnosticOperation
            )
            return
        }
        isLaunchingReview = true
        actionError = nil
        let requestID = UUID()
        let start = ContinuousClock.now
        diagnostics.logger.log(.info, .reviewLaunchStarted, operation: assistant.diagnosticOperation, requestID: requestID)
        defer { isLaunchingReview = false }
        do {
            try await reviewLauncher.launch(
                assistant, for: request.url,
                additionalInstructions: assistant.additionalInstructions(in: store.configuration)
            )
            let duration = start.duration(to: .now).components
            diagnostics.logger.log(
                .info, .reviewLaunchFinished, operation: assistant.diagnosticOperation, requestID: requestID,
                durationMilliseconds: Int(duration.seconds * 1_000 + duration.attoseconds / 1_000_000_000_000_000)
            )
            closePopover()
        } catch is CancellationError {
            diagnostics.logger.log(
                .info, .reviewLaunchFailed, operation: assistant.diagnosticOperation,
                requestID: requestID, reason: .cancelled
            )
        } catch {
            let failure: ReviewLaunchError
            if let error = error as? ReviewLaunchError {
                failure = error
            } else if let error = error as? LocalCommandError {
                switch error {
                case .unavailable: failure = .appUnavailable(assistant)
                case .timeout: failure = .commandTimedOut
                case .failed(let code): failure = .commandFailed(code)
                }
            } else {
                failure = .launchFailed
            }
            actionError = failure.localizedDescription
            diagnostics.logger.log(
                .warning, .reviewLaunchFailed, operation: assistant.diagnosticOperation,
                requestID: requestID, reason: failure.diagnosticReason
            )
            logger.error("Review launch failed. Check ReviewQ Diagnostics.")
            closePopover()
            presentReviewError(failure)
        }
    }

    func openInEdge(_ url: URL) {
        guard let browser = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.microsoft.edgemac") else {
            reportActionFailure(
                "Microsoft Edge is not installed. Install Edge, or click the PR row to use your default browser.",
                event: .browserOpenFailed, operation: .browser, reason: .cliUnavailable
            )
            return
        }
        NSWorkspace.shared.open([url], withApplicationAt: browser, configuration: NSWorkspace.OpenConfiguration()) {
            [weak self] application, _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                if application != nil {
                    self.actionError = nil
                    self.closePopover()
                } else {
                    self.reportActionFailure(
                        "macOS could not open this PR in Edge. Check that Edge opens normally.",
                        event: .browserOpenFailed, operation: .browser
                    )
                }
            }
        }
    }

    private static func showReviewError(_ error: ReviewLaunchError) {
        NSApplication.shared.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Could not start review"
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        if error == .accessibilityRequired { alert.addButton(withTitle: "Open Accessibility Settings") }
        alert.addButton(withTitle: "OK")
        if alert.runModal() == .alertFirstButtonReturn, error == .accessibilityRequired {
            guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"),
                  NSWorkspace.shared.open(url) else {
                let failure = NSAlert()
                failure.messageText = "Open System Settings manually"
                failure.informativeText = "Choose Privacy & Security > Accessibility and allow ReviewQ."
                failure.runModal()
                return
            }
        }
    }

    func openDocumentation(_ text: String) {
        guard let url = URL(string: text), url.scheme == "https" else {
            reportActionFailure(
                "The documentation link is invalid.", event: .browserOpenFailed,
                operation: .browser, reason: .invalidConfiguration
            )
            return
        }
        open(url)
    }

    private func reportActionFailure(
        _ message: String, event: DiagnosticEvent = .applicationActionFailed,
        operation: DiagnosticOperation = .application, reason: DiagnosticReason = .system
    ) {
        actionError = message
        diagnostics.logger.log(.warning, event, operation: operation, reason: reason)
        logger.error("App action failed. Check ReviewQ Diagnostics.")
    }

    private func updateStatusItem() {
        statusItem?.button?.title = store.menuBarTitle
        let description: String
        if let error = store.errorMessage {
            description = "ReviewQ - refresh error: \(error)"
        } else if let snapshot = store.snapshot {
            description = "ReviewQ - \(snapshot.pendingReviewCount) pull requests need your review"
        } else {
            description = store.configuration.hasSources ? "ReviewQ - loading reviews" : "ReviewQ - connect Azure DevOps"
        }
        statusItem?.button?.toolTip = description
        statusItem?.button?.setAccessibilityLabel(description)
    }
}
