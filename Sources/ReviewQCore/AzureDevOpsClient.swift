import Foundation

struct HTTPResult: Sendable {
    let data: Data
    let status: Int
    var continuationToken: String?
}

protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> HTTPResult
}

final class RedirectBlocker: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _ session: URLSession, task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping @Sendable (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

struct URLSessionTransport: HTTPTransport {
    private let session: URLSession

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 60
        session = URLSession(configuration: configuration, delegate: RedirectBlocker(), delegateQueue: nil)
    }

    func send(_ request: URLRequest) async throws -> HTTPResult {
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else {
            throw ReviewQError.invalidResponse("Azure DevOps")
        }
        return HTTPResult(
            data: data, status: response.statusCode,
            continuationToken: response.value(forHTTPHeaderField: "x-ms-continuationtoken")
        )
    }
}

public struct AzureDevOpsReviewLoader: ReviewLoading {
    private let authorization: any AuthorizationProviding
    private let transport: any HTTPTransport
    private let diagnostics: DiagnosticLogger
    private let identityCache: IdentityCache
    private static let pageSize = 100
    private static let historyConcurrency = 4

    public init(
        keychain: any PATStoring = KeychainPATStore(), diagnostics: DiagnosticLogger = .disabled,
        defaults: UserDefaults = .standard
    ) {
        authorization = AzureDevOpsCredentials(keychain: keychain, diagnostics: diagnostics)
        transport = URLSessionTransport()
        self.diagnostics = diagnostics
        identityCache = IdentityCache(defaults: defaults, diagnostics: diagnostics)
    }

    init(
        authorization: any AuthorizationProviding, transport: any HTTPTransport,
        diagnostics: DiagnosticLogger = .disabled, identityCache: IdentityCache = IdentityCache()
    ) {
        self.authorization = authorization
        self.transport = transport
        self.diagnostics = diagnostics
        self.identityCache = identityCache
    }

    public func invalidateIdentityCache() async {
        identityCache.clear()
        await authorization.invalidate()
    }

    public func load(configuration: ReviewConfiguration) async throws -> ReviewSnapshot {
        let sources = try configuration.validatedSources()
        let followedEmails = try configuration.validatedFollowedAuthors()
        do {
            return try await authenticatedFetch(
                sources: sources, followedEmails: followedEmails, configuration: configuration
            )
        } catch let error as ReviewQError {
            if configuration.authentication == .azureCLI, case .http(401, _) = error {
                try Task.checkCancellation()
                await authorization.invalidate()
                return try await authenticatedFetch(
                    sources: sources, followedEmails: followedEmails, configuration: configuration
                )
            }
            throw error
        }
    }

    private func authenticatedFetch(
        sources: [ReviewSource], followedEmails: [String], configuration: ReviewConfiguration
    ) async throws -> ReviewSnapshot {
        let generation = identityCache.requestGeneration()
        let header = try await authorization.authorization(for: configuration)
        return try await fetch(
            sources: sources, followedEmails: followedEmails, configuration: configuration,
            header: header, cacheGeneration: generation
        )
    }

