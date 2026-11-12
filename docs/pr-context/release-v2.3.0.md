# PR Context: ReviewQ v2.3.0

## Summary

Release 2.3.0/build 5 fixes the generic application icon shown by macOS launchers and packages the
existing opt-in followed-author queue, persistent identity caches, and 15-minute default refresh.
It does not add GitHub integration, a My PRs view, or PR-writing/merge actions.

## Key Decisions

| Decision | Rationale | Alternatives considered | Impact and tradeoffs |
| --- | --- | --- | --- |
| Bundle an original `AppIcon.icns` and declare `CFBundleIconFile`. | The app previously had neither, so launchers used the generic placeholder. | Changing only the menu-bar symbol would not fix bundle icon lookup. | All host/ARM/Intel packaging paths copy the same 16-1024-pixel resource. A local Swift generator preserves the editable artwork. |
| Following is off by default and separate from assigned reviews. | The requested feature must not change existing filters or add author requests for opted-out users. | Mixing followed PRs into repository review groups was not the requested UX. | Matching authors are searched across accessible projects in configured organizations; additional permissions and requests are documented. |
| Persist resolved identities for seven days and definitive no-matches for fifteen minutes. | Stable IDs do not need re-resolution on every refresh, but email mappings can change. | Hour-long positive TTLs were rejected in the preceding caching discussion. | Account/tenant/source/email context isolates entries. PR lists and history are not cached across refreshes. |
| Keep explicit identity re-lookup separate from saving Settings. | Switching accounts or reassigning an email needs an immediate escape hatch without changing a draft. | Making ordinary Refresh clear identities would defeat caching. | The loader invalidates its own cache and credentials; superseded lookups cannot repopulate cleared entries. |
| Preserve saved intervals and internal app identity. | Existing settings, Keychain access, diagnostics, and login identity must survive the update. | A saved five-minute choice is not automatically migrated. | Only new configurations default to fifteen minutes; bundle ID remains `dev.prbar.app`. |
| Allow the repository owner to bypass approval through pull requests only. | The owner requested a merge bypass because GitHub forbids self-approval. | A general administrator bypass would also permit direct pushes; temporarily waiving all reviews would not provide the requested ongoing exception. | The active GitHub ruleset uses the Admin role, verified to contain only the personal-repository owner. Other contributors still need a code-owner review, and direct pushes, force pushes, and deletion remain blocked. |

## Key Flows

### Application icon and release packaging

1. The checked-in icon contains standard and Retina representations from 16 through 1024 pixels.
2. Packaging copies the executable, plist, and icon before signing, for every architecture.
   It then updates the bundle timestamp so existing launcher icon caches notice the change.
3. macOS resolves `AppIcon` from the app's Resources directory. The menu-bar symbol remains unchanged.
4. Executable installation uses a new inode to avoid stale code-signing pages from a previously
   launched copy; rebuilding a running output remains prohibited.

### Following and refreshing

1. Validate semicolon-separated emails before saving preferences or changing a PAT.
2. Load assigned reviews, then resolve followed authors separately for each configured organization.
3. Reuse valid identities; otherwise fetch and validate complete identity responses. Errors and
   ambiguous/incomplete results are never cached as no-match.
4. Query active PRs by creator, excluding IDs already included before fetching additional history.
5. Publish one deduplicated, newest-first snapshot. Any failure retains the previous successful queue.
6. Show followed-only cards below repositories in a collapsible section with `{author} in {repo-name}`.

### Repository-owner merge bypass

1. All changes to `main`, including the owner's, must arrive through a pull request.
2. Other contributors need one approving code-owner review; new commits dismiss stale approvals.
3. The owner can explicitly bypass approval when merging a PR, but cannot bypass direct-push,
   force-push, or branch-deletion restrictions.
4. The replacement GitHub ruleset is activated and verified before retiring classic protection.
   The ownership file remains unchanged; this does not add merge actions to the app.

## FAQs

### What is persisted, and what remains fresh?

Saved settings and bounded identity metadata persist locally. Raw authorization headers/tokens,
full identity responses, PR contents, and review history are not stored in the identity cache.
Stable CLI tenant/object claims partition the cache only; Azure DevOps still supplies the actual
reviewer/author UUIDs and authenticates network requests. Cache diagnostics omit private key data.

### What was validated?

The release profile passed 173 ARM tests and 173 Intel/Rosetta tests. It includes both new icon
tests and following, persistence, exact expiry, account isolation, invalidation, privacy, and
fresh-history regression coverage. Five host-dependent OCR checks remain present but were excluded:
three accurate-Vision layout checks and two existing small-caption bitmap OCR checks.

Both actual ZIP payloads passed checksum, architecture, strict code-signature, version/build,
macOS 14 deployment-minimum, resource equality, and native `NSWorkspace` branded-icon checks.
Intel validation uses Rosetta rather than physical Intel hardware.

### Are these notarized builds or new write permissions?

No. Builds remain ad-hoc signed, not Developer ID signed or notarized. The app remains read-only
toward Azure DevOps. Following additionally needs Identity (Read) and Project and Team (Read)
when using a PAT. Scout cold-launch limitations and normal macOS approval requirements remain.

### How can this be rolled back or diagnosed?

Disable following to restore the assigned-only request flow, or use Refresh identities to discard
stale mappings. Existing Diagnostics reports safe failure categories. Installing the previous
v2.2.0 release preserves the existing configuration/PAT identity; its decoder ignores the new
settings fields.

The GitHub ruleset is configured separately from these files. Removing its PR-only owner bypass
restores the previous strict review policy, including the self-approval deadlock. Keep the ruleset
active to preserve mandatory PRs and the restrictions on direct pushes, force pushes, and deletion.
