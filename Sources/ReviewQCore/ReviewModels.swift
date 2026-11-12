import Foundation

public enum ReviewStage: String, CaseIterable, Sendable {
    case firstReview
    case approved, approvedWithSuggestions, waitedForAuthor, rejected, commented
    case commentsResolved

    public var label: String {
        switch self {
        case .firstReview: "First Review"
        case .approved: "You Approved"
        case .approvedWithSuggestions: "You Approved with Suggestions"
        case .waitedForAuthor: "You Waited for Author"
        case .rejected: "You Rejected"
        case .commented: "You commented"
        case .commentsResolved: "Your comments are resolved"
        }
    }

    public var explanation: String {
        switch self {
        case .approved, .approvedWithSuggestions, .waitedForAuthor, .rejected:
            "\(historyExplanation) This PR needs your vote again."
        default:
            historyExplanation
        }
    }

    var historyExplanation: String {
        switch self {
        case .firstReview: "No previous nonzero vote or review comment from you was found."
        case .approved: "Your last recorded nonzero vote was Approved."
        case .approvedWithSuggestions:
            "Your last recorded nonzero vote was Approved with suggestions."
        case .waitedForAuthor:
            "Your last recorded nonzero vote was Waiting for author."
        case .rejected: "Your last recorded nonzero vote was Rejected."
        case .commented: "You previously left a review comment, but no nonzero vote from you was found."
        case .commentsResolved: "All your current review discussion threads are marked Fixed."
        }
    }

    static func fromHistory(
        _ threads: [APIReviewThread], reviewerID: UUID
    ) throws -> (stage: Self, commentResolution: ReviewCommentResolution?) {
        let votes = threads.compactMap { thread -> (thread: APIReviewThread, vote: Int)? in
            guard thread.voterID == reviewerID, let vote = thread.properties?.vote, vote != 0 else { return nil }
            return (thread, vote)
        }
        guard let first = votes.first else {
            return fromComments(threads, reviewerID: reviewerID)
        }
        if Set(votes.map(\.vote)).count == 1 { return (try fromVote(first.vote), nil) }
        guard votes.allSatisfy({ $0.thread.publishedDate != nil }),
              let newestDate = votes.compactMap({ $0.thread.publishedDate }).max() else {
            throw ReviewQError.invalidResponse("Azure DevOps review-history chronology")
        }
        let newest = votes.filter { $0.thread.publishedDate == newestDate }
        if let first = newest.first, Set(newest.map(\.vote)).count == 1 {
            return (try fromVote(first.vote), nil)
        }
        let identifiers = newest.compactMap { $0.thread.id }
        guard identifiers.count == newest.count, Set(identifiers).count == newest.count,
              let latest = zip(identifiers, newest).max(by: { $0.0 < $1.0 }) else {
            throw ReviewQError.invalidResponse("Azure DevOps review-history chronology")
        }
        return (try fromVote(latest.1.vote), nil)
    }

    private static func fromComments(
        _ threads: [APIReviewThread], reviewerID: UUID
    ) -> (stage: Self, commentResolution: ReviewCommentResolution?) {
        var didComment = false
        var resolved = 0
        var open = 0
        var hasCompleteStatuses = true
        for thread in threads {
            guard thread.hasReviewComment(from: reviewerID) else { continue }
            didComment = true
            guard !thread.isDeleted, thread.properties?.isSystemThread != true,
                  thread.hasReviewComment(from: reviewerID, includingDeleted: false) else { continue }
            switch thread.status {
            case .fixed: resolved += 1
            case .active, .pending: open += 1
            case .unknown, .wontFix, .closed, .byDesign: hasCompleteStatuses = false
            }
        }
        guard didComment else { return (.firstReview, nil) }
        guard hasCompleteStatuses, resolved + open > 0 else { return (.commented, nil) }
        let resolution = ReviewCommentResolution(resolvedCount: resolved, openCount: open)
        return (open == 0 ? .commentsResolved : .commented, resolution)
    }

    private static func fromVote(_ vote: Int) throws -> Self {
        switch vote {
        case 10: .approved
        case 5: .approvedWithSuggestions
        case -5: .waitedForAuthor
        case -10: .rejected
        default: throw ReviewQError.invalidResponse("Azure DevOps review vote")
        }
    }
}

