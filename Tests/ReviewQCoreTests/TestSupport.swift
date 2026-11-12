import Foundation
@testable import ReviewQCore

let reviewerID = "abcdef01-abcd-abcd-abcd-abcdef012345"
let authorID = "22222222-2222-2222-2222-222222222222"

func configuration(_ urls: String = "https://dev.azure.com/example/Project") -> ReviewConfiguration {
    var result = ReviewConfiguration()
    result.sourceURLs = urls
    return result
}

func connectionResponse(_ id: String = reviewerID) -> HTTPResult {
    HTTPResult(data: Data("""
    {"authenticatedUser":{"id":"\(id)","isActive":true}}
    """.utf8), status: 200)
}

func pullRequestJSON(
    number: Int = 1, vote: Int = 0, draft: Bool = false, status: String = "active",
    reviewer: String = reviewerID, author: String = authorID,
    declined: Bool = false, date: String = "2026-10-01T12:30:00.1234567Z",
    hasReviewer: Bool = true, repository: String = "Repository", repositoryID: String = "repo-id",
    project: String = "Project", reviewersJSON: String? = nil
) -> String {
    let reviewers = reviewersJSON ?? (hasReviewer
        ? #"{"id":"\#(reviewer)","vote":\#(vote),"isRequired":true,"hasDeclined":\#(declined)}"# : "")
    return """
    {
      "pullRequestId":\(number),"title":"Review fixture \(number)","status":"\(status)",
      "creationDate":"\(date)","isDraft":\(draft),
      "createdBy":{"id":"\(author)","displayName":"Fixture author"},
      "repository":{"id":"\(repositoryID)","name":"\(repository)","project":{"id":"project-id","name":"\(project)"}},
      "reviewers":[\(reviewers)]
    }
    """
}

func pullRequestsResponse(_ items: [String], status: Int = 200) -> HTTPResult {
    HTTPResult(data: Data("{\"value\":[\(items.joined(separator: ","))]}".utf8), status: status)
}

func reviewThreadsResponse(_ items: [String] = [], status: Int = 200) -> HTTPResult {
    HTTPResult(data: Data("{\"value\":[\(items.joined(separator: ","))]}".utf8), status: status)
}

func policyEvaluationJSON(
    number: Int = 1, typeID: String = "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd",
    status: String = "approved", enabled: Bool = true, blocking: Bool = true, deleted: Bool = false,
    evaluationID: String = UUID().uuidString, projectID: String = "project-id"
) -> String {
    """
    {"evaluationId":"\(evaluationID)",
     "artifactId":"vstfs:///CodeReview/CodeReviewId/\(projectID)/\(number)",
     "status":"\(status)",
     "configuration":{"isEnabled":\(enabled),"isBlocking":\(blocking),"isDeleted":\(deleted),
                      "type":{"id":"\(typeID)"}}}
    """
}

func policyEvaluationsResponse(
    _ items: [String] = [], status: Int = 200, continuation: String? = nil
) -> HTTPResult {
    HTTPResult(
        data: Data("{\"value\":[\(items.joined(separator: ","))]}".utf8),
        status: status, continuationToken: continuation
    )
}

func followedIdentityJSON(
    email: String = "alex@example.com", id: String = authorID,
    account: String? = nil, active: Bool = true, group: Bool = false
) -> String {
    """
    {"id":"\(id)","isActive":\(active),"isContainer":\(group),
     "properties":{"Mail":{"$value":"\(email)"},"Account":{"$value":"\(account ?? email)"}}}
    """
}

func identitiesResponse(_ items: [String], continuation: String? = nil) -> HTTPResult {
    HTTPResult(
        data: Data("{\"value\":[\(items.joined(separator: ","))]}".utf8),
        status: 200, continuationToken: continuation
    )
}

func projectsResponse(_ names: [String]) -> HTTPResult {
    HTTPResult(
        data: Data("{\"value\":[\(names.map { #"{"id":"\#($0.lowercased())","name":"\#($0)"}"# }.joined(separator: ","))]}".utf8),
        status: 200
    )
}

func voteThreadJSON(
    voter: String = reviewerID, vote: Int = 10, numeric: Bool = false,
    useIdentityReference: Bool = false, id: Int? = nil, publishedDate: String? = nil,
    lastUpdatedDate: String? = nil
) -> String {
    let value = numeric ? String(vote) : "\"\(vote)\""
    let voterProperty = useIdentityReference
        ? "\"CodeReviewVotedByIdentity\":{\"$type\":\"System.String\",\"$value\":\"1\"}"
        : "\"CodeReviewVotedByTfId\":{\"$type\":\"System.String\",\"$value\":\"\(voter)\"}"
    let identitiesField = useIdentityReference ? ",\"identities\":{\"1\":{\"id\":\"\(voter)\"}}" : ""
    let idField = id.map { "\"id\":\($0)," } ?? ""
    let dateField = publishedDate.map { "\"publishedDate\":\"\($0)\"," } ?? ""
    let updatedField = lastUpdatedDate.map { "\"lastUpdatedDate\":\"\($0)\"," } ?? ""
    return """
    {
      \(idField)\(dateField)\(updatedField)
      "comments":[{"author":{"id":"\(authorID)"},"commentType":"system"}],
      "properties":{
        "CodeReviewThreadType":{"$type":"System.String","$value":"VoteUpdate"},
        \(voterProperty),
        "CodeReviewVoteResult":{"$type":"System.String","$value":\(value)}
      }\(identitiesField)
    }
    """
}

func reviewCommentThreadJSON(
    author: String = reviewerID, type: String? = "text", deleted: Bool = false,
    content: String = "", threadType: String? = nil,
    statusJSON: String? = nil, threadDeleted: Bool = false
) -> String {
    let typeField = type.map { ",\"commentType\":\"\($0)\"" } ?? ""
    let threadTypeField = threadType.map {
        ",\"CodeReviewThreadType\":{\"$type\":\"System.String\",\"$value\":\"\($0)\"}"
    } ?? ""
    let statusField = statusJSON.map { "\"status\":\($0)," } ?? ""
    return """
    {
      \(statusField)"isDeleted":\(threadDeleted),
      "comments":[{"author":{"id":"\(author)"}\(typeField),"isDeleted":\(deleted),"content":"\(content)"}],
      "properties":{
        "Microsoft.TeamFoundation.Discussion.SupportsMarkdown":{"$type":"System.Int32","$value":1}\(threadTypeField)
      }
    }
    """
}

actor StubTransport: HTTPTransport {
    private var responses: [HTTPResult]
    private var histories: [Int: HTTPResult]
    private var policies: [Int: [HTTPResult]]
    private(set) var requests: [URLRequest] = []

    init(
        _ responses: [HTTPResult], histories: [Int: HTTPResult] = [:], policies: [Int: [HTTPResult]] = [:]
    ) {
        self.responses = responses
        self.histories = histories
        self.policies = policies
    }

    func setHistory(_ response: HTTPResult, for number: Int) {
        histories[number] = response
    }

    func setPolicies(_ responses: [HTTPResult], for number: Int) {
        policies[number] = responses
    }

    func send(_ request: URLRequest) async throws -> HTTPResult {
        requests.append(request)
        if let url = request.url, url.path.hasSuffix("/threads") {
            guard let number = url.path.split(separator: "/").dropLast().last.flatMap({ Int($0) }) else {
                throw ReviewQError.invalidResponse("Invalid synthetic review-history URL")
            }
            return histories[number] ?? reviewThreadsResponse()
        }
        if let url = request.url, url.path.hasSuffix("/policy/evaluations") {
            let artifactID = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?
                .first { $0.name == "artifactId" }?.value
            guard let number = artifactID?.split(separator: "/").last.flatMap({ Int($0) }),
                  var responses = policies[number], !responses.isEmpty else {
                throw ReviewQError.invalidResponse("Unexpected synthetic reviewer-policy request")
            }
            let response = responses.removeFirst()
            policies[number] = responses
            return response
        }
        guard !responses.isEmpty else { throw ReviewQError.invalidResponse("Unexpected test request") }
        return responses.removeFirst()
    }
}

actor StubAuthorization: AuthorizationProviding {
    private(set) var calls = 0
    private(set) var invalidations = 0
    private var fixedHeader: String?

    init(header: String? = nil) { fixedHeader = header }
    func setHeader(_ header: String) { fixedHeader = header }

    func authorization(for configuration: ReviewConfiguration) -> String {
        calls += 1
        if let fixedHeader { return fixedHeader }
        return "Bearer fixture-\(calls)"
    }

    func invalidate() { invalidations += 1 }
}

struct StubPATStore: PATStoring {
    var token: String? = "synthetic-token"
    var onSave: (@Sendable (String) -> Void)?
    func load() -> String? { token }
    func save(_ token: String) throws {
        _ = try KeychainPATStore.validate(token)
        onSave?(token)
    }
    func remove() {}
}

actor StubCommandRunner: CommandRunning {
    let response: Data
    private(set) var calls: [[String]] = []

    init(_ response: Data) { self.response = response }
    func run(executable: URL, arguments: [String]) -> Data {
        calls.append(arguments)
        return response
    }
}

actor StubReviewLoader: ReviewLoading {
    private(set) var identityInvalidations = 0
    func invalidateIdentityCache() { identityInvalidations += 1 }

    enum Outcome: Sendable {
        case success(ReviewSnapshot)
        case failure(ReviewQError)
    }

    private var outcomes: [Outcome]
    private(set) var calls = 0

    init(_ outcomes: [Outcome]) { self.outcomes = outcomes }
    func load(configuration: ReviewConfiguration) throws -> ReviewSnapshot {
        calls += 1
        guard !outcomes.isEmpty else { throw ReviewQError.invalidResponse("Unexpected test refresh") }
        switch outcomes.removeFirst() {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

}

actor DeferredReviewLoader: ReviewLoading {
    func invalidateIdentityCache() {}

    private var pending: CheckedContinuation<ReviewSnapshot, any Error>?
    private var started: CheckedContinuation<Void, Never>?

    func load(configuration: ReviewConfiguration) async throws -> ReviewSnapshot {
        try await withCheckedThrowingContinuation { continuation in
            pending = continuation
            started?.resume()
            started = nil
        }
    }

    func waitUntilStarted() async {
        if pending != nil { return }
        await withCheckedContinuation { started = $0 }
    }

    func finish(_ snapshot: ReviewSnapshot) {
        pending?.resume(returning: snapshot)
        pending = nil
    }
}
