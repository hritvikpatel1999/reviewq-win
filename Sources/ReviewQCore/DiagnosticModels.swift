import Foundation

public enum DiagnosticContext {
    @TaskLocal public static var cycleID: UUID?
}

public enum DiagnosticLevel: String, Codable, Sendable {
    case debug, info, warning, error

    var isProblem: Bool { self == .warning || self == .error }
}

public enum DiagnosticEvent: String, Codable, Sendable {
    case startup, shutdown, previousSessionUnclean, appReopened, sleep, wake
    case verboseEnabled, verboseDisabled, logsCleared, logsEvicted
    case logsExportFailed, logsClearFailed, logsFolderFailed
    case authenticationStarted, authenticationFinished, authenticationFailed, authenticationCancelled
    case refreshStarted, refreshFinished, refreshFailed, refreshCancelled
    case requestStarted, requestFinished, requestFailed, requestCancelled
    case identityCacheHit, identityCacheMiss, identityCacheCleared, identityCacheInvalidated
    case settingsSaved, settingsFailed, patDeleted, patDeleteFailed
    case loginItemChanged, loginItemFailed, browserOpenFailed, applicationActionFailed, diagnosticsOpened
    case reviewLaunchStarted, reviewLaunchFinished, reviewLaunchFailed
    case shortcutRegistered, shortcutPressed, shortcutRegistrationFailed, shortcutUnregistrationFailed
}

public enum DiagnosticOperation: String, Codable, Sendable {
    case identity, followedIdentity, identityCache, projects, pullRequests, reviewHistory, reviewerPolicies, reviews
    case azureCLI, personalAccessToken, settings, loginItem, browser, application
    case scout, copilot
    case shortcut
}

public enum DiagnosticReason: String, Codable, Sendable {
    case cancelled, superseded, offline, timeout, network, unauthorized, forbidden, notFound
    case rateLimited, serviceUnavailable, http, decoding, invalidResponse, incompleteHistory
    case invalidConfiguration, corruptSettings, missingPAT, invalidPAT, keychain
    case cliUnavailable, cliFailure, invalidToken, system, storage, unsafePath
    case sizeLimit, retention, malformedRecord, approvalPending
}

public struct DiagnosticFailure: Sendable {
    public let reason: DiagnosticReason
    public var status: Int?
    public var systemCode: Int?

    public init(_ error: any Error) {
        if error is CancellationError {
            reason = .cancelled
        } else if let error = error as? URLError {
            reason = Self.transportReason(error.code.rawValue)
            systemCode = error.code.rawValue
        } else if error is DecodingError {
            reason = .decoding
        } else if let error = error as? ReviewQError {
            switch error {
            case .noSources, .invalidSource, .duplicateSource, .invalidTenant, .reviewPromptTooLong, .invalidReviewPrompt,
                 .invalidFollowedAuthorEmails, .unresolvedFollowedAuthors:
                reason = .invalidConfiguration
            case .missingPAT: reason = .missingPAT
            case .invalidPAT: reason = .invalidPAT
            case .keychain(let code):
                reason = .keychain
                systemCode = Int(code)
            case .corruptSettings: reason = .corruptSettings
            case .cliUnavailable: reason = .cliUnavailable
            case .cliFailure(let code):
                reason = .cliFailure
                systemCode = Int(code)
            case .cliTimeout: reason = .timeout
            case .invalidCLIResponse: reason = .invalidToken
            case .invalidResponse: reason = .invalidResponse
            case .invalidPagination: reason = .incompleteHistory
            case .http(let code, _):
                status = code
                switch code {
                case 401: reason = .unauthorized
                case 403: reason = .forbidden
                case 404: reason = .notFound
                case 429: reason = .rateLimited
                case 500..<600: reason = .serviceUnavailable
                default: reason = .http
                }
            case .network(_, _, let code):
                reason = code.map(Self.transportReason) ?? .network
                systemCode = code
            }
        } else if let error = error as? DiagnosticStorageError {
            reason = error == .unsafePath ? .unsafePath : .storage
        } else {
            reason = .system
        }
    }

    private static func transportReason(_ code: Int) -> DiagnosticReason {
        switch URLError.Code(rawValue: code) {
        case .cancelled: .cancelled
        case .timedOut: .timeout
        case .notConnectedToInternet: .offline
        default: .network
        }
    }
}

public struct DiagnosticRecord: Codable, Sendable {
    public let timestamp: Date
    public let sessionID: UUID
    public let level: DiagnosticLevel
    public let event: DiagnosticEvent
    public var operation: DiagnosticOperation?
    public var requestID: UUID?
    public var cycleID: UUID?
    public var reason: DiagnosticReason?
    public var status: Int?
    public var durationMilliseconds: Int?
    public var responseBytes: Int?
    public var page: Int?
    public var count: Int?
    public var systemCode: Int?
    public var metadata: DiagnosticMetadata?
}

public struct DiagnosticMetadata: Codable, Sendable {
    public let version: String
    public let build: String
    public let macOS: String
    public let architecture: String

    public static var current: Self {
        #if arch(arm64)
        let architecture = "arm64"
        #elseif arch(x86_64)
        let architecture = "x86_64"
        #else
        let architecture = "unknown"
        #endif
        let os = ProcessInfo.processInfo.operatingSystemVersion
        return Self(
            version: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown",
            build: Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "unknown",
            macOS: "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)", architecture: architecture
        )
    }
}

public enum DiagnosticStorageError: LocalizedError, Sendable {
    case unavailable, unsafePath, exportFailed, cleanupFailed, entryTooLarge

    public var errorDescription: String? {
        switch self {
        case .unavailable:
            "Local diagnostics are unavailable. Check disk space and permissions for Library/Logs/PRBar."
        case .unsafePath:
            "The diagnostic location is not a regular local folder or file. Check Library/Logs/PRBar."
        case .exportFailed:
            "Logs could not be exported. Choose a writable local ZIP destination with enough free space."
        case .cleanupFailed:
            "Temporary diagnostic export files could not be removed. Check temporary-folder permissions and retry."
        case .entryTooLarge:
            "A diagnostic entry exceeded the storage budget and was not recorded."
        }
    }
}

enum DiagnosticTiming {
    static func milliseconds(since start: ContinuousClock.Instant) -> Int {
        let components = start.duration(to: ContinuousClock.now).components
        return Int(components.seconds * 1_000 + components.attoseconds / 1_000_000_000_000_000)
    }
}
