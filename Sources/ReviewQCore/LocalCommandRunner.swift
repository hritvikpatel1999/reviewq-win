import Foundation

public protocol CommandRunning: Sendable {
    func run(executable: URL, arguments: [String]) async throws -> Data
}

public enum LocalCommandError: Error, Equatable, Sendable {
    case unavailable, timeout, failed(Int32)
}

public struct LocalCommandRunner: CommandRunning {
    public var timeout: TimeInterval
    public var environment: [String: String]?

    public init(timeout: TimeInterval = 30, environment: [String: String]? = nil) {
        self.timeout = timeout
        self.environment = environment
    }

    public func run(executable: URL, arguments: [String]) async throws -> Data {
        try Task.checkCancellation()
        let process = Process()
        let output = Pipe()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        let timeout = timeout
        let task = Task.detached(priority: .utility) {
            try Task.checkCancellation()
            do {
                try process.run()
            } catch {
                throw LocalCommandError.unavailable
            }
            let deadline = Date().addingTimeInterval(timeout)
            let timeoutAction = DispatchWorkItem {
                if process.isRunning { process.terminate() }
            }
            DispatchQueue.global(qos: .utility).asyncAfter(
                deadline: .now() + timeout, execute: timeoutAction
            )
            defer {
                timeoutAction.cancel()
                if process.isRunning {
                    process.terminate()
                    process.waitUntilExit()
                }
                output.fileHandleForReading.closeFile()
            }
            if Task.isCancelled, process.isRunning { process.terminate() }
            let data = try output.fileHandleForReading.readToEnd() ?? Data()
            process.waitUntilExit()
            try Task.checkCancellation()
            if Date() >= deadline { throw LocalCommandError.timeout }
            guard process.terminationReason == .exit, process.terminationStatus == 0 else {
                throw LocalCommandError.failed(process.terminationStatus)
            }
            return data
        }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
            if process.isRunning { process.terminate() }
        }
    }
}
