import Darwin
import Foundation
import OSLog

// Mutable storage state is confined to the serial worker.
public final class DiagnosticLogger: @unchecked Sendable {
    public static let verboseKey = "verboseLoggingEnabled"
    public static let disabled = DiagnosticLogger(directory: nil)
    public static let filenames = ["warnings-errors.log", "verbose.log"]
    public static var defaultDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/PRBar")
    }

    public let directory: URL?
    private let queue = DispatchQueue(label: "dev.prbar.diagnostics", qos: .utility)
    private let sessionID = UUID()
    private let maxFileBytes: Int
    private let retention: TimeInterval
    private let clock: @Sendable () -> Date
    private var verboseEnabled: Bool
    private var failure: DiagnosticStorageError?
    private var maintenanceAt = Date.distantPast
    private var startupMetadata: DiagnosticMetadata?
    private var bytes = 0
    private let systemLogger = Logger(subsystem: "dev.prbar.app", category: "diagnostics")

    public init(
        directory: URL?, verboseEnabled: Bool = false, maxFileBytes: Int = 5 * 1024 * 1024,
        retention: TimeInterval = 7 * 24 * 3600, clock: @escaping @Sendable () -> Date = { .now }
    ) {
        precondition(maxFileBytes >= 2048 && retention > 0)
        self.directory = directory
        self.verboseEnabled = verboseEnabled
        self.maxFileBytes = maxFileBytes
        self.retention = retention
        self.clock = clock
    }

    public var error: DiagnosticStorageError? { queue.sync { failure } }
    public var storedBytes: Int { queue.sync { bytes } }

    public func inspect() -> (bytes: Int, error: DiagnosticStorageError?) {
        queue.sync {
            guard let directory else { return (0, nil) }
            perform {
                try validateLocation(directory)
                try validateNode(directory, isDirectory: true, allowMissing: false, requireOwner: true)
                for name in Self.filenames {
                    try validateNode(
                        directory.appendingPathComponent(name),
                        isDirectory: false, allowMissing: false, requireOwner: true
                    )
                }
                try updateBytes()
            }
            return (bytes, failure)
        }
    }

    public func log(
        _ level: DiagnosticLevel, _ event: DiagnosticEvent, operation: DiagnosticOperation? = nil,
        requestID: UUID? = nil, cycleID: UUID? = DiagnosticContext.cycleID,
        reason: DiagnosticReason? = nil, status: Int? = nil, durationMilliseconds: Int? = nil,
        responseBytes: Int? = nil, page: Int? = nil, count: Int? = nil, systemCode: Int? = nil
    ) {
        guard directory != nil else { return }
        let record = DiagnosticRecord(
            timestamp: clock(), sessionID: sessionID, level: level, event: event,
            operation: operation, requestID: requestID, cycleID: cycleID, reason: reason,
            status: status, durationMilliseconds: durationMilliseconds, responseBytes: responseBytes,
            page: page, count: count, systemCode: systemCode
        )
        queue.async { [self] in perform { try write(record) } }
    }

    public func logFailure(
        _ event: DiagnosticEvent, error: any Error, operation: DiagnosticOperation? = nil,
        cycleID: UUID? = DiagnosticContext.cycleID
    ) {
        let issue = DiagnosticFailure(error)
        log(
            issue.reason == .cancelled ? .info : .warning, event, operation: operation,
            cycleID: cycleID, reason: issue.reason, status: issue.status, systemCode: issue.systemCode
        )
    }

    public func start(metadata: DiagnosticMetadata = .current, previousSessionUnclean: Bool = false) {
        queue.async { [self] in
            startupMetadata = metadata
            perform {
                try writeStartup()
                if previousSessionUnclean {
                    try write(DiagnosticRecord(
                        timestamp: clock(), sessionID: sessionID, level: .warning, event: .previousSessionUnclean
                    ))
                }
            }
        }
    }

    public func setVerboseEnabled(_ value: Bool) {
        queue.async { [self] in
            let previous = verboseEnabled
            defer { verboseEnabled = value }
            perform {
                if !value && previous {
                    try write(DiagnosticRecord(
                        timestamp: clock(), sessionID: sessionID, level: .info, event: .verboseDisabled
                    ))
                }
                verboseEnabled = value
                if value {
                    try writeStartup()
                    try write(DiagnosticRecord(
                        timestamp: clock(), sessionID: sessionID, level: .info, event: .verboseEnabled
                    ))
                }
            }
        }
    }

    public func flush() {
        queue.sync {
            guard let directory, failure == nil else { return }
            perform {
                for name in Self.filenames {
                    let url = directory.appendingPathComponent(name)
                    try validateNode(url, isDirectory: false, allowMissing: false, requireOwner: true)
                    let handle = try FileHandle(forWritingTo: url)
                    defer { handle.closeFile() }
                    try handle.synchronize()
                }
            }
        }
    }

    public func prepareFolder() throws -> URL {
        guard directory != nil else { throw DiagnosticStorageError.unavailable }
        return try queue.sync {
            do {
                guard let directory else { throw DiagnosticStorageError.unavailable }
                try prepare()
                failure = nil
                return directory
            } catch {
                reportFailure(error)
                throw failure ?? .unavailable
            }
        }
    }

    public func clear() throws {
        guard directory != nil else { throw DiagnosticStorageError.unavailable }
        try queue.sync {
            do {
                guard let directory else { throw DiagnosticStorageError.unavailable }
                try prepare()
                for name in Self.filenames {
                    let url = directory.appendingPathComponent(name)
                    try Data().write(to: url, options: .atomic)
                    try protect(url)
                }
                maintenanceAt = .distantPast
                try writeStartup()
                try write(DiagnosticRecord(
                    timestamp: clock(), sessionID: sessionID, level: .info, event: .logsCleared
                ))
                try updateBytes()
                failure = nil
            } catch {
                reportFailure(error)
                throw failure ?? .unavailable
            }
        }
    }

    public func export(to destination: URL) throws {
        let temporary = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("ReviewQ-diagnostics-\(UUID())")
        var outcome: Result<Void, DiagnosticStorageError>
        do {
            try validateLocation(destination.deletingLastPathComponent())
            try validateNode(destination, isDirectory: false, allowMissing: true, requireOwner: true)
            guard destination.isFileURL, destination.pathExtension.lowercased() == "zip" else {
                throw DiagnosticStorageError.exportFailed
            }
            let manager = FileManager.default
            try manager.createDirectory(
                at: temporary, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700]
            )
            let snapshot = temporary.appendingPathComponent("ReviewQ-Logs")
            try manager.createDirectory(
                at: snapshot, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700]
            )
            try queue.sync {
                guard let directory else { throw DiagnosticStorageError.unavailable }
                try prepare()
                try maintain(force: true)
                for name in Self.filenames {
                    let target = snapshot.appendingPathComponent(name)
                    try manager.copyItem(at: directory.appendingPathComponent(name), to: target)
                    try protect(target)
                }
                let encoder = JSONEncoder()
                encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                let summary = snapshot.appendingPathComponent("diagnostic-summary.json")
                try encoder.encode(startupMetadata ?? .current).write(to: summary, options: .atomic)
                try protect(summary)
                try updateBytes()
            }
            let archive = temporary.appendingPathComponent("Logs.zip")
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            process.arguments = ["-c", "-k", "--keepParent", snapshot.path, archive.path]
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            try process.run()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else { throw DiagnosticStorageError.exportFailed }
            try Data(contentsOf: archive).write(to: destination, options: .atomic)
            try protect(destination)
            outcome = .success(())
        } catch {
            outcome = .failure(error as? DiagnosticStorageError ?? .exportFailed)
        }
        do {
            if FileManager.default.fileExists(atPath: temporary.path) {
                try FileManager.default.removeItem(at: temporary)
            }
        } catch {
            systemLogger.error("Temporary diagnostic export cleanup failed.")
            outcome = .failure(.cleanupFailed)
        }
        if case .failure(let error) = outcome {
            logFailure(.logsExportFailed, error: error)
            throw error
        }
    }

    private func perform(_ action: () throws -> Void) {
        do { try action() } catch { reportFailure(error) }
    }

    private func reportFailure(_ error: any Error) {
        failure = error as? DiagnosticStorageError ?? .unavailable
        systemLogger.error("Local diagnostics are unavailable. Open ReviewQ Diagnostics to retry.")
    }

    private func validateNode(
        _ url: URL, isDirectory: Bool, allowMissing: Bool, requireOwner: Bool = false
    ) throws {
        guard url.isFileURL, !url.path.utf8.contains(0) else { throw DiagnosticStorageError.unsafePath }
        var info = stat()
        if lstat(url.path, &info) != 0 {
            if errno == ENOENT && allowMissing { return }
            throw DiagnosticStorageError.unavailable
        }
        // macOS's root-owned system aliases are trusted; user-controlled symlinks are not.
        if isDirectory, !requireOwner, info.st_uid == 0, info.st_mode & S_IFMT == S_IFLNK,
           ["/var", "/tmp", "/etc"].contains(url.path) {
            let link = try FileManager.default.destinationOfSymbolicLink(atPath: url.path)
            let target = link.hasPrefix("/")
                ? URL(fileURLWithPath: link) : url.deletingLastPathComponent().appendingPathComponent(link)
            var targetInfo = stat()
            guard target.path == "/private" + url.path, lstat(target.path, &targetInfo) == 0,
                  targetInfo.st_mode & S_IFMT == S_IFDIR, targetInfo.st_uid == 0 else {
                throw DiagnosticStorageError.unsafePath
            }
            return
        }
        let expected = isDirectory ? S_IFDIR : S_IFREG
        guard info.st_mode & S_IFMT == expected, !requireOwner || info.st_uid == getuid() else {
            throw DiagnosticStorageError.unsafePath
        }
    }

    private func validateLocation(_ url: URL) throws {
        guard url.isFileURL, url.path != "/" else { throw DiagnosticStorageError.unsafePath }
        var ancestor = url
        while ancestor.path != "/" {
            try validateNode(ancestor, isDirectory: true, allowMissing: true)
            ancestor = ancestor.deletingLastPathComponent()
        }
    }

    private func prepare() throws {
        guard let directory else { return }
        try validateLocation(directory)
        try validateNode(directory, isDirectory: true, allowMissing: true, requireOwner: true)
        let manager = FileManager.default
        try manager.createDirectory(
            at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700]
        )
        try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        for name in Self.filenames {
            let url = directory.appendingPathComponent(name)
            try validateNode(url, isDirectory: false, allowMissing: true, requireOwner: true)
            if !manager.fileExists(atPath: url.path) {
                guard manager.createFile(atPath: url.path, contents: Data(), attributes: [.posixPermissions: 0o600])
                else { throw DiagnosticStorageError.unavailable }
            }
            try protect(url)
        }
    }

    private func protect(_ url: URL) throws {
        try validateNode(url, isDirectory: false, allowMissing: false, requireOwner: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private func encode(_ record: DiagnosticRecord) throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        var data = try encoder.encode(record)
        data.append(0x0a)
        return data
    }

    private func writeStartup() throws {
        if let startupMetadata {
            try write(DiagnosticRecord(
                timestamp: clock(), sessionID: sessionID, level: .info, event: .startup, metadata: startupMetadata
            ), startup: true)
        } else {
            try prepare()
        }
    }

    private func write(_ record: DiagnosticRecord, startup: Bool = false) throws {
        guard directory != nil, startup || record.level.isProblem || verboseEnabled else { return }
        try prepare()
        try maintain()
        let data = try encode(record)
        if startup || record.level.isProblem { try append(data, name: Self.filenames[0]) }
        if startup || verboseEnabled { try append(data, name: Self.filenames[1]) }
        try updateBytes()
        failure = nil
    }

    private func maintain(force: Bool = false) throws {
        guard let directory, force || clock().timeIntervalSince(maintenanceAt) >= 3600 else { return }
        let cutoff = clock().addingTimeInterval(-retention)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        var notices: [DiagnosticRecord] = []
        for name in Self.filenames {
            let url = directory.appendingPathComponent(name)
            let size = try fileSize(url)
            let handle = try FileHandle(forReadingFrom: url)
            defer { handle.closeFile() }
            let offset = max(0, size - maxFileBytes)
            try handle.seek(toOffset: UInt64(offset))
            let data = try handle.read(upToCount: maxFileBytes) ?? Data()
            let lines = data.split(separator: 0x0a)
            let complete = offset > 0 ? lines.dropFirst() : lines[...]
            var kept = Data()
            var expired = 0
            var malformed = 0
            for line in complete {
                guard let record = try? decoder.decode(DiagnosticRecord.self, from: Data(line)) else {
                    malformed += 1
                    continue
                }
                if record.timestamp < cutoff { expired += 1; continue }
                kept.append(try encode(record))
            }
            if force || kept != data {
                try kept.write(to: url, options: .atomic)
                try protect(url)
            }
            for (reason, count) in [(DiagnosticReason.retention, expired), (.malformedRecord, malformed)]
                where count > 0 {
                notices.append(DiagnosticRecord(
                    timestamp: clock(), sessionID: sessionID, level: .warning, event: .logsEvicted,
                    reason: reason, count: count
                ))
            }
            if offset > 0 {
                notices.append(DiagnosticRecord(
                    timestamp: clock(), sessionID: sessionID, level: .warning, event: .logsEvicted, reason: .sizeLimit
                ))
            }
        }
        maintenanceAt = clock()
        for notice in notices {
            let data = try encode(notice)
            try append(data, name: Self.filenames[0])
            if verboseEnabled { try append(data, name: Self.filenames[1]) }
        }
    }

    private func append(_ data: Data, name: String) throws {
        guard let directory else { return }
        guard data.count <= maxFileBytes - 1024 else { throw DiagnosticStorageError.entryTooLarge }
        let url = directory.appendingPathComponent(name)
        let size = try fileSize(url)
        if size + data.count > maxFileBytes {
            let handle = try FileHandle(forReadingFrom: url)
            defer { handle.closeFile() }
            let offset = max(0, size - maxFileBytes)
            try handle.seek(toOffset: UInt64(offset))
            let contents = try handle.read(upToCount: maxFileBytes) ?? Data()
            var kept = Data()
            let budget = maxFileBytes - data.count - 1024
            let lines = contents.split(separator: 0x0a)
            let complete = offset > 0 ? lines.dropFirst() : lines[...]
            for line in complete.reversed() {
                guard kept.count + line.count + 1 <= budget else { break }
                var complete = Data(line)
                complete.append(0x0a)
                kept.insert(contentsOf: complete, at: 0)
            }
            try kept.write(to: url, options: .atomic)
            try protect(url)
            let notice = try encode(DiagnosticRecord(
                timestamp: clock(), sessionID: sessionID, level: .warning, event: .logsEvicted, reason: .sizeLimit
            ))
            try appendDirect(notice, to: url)
            if name == Self.filenames[1] { try append(notice, name: Self.filenames[0]) }
        }
        try appendDirect(data, to: url)
    }

    private func appendDirect(_ data: Data, to url: URL) throws {
        let descriptor = Darwin.open(url.path, O_WRONLY | O_APPEND | O_NOFOLLOW | O_NONBLOCK)
        guard descriptor >= 0 else {
            throw errno == ELOOP ? DiagnosticStorageError.unsafePath : .unavailable
        }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        defer { handle.closeFile() }
        try handle.write(contentsOf: data)
    }

    private func updateBytes() throws {
        guard let directory else { return }
        bytes = try Self.filenames.reduce(0) {
            $0 + (try fileSize(directory.appendingPathComponent($1)))
        }
    }

    private func fileSize(_ url: URL) throws -> Int {
        guard let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size >= 0 else {
            throw DiagnosticStorageError.unavailable
        }
        return size
    }
}
