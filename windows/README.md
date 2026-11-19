# ReviewQ for Windows

Native WPF/.NET 10 LTS application for **Windows 11 22H2 or newer**.
Packages are available as **x86 (32-bit), x64, and ARM64**. Use x64 for Intel/AMD
Windows 11 PCs and ARM64 for Windows on ARM; x86 is a compatibility build for
32-bit processes, not support for a 32-bit Windows 11 operating system.
The Swift/macOS application is unchanged. This port reads Azure DevOps; it never
votes, comments, merges, or changes pull requests.

## Install or run

Use the package matching **Settings > System > About > System type**:

| CPU | Portable package | Per-user installer |
|---|---|---|
| x86 compatibility (32-bit process) | `ReviewQ-4.0.0-win-x86.zip` | `ReviewQ-4.0.0-win-x86-setup.exe` |
| Intel/AMD x64 | `ReviewQ-4.0.0-win-x64.zip` | `ReviewQ-4.0.0-win-x64-setup.exe` |
| Windows on ARM | `ReviewQ-4.0.0-win-arm64.zip` | `ReviewQ-4.0.0-win-arm64-setup.exe` |

Download packages and checksums from the
[v4.0.0 Windows release](https://github.com/hritvikpatel1999/reviewq-win/releases/tag/v4.0.0).

Build outputs are in `dist\windows` relative to the repository root.
For portable use, extract **the entire ZIP** into a permanent folder and open
`ReviewQ.exe`. Do not copy just the executable. Moving a portable installation
requires toggling startup off and on to update its registered path.

The installer needs no administrator privileges and installs under
`%LOCALAPPDATA%\Programs\ReviewQ`, with a Start menu shortcut.
All packages include the matching native .NET runtime.

These packages are **unsigned**. Windows SmartScreen may show a warning.
Verify provenance and compare `Get-FileHash -Algorithm SHA256 <package>` against
`SHA256SUMS.txt`; a matching checksum is not a publisher signature.
Publisher signing and reputation require the owner's signing identity/service.
There is no auto-updater or Microsoft Store package.
Update notifications are available, but all installation remains manual.

## Visibility and background behavior

- Interactive launch opens the queue, with a normal taskbar button.
- First launch also opens resumable setup. Closing the queue keeps polling in the tray.
- Clicking the tray icon opens/focuses the queue. Its menu includes Refresh,
  Settings, Run Setup, Diagnostics, and **Quit ReviewQ**, which ends background work.
- Windows may hide the tray icon behind overflow. Drag ReviewQ beside the clock,
  or use Windows taskbar tray settings. The app cannot force permanent visibility.
- Pin ReviewQ yourself from its taskbar button or Start menu if desired.
- **Ctrl+Alt+Q** toggles the queue. Change it or disable it in Settings. Conflicts
  are visible; use **Retry saved shortcut** after freeing the combination.
- Startup at sign-in is opt-in and starts quietly in the tray. An unconfigured
  app or startup error still opens the window.
- Reopening the app activates the existing per-user instance.
- Queue, Setup, Settings, and Diagnostics all follow the Windows light/dark theme.
- The queue header follows the Mac layout: **Setup, Refresh, Settings, Quit**.
  Right-click Settings for Diagnostics, or use the tray menu. The footer has
  a live update age and a link to the creator's GitHub profile.
- A pending count appears in the title/tray tooltip; a taskbar overlay indicates
  count or errors where Windows supports it. Only assigned reviews count.
- Green tray/taskbar, section, and PR dots show changes since the previous
  successful refresh. Opening a PR does not clear them, and failed refreshes
  preserve them. Removed PRs mark their former sections. These are not unread dots.

## First-run setup

Six screens guide you through welcome, sources, authentication, optional
companions, preferences, and the ready queue. **Test connection** verifies access
using the same read-only loader. Preference changes recheck the selected queues.
**Open my queue** saves settings and reuses the verified result, avoiding an
immediate duplicate fetch. Your default browser is sufficient; VS Code/Copilot
are optional. Setup detects Scout in the standard per-user and system installation
folders, even when it is not running. Its queue action starts a new Scout review chat.

The setup layout follows the macOS app's centered headings, rounded cards,
six progress markers and pill-shaped navigation, with a native Windows title
bar and scrolling on smaller displays. CLI and PAT authentication use a
segmented selector and show only the relevant fields. Edge, Scout and Copilot
actions use the supplied reference artwork; queue icons stay compact and grow
slightly on hover. High-contrast mode uses system-colored vector symbols.
Successful connection checks show a green tick beside the confirmation text
(a system-colored tick in high-contrast mode); pending and failed checks do not.

Closing setup preserves non-secret choices and progress, but clears an unsaved
PAT and connection verification. Resume requires a fresh check. Existing
configured users skip automatic setup. **Setup** in the queue header or **Run Setup** in the tray
replays it; **Keep existing settings** discards the draft without changing saved
preferences. Save/close any Settings editor before replaying setup.
The compact footer shows the update age on the left without a tray-help paragraph.

Settings uses the Mac-inspired grouped-card layout: review sources and segmented
CLI/PAT authentication first, followed by optional queues, per-assistant review
instructions with character counts, review-queue preferences, reminders, startup,
caches, and app updates. Only the selected authentication fields are shown;
switching methods retains the unsaved draft. The scrolling body has a fixed
footer with **Diagnostics** on the left and **Save and Refresh** on the right.
Reminder weekdays use compact selectable chips, with labeled time rows and a
live reminders-per-day count. Platform-specific setup/retry actions remain under
**Windows notification options**; errors and saved-schedule status stay visible.
When **Enable review reminders** is unchecked, the other reminder controls stay
visible but greyed out and disabled, including test/notification options.
Checking it re-enables them without clearing the selected days, times, or sound.
Enabling the draft still does not apply a schedule until you save.

## App update notifications

ReviewQ checks the public **reviewq-win** latest stable release on launch, resume,
or queue opening when due (normally once per 24 hours). This is separate from
Azure DevOps authentication and queue refreshes; no PAT, CLI token, PR data, or
review instructions are sent to GitHub. Smoke tests skip all app-update requests.

Newer versions show a compact **View Updates** banner beneath the header.
Dismiss hides that version only; a later version appears again. **Settings >
App updates** shows the installed version, last successful check, available
release, errors, and **Check for Updates**. A dismissed release remains available
there. The link opens the exact official Windows release page in your default
browser; it never downloads or starts an installer. Choose the correct CPU
package, verify the checksums, and install it yourself.

Stable versions are compared numerically; drafts and prereleases are not offered.
Conditional ETag requests reuse validated cached results. Network or server
errors preserve the last known release and successful-check time, with automatic
retry delays from 15 minutes up to one day. Manual checks bypass normal scheduling,
but respect GitHub rate-limit/Retry-After cooldowns. Only one check runs at a time.
Invalid saved update information is cleared with an explicit error. The bounded,
atomic local update cache contains public release metadata, scheduling, and your
dismissed version, never Azure DevOps credentials; it is not exported in logs.

## Local PR archives and scheduled reminders

The Windows v4 source ports the archive/reminder features from macOS
[v4.0.0](https://github.com/hritvikpatel1999/reviewq/releases/tag/v4.0.0),
pinned to commit `d02408b7ee1047520ac0de20c8fa1a6514b475e9`.

### Archives

Every active PR has a white-backed blue folder **Archive** action, including
followed and authored PRs. Archiving is local and reversible, never an Azure
DevOps change or additional request. It immediately removes the PR from active
sections, assigned-review counts, and both sides of refresh-change comparisons.
The raw queue/cache remains intact. Restoring can reveal a genuine change from
the preceding successful network refresh.

The bottom **Archives** section starts collapsed, remains reachable when all
PRs are archived, and shows current snapshot metadata newest-first with repository
context, **Restore**, and a generic browser action. It has no AI actions.
Authored active cards retain the explicit Edge shortcut.

Only canonical PR IDs are saved, under the authenticated account/source scope,
with a global limit of 4,096 IDs and 512 KiB. Choices survive absent/reappearing
PRs and are separate between scopes. This is not a historical archive: PRs no
longer returned by the configured sources/filters are not displayed. Corrupt
state is cleared with an explicit error; failed writes or full storage do not
silently hide PRs.

### Review reminders

Setup Preferences and Settings share controls for enabling reminders, weekdays,
up to eight distinct local 24-hour times per day, and optional sound. Suggested
defaults are Monday-Friday at **10:30 and 15:30**, disabled with sound off.
The number of time rows is the reminders-per-day count.

**Set up / check notifications** registers ReviewQ with Windows and reports
permission status. **Send test reminder** requests one immediate generic
notification using the draft sound choice, without enabling or saving a schedule.
On first use Windows may report **NotInitialized** until that first notification
creates its per-app profile; this is shown distinctly from blocked permission.
**Windows notification settings** opens the system controls. Permission actions
do not save drafts. Only **Save and Refresh** or **Open my queue** commits the
schedule. Reminder edits do not invalidate a verified Azure DevOps queue.
**Retry saved schedule** retries saved choices, not unsaved edits.

Windows uses one per-user Task Scheduler task, `ReviewQ-Reminders-<user SID>`,
with weekly calendar triggers. It runs without stored credentials or elevation.
Its `--deliver-reminder` action reads saved reminder choices and sends only:

> Review reminder  
> Take a moment to check your ReviewQ.

This delivery mode does not initialize authentication, fetch PRs, or check app
updates. Protocol activation, `reviewq-reminder:queue`, opens/focuses the queue
rather than toggling it or routing a cold launch to Setup/Settings. Normal queue
opening then uses the existing refresh/cache path.

Quitting ReviewQ does **not** disable saved reminders. Disable reminders and save
to remove the task, including before rolling back to a version without reminders.
Windows must remain signed in and awake; the task neither wakes the PC nor
delivers a backlog of missed reminders. Focus/Do Not Disturb, system permissions,
and notification/sound settings control actual display and sound.

The app's Start menu shortcut carries the Windows notification identity; a
per-user protocol handler supports cold activation. Moving a portable app
requires checking notifications and re-saving the enabled schedule to update
paths. Portable users should disable/save reminders, then run
`.\ReviewQ.exe --remove-reminder-registration` before deleting the app.
The installer removes its own task/protocol registration on uninstall, preserving
registrations pointing to a different installation. Saved choices remain for
reinstall; invalid reminder choices disable only reminders with a visible warning,
preserving account settings.

## Configure Azure DevOps

Enter one organization or project URL per line, for example:

```text
https://dev.azure.com/your-organization/your-project
https://dev.azure.com/another-organization
```

Legacy `https://organization.visualstudio.com/DefaultCollection/project` URLs
are accepted and normalized. Queries, credentials, non-HTTPS URLs, and repository
URLs are rejected.

Choose either:

1. **AzureCli**: install Azure CLI separately and run
   `az login --allow-no-subscriptions`. Add an optional Entra tenant GUID/domain.
   The app obtains an Azure DevOps token for resource
   `499b84ac-1321-427f-aa17-267ca6975798`, respects expiry, and retries authentication
   once after HTTP 401. It does not open an unattended interactive login.
2. **PersonalAccessToken**: create a PAT for the appropriate organization(s)
   with **Code (Read)** permissions and sufficient project/identity access.
   Paste only its value into New PAT. Leaving that field blank retains the saved
   token. Azure CLI is not required for PAT authentication.

The PAT is stored only as the current user's Windows Credential Manager generic
credential `ReviewQ/AzureDevOps/PAT`. Remove it with **Remove saved PAT** in Settings.
No automatic macOS settings/Keychain migration is attempted.

If Refresh reports **Azure CLI authentication failed**, open PowerShell, run
`az login --allow-no-subscriptions`, complete the browser/account sign-in, and
click Refresh again. If a tenant is configured, add `--tenant YOUR_TENANT`.
Expired/MFA sessions, network/proxy failures, and broken CLI installations get
separate recovery guidance. Conditional Access denials require the correct Entra
tenant and, if still blocked, help from your organization's IT administrator;
ReviewQ and PATs cannot bypass organizational access restrictions.
Raw CLI stderr is classified but never displayed or
logged because it may contain account details. To diagnose a persistent failure
without printing an access token, run
`az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query expiresOn --output tsv`
(with `--tenant YOUR_TENANT` if applicable). PAT authentication is an alternative.

Supported Azure CLI discovery includes `az.exe` on PATH and standard CLI2
installations with `az.cmd` plus the bundled adjacent Python runtime. Unsupported
custom wrappers produce an explicit error rather than invoking a shell.

## Queue semantics

- **Needs your review**: active, not self-authored PRs where you are a direct
  reviewer, your current vote is zero, and you have not declined. Drafts are opt-in.
- **People you follow**: opt-in emails separated by semicolons without spaces.
  Exact Mail/Account identity matches are searched across configured organizations,
  then all accessible projects. Unresolved people are an explicit error.
- **I'm the Author**: opt-in, across accessible projects in configured organizations.
  Authored entries supersede duplicates and have browser-only actions.
- Within repository groups, PRs are newest first. Optional queues do not inflate
  the assigned pending count. Disabled optional queues make no discovery requests.
- Historical nonzero votes take precedence over comments; a later reset to zero
  does not erase prior feedback. Ambiguous chronology fails visibly.
- Discussion resolution counts relevant threads, not individual comments.
  Deleted/system threads and unknown closure states do not create false claims.
- **Merge Ready** means required reviewer approvals and no active discussion
  threads on a non-draft PR, including relevant blocking reviewer policies.
  **It does not check builds, conflicts, or all merge requirements.**
- Polling supports 1/5/15/30 minutes, manual refresh, and refresh after resume.
  A failed refresh retains the last successful queue and displays an error.
- **Re-lookup account and followed identities** clears the bounded account-scoped
  identity cache and refreshes.

## Browser and assistant actions

The title opens the default browser. **Edge** opens explicitly in Microsoft Edge.
**Copilot** starts a new VS Code stable agent-chat window using its supported CLI,
not keystroke simulation. Install/update VS Code and sign into Copilot first.
The app validates `chat --help` capability before launching and reports failures.
It never finds or reuses existing chats.
Both legacy and versioned VS Code resource directories are supported; the app
reads the bundled CLI path without running a batch shell.

The prompt starts with exactly:

```text
Review PR: {PR Link}
```

Nonempty instructions follow after one blank line. Settings enforce **1,000
Unicode graphemes per assistant** without silently truncating. Multiline text is
supported; tabs/control characters are rejected. Prompt text is passed as one
process argument, never concatenated into a shell command. Instructions are
stored as local settings, not encrypted: do not put secrets in them.

**Scout** opens a new chat using Windows UI Automation, fills the accessible
Message composer with the PR link and Scout instructions, verifies the full
prompt, then invokes Send once. It confirms the composer cleared and Scout
started responding or recorded a user message. It does not use a URL protocol,
global keystrokes, the clipboard, or private Scout APIs. Scout's Chromium composer
receives process-targeted Unicode text and thread-scoped Shift+Enter line breaks;
ReviewQ checks focus and acknowledges each text chunk before continuing.
Whitespace-only composers (including Scout's empty placeholder newline) are
treated as empty, but an unreadable composer is never assumed to be safe.

Sign in to Scout first and run both applications without administrator elevation.
If Scout already has a draft, ReviewQ leaves it untouched and asks you to send or
save it before retrying. Changed drafts, ambiguous/missing controls, and sign-in
dialogs stop the handoff with an error. If submission cannot be confirmed, check
Scout before retrying to avoid duplicate reviews. Scout's accessible English
control labels (`New chat`, `Message`, `Send`) are required.

## Storage, diagnostics, and uninstall

`%LOCALAPPDATA%\ReviewQ` contains:

- `settings.json`: versioned, atomically replaced settings (no PAT).
- `identity-cache.json`: scoped identity IDs and timestamps (no access tokens).
- `setup-identity-cache.json`: identity IDs scoped to setup verification.
- `queue-cache.json`: unencrypted, bounded PR display data for authenticated startup.
- `archives.json`: bounded, account/source-scoped canonical PR IDs only.
- `app-updates.json`: public Windows release metadata, ETag, check/retry times, and dismissed version (at most 32 KiB).
- `setup-progress.json`: non-secret setup draft/progress, never PATs or verification.
- `Logs\reviewq.log` and up to three rotated 1 MiB backups.

Diagnostics contains allowlisted event/category/status/timing fields and safe
numeric native-system/CLI exit codes, never raw native exception descriptions,
credentials, URLs, titles, discussion text, prompts, or settings. Verbose logging
is opt-in for the current process only. Exports include only these logs and
version/OS/architecture metadata. Inspect any ZIP before sharing it.
Queue caches, archives, reminder choices, and setup progress are never exported.
Reminder authorization, scheduling, test, delivery, and callback failures have
separate categories; failure logging does not require verbose mode.

The startup queue cache contains titles, authors, repository/project names,
links, badges, timestamps, compact change fingerprints, and hashed scopes.
It excludes credentials, descriptions, discussion bodies, and prompts. It loads
only after normal authentication matches the account and queue configuration.
The original **Updated ... ago** time remains visible while fresh data loads.
It expires after seven days, with a maximum of 2 MiB and 4,096 PRs; oversized
queues are not partially saved. Saving Settings or removing the PAT clears the
prior cache. Use **Clear startup queue cache** in Settings for manual removal.
Cache restoration and change detection add no HTTP/authentication requests or
polling timers. Read/cache-write failures are reported without hiding a live queue.

The Diagnostics window follows the system theme and provides a live log-storage
readout, **Export Logs**, **Show in Explorer**, and **Clear Logs** (with confirmation).
Clearing removes only the current log and its three named backups; settings,
credentials, the identity cache, and existing exported ZIPs are untouched. New
activity may immediately create new log records. Each file is capped at 1 MiB
(4 MiB combined). Records are retained until rotation or manual clearing, not
expired by age; this differs from the macOS retention policy. The privacy and
collection descriptions in the window describe Windows behavior specifically.

The assigned queue keeps repository headers with organization/project metadata
and count badges. **People you follow** and **I'm the Author** instead show flat
PR lists in queue order, with **author in repository** metadata and no nested
repository or organization headings. Repository and optional queue sections can
be collapsed independently.

Icon-only Edge/Copilot/Scout actions have accessible names and tooltips. Their
16-DIP icons grow subtly to 18 DIP on hover without changing the button hit area
or shifting nearby text. Scout and Copilot open new review chats; authored PRs keep browser-only
actions.

Uninstall through Windows Installed apps. The installer removes its own startup,
reminder-task, and reminder-protocol registrations but **retains settings, archives,
caches, setup progress, logs, and the PAT**, so
reinstallation does not discard account configuration. To remove personal data,
first remove the PAT in Settings, quit ReviewQ, then delete the specific
`%LOCALAPPDATA%\ReviewQ` folder yourself. Portable users should disable startup
before deleting the extracted app.

## Build and test

Prerequisites: Windows 11, .NET **10 SDK**, and **Inno Setup 6.3+** for installers.
No third-party runtime UI framework is needed. Tests use xUnit.

From the repository root:

```powershell
dotnet test .\windows\ReviewQ.sln -c Release
.\scripts\build-windows.ps1
```

Options:

```powershell
# Only one architecture:
.\scripts\build-windows.ps1 -Runtime win-x64
# Explicitly ZIP-only, without Inno Setup:
.\scripts\build-windows.ps1 -SkipInstaller
# Nonstandard compiler installation:
.\scripts\build-windows.ps1 -InstallerCompiler 'C:\Tools\Inno Setup 6\ISCC.exe'
```

The script regenerates the multi-resolution icon, runs both test projects,
publishes self-contained x86/x64/ARM64 apphosts, checks each PE machine type, produces
portable ZIPs and per-user installers, and writes SHA-256 checksums. `-SkipTests`
is available for packaging an already validated build. Do not treat it as testing.
Outputs are under `dist\windows`; intermediary native publish folders are under
`dist\windows\publish`.

The Windows workflow runs the solution tests, packages the native target, and
runs `ReviewQ.exe --smoke-test` for x86 and x64 on x64 GitHub runners and ARM64
on a native ARM64 runner.
The ARM runner requires availability for this repository/account. The smoke
switch uses a disposable, unconfigured profile, verifies the queue/setup
windows loaded, and exits automatically without changing your real settings,
PAT, startup setting, or global shortcut.

## Verification status and release acceptance

Locally verified on x64: solution compilation, deterministic core/platform tests,
self-contained x86/x64/ARM64 publishing and architecture checks, installer
compilation, x86 and x64 desktop startup smoke tests, per-user x64 install/uninstall with
startup of the installed application, and installed VS Code 1.141.2's
`chat --help` capability (without sending a prompt).

The installed x64 app has also been checked with live Azure CLI token acquisition,
queue refresh, system dark-theme rendering, optional queue layout, hover sizing,
diagnostics controls, and settings-preserving upgrades.

The Windows workflow builds and smoke-tests x86, x64, and native ARM64 targets.
Check the workflow results for the release commit; these checks do not replace
interactive acceptance. Local Scout prompt-entry verification covered exact
multiline text, Unicode, emoji, and blank lines against a running Scout instance
from both x86 and x64 clients, without submitting validation chats.

**Not yet release-validated:** full interactive native ARM64 acceptance; live Azure DevOps
PAT/tenant/account acceptance scenarios; actual Copilot/Edge interactions; complete
keyboard/screen-reader, light/high-contrast, reduced-motion, DPI/multi-monitor,
Explorer restart, wake, startup, and install-upgrade acceptance. CI definitions
alone are not evidence that CI ran.

The local v4 parity build passed **248 tests** (146 core, 102 Windows), including
explicitly enabled real Task Scheduler registration/removal. Native light/dark
UI checks cover Archive/Restore, all-archived reachability, reminder controls,
and draft preservation. All three architectures were packaged; final x86/x64
startup smoke tests and all six SHA-256 checks passed. ARM64 was cross-built,
not executed locally. See the [v4 release notes](../docs/windows-release-v4.0.0.md)
and the release commit's workflow results for native CI verification.

Installed x64 acceptance verified a real generic test-notification request
(Windows subsequently reported Allowed), notification shortcut identity,
warm/cold protocol queue activation, one coalesced warm refresh, preservation
of an open Settings draft, and unchanged saved preferences with reminders off.
The actual v4 uninstaller removed its owned task/protocol and retained settings;
the same build was reinstalled. These checks do not prove a banner was visible,
that a clock-triggered reminder fired, or time-zone/DST/sleep/Focus behavior.

Before distribution:

1. Run the workflow on both native architectures and inspect uploaded packages.
2. Exercise CLI and PAT auth against a real test account, including expiry/401,
   access errors, multiple organizations, and all optional queues.
3. Check title/Edge/Copilot actions and exact prompts without putting secrets in them.
4. Verify single-instance reopening, close-to-tray/Quit, shortcut conflict/retry,
   opt-in startup, resume, and Explorer tray restoration.
5. Test keyboard navigation and Narrator; light/dark/high-contrast; 100-200% DPI,
   different monitors/taskbar positions, long titles, and reduced motion.
6. Exercise real reminder registration, test display, warm/cold notification
   activation, permission denial, time-zone/DST behavior, and opt-out; do not
   interpret a successful toast API call as proof a banner appeared.
7. Install, upgrade, and uninstall each architecture without losing settings or
   affecting unrelated startup/task/protocol entries; sign packages using the owner identity.

Do not interpret a cross-published ARM64 executable as a native ARM64 runtime test.
