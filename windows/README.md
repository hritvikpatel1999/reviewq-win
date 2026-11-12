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
| x86 compatibility (32-bit process) | `ReviewQ-3.0.0-win-x86.zip` | `ReviewQ-3.0.0-win-x86-setup.exe` |
| Intel/AMD x64 | `ReviewQ-3.0.0-win-x64.zip` | `ReviewQ-3.0.0-win-x64-setup.exe` |
| Windows on ARM | `ReviewQ-3.0.0-win-arm64.zip` | `ReviewQ-3.0.0-win-arm64-setup.exe` |

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

## Visibility and background behavior

- Interactive launch opens the queue, with a normal taskbar button.
- First launch also opens Settings. Closing the queue keeps polling in the tray.
- Clicking the tray icon opens/focuses the queue. Its menu includes Refresh,
  Settings, Diagnostics, and **Quit ReviewQ**, which ends background work.
- Windows may hide the tray icon behind overflow. Drag ReviewQ beside the clock,
  or use Windows taskbar tray settings. The app cannot force permanent visibility.
- Pin ReviewQ yourself from its taskbar button or Start menu if desired.
- **Ctrl+Alt+Q** toggles the queue. Change it or disable it in Settings. Conflicts
  are visible; use **Retry saved shortcut** after freeing the combination.
- Startup at sign-in is opt-in and starts quietly in the tray. An unconfigured
  app or startup error still opens the window.
- Reopening the app activates the existing per-user instance.
- Queue, Settings, and Diagnostics all follow the Windows light/dark theme.
- A pending count appears in the title/tray tooltip; a taskbar overlay indicates
  count or errors where Windows supports it. Only assigned reviews count.

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

**Scout is disabled** with an explanatory tooltip. A reliable Windows integration
has not been verified. Saved Scout instructions reserve future compatibility but
do not make Scout functional.

## Storage, diagnostics, and uninstall

`%LOCALAPPDATA%\ReviewQ` contains:

- `settings.json`: versioned, atomically replaced settings (no PAT).
- `identity-cache.json`: scoped identity IDs and timestamps (no access tokens).
- `Logs\reviewq.log` and up to three rotated 1 MiB backups.

Diagnostics contains allowlisted event/category/status/timing fields, never
credentials, URLs, titles, discussion text, prompts, or settings. Verbose logging
is opt-in for the current process only. Exports include only these logs and
version/OS/architecture metadata. Inspect any ZIP before sharing it.

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
or shifting nearby text. Scout remains disabled; authored PRs keep browser-only
actions.

Uninstall through Windows Installed apps. The installer removes its own startup
registration but **retains settings, identity cache, logs, and the PAT**, so
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
switch uses a disposable, unconfigured profile, verifies the queue/settings
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

**Not yet release-validated:** native ARM64 execution; live Azure DevOps
PAT/tenant/account acceptance scenarios; actual Copilot/Edge interactions; complete
keyboard/screen-reader, light/high-contrast, reduced-motion, DPI/multi-monitor,
Explorer restart, wake, startup, and install-upgrade acceptance. CI definitions
alone are not evidence that CI ran.

Before distribution:

1. Run the workflow on both native architectures and inspect uploaded packages.
2. Exercise CLI and PAT auth against a real test account, including expiry/401,
   access errors, multiple organizations, and all optional queues.
3. Check title/Edge/Copilot actions and exact prompts without putting secrets in them.
4. Verify single-instance reopening, close-to-tray/Quit, shortcut conflict/retry,
   opt-in startup, resume, and Explorer tray restoration.
5. Test keyboard navigation and Narrator; light/dark/high-contrast; 100-200% DPI,
   different monitors/taskbar positions, long titles, and reduced motion.
6. Install, upgrade, and uninstall each architecture without losing settings or
   affecting unrelated startup entries; sign packages using the owner identity.

Do not interpret a cross-published ARM64 executable as a native ARM64 runtime test.
