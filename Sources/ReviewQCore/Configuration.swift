import Foundation

public enum AuthenticationMethod: String, Codable, CaseIterable, Sendable {
    case azureCLI
    case personalAccessToken
}

public enum RefreshInterval: Int, Codable, CaseIterable, Sendable {
    case oneMinute = 60
    case fiveMinutes = 300
    case fifteenMinutes = 900
    case thirtyMinutes = 1800

    public var label: String {
        switch self {
        case .oneMinute: "Every minute"
        case .fiveMinutes: "Every 5 minutes"
        case .fifteenMinutes: "Every 15 minutes"
        case .thirtyMinutes: "Every 30 minutes"
        }
    }
}

public struct ReviewConfiguration: Codable, Equatable, Sendable {
    public static let additionalPromptCharacterLimit = 1_000

    public var sourceURLs = ""
    public var authentication = AuthenticationMethod.azureCLI
    public var tenantID = ""
    public var refreshInterval = RefreshInterval.fifteenMinutes
    public var includeDrafts = false
    public var showCount = true
    public var followPeople = false
    public var followedAuthorEmails = ""
    public var showAuthoredPullRequests = false
    public var scoutAdditionalInstructions = ""
    public var copilotAdditionalInstructions = ""

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case sourceURLs, authentication, tenantID, refreshInterval, includeDrafts, showCount
        case followPeople, followedAuthorEmails
        case showAuthoredPullRequests
        case scoutAdditionalInstructions, copilotAdditionalInstructions
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        sourceURLs = try container.decode(String.self, forKey: .sourceURLs)
        authentication = try container.decode(AuthenticationMethod.self, forKey: .authentication)
        tenantID = try container.decode(String.self, forKey: .tenantID)
        refreshInterval = try container.decode(RefreshInterval.self, forKey: .refreshInterval)
        includeDrafts = try container.decode(Bool.self, forKey: .includeDrafts)
        showCount = try container.decode(Bool.self, forKey: .showCount)
        followPeople = container.contains(.followPeople)
            ? try container.decode(Bool.self, forKey: .followPeople) : false
        followedAuthorEmails = container.contains(.followedAuthorEmails)
            ? try container.decode(String.self, forKey: .followedAuthorEmails) : ""
        showAuthoredPullRequests = container.contains(.showAuthoredPullRequests)
            ? try container.decode(Bool.self, forKey: .showAuthoredPullRequests) : false
        scoutAdditionalInstructions = container.contains(.scoutAdditionalInstructions)
            ? try container.decode(String.self, forKey: .scoutAdditionalInstructions) : ""
        copilotAdditionalInstructions = container.contains(.copilotAdditionalInstructions)
            ? try container.decode(String.self, forKey: .copilotAdditionalInstructions) : ""
    }

    public static func validateAdditionalInstructions(_ instructions: String) throws {
        guard instructions.count <= additionalPromptCharacterLimit else { throw ReviewQError.reviewPromptTooLong }
        guard !instructions.unicodeScalars.contains(where: {
            ($0.value < 0x20 || (0x7f...0x9f).contains($0.value)) && !CharacterSet.newlines.contains($0)
        }) else { throw ReviewQError.invalidReviewPrompt }
    }

    public func validatedReviewPrompts() throws {
        try Self.validateAdditionalInstructions(scoutAdditionalInstructions)
        try Self.validateAdditionalInstructions(copilotAdditionalInstructions)
    }

    public func validatedFollowedAuthors() throws -> [String] {
        guard followPeople else { return [] }
        return try Self.parseFollowedAuthorEmails(followedAuthorEmails)
    }

    public static func parseFollowedAuthorEmails(_ text: String) throws -> [String] {
        let entries = text.components(separatedBy: ";")
        var emails: [String] = []
        var seen: Set<String> = []
        for entry in entries {
            let parts = entry.split(separator: "@", omittingEmptySubsequences: false)
            guard !entry.contains(where: \.isWhitespace), entry.utf8.count <= 254, parts.count == 2,
                  !parts[0].isEmpty, parts[0].utf8.count <= 64,
                  !parts[0].hasPrefix("."), !parts[0].hasSuffix("."), !parts[0].contains(".."),
                  parts[0].range(of: #"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+$"#, options: .regularExpression) != nil,
                  parts[1].range(
                    of: #"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$"#,
                    options: .regularExpression
                  ) != nil
            else { throw ReviewQError.invalidFollowedAuthorEmails }
            let email = entry.lowercased()
            if seen.insert(email).inserted { emails.append(email) }
        }
        return emails
    }

    public var hasSources: Bool {
        !sourceURLs.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public func validatedSources() throws -> [ReviewSource] {
        try validatedReviewPrompts()
        _ = try validatedFollowedAuthors()
        let lines = sourceURLs.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        guard !lines.isEmpty else { throw ReviewQError.noSources }
        var sources: [ReviewSource] = []
        var identifiers: Set<String> = []
        for line in lines {
            let source = try ReviewSource.parse(line)
            guard identifiers.insert(source.id).inserted else {
                throw ReviewQError.duplicateSource(source.displayName)
            }
            sources.append(source)
        }
        let tenant = tenantID.trimmingCharacters(in: .whitespacesAndNewlines)
        if authentication == .azureCLI, !tenant.isEmpty {
            let isDomain = tenant.range(
                of: "^[A-Za-z0-9][A-Za-z0-9.-]*\\.[A-Za-z0-9-]+$",
                options: .regularExpression
            ) != nil
            guard UUID(uuidString: tenant) != nil || isDomain else {
                throw ReviewQError.invalidTenant
            }
        }
        return sources
    }
}

public struct ReviewSource: Hashable, Sendable, Identifiable {
    public let organization: String
    public let project: String?

    public var id: String {
        "\(organization.lowercased())/\(project?.lowercased() ?? "")"
    }

    public var displayName: String {
        project.map { "\(organization)/\($0)" } ?? organization
    }

    public static func parse(_ text: String) throws -> ReviewSource {
        guard let components = URLComponents(string: text),
              components.scheme?.lowercased() == "https",
              let host = components.host?.lowercased(),
              components.user == nil, components.password == nil,
              components.port == nil, components.query == nil, components.fragment == nil
        else { throw ReviewQError.invalidSource(text) }

        var segments = components.path.split(separator: "/").map(String.init)
        let organization: String
        if host == "dev.azure.com" {
            guard !segments.isEmpty else { throw ReviewQError.invalidSource(text) }
            organization = segments.removeFirst()
        } else if host.hasSuffix(".visualstudio.com") {
            organization = String(host.dropLast(".visualstudio.com".count))
            if segments.first?.lowercased() == "defaultcollection" {
                segments.removeFirst()
            }
        } else {
            throw ReviewQError.invalidSource(text)
        }

        guard organization.range(of: "^[A-Za-z0-9][A-Za-z0-9-]*$", options: .regularExpression) != nil,
              segments.count <= 1,
              segments.first?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty != true,
              segments.first?.hasPrefix("_") != true,
              segments.first != ".", segments.first != "..",
              segments.allSatisfy({ segment in
                  !segment.contains("\\") && !segment.unicodeScalars.contains(where: {
                      CharacterSet.controlCharacters.contains($0)
                  })
              })
        else { throw ReviewQError.invalidSource(text) }
        return ReviewSource(organization: organization, project: segments.first)
    }

    public func webURL() throws -> URL {
        try AzureDevOpsURL.make(organization: organization, path: project.map { [$0] } ?? [])
    }
}

enum AzureDevOpsURL {
    static func make(
        organization: String, path: [String], query: [URLQueryItem] = [],
        identityService: Bool = false
    ) throws -> URL {
        var components = URLComponents()
        components.scheme = "https"
        components.host = identityService ? "vssps.dev.azure.com" : "dev.azure.com"
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "/%?#")
        let encoded = try ([organization] + path).map { segment in
            guard let value = segment.addingPercentEncoding(withAllowedCharacters: allowed) else {
                throw ReviewQError.invalidSource(segment)
            }
            return value
        }
        components.percentEncodedPath = "/" + encoded.joined(separator: "/")
        if !query.isEmpty {
            components.queryItems = query
            components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        }
        guard let url = components.url else { throw ReviewQError.invalidSource(organization) }
        return url
    }
}