public struct ReviewCommentResolution: Equatable, Sendable {
    public let resolvedCount: Int
    public let openCount: Int

    public var summary: String { "\(resolvedCount) resolved, \(openCount) open" }
}

public struct ReviewPullRequest: Equatable, Sendable, Identifiable {
    public let number: Int
    public let title: String
    public let author: String
    public let organization: String
    public let project: String
    public let repository: String
    public let repositoryID: String
    public let createdAt: Date
    public let url: URL
    public let isRequired: Bool
    public let isDraft: Bool
    public let reviewStage: ReviewStage
    public let commentResolution: ReviewCommentResolution?
    public let isFollowedOnly: Bool
    public let authoredStatus: AuthoredPullRequestStatus?

    public init(
        number: Int, title: String, author: String, organization: String, project: String,
        repository: String, repositoryID: String, createdAt: Date, url: URL,
        isRequired: Bool, isDraft: Bool, reviewStage: ReviewStage,
        commentResolution: ReviewCommentResolution? = nil, isFollowedOnly: Bool = false,
        authoredStatus: AuthoredPullRequestStatus? = nil
    ) {
        self.number = number
        self.title = title
        self.author = author
        self.organization = organization
        self.project = project
        self.repository = repository
        self.repositoryID = repositoryID
        self.createdAt = createdAt
        self.url = url
        self.isRequired = isRequired
        self.isDraft = isDraft
        self.reviewStage = reviewStage
        self.commentResolution = commentResolution
        self.isFollowedOnly = isFollowedOnly
        self.authoredStatus = authoredStatus
    }

    public var id: String { Self.identifier(organization: organization, repositoryID: repositoryID, number: number) }
    public var groupID: String { "\(organization)/\(project)/\(repository)" }
    public var authorLabel: String {
        isFollowedOnly || authoredStatus != nil ? "\(author) in \(repository)" : author
    }

    public var isMergeReady: Bool {
        guard let authoredStatus else { return false }
        return !isDraft && authoredStatus.hasRequiredApprovals && authoredStatus.activeCommentCount == 0
    }

    public var authoredStatusLabel: String? {
        guard let authoredStatus else { return nil }
        if authoredStatus.activeCommentCount > 0 { return "\(authoredStatus.activeCommentCount) Active Comments" }
        return isMergeReady ? "Merge Ready" : nil
    }

    public var authoredStatusExplanation: String {
        "Active Comments counts unresolved discussion threads (including pending), not replies or system events. "
            + "Merge Ready means required reviewer approvals are met and no active comments remain on a non-draft PR. "
            + "Builds and other merge requirements are not checked."
    }

    static func identifier(organization: String, repositoryID: String, number: Int) -> String {
        "\(organization.lowercased())/\(repositoryID.lowercased())/\(number)"
    }

    public var reviewStageLabel: String {
        guard reviewStage == .commented, let commentResolution else { return reviewStage.label }
        return "\(reviewStage.label) [\(commentResolution.summary)]"
    }

    public var reviewStageExplanation: String {
        let explanation = isFollowedOnly ? reviewStage.historyExplanation : reviewStage.explanation
        if let commentResolution {
            return "\(explanation)\n\(commentResolution.summary). Counts refer to discussion threads, not individual replies."
        }
        guard reviewStage == .commented else { return explanation }
        return "\(explanation)\nResolution counts are unavailable for deleted discussions, unknown statuses, or threads closed for other reasons."
    }

    public static func newestFirst(_ lhs: Self, _ rhs: Self) -> Bool {
        if lhs.createdAt != rhs.createdAt { return lhs.createdAt > rhs.createdAt }
        return lhs.id < rhs.id
    }
}

public struct ReviewSnapshot: Equatable, Sendable {
    public let pullRequests: [ReviewPullRequest]
    public let updatedAt: Date

    public init(pullRequests: [ReviewPullRequest], updatedAt: Date = Date()) {
        self.pullRequests = pullRequests
        self.updatedAt = updatedAt
    }

    public var pendingReviewCount: Int {
        pullRequests.filter { $0.authoredStatus == nil }.count
    }
}

public protocol ReviewLoading: Sendable {
    func load(configuration: ReviewConfiguration) async throws -> ReviewSnapshot
    func invalidateIdentityCache() async
}

struct APIList<Item: Decodable & Sendable>: Decodable, Sendable {
    let value: [Item]
}

