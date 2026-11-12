# PR Context: Public repository ownership and approvals

## Summary

Prepare ReviewQ's public download documentation and assign every repository path to its owner.
Pair this change with GitHub's server-side protection of `main`; a CODEOWNERS file alone is
not an approval gate. Application code and release assets are unchanged.

## Key Decisions

| Decision | Rationale | Alternatives considered | Impact and tradeoffs |
| --- | --- | --- | --- |
| Require the sole code owner's review for every path. | A general one-review requirement could accept another collaborator's approval. | Requiring any reviewer; a custom Actions approval check. | Native code-owner enforcement avoids a forgeable, PR-controlled status check and needs no CI credentials. |
| Enforce reviews for administrators without a bypass. | GitHub bypass permissions concern the merge actor, not the PR author. | A general owner/admin bypass. | GitHub forbids self-approval, so owner-authored PRs also remain blocked under this strict policy. |
| Dismiss stale approvals when new commits arrive. | Approval must cover the current changes rather than an older revision. | Keeping approvals after subsequent pushes. | A changed PR must be reviewed again. |

## Key Flows

1. A contributor opens a PR targeting `main`; GitHub reads CODEOWNERS from the base branch.
2. The owner reviews the proposed changes. Other reviewers' approvals do not replace the
   required code-owner approval.
3. New commits invalidate previous approvals. Protection applies to administrators too,
   while direct pushes, force pushes, and branch deletion remain disallowed.

## FAQs

### How is this activated and verified?

Merge this bootstrap PR before enabling strict protection, since the current owner cannot
self-approve it. Then require one approval and code-owner review, dismiss stale approvals,
enforce protection for administrators, and disable force pushes and deletion on `main`.
Verify the persisted protection settings and GitHub's CODEOWNERS error endpoint.

### Can the owner bypass only their own PRs?

Not with native GitHub bypass permissions. No general bypass is enabled: self-authored PRs
cannot obtain the required review unless the repository's policy is deliberately changed.

### What remains unchanged?

No Swift code, app preferences, credentials, Azure DevOps request behavior, signing identity,
or published release assets change. Local, unrelated documentation edits are not part of this PR.

### How can this be diagnosed or changed?

Check `main`'s branch-protection settings and CODEOWNERS errors in GitHub. Changing this
Markdown document does not change server-side policy. A repository administrator must
explicitly update protection settings to change or roll back the gate.