public enum ReviewQError: LocalizedError, Sendable {
    case noSources
    case invalidSource(String)
    case duplicateSource(String)
    case invalidTenant
    case reviewPromptTooLong
    case invalidReviewPrompt
    case invalidFollowedAuthorEmails
    case unresolvedFollowedAuthors([String])
    case missingPAT
    case invalidPAT
    case keychain(Int32)
    case corruptSettings
    case cliUnavailable
    case cliFailure(Int32)
    case cliTimeout
    case invalidCLIResponse
    case invalidResponse(String)
    case invalidPagination(String)
    case http(Int, String)
    case network(String, String, code: Int? = nil)

    public var errorDescription: String? {
        switch self {
        case .noSources:
            "Add at least one Azure DevOps organization or project URL."
        case .invalidSource(let source):
            "Invalid source: \(source). Use https://dev.azure.com/organization or "
                + "https://dev.azure.com/organization/project, without query parameters."
        case .duplicateSource(let source):
            "The source \(source) appears more than once."
        case .invalidTenant:
            "Enter a valid Entra tenant ID (GUID) or tenant domain, or leave it blank."
        case .reviewPromptTooLong:
            "Each app's additional review instructions must be \(ReviewConfiguration.additionalPromptCharacterLimit) characters or fewer."
        case .invalidReviewPrompt:
            "Additional review instructions support text and line breaks, not tabs or other control characters."
        case .invalidFollowedAuthorEmails:
            "Enter at least one email, separated by semicolons with no spaces, for example alex@example.com;sam@example.com."
        case .unresolvedFollowedAuthors(let emails):
            "No matching Azure DevOps user was found in your configured organizations for: \(emails.joined(separator: ";")). "
                + "Check the email addresses and your access, or turn off People you follow."
        case .missingPAT:
            "No personal access token is saved. Add a Code (Read) PAT in Settings."
        case .invalidPAT:
            "Paste only the PAT value, without a header, spaces, or control characters."
        case .keychain(let status):
            "macOS Keychain could not access the saved token (status \(status)). "
                + "Unlock your Keychain and try again."
        case .corruptSettings:
            "Saved settings could not be read. Re-enter your sources and save Settings."
        case .cliUnavailable:
            "Azure CLI was not found. Install it and run az login --allow-no-subscriptions "
                + "in Terminal, or choose the PAT option."
        case .cliFailure(let status):
            "Azure CLI could not get an Azure DevOps token (exit \(status)). "
                + "Run az login --allow-no-subscriptions in Terminal for the correct tenant."
        case .cliTimeout:
            "Azure CLI timed out after requesting a token. Sign in again in Terminal and retry."
        case .invalidCLIResponse:
            "Azure CLI returned an invalid or expired token response. Update Azure CLI and sign in again."
        case .invalidResponse(let source):
            "\(source) returned an unexpected Azure DevOps response. Check your sign-in and access."
        case .invalidPagination(let source):
            "\(source) repeated a results page. Refresh again; the queue has not been replaced."
        case .http(let status, let source):
            switch status {
            case 401:
                "\(source): authentication expired or was rejected. Sign in to Azure CLI again "
                    + "or replace your PAT in Settings."
            case 403:
                "\(source): access denied. Check your organization/project access and PAT permissions."
            case 404:
                "\(source): organization or project not found, or not accessible to this account."
            case 429:
                "\(source): Azure DevOps is rate-limiting requests. Wait and refresh again."
            case 300..<400:
                "\(source): Azure DevOps redirected the request. Check your sign-in and source URL."
            default:
                "\(source): Azure DevOps returned HTTP \(status). Try refreshing again."
            }
        case .network(let source, let reason, _):
            "\(source): \(reason)"
        }
    }
}
