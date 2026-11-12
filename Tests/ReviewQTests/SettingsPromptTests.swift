import AppKit
@testable import ReviewQ
@testable import ReviewQCore
import SwiftUI
import Vision
import XCTest

@MainActor
final class SettingsPromptTests: XCTestCase {
    func testDefaultEditorShowsItsOwnNameAndCharacterLimit() throws {
        let lines = try recognize(render(ReviewInstructionsEditor(title: "Scout", text: .constant(""))))
        XCTAssertTrue(lines.contains { $0.text == "Scout" }, "\(lines)")
        XCTAssertTrue(lines.contains { $0.text.contains("of 1,000 characters") }, "\(lines)")
    }

    func testEachEditorShowsTheExactCountAndTurnsRedOnlyOverTheLimit() throws {
        for title in ["Scout", "GitHub Copilot"] {
            for (count, label) in [(1_000, "1,000"), (1_001, "1,001")] {
                let image = try render(ReviewInstructionsEditor(title: title, text: .constant(String(repeating: "x", count: count))))
                let lines = try recognize(image)
                let counter = try XCTUnwrap(lines.first { $0.text.contains("\(label) of 1,000 characters") })
                let bounds = CGRect(
                    x: counter.bounds.minX * CGFloat(image.width),
                    y: (1 - counter.bounds.maxY) * CGFloat(image.height),
                    width: counter.bounds.width * CGFloat(image.width),
                    height: counter.bounds.height * CGFloat(image.height)
                )
                let cropped = try XCTUnwrap(image.cropping(to: bounds))
                XCTAssertEqual(try hasRedPixels(cropped), count > 1_000)
            }
        }
    }

    func testFollowSettingsShowsTheSemicolonExampleAndOptInExplanation() async throws {
        let lines = try recognize(await captureFollowSettings(enabled: false, emails: ""))
        let text = lines.map(\.text).joined(separator: "\n")
        XCTAssertTrue(text.contains("People you follow"), text)
        XCTAssertTrue(text.contains("alex@example.com") && text.contains("sam@example.com"), text)
        XCTAssertTrue(text.contains("Off by default"), text)
        XCTAssertTrue(text.contains("semicolons"), text)
        XCTAssertTrue(text.contains("no spaces"), text)
        XCTAssertFalse(text.contains("Enter at least one email"), text)
    }

    func testInvalidFollowEmailsShowAnExplicitValidationErrorWhenEnabled() async throws {
        let lines = try recognize(await captureFollowSettings(
            enabled: true, emails: "alex@example.com; sam@example.com"
        ))
        XCTAssertTrue(lines.map(\.text).joined(separator: " ").contains("Enter at least one email"), "\(lines)")
    }

    func testAuthoredSettingsToggleUpdatesItsDraftBinding() async throws {
        _ = NSApplication.shared
        let state = AuthoredSettingsState()
        let host = NSHostingView(rootView: AuthoredSettingsHarness(state: state).frame(width: 560))
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 260),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        await Task.yield()
        host.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(200))
        let top = host.isFlipped
            ? host.bounds.midY - host.intrinsicContentSize.height / 2
            : host.bounds.midY + host.intrinsicContentSize.height / 2
        let direction: CGFloat = host.isFlipped ? 1 : -1
        let location = host.convert(NSPoint(x: 80, y: top + direction * 38), to: nil)
        XCTAssertFalse(state.configuration.showAuthoredPullRequests)
        for expected in [true, false] {
            for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
                let event = try XCTUnwrap(NSEvent.mouseEvent(
                    with: type, location: location, modifierFlags: [],
                    timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                    context: nil, eventNumber: 1, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0
                ))
                window.sendEvent(event)
            }
            await Task.yield()
            try await Task.sleep(for: .milliseconds(100))
            XCTAssertEqual(state.configuration.showAuthoredPullRequests, expected)
            XCTAssertEqual(state.configuration.sourceURLs, "")
            XCTAssertFalse(state.configuration.followPeople)
        }
    }

    private func captureFollowSettings(enabled: Bool, emails: String) async throws -> CGImage {
        _ = NSApplication.shared
        let host = NSHostingView(rootView:
            FollowedPeopleSettings(isEnabled: .constant(enabled), emails: .constant(emails))
                .frame(width: 560, height: 340)
                .environment(\.colorScheme, .light)
                .environment(\.locale, Locale(identifier: "en_US"))
                .background(Color.white)
        )
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 340),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .aqua)
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.close() }
        await Task.yield()
        host.layoutSubtreeIfNeeded()
        try await Task.sleep(for: .milliseconds(200))
        host.displayIfNeeded()
        let field = try XCTUnwrap(textFields(in: host).first { $0.placeholderString != nil })
        XCTAssertEqual(field.placeholderString, "alex@example.com;sam@example.com")
        XCTAssertEqual(field.isEnabled, enabled)
        XCTAssertEqual(field.stringValue, emails)
        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: bitmap)
        return try XCTUnwrap(bitmap.cgImage)
    }

    @MainActor
    private final class AuthoredSettingsState: ObservableObject {
        @Published var configuration = ReviewConfiguration()
    }

    private struct AuthoredSettingsHarness: View {
        @ObservedObject var state: AuthoredSettingsState

        var body: some View {
            AuthoredPullRequestsSettings(isEnabled: $state.configuration.showAuthoredPullRequests)
        }
    }

    private func textFields(in view: NSView) -> [NSTextField] {
        if let field = view as? NSTextField { return [field] }
        return view.subviews.flatMap { textFields(in: $0) }
    }

    private func render(_ view: some View) throws -> CGImage {
        _ = NSApplication.shared
        let renderer = ImageRenderer(
            content: view.frame(width: 560, height: 160)
                .environment(\.colorScheme, .light)
                .environment(\.locale, Locale(identifier: "en_US"))
                .background(Color(nsColor: .windowBackgroundColor))
        )
        renderer.scale = 3
        return try XCTUnwrap(renderer.cgImage)
    }

    private func recognize(_ image: CGImage) throws -> [(text: String, bounds: CGRect)] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .fast
        request.usesLanguageCorrection = false
        try VNImageRequestHandler(cgImage: image).perform([request])
        return try XCTUnwrap(request.results).map {
            (try XCTUnwrap($0.topCandidates(1).first).string, $0.boundingBox)
        }
    }

    private func hasRedPixels(_ image: CGImage) throws -> Bool {
        let bitmap = NSBitmapImageRep(cgImage: image)
        for y in 0..<bitmap.pixelsHigh {
            for x in 0..<bitmap.pixelsWide {
                let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB))
                if color.redComponent > 0.7 && color.greenComponent < 0.4 && color.blueComponent < 0.4 { return true }
            }
        }
        return false
    }
}
