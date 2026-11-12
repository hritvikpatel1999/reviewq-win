import CryptoKit
import Foundation
import OSLog

// State and preference access are confined to the serial queue.
final class IdentityCache: @unchecked Sendable {
    static let settingsKey = "reviewIdentityCache"
    static let positiveLifetime: TimeInterval = 7 * 24 * 3600
    static let negativeLifetime: TimeInterval = 15 * 60
    static let maximumEntries = 512

    struct Context: Sendable {
        fileprivate let scope: String
        fileprivate let generation: UUID
    }

    enum Lookup: Equatable, Sendable {
        case missing, notFound, identity(UUID)
    }

    private enum Kind: String, Codable {
        case currentUser, followedAuthor
    }

    private struct Key: Hashable {
        let kind: Kind
        let organization: String
        let email: String?
    }

    private struct Entry: Codable {
        let kind: Kind
        let organization: String
        let email: String?
        let identity: UUID?
        let resolvedAt: Date

        var key: Key { Key(kind: kind, organization: organization, email: email) }
        var orderingKey: String { "\(kind.rawValue)/\(organization)/\(email ?? "")" }

        func isFresh(at now: Date) -> Bool {
            let age = now.timeIntervalSince(resolvedAt)
            let lifetime = identity == nil ? IdentityCache.negativeLifetime : IdentityCache.positiveLifetime
            return age >= 0 && age < lifetime
        }

        var isValid: Bool {
            guard !organization.isEmpty, organization == organization.lowercased(),
                  organization.utf8.count <= 256, resolvedAt.timeIntervalSince1970.isFinite,
                  identity != UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
            else { return false }
            switch kind {
            case .currentUser:
                return email == nil && identity != nil
            case .followedAuthor:
                guard let email, let parsed = try? ReviewConfiguration.parseFollowedAuthorEmails(email) else {
                    return false
                }
                return parsed == [email]
            }
        }
    }

    private struct Storage: Codable {
        let version: Int
        let scope: String
        let entries: [Entry]
    }

    private struct TokenPrincipal: Decodable {
        let oid: UUID
        let tid: UUID
    }

    private let queue = DispatchQueue(label: "dev.prbar.identity-cache", qos: .utility)
    private let defaults: UserDefaults?
    private let diagnostics: DiagnosticLogger
    private let clock: @Sendable () -> Date
    private let logger = Logger(subsystem: "dev.prbar.app", category: "identity-cache")
    private var loaded = false
    private var scope: String?
    private var generation = UUID()
    private var entries: [Key: Entry] = [:]

    init(defaults: UserDefaults? = nil, diagnostics: DiagnosticLogger = .disabled,
         clock: @escaping @Sendable () -> Date = { .now }) {
        self.defaults = defaults
        self.diagnostics = diagnostics
        self.clock = clock
    }

    static func scope(
        configuration: ReviewConfiguration, header: String, sources: [ReviewSource], followedEmails: [String]
    ) throws -> String {
        var principal = "credential:\(fingerprint(Data(header.utf8)))"
        if configuration.authentication == .azureCLI, header.hasPrefix("Bearer "),
           let tokenPrincipal = tokenPrincipal(String(header.dropFirst("Bearer ".count))) {
            // Claims partition the cache only; Azure DevOps still authenticates every HTTP request.
            principal = "entra:\(tokenPrincipal.tid.uuidString.lowercased()):\(tokenPrincipal.oid.uuidString.lowercased())"
        }
        let components = [
            [configuration.authentication.rawValue,
             configuration.tenantID.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(), principal],
            sources.map(\.id).sorted(), followedEmails.sorted()
        ]
        return fingerprint(try JSONEncoder().encode(components))
    }

