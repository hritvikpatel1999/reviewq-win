import Carbon
@testable import ReviewQ
@testable import ReviewQCore
import XCTest

@MainActor
final class GlobalShortcutTests: XCTestCase {
    func testShortcutIsCommandOptionQOnReleaseWithoutExtraModifiers() {
        XCTAssertEqual(CarbonShortcutRegistrar.keyCode, UInt32(kVK_ANSI_Q))
        XCTAssertEqual(CarbonShortcutRegistrar.modifiers, UInt32(cmdKey | optionKey))
        XCTAssertEqual(CarbonShortcutRegistrar.modifiers & UInt32(shiftKey | controlKey), 0)
        XCTAssertEqual(CarbonShortcutRegistrar.eventKind, UInt32(kEventHotKeyReleased))
        XCTAssertNotEqual(CarbonShortcutRegistrar.signature, 0x4C42_724B)
    }

    func testStartAndStopAreIdempotentAndDeliverOnlyTheRegisteredAction() {
        let registrar = ShortcutRegistrarFake()
        let store = GlobalShortcutStore(registrar: registrar)
        var calls = 0
        store.start { calls += 1 }
        store.start { calls += 100 }
        XCTAssertEqual(registrar.registrations, 1)
        registrar.action?()
        XCTAssertEqual(calls, 1)
        store.stop()
        store.stop()
        XCTAssertEqual(registrar.unregistrations, 1)
        XCTAssertNil(registrar.action)
        store.start { calls += 1 }
        registrar.action?()
        XCTAssertEqual(calls, 2)
        store.stop()
    }

    func testConflictIsVisibleAndCanBeRetriedWithoutRestarting() throws {
        let registrar = ShortcutRegistrarFake()
        registrar.failure = GlobalShortcutError(status: OSStatus(eventHotKeyExistsErr))
        let store = GlobalShortcutStore(registrar: registrar)
        store.start { XCTFail("A failed registration must not deliver a shortcut.") }
        XCTAssertTrue(try XCTUnwrap(store.errorMessage).contains("already in use"))
        XCTAssertNil(registrar.action)
        store.stop()
        XCTAssertEqual(registrar.unregistrations, 0)
        registrar.failure = nil
        store.start {}
        XCTAssertEqual(registrar.registrations, 2)
        XCTAssertNil(store.errorMessage)
        store.stop()
    }

    func testSystemAndCleanupFailuresRemainVisible() {
        let registrar = ShortcutRegistrarFake()
        let store = GlobalShortcutStore(registrar: registrar)
        registrar.failure = GlobalShortcutError(status: -50)
        store.start {}
        XCTAssertTrue(store.errorMessage?.contains("-50") == true)
        registrar.failure = nil
        store.start {}
        registrar.cleanupFailure = GlobalShortcutError(status: -50, isUnregistering: true)
        store.stop()
        XCTAssertTrue(store.errorMessage?.contains("could not be released") == true)
    }

    func testForeignAndUnregisteredIdentifiersArePassedAlong() {
        let registrar = CarbonShortcutRegistrar()
        for identifier in [
            EventHotKeyID(signature: CarbonShortcutRegistrar.signature, id: 1),
            EventHotKeyID(signature: 0, id: 1),
            EventHotKeyID(signature: CarbonShortcutRegistrar.signature, id: 2)
        ] {
            XCTAssertEqual(registrar.handle(identifier: identifier), OSStatus(eventNotHandledErr))
        }
    }
}

@MainActor
private final class ShortcutRegistrarFake: GlobalShortcutRegistering {
    var registrations = 0
    var unregistrations = 0
    var failure: GlobalShortcutError?
    var cleanupFailure: GlobalShortcutError?
    var action: (@MainActor () -> Void)?

    func register(action: @escaping @MainActor () -> Void) throws {
        registrations += 1
        if let failure { throw failure }
        self.action = action
    }

    func unregister() throws {
        unregistrations += 1
        action = nil
        if let cleanupFailure { throw cleanupFailure }
    }
}
