import AppKit
import ImageIO
import XCTest

@MainActor
final class ApplicationIconTests: XCTestCase {
    private let root = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

    private func iconName() throws -> String {
        let data = try Data(contentsOf: root.appendingPathComponent("Support/Info.plist"))
        let plist = try XCTUnwrap(
            PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]
        )
        return try XCTUnwrap(plist["CFBundleIconFile"] as? String)
    }

    func testBundleDeclaresADecodableIconAtStandardAndRetinaSizes() throws {
        let name = try iconName()
        XCTAssertEqual(name, "AppIcon")
        let url = root.appendingPathComponent("Support/\(name).icns")
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        var sizes: Set<Int> = []
        for index in 0..<CGImageSourceGetCount(source) {
            let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, index, nil))
            XCTAssertEqual(image.width, image.height)
            sizes.insert(image.width)
        }
        XCTAssertTrue(Set([16, 32, 64, 128, 256, 512, 1024]).isSubset(of: sizes), "\(sizes)")
    }

    func testPackagedBundleResolvesTheDeclaredIcon() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ReviewQ-icon-tests-\(UUID())")
        defer {
            do { try FileManager.default.removeItem(at: directory) }
            catch { XCTFail("Could not clean up the temporary icon test bundle: \(error)") }
        }
        let app = directory.appendingPathComponent("ReviewQ.app")
        let resources = app.appendingPathComponent("Contents/Resources")
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true)
        try FileManager.default.copyItem(
            at: root.appendingPathComponent("Support/Info.plist"),
            to: app.appendingPathComponent("Contents/Info.plist")
        )
        let name = try iconName()
        let packagedIcon = resources.appendingPathComponent("\(name).icns")
        try FileManager.default.copyItem(at: root.appendingPathComponent("Support/\(name).icns"), to: packagedIcon)
        let bundle = try XCTUnwrap(Bundle(url: app))
        XCTAssertEqual(bundle.urlForImageResource(name)?.standardizedFileURL, packagedIcon.standardizedFileURL)
        XCTAssertNotNil(NSImage(contentsOf: packagedIcon))
    }
}