    private func fetch(
        sources: [ReviewSource], followedEmails: [String], configuration: ReviewConfiguration,
        header: String, cacheGeneration: UUID
    ) async throws -> ReviewSnapshot {
        try Task.checkCancellation()
        let cacheContext = try identityCache.begin(scope: IdentityCache.scope(
            configuration: configuration, header: header, sources: sources, followedEmails: followedEmails
        ), requestGeneration: cacheGeneration)
        var identities: [String: UUID] = [:]
        var organizationProjects: [String: [String]] = [:]
        var pullRequests: [String: ReviewPullRequest] = [:]
        for source in sources {
            try Task.checkCancellation()
            let organizationKey = source.organization.lowercased()
            let reviewerID: UUID
            if let cached = identities[organizationKey] {
                reviewerID = cached
            } else if let cached = try identityCache.currentUser(
                organization: source.organization, context: cacheContext
            ) {
                reviewerID = cached
                identities[organizationKey] = cached
            } else {
                let url = try AzureDevOpsURL.make(
                    organization: source.organization, path: ["_apis", "connectionData"],
                    query: [URLQueryItem(name: "api-version", value: "7.1-preview.1")]
                )
                let (connection, _): (APIConnection, HTTPResult) = try await get(
                    url, header: header, source: source.displayName, operation: .identity
                )
                guard connection.authenticatedUser.isActive != false,
                      connection.authenticatedUser.id != UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
                else { throw ReviewQError.http(401, source.displayName) }
                reviewerID = connection.authenticatedUser.id
                try Task.checkCancellation()
                try identityCache.storeCurrentUser(
                    reviewerID, organization: source.organization, context: cacheContext
                )
                identities[organizationKey] = reviewerID
            }
            let projects: [String]
            if let project = source.project {
                projects = [project]
            } else {
                projects = try await projectNames(source: source, header: header)
                organizationProjects[organizationKey] = projects
            }
            for project in projects {
                let reviews = try await reviews(
                    source: source, project: project, reviewerID: reviewerID.uuidString,
                    includeDrafts: configuration.includeDrafts, header: header
                )
                for review in reviews { pullRequests[review.id] = review }
            }
        }
        if configuration.showAuthoredPullRequests {
            var seenOrganizations: Set<String> = []
            for source in sources where seenOrganizations.insert(source.organization.lowercased()).inserted {
                try Task.checkCancellation()
                let organizationKey = source.organization.lowercased()
                guard let reviewerID = identities[organizationKey] else {
                    throw ReviewQError.invalidResponse(source.displayName)
                }
                let projects: [String]
                if let cached = organizationProjects[organizationKey] {
                    projects = cached
                } else {
                    projects = try await projectNames(source: source, header: header)
                    organizationProjects[organizationKey] = projects
                }
                for project in projects {
                    let authored = try await reviews(
                        source: source, project: project, reviewerID: reviewerID.uuidString,
                        creatorID: reviewerID, includeDrafts: configuration.includeDrafts, header: header,
                        isAuthored: true
                    )
                    for request in authored { pullRequests[request.id] = request }
                }
            }
        }
        if !followedEmails.isEmpty {
            var seenOrganizations: Set<String> = []
            var matchedEmails: Set<String> = []
            for source in sources where seenOrganizations.insert(source.organization.lowercased()).inserted {
                try Task.checkCancellation()
                let organizationKey = source.organization.lowercased()
                guard let reviewerID = identities[organizationKey] else {
                    throw ReviewQError.invalidResponse(source.displayName)
                }
                let authors = try await followedAuthors(
                    followedEmails, source: source, header: header, cacheContext: cacheContext
                )
                matchedEmails.formUnion(authors.emails)
                guard !authors.ids.isEmpty else { continue }
                let projects: [String]
                if let cached = organizationProjects[organizationKey] {
                    projects = cached
                } else {
                    projects = try await projectNames(source: source, header: header)
                    organizationProjects[organizationKey] = projects
                }
                for project in projects {
                    for creatorID in authors.ids where !configuration.showAuthoredPullRequests || creatorID != reviewerID {
                        let followed = try await reviews(
                            source: source, project: project, reviewerID: reviewerID.uuidString,
                            creatorID: creatorID, includeDrafts: configuration.includeDrafts, header: header,
                            excludingIDs: Set(pullRequests.keys)
                        )
                        for review in followed { pullRequests[review.id] = review }
                    }
                }
            }
            let unresolved = followedEmails.filter { !matchedEmails.contains($0) }
            guard unresolved.isEmpty else { throw ReviewQError.unresolvedFollowedAuthors(unresolved) }
        }
        let sorted = pullRequests.values.sorted(by: ReviewPullRequest.newestFirst)
        return ReviewSnapshot(pullRequests: sorted)
    }

