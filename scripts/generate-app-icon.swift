import AppKit
import Foundation

func iconError(_ message: String) -> NSError {
    NSError(domain: "ReviewQIcon", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
}

func renderIcon(pixels: Int) throws -> Data {
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw iconError("Could not create the \(pixels)-pixel icon canvas.")
    }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    defer { NSGraphicsContext.restoreGraphicsState() }
    context.imageInterpolation = .high
    context.cgContext.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)

    let tile = NSBezierPath(
        roundedRect: NSRect(x: 64, y: 64, width: 896, height: 896),
        xRadius: 200, yRadius: 200
    )
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
    shadow.shadowBlurRadius = 28
    shadow.shadowOffset = NSSize(width: 0, height: -12)
    shadow.set()
    NSColor(calibratedRed: 0.06, green: 0.27, blue: 0.60, alpha: 1).setFill()
    tile.fill()
    NSGraphicsContext.restoreGraphicsState()
    guard let gradient = NSGradient(
        starting: NSColor(calibratedRed: 0.16, green: 0.56, blue: 0.97, alpha: 1),
        ending: NSColor(calibratedRed: 0.05, green: 0.22, blue: 0.58, alpha: 1)
    ) else { throw iconError("Could not create the icon gradient.") }
    gradient.draw(in: tile, angle: 270)
    NSColor.white.withAlphaComponent(0.18).setStroke()
    tile.lineWidth = 3
    tile.stroke()

    NSColor.white.setStroke()
    let main = NSBezierPath()
    main.move(to: NSPoint(x: 330, y: 350))
    main.line(to: NSPoint(x: 330, y: 674))
    main.lineWidth = 64
    main.lineCapStyle = .round
    main.stroke()

    let branch = NSBezierPath()
    branch.move(to: NSPoint(x: 710, y: 350))
    branch.line(to: NSPoint(x: 710, y: 526))
    branch.curve(
        to: NSPoint(x: 550, y: 728),
        controlPoint1: NSPoint(x: 710, y: 650), controlPoint2: NSPoint(x: 650, y: 728)
    )
    branch.line(to: NSPoint(x: 510, y: 728))
    branch.lineWidth = 64
    branch.lineCapStyle = .round
    branch.stroke()

    let arrow = NSBezierPath()
    arrow.move(to: NSPoint(x: 570, y: 812))
    arrow.line(to: NSPoint(x: 486, y: 728))
    arrow.line(to: NSPoint(x: 570, y: 644))
    arrow.lineWidth = 64
    arrow.lineCapStyle = .round
    arrow.lineJoinStyle = .round
    arrow.stroke()

    for center in [NSPoint(x: 330, y: 750), NSPoint(x: 330, y: 274), NSPoint(x: 710, y: 274)] {
        if center.x == 710 {
            NSColor(calibratedRed: 0.47, green: 0.97, blue: 0.91, alpha: 1).setStroke()
        }
        let node = NSBezierPath(ovalIn: NSRect(x: center.x - 76, y: center.y - 76, width: 152, height: 152))
        node.lineWidth = 58
        node.stroke()
    }
    guard let png = bitmap.representation(using: .png, properties: [:]) else {
        throw iconError("Could not encode the \(pixels)-pixel icon.")
    }
    return png
}

func generateIcon() throws {
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
    let output = root.appendingPathComponent("Support/AppIcon.icns")
    let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("ReviewQ-\(UUID()).iconset")
    try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: false)
    defer {
        do { try FileManager.default.removeItem(at: iconset) }
        catch { fputs("Could not remove the temporary iconset: \(error.localizedDescription)\n", stderr) }
    }
    for size in [16, 32, 128, 256, 512] {
        for scale in [1, 2] {
            let suffix = scale == 1 ? "" : "@2x"
            let file = iconset.appendingPathComponent("icon_\(size)x\(size)\(suffix).png")
            try renderIcon(pixels: size * scale).write(to: file)
        }
    }
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
    process.arguments = ["--convert", "icns", "--output", output.path, iconset.path]
    try process.run()
    process.waitUntilExit()
    guard process.terminationStatus == 0 else { throw iconError("iconutil could not compile AppIcon.icns.") }
    print("Generated \(output.path)")
}

do { try generateIcon() }
catch {
    fputs("Icon generation failed: \(error.localizedDescription)\n", stderr)
    exit(1)
}
