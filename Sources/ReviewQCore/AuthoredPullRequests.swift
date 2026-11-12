import Foundation

public struct AuthoredPullRequestStatus: Equatable, Sendable {
    public let activeCommentCount: Int
    public let hasRequiredApprovals: Bool

    static func from(
        threads: [APIReviewThread], reviewers: [APIReviewer], evaluations: [APIPolicyEvaluation]
    ) throws -> Self {
        var activeComments = 0
        for thread in threads {
            guard !thread.isDeleted, thread.properties?.isSystemThread != true,
                  thread.comments.contains(where: {
                      !$0.isDeleted && $0.isReviewComment(inSystemThread: false)
                  }) else { continue }
            switch thread.status {
            case .active, .pending: activeComments += 1
            case .fixed, .closed, .wontFix, .byDesign: break
            case .unknown:
                throw ReviewQError.invalidResponse("Azure DevOps authored discussion status")
            }
        }
        var approvalsSatisfied = true
        for reviewer in reviewers where reviewer.isRequired == true {
            guard [-10, -5, 0, 5, 10].contains(reviewer.vote) else {
                throw ReviewQError.invalidResponse("Azure DevOps required reviewer vote")
            }
            if reviewer.hasDeclined == true || (reviewer.vote != 5 && reviewer.vote != 10) {
                approvalsSatisfied = false
            }
        }
        for evaluation in evaluations where evaluation.configuration.type.isReviewerPolicy {
            let policy = evaluation.configuration
            guard policy.isEnabled, policy.isBlocking, policy.isDeleted != true else { continue }
            switch evaluation.status.lowercased() {
            case "approved", "notapplicable": break
            case "queued", "running", "rejected", "broken": approvalsSatisfied = false
            default:
                throw ReviewQError.invalidResponse("Azure DevOps reviewer policy status")
            }
        }
        return Self(activeCommentCount: activeComments, hasRequiredApprovals: approvalsSatisfied)
    }
}

struct APIPolicyEvaluation: Decodable, Sendable {
    let evaluationId: UUID
    let artifactId: String
    let status: String
    let configuration: Configuration

    struct Configuration: Decodable, Sendable {
        let isEnabled: Bool
        let isBlocking: Bool
        let isDeleted: Bool?
        let type: PolicyType
    }

    struct PolicyType: Decodable, Sendable {
        let id: UUID

        var isReviewerPolicy: Bool {
            // Minimum approval count and automatically included reviewers.
            switch id.uuidString.lowercased() {
            case "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd",
                 "fd2167ab-b0be-447a-8ec8-39368250530e":
                true
            default:
                false
            }
        }
    }
}
