import Foundation
import Security

public protocol PATStoring: Sendable {
    func load() throws -> String?
    func save(_ token: String) throws
    func remove() throws
}

public struct KeychainPATStore: PATStoring {
    private let service: String

    public init(service: String = "dev.prbar.app.azure-devops") {
        self.service = service
    }

    private var query: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: "personal-access-token",
            kSecAttrSynchronizable as String: false
        ]
    }

    public func load() throws -> String? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw ReviewQError.keychain(status) }
        guard let data = result as? Data, let token = String(data: data, encoding: .utf8) else {
            throw ReviewQError.invalidPAT
        }
        return token
    }

    public func save(_ token: String) throws {
        let token = try Self.validate(token)
        let attributes = [kSecValueData as String: Data(token.utf8)]
        let status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var request = query
            request[kSecValueData as String] = Data(token.utf8)
            request[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            let addStatus = SecItemAdd(request as CFDictionary, nil)
            guard addStatus == errSecSuccess else { throw ReviewQError.keychain(addStatus) }
        } else if status != errSecSuccess {
            throw ReviewQError.keychain(status)
        }
    }

    public func remove() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw ReviewQError.keychain(status)
        }
    }

    public static func validate(_ value: String) throws -> String {
        let token = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !token.isEmpty else { throw ReviewQError.missingPAT }
        guard token.unicodeScalars.allSatisfy({ (33...126).contains($0.value) }) else {
            throw ReviewQError.invalidPAT
        }
        return token
    }
}

protocol AuthorizationProviding: Sendable {
    func authorization(for configuration: ReviewConfiguration) async throws -> String
    func invalidate() async
}

struct AzureCLICommandRunner: CommandRunning {
    var timeout: TimeInterval = 30

    func run(executable: URL, arguments: [String]) async throws -> Data {
        do {
            return try await LocalCommandRunner(timeout: timeout).run(
                executable: executable, arguments: arguments
            )
        } catch let error as LocalCommandError {
            switch error {
            case .unavailable: throw ReviewQError.cliUnavailable
            case .timeout: throw ReviewQError.cliTimeout
            case .failed(let code): throw ReviewQError.cliFailure(code)
            }
        }
    }
}

actor AzureDevOpsCredentials: AuthorizationProviding {
    static let resource = "499b84ac-1321-427f-aa17-267ca6975798"
    private let keychain: any PATStoring
    private let runner: any CommandRunning
    private let executable: URL?
    private let diagnostics: DiagnosticLogger
    private var cachedToken: CLIToken?
    private var cachedTenant = ""

    init(
        keychain: any PATStoring = KeychainPATStore(),
        runner: any CommandRunning = AzureCLICommandRunner(),
        executable: URL? = nil, diagnostics: DiagnosticLogger = .disabled
    ) {
        self.keychain = keychain
        self.runner = runner
        self.executable = executable
        self.diagnostics = diagnostics
    }

    static func findAzureCLI() -> URL? {
        let paths = ["/opt/homebrew/bin/az", "/usr/local/bin/az"]
            + (ProcessInfo.processInfo.environment["PATH"] ?? "")
                .split(separator: ":").map { "\($0)/az" }
        return paths.first { FileManager.default.isExecutableFile(atPath: $0) }
            .map { URL(fileURLWithPath: $0) }
    }

    func authorization(for configuration: ReviewConfiguration) async throws -> String {
        let operation: DiagnosticOperation = configuration.authentication == .azureCLI
            ? .azureCLI : .personalAccessToken
        diagnostics.log(.debug, .authenticationStarted, operation: operation)
        do {
            let header = try await authorizationValue(for: configuration)
            diagnostics.log(.info, .authenticationFinished, operation: operation)
            return header
        } catch {
            let event: DiagnosticEvent = DiagnosticFailure(error).reason == .cancelled
                ? .authenticationCancelled : .authenticationFailed
            diagnostics.logFailure(event, error: error, operation: operation)
            throw error
        }
    }

    private func authorizationValue(for configuration: ReviewConfiguration) async throws -> String {
        if configuration.authentication == .personalAccessToken {
            cachedToken = nil
            cachedTenant = ""
            guard let saved = try keychain.load() else { throw ReviewQError.missingPAT }
            let token = try KeychainPATStore.validate(saved)
            return "Basic " + Data(":\(token)".utf8).base64EncodedString()
        }
        let tenant = configuration.tenantID.trimmingCharacters(in: .whitespacesAndNewlines)
        if let cachedToken, cachedTenant == tenant,
           cachedToken.expiresAt > Date().addingTimeInterval(120) {
            return "Bearer \(cachedToken.value)"
        }
        guard let executable = executable ?? Self.findAzureCLI() else { throw ReviewQError.cliUnavailable }
        var arguments = [
            "account", "get-access-token", "--resource", Self.resource, "--output", "json"
        ]
        if !tenant.isEmpty { arguments += ["--tenant", tenant] }
        let data = try await runner.run(executable: executable, arguments: arguments)
        let token = try CLIToken.decode(data)
        cachedToken = token
        cachedTenant = tenant
        return "Bearer \(token.value)"
    }

    func invalidate() {
        cachedToken = nil
        cachedTenant = ""
    }
}

struct CLIToken: Sendable {
    let value: String
    let expiresAt: Date

    static func decode(_ data: Data) throws -> CLIToken {
        let envelope: TokenEnvelope
        do {
            envelope = try JSONDecoder().decode(TokenEnvelope.self, from: data)
        } catch {
            throw ReviewQError.invalidCLIResponse
        }
        guard !envelope.accessToken.isEmpty,
              !envelope.accessToken.contains(where: { $0.isWhitespace }) else {
            throw ReviewQError.invalidCLIResponse
        }
        let expiresAt: Date
        if let epoch = envelope.expires_on {
            expiresAt = Date(timeIntervalSince1970: TimeInterval(epoch.value))
        } else if let legacy = envelope.expiresOn {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSSSSS"
            formatter.timeZone = .current
            guard let date = formatter.date(from: legacy) else {
                throw ReviewQError.invalidCLIResponse
            }
            expiresAt = date
        } else {
            throw ReviewQError.invalidCLIResponse
        }
        guard expiresAt > Date().addingTimeInterval(30) else {
            throw ReviewQError.invalidCLIResponse
        }
        return CLIToken(value: envelope.accessToken, expiresAt: expiresAt)
    }

    private struct TokenEnvelope: Decodable {
        let accessToken: String
        let expires_on: Epoch?
        let expiresOn: String?
    }

    private struct Epoch: Decodable {
        let value: Int64

        init(from decoder: any Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let number = try? container.decode(Int64.self) {
                value = number
            } else {
                let text = try container.decode(String.self)
                guard let number = Int64(text) else {
                    throw ReviewQError.invalidCLIResponse
                }
                value = number
            }
        }
    }
}
