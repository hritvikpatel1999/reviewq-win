import Carbon
import Combine
import Foundation
import ReviewQCore

@MainActor
protocol GlobalShortcutRegistering: AnyObject {
    func register(action: @escaping @MainActor () -> Void) throws
    func unregister() throws
}

struct GlobalShortcutError: LocalizedError {
    let status: OSStatus
    var isUnregistering = false

    var errorDescription: String? {
        if isUnregistering {
            return "Command-Option-Q could not be released (macOS error \(status)). Restart ReviewQ."
        }
        if status == eventHotKeyExistsErr {
            return "Command-Option-Q is already in use. Free it in the other app, then choose Retry shortcut in Settings."
        }
        return "Command-Option-Q could not be registered (macOS error \(status)). Choose Retry shortcut in Settings."
    }
}

@MainActor
final class GlobalShortcutStore: ObservableObject {
    @Published private(set) var errorMessage: String?
    private let registrar: any GlobalShortcutRegistering
    private let diagnostics: DiagnosticLogger
    private var isRegistered = false

    init(registrar: any GlobalShortcutRegistering, diagnostics: DiagnosticLogger = .disabled) {
        self.registrar = registrar
        self.diagnostics = diagnostics
    }

    func start(action: @escaping @MainActor () -> Void) {
        guard !isRegistered else { return }
        do {
            try registrar.register { [diagnostics] in
                diagnostics.log(.debug, .shortcutPressed, operation: .shortcut)
                action()
            }
            isRegistered = true
            errorMessage = nil
            diagnostics.log(.info, .shortcutRegistered, operation: .shortcut)
        } catch {
            errorMessage = error.localizedDescription
            diagnostics.log(
                .warning, .shortcutRegistrationFailed, operation: .shortcut, reason: .system,
                systemCode: (error as? GlobalShortcutError).map { Int($0.status) }
            )
        }
    }

    func stop() {
        guard isRegistered else { return }
        defer { isRegistered = false }
        do {
            try registrar.unregister()
        } catch {
            errorMessage = error.localizedDescription
            diagnostics.log(
                .warning, .shortcutUnregistrationFailed, operation: .shortcut, reason: .system,
                systemCode: (error as? GlobalShortcutError).map { Int($0.status) }
            )
        }
    }
}

@MainActor
final class CarbonShortcutRegistrar: GlobalShortcutRegistering {
    static let keyCode = UInt32(kVK_ANSI_Q)
    static let modifiers = UInt32(cmdKey | optionKey)
    static let signature: OSType = 0x5251_724B
    static let eventKind = UInt32(kEventHotKeyReleased)
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var action: (@MainActor () -> Void)?
    private let diagnostics: DiagnosticLogger

    init(diagnostics: DiagnosticLogger = .disabled) { self.diagnostics = diagnostics }

    func register(action: @escaping @MainActor () -> Void) throws {
        try unregister()
        var event = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: Self.eventKind)
        let handlerStatus = InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, userData in
                guard let event, let userData else { return OSStatus(eventNotHandledErr) }
                return MainActor.assumeIsolated {
                    let registrar = Unmanaged<CarbonShortcutRegistrar>.fromOpaque(userData).takeUnretainedValue()
                    var identifier = EventHotKeyID()
                    let status = GetEventParameter(
                        event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                        nil, MemoryLayout<EventHotKeyID>.size, nil, &identifier
                    )
                    guard status == noErr else { return status }
                    return registrar.handle(identifier: identifier)
                }
            },
            1, &event, Unmanaged.passUnretained(self).toOpaque(), &handler
        )
        guard handlerStatus == noErr else { throw GlobalShortcutError(status: handlerStatus) }
        let status = RegisterEventHotKey(
            Self.keyCode, Self.modifiers, EventHotKeyID(signature: Self.signature, id: 1),
            GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &hotKey
        )
        guard status == noErr else {
            do { try unregister() }
            catch {
                diagnostics.log(
                    .warning, .shortcutUnregistrationFailed, operation: .shortcut, reason: .system,
                    systemCode: (error as? GlobalShortcutError).map { Int($0.status) }
                )
            }
            throw GlobalShortcutError(status: status)
        }
        self.action = action
    }

    func handle(identifier: EventHotKeyID) -> OSStatus {
        guard identifier.signature == Self.signature, identifier.id == 1, let action else {
            return OSStatus(eventNotHandledErr)
        }
        action()
        return noErr
    }

    func unregister() throws {
        action = nil
        var failure: OSStatus?
        if let hotKey {
            let status = UnregisterEventHotKey(hotKey)
            if status == noErr { self.hotKey = nil }
            else { failure = status }
        }
        if let handler {
            let status = RemoveEventHandler(handler)
            if status == noErr { self.handler = nil }
            else { failure = failure ?? status }
        }
        if let failure { throw GlobalShortcutError(status: failure, isUnregistering: true) }
    }

    isolated deinit {
        do { try unregister() }
        catch {
            diagnostics.log(
                .warning, .shortcutUnregistrationFailed, operation: .shortcut, reason: .system,
                systemCode: (error as? GlobalShortcutError).map { Int($0.status) }
            )
        }
    }
}