struct APIProject: Decodable, Sendable {
    let id: String
    let name: String
}

struct APIConnection: Decodable, Sendable {
    let authenticatedUser: APIConnectionIdentity
}

struct APIConnectionIdentity: Decodable, Sendable {
    let id: UUID
    let isActive: Bool?
}

struct APIFollowedIdentity: Decodable, Sendable {
    let id: UUID
    let isContainer: Bool?
    private let properties: Properties

    func matches(email: String) -> Bool {
        isContainer != true
            && [properties.mail?.value, properties.account?.value].compactMap { $0 }.contains {
                $0.caseInsensitiveCompare(email) == .orderedSame
            }
    }

    private struct Properties: Decodable, Sendable {
        let mail: APIProperty<String>?
        let account: APIProperty<String>?

        private enum CodingKeys: String, CodingKey {
            case mail = "Mail"
            case account = "Account"
        }
    }
}

struct APIIdentity: Decodable, Sendable {
    let id: String
    let displayName: String
}

struct APIReviewer: Decodable, Sendable {
    let id: String
    let vote: Int
    let isRequired: Bool?
    let hasDeclined: Bool?
}

struct APIRepository: Decodable, Sendable {
    let id: String
    let name: String
    let project: APIProject
}

struct APIPullRequest: Decodable, Sendable {
    let pullRequestId: Int
    let title: String
    let status: String
    let creationDate: Date
    let createdBy: APIIdentity
    let repository: APIRepository
    let reviewers: [APIReviewer]
    let isDraft: Bool?

    func reviewID(source: ReviewSource) -> String {
        ReviewPullRequest.identifier(
            organization: source.organization, repositoryID: repository.id, number: pullRequestId
        )
    }

    func pendingReviewer(for reviewerID: String, includeDrafts: Bool) -> APIReviewer? {
        guard status.lowercased() == "active",
              includeDrafts || isDraft != true,
              createdBy.id.caseInsensitiveCompare(reviewerID) != .orderedSame,
              let reviewer = reviewers.first(where: {
                  $0.id.caseInsensitiveCompare(reviewerID) == .orderedSame
              }),
              reviewer.vote == 0, reviewer.hasDeclined != true else { return nil }
        return reviewer
    }

    func review(
        source: ReviewSource, reviewer: APIReviewer?, stage: ReviewStage,
        commentResolution: ReviewCommentResolution? = nil, isFollowedOnly: Bool = false,
        authoredStatus: AuthoredPullRequestStatus? = nil
    ) throws -> ReviewPullRequest {
        let url = try AzureDevOpsURL.make(
            organization: source.organization,
            path: [repository.project.name, "_git", repository.id, "pullrequest", String(pullRequestId)]
        )
        return ReviewPullRequest(
            number: pullRequestId, title: title, author: createdBy.displayName,
            organization: source.organization, project: repository.project.name,
            repository: repository.name, repositoryID: repository.id,
            createdAt: creationDate, url: url,
            isRequired: reviewer?.isRequired == true, isDraft: isDraft == true,
            reviewStage: stage, commentResolution: commentResolution, isFollowedOnly: isFollowedOnly,
            authoredStatus: authoredStatus
        )
    }
}

struct APIReviewThread: Decodable, Sendable {
    let id: Int?
    let publishedDate: Date?
    let comments: [APIReviewComment]
    let properties: APIReviewThreadProperties?
    let voterID: UUID?
    let status: APIReviewThreadStatus
    let isDeleted: Bool

    private enum CodingKeys: String, CodingKey {
        case id, publishedDate, comments, properties, identities, status, isDeleted
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decodeIfPresent(Int.self, forKey: .id)
        publishedDate = try container.decodeIfPresent(Date.self, forKey: .publishedDate)
        comments = try container.decode([APIReviewComment].self, forKey: .comments)
        properties = try container.decodeIfPresent(APIReviewThreadProperties.self, forKey: .properties)
        status = try container.decodeIfPresent(APIReviewThreadStatus.self, forKey: .status) ?? .unknown
        isDeleted = try container.decodeIfPresent(Bool.self, forKey: .isDeleted) ?? false
        if let directID = properties?.voterID {
            voterID = directID
        } else if let reference = properties?.voterReference, properties?.vote != 0 {
            let identities = try container.decode([String: APIReviewComment.Author].self, forKey: .identities)
            guard let identity = identities[reference] else {
                throw DecodingError.dataCorruptedError(
                    forKey: .identities, in: container, debugDescription: "Unresolved voting identity."
                )
            }
            voterID = identity.id
        } else {
            voterID = nil
        }
    }

