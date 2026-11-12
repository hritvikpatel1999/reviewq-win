import AppKit
import Carbon
@testable import ReviewQ
import XCTest

@MainActor
final class NativeEditMenuTests: XCTestCase {
    func testEditingCommandsUseNativeSelectorsAndTheFirstResponder() throws {
        let menu = ReviewQApp.makeMainMenu()
        let edit = try XCTUnwrap(menu.items.first { $0.title == "Edit" }?.submenu)
        XCTAssertTrue(menu.autoenablesItems)
        XCTAssertTrue(edit.autoenablesItems)
        let commands = edit.items.filter { !$0.isSeparatorItem }
        XCTAssertEqual(commands.map(\.title), ["Undo", "Redo", "Cut", "Copy", "Paste", "Select All"])
        XCTAssertEqual(commands.compactMap(\.action).map(NSStringFromSelector), [
            "undo:", "redo:", "cut:", "copy:", "paste:", "selectAll:"
        ])
        for item in commands { XCTAssertNil(item.target, item.title) }
    }

    func testEditingShortcutsDoNotCaptureTheGlobalMenuShortcutOrSettingsActions() throws {
        let menu = ReviewQApp.makeMainMenu()
        let edit = try XCTUnwrap(menu.items.first { $0.title == "Edit" }?.submenu)
        XCTAssertEqual(edit.items.filter { !$0.isSeparatorItem }.map(\.keyEquivalent), ["z", "Z", "x", "c", "v", "a"])
        for item in edit.items.filter({ !$0.isSeparatorItem }) {
            XCTAssertEqual(item.keyEquivalentModifierMask, item.title == "Redo" ? [.command, .shift] : .command)
        }
        XCTAssertFalse(edit.items.contains { $0.keyEquivalent == "q" || $0.keyEquivalent == "d" || $0.keyEquivalent == "\r" })
        let application = try XCTUnwrap(menu.items.first { $0.title == "ReviewQ" }?.submenu)
        let quit = try XCTUnwrap(application.items.first)
        XCTAssertEqual(quit.action, #selector(NSApplication.terminate(_:)))
        XCTAssertEqual(quit.keyEquivalent, "q")
        XCTAssertEqual(quit.keyEquivalentModifierMask, .command)
    }

    func testMultilineEditorSelectsCopiesAndPastesExactText() throws {
        let fixture = try EditingFixture()
        defer { fixture.close() }
        fixture.window.contentView = fixture.editor
        fixture.editor.string = "Synthetic first line\nSynthetic second line"
        try fixture.focus(fixture.editor)
        try verifySelectionCopyPaste(fixture)
    }

    func testSingleLineFieldEditorSelectsCopiesAndPastesExactText() throws {
        let fixture = try EditingFixture()
        defer { fixture.close() }
        let field = NSTextField(string: "synthetic-tenant.example.invalid")
        fixture.window.contentView = field
        fixture.editor.isFieldEditor = true
        try fixture.focus(field)
        XCTAssertTrue(field.currentEditor() === fixture.editor)
        try verifySelectionCopyPaste(fixture)
        XCTAssertTrue(fixture.window.makeFirstResponder(nil))
        XCTAssertEqual(field.stringValue, "Synthetic pasted fixture")
    }

    func testSecureFieldKeepsNativeCopyAndCutRestrictions() throws {
        let fixture = try EditingFixture()
        defer { fixture.close() }
        let field = NSSecureTextField(string: "synthetic-token-not-a-credential")
        fixture.window.contentView = field
        try fixture.focus(field)
        let editor = try XCTUnwrap(field.currentEditor() as? NSTextView)
        XCTAssertFalse(editor === fixture.editor)
        try fixture.press("a", code: UInt16(kVK_ANSI_A))
        XCTAssertEqual(editor.selectedRange().length, (field.stringValue as NSString).length)
        fixture.edit.update()
        XCTAssertFalse(try fixture.item("Copy").isEnabled)
        XCTAssertFalse(try fixture.item("Cut").isEnabled)
        XCTAssertTrue(try fixture.item("Copy").target === editor)
    }

    func testEditingCommandsAreDisabledWithoutATextResponder() throws {
        let fixture = try EditingFixture()
        defer { fixture.close() }
        fixture.window.contentView = NSView()
        try fixture.focus(fixture.window)
        fixture.edit.update()
        for title in ["Cut", "Copy", "Paste", "Select All"] {
            XCTAssertFalse(try fixture.item(title).isEnabled, title)
        }
    }

    func testUndoAndRedoUseTheFocusedEditorsUndoManager() throws {
        let fixture = try EditingFixture()
        defer { fixture.close() }
        fixture.window.contentView = fixture.editor
        fixture.editor.allowsUndo = true
        fixture.editor.string = "Original fixture"
        try fixture.focus(fixture.editor)
        let undo = try XCTUnwrap(fixture.editor.undoManager)
        undo.beginUndoGrouping()
        fixture.editor.insertText("Changed fixture", replacementRange: NSRange(location: 0, length: fixture.editor.string.utf16.count))
        undo.endUndoGrouping()
        XCTAssertTrue(undo.canUndo)
        try fixture.press("z", code: UInt16(kVK_ANSI_Z))
        XCTAssertEqual(fixture.editor.string, "Original fixture")
        XCTAssertTrue(undo.canRedo)
        try fixture.press("z", code: UInt16(kVK_ANSI_Z), modifiers: [.command, .shift])
        XCTAssertEqual(fixture.editor.string, "Changed fixture")
    }

    private func verifySelectionCopyPaste(_ fixture: EditingFixture) throws {
        let original = fixture.editor.string
        XCTAssertFalse(original.isEmpty)
        fixture.editor.setSelectedRange(NSRange(location: 3, length: 0))
        try fixture.press("a", code: UInt16(kVK_ANSI_A))
        XCTAssertEqual(fixture.editor.selectedRange(), NSRange(location: 0, length: original.utf16.count))
        try fixture.press("c", code: UInt16(kVK_ANSI_C))
        XCTAssertEqual(fixture.clipboard.string(forType: .string), original)
        fixture.clipboard.clearContents()
        XCTAssertTrue(fixture.clipboard.setString("Synthetic pasted fixture", forType: .string))
        try fixture.press("v", code: UInt16(kVK_ANSI_V))
        XCTAssertEqual(fixture.editor.string, "Synthetic pasted fixture")
    }
}

@MainActor
private final class EditingFixture: NSObject, NSWindowDelegate {
    let clipboard: NSPasteboard
    let editor: IsolatedClipboardEditor
    let window: NSWindow
    let menu: NSMenu
    let edit: NSMenu
    private let previousMenu = NSApplication.shared.mainMenu
    private let previousWindow = NSApplication.shared.keyWindow

