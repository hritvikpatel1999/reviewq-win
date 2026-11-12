// swift-tools-version: 6.2

import PackageDescription

let package = Package(
    name: "ReviewQ",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "ReviewQ", targets: ["ReviewQ"])
    ],
    targets: [
        .target(name: "ReviewQCore"),
        .executableTarget(name: "ReviewQ", dependencies: ["ReviewQCore"]),
        .testTarget(name: "ReviewQCoreTests", dependencies: ["ReviewQCore"]),
        .testTarget(name: "ReviewQTests", dependencies: ["ReviewQ", "ReviewQCore"])
    ]
)