    func hasReviewComment(from reviewerID: UUID, includingDeleted: Bool = true) -> Bool {
        comments.contains {
            $0.author.id == reviewerID
                && (includingDeleted || !$0.isDeleted)
                && $0.isReviewComment(inSystemThread: properties?.isSystemThread == true)
        }
    }
}

enum APIReviewThreadStatus: String, Decodable, Sendable {
    case unknown, active, fixed, closed, pending
    case wontFix = "wontfix"
    case byDesign = "bydesign"

    init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let name = try? container.decode(String.self) {
            self = Self(rawValue: name.lowercased()) ?? .unknown
        } else {
            switch try container.decode(Int.self) {
            case 1: self = .active
            case 2: self = .fixed
            case 3: self = .wontFix
            case 4: self = .closed
            case 5: self = .byDesign
            case 6: self = .pending
            default: self = .unknown
            }
        }
    }
}

struct APIReviewComment: Decodable, Sendable {
    let author: Author
    let commentType: String?
    let isDeleted: Bool
    let hasContent: Bool

    private enum CodingKeys: String, CodingKey {
        case author, commentType, isDeleted, content
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        author = try container.decode(Author.self, forKey: .author)
        commentType = try container.decodeIfPresent(String.self, forKey: .commentType)
        isDeleted = try container.decodeIfPresent(Bool.self, forKey: .isDeleted) ?? false
        let content = try container.decodeIfPresent(String.self, forKey: .content)
        hasContent = content?.contains { !$0.isWhitespace } == true
    }

    func isReviewComment(inSystemThread: Bool) -> Bool {
        let type = commentType?.lowercased()
        if type == "text" { return true }
        guard type == nil || type == "unknown", !inSystemThread else { return false }
        // Older discussion comments can omit the type without losing their authorship.
        return hasContent || isDeleted
    }

    struct Author: Decodable, Sendable {
        let id: UUID
    }
}

struct APIReviewThreadProperties: Decodable, Sendable {
    let voterID: UUID?
    let voterReference: String?
    let vote: Int?
    let isSystemThread: Bool

    private enum CodingKeys: String, CodingKey {
        case threadType = "CodeReviewThreadType"
        case voterID = "CodeReviewVotedByTfId"
        case voterReference = "CodeReviewVotedByIdentity"
        case vote = "CodeReviewVoteResult"
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let type = try container.decodeIfPresent(APIProperty<String>.self, forKey: .threadType)?.value
        isSystemThread = type?.isEmpty == false
        if type?.lowercased() == "voteupdate" {
            voterID = try container.decodeIfPresent(APIProperty<UUID>.self, forKey: .voterID)?.value
            voterReference = try container.decodeIfPresent(APIProperty<String>.self, forKey: .voterReference)?.value
            vote = try container.decode(APIProperty<VoteValue>.self, forKey: .vote).value.value
            guard vote == 0 || voterID != nil || voterReference != nil else {
                throw DecodingError.keyNotFound(CodingKeys.voterID, .init(
                    codingPath: decoder.codingPath, debugDescription: "Missing voting identity."
                ))
            }
        } else {
            voterID = nil
            voterReference = nil
            vote = nil
        }
    }

    private struct VoteValue: Decodable, Sendable {
        let value: Int

        init(from decoder: any Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let number = try? container.decode(Int.self) {
                value = number
            } else {
                let text = try container.decode(String.self)
                guard let number = Int(text) else {
                    throw DecodingError.dataCorruptedError(
                        in: container, debugDescription: "Invalid review vote."
                    )
                }
                value = number
            }
            guard [-10, -5, 0, 5, 10].contains(value) else {
                throw DecodingError.dataCorruptedError(
                    in: container, debugDescription: "Unsupported review vote."
                )
            }
        }
    }
}

private struct APIProperty<Value: Decodable & Sendable>: Decodable, Sendable {
    let value: Value

    private enum CodingKeys: String, CodingKey {
        case value = "$value"
    }
}