    init(menu: NSMenu = ReviewQApp.makeMainMenu()) throws {
        self.menu = menu
        clipboard = NSPasteboard(name: NSPasteboard.Name("ReviewQEditingTests-\(UUID().uuidString)"))
        editor = IsolatedClipboardEditor(clipboard: clipboard)
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 400, height: 180),
            styleMask: [.titled], backing: .buffered, defer: false
        )
        edit = try XCTUnwrap(menu.items.first { $0.title == "Edit" }?.submenu)
        super.init()
        window.isReleasedWhenClosed = false
        window.delegate = self
        NSApplication.shared.mainMenu = menu
    }

    func windowWillReturnFieldEditor(_ sender: NSWindow, to client: Any?) -> Any? {
        client is NSSecureTextField ? nil : editor
    }

    func focus(_ responder: NSResponder) throws {
        window.makeKeyAndOrderFront(nil)
        drainEvents()
        XCTAssertTrue(window.makeFirstResponder(responder))
        // XCTest has no application key-window loop; resolve this fixture's native responder chain explicitly.
        for item in edit.items {
            guard let action = item.action else { continue }
            var responder = window.firstResponder
            while let current = responder, !current.responds(to: action) { responder = current.nextResponder }
            item.target = responder
        }
    }

    func item(_ title: String) throws -> NSMenuItem {
        try XCTUnwrap(edit.items.first { $0.title == title })
    }

    func press(_ letter: String, code: UInt16, modifiers: NSEvent.ModifierFlags = .command) throws {
        edit.update()
        let characters = modifiers.contains(.shift) ? letter.uppercased() : letter
        let event = try XCTUnwrap(NSEvent.keyEvent(
            with: .keyDown, location: .zero, modifierFlags: modifiers,
            timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
            context: nil, characters: characters, charactersIgnoringModifiers: characters,
            isARepeat: false, keyCode: code
        ))
        XCTAssertTrue(menu.performKeyEquivalent(with: event), "Command shortcut \(letter) was not handled.")
        drainEvents()
    }

    func close() {
        window.close()
        NSApplication.shared.mainMenu = previousMenu
        previousWindow?.makeKey()
        clipboard.releaseGlobally()
    }

    private func drainEvents() {
        let deadline = Date().addingTimeInterval(0.02)
        while Date() < deadline {
            while let event = NSApplication.shared.nextEvent(
                matching: .any, until: .distantPast, inMode: .default, dequeue: true
            ) {
                NSApplication.shared.sendEvent(event)
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.002))
        }
    }
}

@MainActor
private final class IsolatedClipboardEditor: NSTextView {
    private let clipboard: NSPasteboard
    private let storage = NSTextStorage()

    init(clipboard: NSPasteboard) {
        self.clipboard = clipboard
        let layout = NSLayoutManager()
        let container = NSTextContainer(size: NSSize(width: 400, height: CGFloat.greatestFiniteMagnitude))
        storage.addLayoutManager(layout)
        layout.addTextContainer(container)
        super.init(frame: .zero, textContainer: container)
    }

    required init?(coder: NSCoder) { preconditionFailure("This fixture does not use archived views.") }

    override func copy(_ sender: Any?) {
        clipboard.clearContents()
        XCTAssertTrue(clipboard.setString((string as NSString).substring(with: selectedRange()), forType: .string))
    }

    override func paste(_ sender: Any?) {
        guard let text = clipboard.string(forType: .string) else {
            XCTFail("The isolated test clipboard has no text.")
            return
        }
        insertText(text, replacementRange: selectedRange())
    }
}
