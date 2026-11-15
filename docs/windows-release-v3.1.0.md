# ReviewQ for Windows 3.1.0

Feature parity with [macOS ReviewQ 3.1.0](https://github.com/hritvikpatel1999/reviewq/releases/tag/v3.1.0),
adapted to native Windows behavior.

## New

- **Cached startup queue:** after the normal authentication step matches the
  account and queue configuration, restore the last successful queue while fresh
  data loads. The original update age remains visible during refresh. Failed
  refreshes preserve the queue and show an explicit error.
- **Changes-since-refresh dots:** green dots on the tray/taskbar icon, affected
  sections, and new or changed PRs. Source-commit, reviewer, and discussion
  fingerprints supplement visible-card changes. Removed PRs mark their former
  sections. Dots describe differences from the previous successful refresh,
  not unread state; opening a PR does not clear them.
- **Resumable native setup:** six screens for welcome, sources, verified access,
  optional review companions, preferences, and the ready queue. Existing
  configured users skip automatic setup. Replaying setup preserves saved
  settings and refuses to discard an open Settings editor.
- **Run Setup:** accent-colored queue-footer button and tray menu entry, available
  even before the first queue loads.

Cache restoration and change detection add no authentication requests, Azure
DevOps calls, or polling timers. Explicit setup checks use an isolated instance
of the same read-only loader; finishing uses the verified snapshot rather than
immediately repeating the network load.

## Privacy

The startup cache is local and **unencrypted**. It includes PR titles, author
names, repository/project names, links, badges, timestamps, compact hashes, and
hashed scopes. It excludes PATs, access tokens, descriptions, discussion bodies,
and assistant prompts. It expires after seven days, is limited to 2 MiB and
4,096 PRs, and is never partially saved. Saving Settings or removing a PAT clears
the prior cache. Settings offers manual clearing and reports storage failures.

Setup stores only non-secret draft preferences and progress. Closing setup clears
an unsaved PAT and verification; resuming requires another connection check.
Credentials are saved to Windows Credential Manager only when setup finishes.
Neither setup progress nor the queue cache is included in diagnostic exports.
Do not put secrets in locally saved review instructions.

## Packages and limits

Local validation: all **147 tests passed** (86 core, 61 Windows), including cache
privacy/bounds/authentication gating, hidden change fingerprints, resumable setup,
cancellation, save failures, native system-theme UI, and existing queue behavior.
All three architectures were packaged with PE architecture checks; x86 and x64
passed disposable-profile first-run setup and quit smoke tests.

Per-user installers and self-contained portable ZIPs are built for x86, x64,
and ARM64. Requires Windows 11 22H2 or newer. x86 means 32-bit process
compatibility, not a supported 32-bit Windows OS. No separate .NET runtime is
needed. Packages remain unsigned and may trigger SmartScreen warnings; compare
downloads with SHA256SUMS.txt.

Scout remains disabled because reliable Windows integration has not been
verified. Windows does not need the macOS Accessibility permission screen.
The app never changes PRs. Full interactive ARM64, accessibility,
high-contrast, and multi-DPI acceptance remain outstanding.

For setup and troubleshooting, see the [Windows guide](../windows/README.md).