    private func followedAuthors(
        _ emails: [String], source: ReviewSource, header: String, cacheContext: IdentityCache.Context
    ) async throws -> (ids: [UUID], emails: Set<String>) {
        var ids: [UUID] = []
        var seenIDs: Set<UUID> = []
        var matchedEmails: Set<String> = []
        let scope = "\(source.organization) followed people"
        for email in emails {
            try Task.checkCancellation()
            switch try identityCache.followedAuthor(
                organization: source.organization, email: email, context: cacheContext
            ) {
            case .identity(let id):
                matchedEmails.insert(email)
                if seenIDs.insert(id).inserted { ids.append(id) }
                continue
            case .notFound:
                continue
            case .missing:
                break
            }
            let url = try AzureDevOpsURL.make(
                organization: source.organization, path: ["_apis", "identities"],
                query: [
                    URLQueryItem(name: "api-version", value: "7.1"),
                    URLQueryItem(name: "searchFilter", value: "General"),
                    URLQueryItem(name: "filterValue", value: email),
                    URLQueryItem(name: "queryMembership", value: "None")
                ],
                identityService: true
            )
            let (page, response): (APIList<APIFollowedIdentity>, HTTPResult) = try await get(
                url, header: header, source: scope, operation: .followedIdentity
            )
            if let continuation = response.continuationToken, !continuation.isEmpty {
                throw ReviewQError.invalidPagination(scope)
            }
            let matches = Set(page.value.filter { $0.matches(email: email) }.map(\.id))
            guard matches.count <= 1,
                  !matches.contains(UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)))
            else { throw ReviewQError.invalidResponse(scope) }
            try Task.checkCancellation()
            try identityCache.storeFollowedAuthor(
                matches.first, organization: source.organization, email: email, context: cacheContext
            )
            if let id = matches.first {
                matchedEmails.insert(email)
                if seenIDs.insert(id).inserted { ids.append(id) }
            }
        }
        return (ids, matchedEmails)
    }

    private func projectNames(source: ReviewSource, header: String) async throws -> [String] {
        var names: [String] = []
        var seenProjects: Set<String> = []
        var seenTokens: Set<String> = []
        var continuation: String?
        repeat {
            try Task.checkCancellation()
            var query = [
                URLQueryItem(name: "api-version", value: "7.1"),
                URLQueryItem(name: "$top", value: String(Self.pageSize))
            ]
            if let continuation {
                query.append(URLQueryItem(name: "continuationToken", value: continuation))
            }
            let url = try AzureDevOpsURL.make(
                organization: source.organization, path: ["_apis", "projects"], query: query
            )
            let (page, response): (APIList<APIProject>, HTTPResult) = try await get(
                url, header: header, source: source.displayName, operation: .projects, page: seenTokens.count + 1
            )
            for project in page.value where seenProjects.insert(project.id.lowercased()).inserted {
                names.append(project.name)
            }
            continuation = response.continuationToken.flatMap { $0.isEmpty ? nil : $0 }
            if let continuation, !seenTokens.insert(continuation).inserted {
                throw ReviewQError.invalidPagination(source.displayName)
            }
        } while continuation != nil
        return names
    }

    private func reviews(
        source: ReviewSource, project: String, reviewerID: String, creatorID: UUID? = nil,
        includeDrafts: Bool, header: String, excludingIDs: Set<String> = [], isAuthored: Bool = false
    ) async throws -> [ReviewPullRequest] {
        let scope = "\(source.organization)/\(project)"
        var reviews: [ReviewPullRequest] = []
        var seenIDs: Set<Int> = []
        var skip = 0
        while true {
            try Task.checkCancellation()
            let url = try AzureDevOpsURL.make(
                organization: source.organization, path: [project, "_apis", "git", "pullrequests"],
                query: [
                    URLQueryItem(name: "api-version", value: "7.1"),
                    URLQueryItem(name: "searchCriteria.status", value: "active"),
                    URLQueryItem(
                        name: creatorID == nil ? "searchCriteria.reviewerId" : "searchCriteria.creatorId",
                        value: creatorID?.uuidString ?? reviewerID
                    ),
                    URLQueryItem(name: "$top", value: String(Self.pageSize)),
                    URLQueryItem(name: "$skip", value: String(skip))
                ]
            )
            let (page, _): (APIList<APIPullRequest>, HTTPResult) = try await get(
                url, header: header, source: scope, operation: .pullRequests, page: skip / Self.pageSize + 1
            )
            var newIDs = 0
            var candidates: [(request: APIPullRequest, reviewer: APIReviewer?)] = []
            for request in page.value where seenIDs.insert(request.pullRequestId).inserted {
                newIDs += 1
                if let creatorID {
                    guard request.status.lowercased() == "active",
                          includeDrafts || request.isDraft != true,
                          UUID(uuidString: request.createdBy.id) == creatorID,
                          !excludingIDs.contains(request.reviewID(source: source)) else { continue }
                    let reviewer = request.reviewers.first { $0.id.caseInsensitiveCompare(reviewerID) == .orderedSame }
                    candidates.append((request, reviewer))
                } else if let reviewer = request.pendingReviewer(for: reviewerID, includeDrafts: includeDrafts) {
                    candidates.append((request, reviewer))
                }
            }
            reviews += try await reviewsWithHistory(
                candidates, source: source, project: project, reviewerID: reviewerID, header: header,
                isFollowedOnly: creatorID != nil && !isAuthored, isAuthored: isAuthored
            )
            if page.value.count < Self.pageSize { return reviews }
            guard newIDs > 0 else { throw ReviewQError.invalidPagination(scope) }
            skip += page.value.count
        }
    }

    private func reviewsWithHistory(
        _ candidates: [(request: APIPullRequest, reviewer: APIReviewer?)],
        source: ReviewSource, project: String, reviewerID: String, header: String,
        isFollowedOnly: Bool, isAuthored: Bool
    ) async throws -> [ReviewPullRequest] {
        guard let identity = UUID(uuidString: reviewerID) else {
            throw ReviewQError.invalidResponse("\(source.organization)/\(project)")
        }
        return try await withThrowingTaskGroup(of: ReviewPullRequest.self) { group in
            var remaining = candidates.makeIterator()
            for _ in 0..<Self.historyConcurrency {
                if let candidate = remaining.next() {
                    group.addTask {
                        try await reviewWithHistory(
                            candidate, source: source, project: project, reviewerID: identity, header: header,
                            isFollowedOnly: isFollowedOnly, isAuthored: isAuthored
                        )
                    }
                }
            }
            var reviews: [ReviewPullRequest] = []
            while let review = try await group.next() {
                reviews.append(review)
                if let candidate = remaining.next() {
                    group.addTask {
                        try await reviewWithHistory(
                            candidate, source: source, project: project, reviewerID: identity, header: header,
                            isFollowedOnly: isFollowedOnly, isAuthored: isAuthored
                        )
                    }
                }
            }
            return reviews
        }
    }

    private func reviewWithHistory(
        _ candidate: (request: APIPullRequest, reviewer: APIReviewer?),
        source: ReviewSource, project: String, reviewerID: UUID, header: String,
        isFollowedOnly: Bool, isAuthored: Bool
    ) async throws -> ReviewPullRequest {
        try Task.checkCancellation()
        let request = candidate.request
        let scope = "\(source.organization)/\(project)/\(request.repository.name) review history"
        let url = try AzureDevOpsURL.make(
            organization: source.organization,
            path: [
                project, "_apis", "git", "repositories", request.repository.id,
                "pullRequests", String(request.pullRequestId), "threads"
            ],
            query: [URLQueryItem(name: "api-version", value: "7.1")]
        )
        let (threads, response): (APIList<APIReviewThread>, HTTPResult) = try await get(
            url, header: header, source: scope, operation: .reviewHistory
        )
        if let continuation = response.continuationToken, !continuation.isEmpty {
            throw ReviewQError.invalidPagination(scope)
        }
        if isAuthored {
            let evaluations = try await approvalPolicies(for: request, source: source, header: header)
            let status = try AuthoredPullRequestStatus.from(
                threads: threads.value, reviewers: request.reviewers, evaluations: evaluations
            )
            return try request.review(
                source: source, reviewer: nil, stage: .firstReview, authoredStatus: status
            )
        }
        let history = try ReviewStage.fromHistory(threads.value, reviewerID: reviewerID)
        return try request.review(
            source: source, reviewer: candidate.reviewer,
            stage: history.stage, commentResolution: history.commentResolution, isFollowedOnly: isFollowedOnly
        )
    }

    private func approvalPolicies(
        for request: APIPullRequest, source: ReviewSource, header: String
    ) async throws -> [APIPolicyEvaluation] {
        let project = request.repository.project
        let scope = "\(source.organization)/\(project.name) reviewer approvals"
        let artifactID = "vstfs:///CodeReview/CodeReviewId/\(project.id)/\(request.pullRequestId)"
        var evaluations: [APIPolicyEvaluation] = []
        var seenIDs: Set<UUID> = []
        var skip = 0
        while true {
            try Task.checkCancellation()
            let url = try AzureDevOpsURL.make(
                organization: source.organization, path: [project.name, "_apis", "policy", "evaluations"],
                query: [
                    URLQueryItem(name: "api-version", value: "7.1-preview.1"),
                    URLQueryItem(name: "artifactId", value: artifactID),
                    URLQueryItem(name: "includeNotApplicable", value: "true"),
                    URLQueryItem(name: "$top", value: String(Self.pageSize)),
                    URLQueryItem(name: "$skip", value: String(skip))
                ]
            )
            let (page, response): (APIList<APIPolicyEvaluation>, HTTPResult) = try await get(
                url, header: header, source: scope, operation: .reviewerPolicies, page: skip / Self.pageSize + 1
            )
            guard response.continuationToken?.isEmpty != false else {
                throw ReviewQError.invalidPagination(scope)
            }
            let count = evaluations.count
            for evaluation in page.value {
                guard evaluation.artifactId.removingPercentEncoding?.caseInsensitiveCompare(artifactID) == .orderedSame else {
                    throw ReviewQError.invalidResponse(scope)
                }
                if seenIDs.insert(evaluation.evaluationId).inserted { evaluations.append(evaluation) }
            }
            if page.value.count < Self.pageSize { return evaluations }
            guard evaluations.count > count else { throw ReviewQError.invalidPagination(scope) }
            skip += page.value.count
        }
    }

    private func get<Value: Decodable & Sendable>(
        _ url: URL, header: String, source: String, operation: DiagnosticOperation, page: Int? = nil
    ) async throws -> (Value, HTTPResult) {
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue(header, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let requestID = UUID()
        let started = ContinuousClock.now
        diagnostics.log(.debug, .requestStarted, operation: operation, requestID: requestID, page: page)
        var response: HTTPResult?
        do {
            let received = try await transport.send(request)
            response = received
            guard (200..<300).contains(received.status) else {
                throw ReviewQError.http(received.status, source)
            }
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .custom { decoder in
                let container = try decoder.singleValueContainer()
                let text = try container.decode(String.self)
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                if let date = formatter.date(from: text) { return date }
                formatter.formatOptions = [.withInternetDateTime]
                guard let date = formatter.date(from: text) else {
                    throw DecodingError.dataCorruptedError(
                        in: container, debugDescription: "Invalid ISO 8601 date."
                    )
                }
                return date
            }
            let value = try decoder.decode(Value.self, from: received.data)
            diagnostics.log(
                .info, .requestFinished, operation: operation, requestID: requestID,
                status: received.status, durationMilliseconds: DiagnosticTiming.milliseconds(since: started),
                responseBytes: received.data.count, page: page
            )
            return (value, received)
        } catch {
            let issue = DiagnosticFailure(error)
            diagnostics.log(
                issue.reason == .cancelled ? .info : .warning,
                issue.reason == .cancelled ? .requestCancelled : .requestFailed,
                operation: operation, requestID: requestID, reason: issue.reason,
                status: response?.status ?? issue.status,
                durationMilliseconds: DiagnosticTiming.milliseconds(since: started),
                responseBytes: response?.data.count, page: page, systemCode: issue.systemCode
            )
            if let error = error as? URLError {
                if error.code == .cancelled { throw CancellationError() }
                throw ReviewQError.network(source, error.localizedDescription, code: error.code.rawValue)
            }
            if error is DecodingError { throw ReviewQError.invalidResponse(source) }
            throw error
        }
    }
}
