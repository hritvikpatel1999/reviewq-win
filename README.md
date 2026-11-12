# ReviewQ for Windows

**Your reviews. One queue.**

A native Windows app for Azure DevOps pull requests, built with WPF and .NET 10
LTS. ReviewQ opens a normal queue/taskbar window and keeps refreshing in the
notification area when you close it.

This is the Windows port of [ReviewQ](https://github.com/hritvikpatel1999/reviewq).
The original Swift sources are retained for reference and do not participate in
the Windows build. See [original project documentation](README.macos.md).

## Download and install

Get packages from [Releases](https://github.com/hritvikpatel1999/reviewq-win/releases).
This repository is private; downloads require repository access.

Requires **Windows 11 22H2 or newer**. Choose the appropriate architecture:

| Package | Recommended use |
|---|---|
| `ReviewQ-3.0.0-win-x64-setup.exe` | Intel/AMD Windows 11 PCs |
| `ReviewQ-3.0.0-win-arm64-setup.exe` | Windows on ARM |
| `ReviewQ-3.0.0-win-x86-setup.exe` | 32-bit process compatibility on supported Windows |

Matching portable ZIPs are also provided. Extract the **entire ZIP** before
opening `ReviewQ.exe`; no separate .NET runtime is required.

Installers run per-user without administrator privileges. Packages are currently
**unsigned**, so Windows SmartScreen may warn. Compare the package's SHA-256 hash
against the release's `SHA256SUMS.txt`; checksums are not publisher signatures.
There is no auto-updater. Windows 11 does not have a supported 32-bit x86 OS
edition; the x86 package is a compatibility build, not support for legacy Windows.

## Features

- Assigned review queue, grouped by repository with organization/project metadata.
- Optional **People you follow** and **I'm the Author** flat queues, with
  **author in repository** metadata.
- Review-history and comment-resolution labels, draft filtering, and limited
  required-reviewer readiness checks.
- Default-browser and Microsoft Edge actions; VS Code Copilot agent-chat actions
  for review queues. Compact action icons enlarge subtly on hover.
- Azure CLI authentication or a PAT stored in Windows Credential Manager.
- Configurable refresh interval, resume refresh, and retention of the last
  successful queue on errors.
- System light/dark theme, collapsible sections, accessible action labels,
  optional sign-in startup, and a configurable **Ctrl+Alt+Q** shortcut.
- Local private diagnostics with log export, folder access, storage usage, and
  confirmed log clearing.

ReviewQ **reads** Azure DevOps. It does not vote, comment, merge, or modify PRs.
GitHub PR integration is not included. Scout remains disabled because a reliable
Windows integration has not been verified.

## First launch

1. Open **Settings** and enter one Azure DevOps organization/project URL per line.
2. Choose Azure CLI or PAT authentication:
   - Azure CLI: install it separately, run `az login --allow-no-subscriptions`,
     and complete sign-in. Configure the Entra tenant if needed.
   - PAT: supply a token with **Code (Read)** permissions and sufficient project
     and identity access. It is not stored in settings or diagnostic logs.
3. Choose optional queues, followed-author emails, drafts, and refresh interval.
4. Choose **Save and Refresh**.

Organizational Conditional Access policies still apply. ReviewQ does not bypass
them. See the [Windows guide](windows/README.md) for authentication troubleshooting,
queue semantics, integration prerequisites, privacy, and uninstall behavior.

Closing the queue leaves the app running in the tray. Use **Quit ReviewQ** from
the tray menu to exit. Windows may put the tray icon in overflow; drag it beside
the clock or pin the app to the taskbar yourself.

## Build and test

Prerequisites: Windows 11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
and [Inno Setup 6.3+](https://jrsoftware.org/isinfo.php) for installers.

```powershell
dotnet test .\windows\ReviewQ.sln -c Release --nologo
.\scripts\build-windows.ps1
```

The script tests the solution, generates the icon, publishes self-contained
**x86, x64, and ARM64** apps, validates executable architectures, builds installers,
and writes ZIPs and `SHA256SUMS.txt` under `dist\windows`.

Build a single target or omit installers:

```powershell
.\scripts\build-windows.ps1 -Runtime win-x86
.\scripts\build-windows.ps1 -Runtime win-arm64 -SkipInstaller
```

Startup smoke test (uses a disposable profile, not your saved account):

```powershell
$process = Start-Process .\dist\windows\publish\win-x64\ReviewQ.exe `
    -ArgumentList '--smoke-test' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Startup smoke test failed.' }
```

## Repository layout

| Path | Purpose |
|---|---|
| [windows/src/ReviewQ.Core](windows/src/ReviewQ.Core) | Azure DevOps API, models, settings, and queue logic |
| [windows/src/ReviewQ.Windows](windows/src/ReviewQ.Windows) | WPF UI, authentication, tray, and Windows integrations |
| [windows/tests](windows/tests) | Core and Windows regression tests |
| [windows/packaging](windows/packaging) | Per-user Inno Setup installer |
| [scripts/build-windows.ps1](scripts/build-windows.ps1) | Local packaging and checksums |
| [.github/workflows/windows.yml](.github/workflows/windows.yml) | Windows test/package workflow |
| [Sources](Sources), [Tests](Tests), [Support](Support) | Original macOS implementation and support files |

## Privacy and known limitations

Settings and logs are under `%LOCALAPPDATA%\ReviewQ`; PATs are in Windows
Credential Manager. Diagnostic exports exclude credentials, identities, private
URLs, titles, comments, prompts, and settings. Windows logs rotate at **1 MiB per
file, four files total**; they do not expire by age. Clearing logs does not remove
settings or already exported ZIPs. Do not put secrets in saved review instructions.

Read [release notes and validation limits](docs/windows-release-v3.0.0.md).
Native ARM64 execution and exhaustive accessibility/high-contrast/DPI acceptance
remain to be verified. A **Merge Ready** label is not a guarantee of build,
conflict, or every completion-policy requirement.

No separate license grant is added by this Windows port.
