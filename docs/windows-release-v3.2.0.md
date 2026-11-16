# ReviewQ for Windows 3.2.0

## New and improved

- **Scout review handoff:** start a new review chat from a PR's Scout action.
  ReviewQ detects standard per-user and system installations, including x64
  Scout from the x86 Windows app. It verifies the complete PR prompt and invokes
  Send once, then checks that Scout accepted the submission.
- **Reliable composer input:** Scout's empty placeholder newline no longer
  triggers an existing-draft warning. Process-targeted Unicode input and
  thread-scoped Shift+Enter line breaks replace the text-setting API Scout
  silently ignores. Existing meaningful drafts remain protected; unreadable
  composers, lost focus, changed drafts, and uncertain submissions stop with an
  explicit error. The app does not use global keystrokes or the clipboard.
- **Mac-inspired setup:** centered headings, rounded cards, pill buttons,
  progress markers, and a segmented CLI/PAT selector. A green success check
  accompanies a verified connection. Setup remains resumable and preserves
  saved preferences and credential-handling safeguards.
- **Companion artwork:** refreshed Edge, Scout, and Copilot icons with compact
  queue actions and subtle hover growth. High-contrast mode retains
  foreground-bound vector alternatives.
- **Companion preferences:** edit Scout's additional review instructions in
  Setup or Settings. Scout and Copilot launches share a single-flight guard to
  prevent overlapping handoffs. Diagnostics identify Scout failures without
  exporting prompts or private PR data.

This release also includes the authenticated startup cache, changes-since-refresh
indicators, six-screen verified setup, and right-aligned Run Setup footer from
the [3.1.0 parity work](windows-release-v3.1.0.md). Assigned reviews retain
repository grouping; followed/authored queues retain their flat author/repository
presentation. Azure DevOps requests remain read-only.

## Downloads

Per-user installers and self-contained portable ZIPs are available for **x64**,
**ARM64**, and **x86**. Most Intel/AMD PCs should use the x64 installer.
Windows 11 22H2 or newer is required. x86 means a 32-bit process on a supported
Windows installation, not support for a 32-bit Windows OS. No separate .NET
runtime is needed.

Packages remain unsigned and may trigger SmartScreen warnings. Verify downloads
against the release's `SHA256SUMS.txt`. Existing settings and credentials are
preserved during upgrades.

## Verification and limitations

- All **171 local tests passed**: 86 core and 85 Windows tests.
- All three architecture packages were built and checked for the correct PE
  architecture; x86 and x64 passed disposable-profile desktop startup smoke.
- Exact Scout prompt entry was verified against the installed Scout app using
  both x86 and x64 clients, including multiline Unicode, emoji, and blank lines.
  Validation drafts were removed without submitting test chats. Submission
  safety and acknowledgement are covered by automated state-machine and
  synthetic native-accessibility tests, not a live agent execution.
- The [Windows workflow](https://github.com/hritvikpatel1999/reviewq-win/actions/workflows/windows.yml) builds, tests, and
  smoke-tests each architecture, including a native Windows ARM runner. Consult
  its results for the release commit.
- Full interactive ARM64 acceptance, complete accessibility/high-contrast/DPI
  coverage, and all live authentication/companion combinations remain
  outstanding. Scout must be signed in, expose its English chat controls, and
  run at the same normal privilege level as ReviewQ.

See the [Windows guide](../windows/README.md) for setup and troubleshooting.
