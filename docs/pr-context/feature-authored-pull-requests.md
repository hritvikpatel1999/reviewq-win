# PR Context: ReviewQ v3.0.0 authored pull requests

## Summary

Release 3.0.0/build 6 adds the default-off **I'm the Author** queue and fixes the live refresh
failure caused by URL-encoded Azure DevOps policy artifact identifiers. It preserves existing
assigned/followed review queues, settings, app identity, and read-only permissions. Copilot Terminal,
automatic assistant-session reuse, merge actions, and comment-writing actions are out of scope.

## Key Decisions

| Decision | Rationale | Alternatives considered | Impact and tradeoffs |
| --- | --- | --- | --- |
| Make authored PRs a separate, default-off, collapsible section with browser-only cards. | The requested author workflow is distinct from reviewing other people's PRs. | Mixing authored PRs into repository review groups or adding merge/assistant actions was not requested. | Existing installations make no authored requests until enabled. Authored cards do not increase pending-review counts. |
| Count unresolved discussion threads, including pending, rather than individual replies. | Replies in one discussion should not inflate the outstanding-discussion count. | Individual-comment counts were rejected in the implementation's stated assumptions. | Deleted/system discussions are excluded; unknown eligible statuses fail explicitly instead of falsely showing Merge Ready. |
| Require individually required reviewer votes and Azure DevOps blocking reviewer-policy evaluations. | Server policy evaluation accounts for minimum approvals, author-vote rules, and vote resets. | Counting reviewer votes alone would miss policy requirements. | Builds and non-reviewer completion policies are deliberately ignored. Merge Ready is not a guarantee of mergeability. |
| Reuse identity caches and per-refresh project discovery, but refresh discussions and approvals. | Stable identity lookups can be reused without showing stale PR readiness. | Caching PR state across refreshes is not part of this feature. | Opt-in adds creator-filtered project queries and per-PR discussions/policy reads, retaining the four-PR enrichment limit. |
| Percent-decode returned policy artifact IDs exactly once before correlation. | A live reproduction showed valid URL-encoded IDs failing the original exact comparison. | Removing the PR-identity guard would allow unrelated policy results to affect readiness. | Raw and equivalently encoded IDs pass; wrong-PR, malformed, and double-encoded IDs remain rejected. |
| Preserve internal bundle, Keychain, preference, and diagnostics identity. | Updates must retain existing user configuration and credentials. | A new app identity was not requested. | No migration is needed; older configurations default the new setting to false. |

## Key Flows

### Opt-in authored refresh

1. Save **Show PRs I've authored** in Settings. Missing settings keys decode to false.
2. Load assigned reviews, then authored PRs across accessible projects in each configured organization,
   using that organization's cached current-user identity and shared project discovery.
3. Filter to active PRs, apply the existing draft preference, paginate creator queries, and enrich
   each authored PR with fresh discussions and complete reviewer-policy evaluations.
4. Validate that policy results refer to the requested PR, then compute active-discussion counts and
   approval readiness. Relevant unknown or incomplete results remain explicit refresh errors.
5. Load followed authors, skipping self-follow duplication, and publish one deduplicated newest-first
   snapshot. A failure retains the previous successful snapshot with an error indicator.
6. Display authored cards below the review queues. The section starts collapsed, retains expansion
   while the app runs, and offers only browser navigation. Turning it off stops its extra requests.

### Release packaging

1. The existing packaging script reads version 3.0.0/build 6 from the bundle plist.
2. Build ARM64 and Intel executables, package the existing icon, strictly verify ad-hoc signatures,
   and produce separate versioned ZIPs plus SHA256SUMS.
3. Merge through a GitHub pull request before tagging the merged main commit and publishing assets.
   The existing owner PR-only approval bypass is unchanged; no direct push to main is required.

## FAQs

### What does Merge Ready mean?

A non-draft authored PR has no active/pending human discussions and satisfies required reviewer
votes plus enabled blocking minimum-reviewer/required-reviewer policies. Builds, conflicts, linked
work items, and external status checks are not evaluated. No approvals are needed when the PR has
no approval requirements.

### What are the permissions and performance implications?

The new requests are GET-only and require Code (Read) and Project and Team (Read) for PAT users.
No extra authored requests occur while opted out. Organization identity and project discovery are
reused; PR state is not cached across refreshes. Policy diagnostics record safe categories, never
artifact IDs, response payloads, or PR details.

### What was validated?

The [documented release profile](../../README.md#build-and-open) passed **199 Apple Silicon tests**
and **199 Intel/Rosetta tests**. Coverage includes migration/default-off behavior, per-organization
creator queries, pagination/deduplication, self-follow behavior, discussion counts, reviewer policies,
encoded artifact correlation and rejection cases, fresh readiness on refresh, diagnostic privacy,
settings binding, section placement/collapse retention, and browser-only interactions.

The same five host-dependent OCR checks remain present but excluded: three accurate-Vision layout
checks and two existing small-caption settings checks. No assertions were weakened. Release review
moved the policy-diagnostics privacy test to class scope; its execution and success were confirmed
in both architecture logs. Intel validation uses Rosetta rather than physical Intel hardware.

Both actual ZIP payloads passed checksum, architecture, strict ad-hoc signature, version/build,
macOS 14 deployment-minimum, background-app, icon-resource equality, packaged-executable equality,
and compiled/packaged UUID checks. The preceding privacy-safe live refresh also verified the
encoded artifact-ID correction without retaining private PR responses.

### How can this be rolled back or diagnosed?

Turn off **Show PRs I've authored**, then choose **Save and Refresh** to restore the previous request
flow. Existing Diagnostics reports refresh, HTTP, decoding, and policy-operation failure categories;
failed refreshes preserve the last successful queue. The previous v2.3.0 release remains available
and ignores the new settings field.

### Are these notarized builds or a new chat integration?

No. Release builds remain ad-hoc signed, not Developer ID signed or Apple-notarized, and require
macOS 14 or later. Scout cold-launch limitations and Accessibility approval requirements remain.
Existing Scout and VS Code actions still start new chats; no CLI session mapping is added.