    private static func fingerprint(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    private static func tokenPrincipal(_ token: String) -> TokenPrincipal? {
        let parts = token.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, parts[1].utf8.count <= 16_384 else { return nil }
        var payload = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        payload += String(repeating: "=", count: (4 - payload.utf8.count % 4) % 4)
        guard let data = Data(base64Encoded: payload) else { return nil }
        guard let principal = try? JSONDecoder().decode(TokenPrincipal.self, from: data),
              principal.oid != UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
              principal.tid != UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
        else { return nil }
        return principal
    }

    func requestGeneration() -> UUID { queue.sync { generation } }

    func begin(scope requestedScope: String, requestGeneration: UUID? = nil) throws -> Context {
        try queue.sync {
            try Task.checkCancellation()
            if let requestGeneration, requestGeneration != generation { throw CancellationError() }
            loadIfNeeded()
            var changed = false
            if scope != requestedScope {
                if scope != nil {
                    diagnostics.log(.info, .identityCacheCleared, operation: .identityCache, reason: .superseded)
                }
                scope = requestedScope
                entries.removeAll()
                generation = UUID()
                changed = true
            }
            let fresh = entries.filter { $0.value.isFresh(at: clock()) }
            if fresh.count != entries.count {
                entries = fresh
                changed = true
            }
            if changed { try persist() }
            return Context(scope: requestedScope, generation: generation)
        }
    }

    func currentUser(organization: String, context: Context) throws -> UUID? {
        try lookup(
            Key(kind: .currentUser, organization: organization.lowercased(), email: nil),
            context: context
        )?.identity
    }

    func followedAuthor(organization: String, email: String, context: Context) throws -> Lookup {
        guard let entry = try lookup(
            Key(kind: .followedAuthor, organization: organization.lowercased(), email: email.lowercased()),
            context: context
        ) else { return .missing }
        return entry.identity.map(Lookup.identity) ?? .notFound
    }

    func storeCurrentUser(_ identity: UUID, organization: String, context: Context) throws {
        try store(Entry(
            kind: .currentUser, organization: organization.lowercased(), email: nil,
            identity: identity, resolvedAt: clock()
        ), context: context)
    }

    func storeFollowedAuthor(_ identity: UUID?, organization: String, email: String, context: Context) throws {
        try store(Entry(
            kind: .followedAuthor, organization: organization.lowercased(), email: email.lowercased(),
            identity: identity, resolvedAt: clock()
        ), context: context)
    }

    func clear() {
        queue.sync {
            loaded = true
            scope = nil
            entries.removeAll()
            generation = UUID()
            defaults?.removeObject(forKey: Self.settingsKey)
        }
        diagnostics.log(.info, .identityCacheCleared, operation: .identityCache)
    }

    private func check(_ context: Context) throws {
        try Task.checkCancellation()
        guard context.scope == scope, context.generation == generation else { throw CancellationError() }
    }

    private func lookup(_ key: Key, context: Context) throws -> Entry? {
        try queue.sync {
            try check(context)
            let entry = entries[key]
            let fresh = entry?.isFresh(at: clock()) == true
            if entry != nil && !fresh {
                entries.removeValue(forKey: key)
                try persist()
            }
            diagnostics.log(
                .debug, fresh ? .identityCacheHit : .identityCacheMiss,
                operation: key.kind == .currentUser ? .identity : .followedIdentity
            )
            return fresh ? entry : nil
        }
    }

    private func store(_ entry: Entry, context: Context) throws {
        try queue.sync {
            try check(context)
            guard entry.isValid else { throw ReviewQError.invalidResponse("Azure DevOps identity cache") }
            entries[entry.key] = entry
            if entries.count > Self.maximumEntries {
                let oldest = entries.values.sorted {
                    $0.resolvedAt == $1.resolvedAt
                        ? $0.orderingKey < $1.orderingKey : $0.resolvedAt < $1.resolvedAt
                }
                for entry in oldest.prefix(entries.count - Self.maximumEntries) {
                    entries.removeValue(forKey: entry.key)
                }
            }
            try persist()
        }
    }

    private func loadIfNeeded() {
        guard !loaded else { return }
        loaded = true
        guard let defaults, defaults.object(forKey: Self.settingsKey) != nil else { return }
        if let data = defaults.data(forKey: Self.settingsKey), data.count <= 1_048_576,
           let storage = try? JSONDecoder().decode(Storage.self, from: data),
           storage.version == 1, storage.scope.utf8.count == 64,
           storage.scope.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }),
           storage.entries.count <= Self.maximumEntries,
           storage.entries.allSatisfy(\.isValid),
           Set(storage.entries.map(\.key)).count == storage.entries.count {
            scope = storage.scope
            entries = Dictionary(uniqueKeysWithValues: storage.entries.map { ($0.key, $0) })
        } else {
            defaults.removeObject(forKey: Self.settingsKey)
            logger.warning("Stored identity cache was invalid and has been cleared.")
            diagnostics.log(.warning, .identityCacheInvalidated, operation: .identityCache, reason: .decoding)
        }
    }

    private func persist() throws {
        guard let defaults, let scope else { return }
        let storage = Storage(
            version: 1, scope: scope, entries: entries.values.sorted { $0.orderingKey < $1.orderingKey }
        )
        defaults.set(try JSONEncoder().encode(storage), forKey: Self.settingsKey)
    }
}
