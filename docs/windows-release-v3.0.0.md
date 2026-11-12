# ReviewQ for Windows 3.0.0

Initial Windows release of the native ReviewQ Azure DevOps review queue.

## Packages

- `win-x86`: 32-bit process compatibility on supported Windows.
- `win-x64`: recommended for Intel/AMD Windows 11.
- `win-arm64`: recommended for Windows on ARM.
- Each target has a per-user installer and a self-contained portable ZIP.
- `SHA256SUMS.txt` lists package checksums.

Requires Windows 11 22H2 or newer. No separate .NET runtime is required.
Packages are unsigned and can trigger SmartScreen warnings. This is not a
32-bit Windows OS compatibility release, and there is no auto-updater.

## Included

- Assigned, followed-author, and authored queues.
- Review-history/comment-resolution labels and limited approval readiness.
- Azure CLI and Windows Credential Manager PAT authentication.
- Browser, Edge, and stable VS Code Copilot agent-chat actions.
- Queue/taskbar window, background tray access, global shortcut, and startup option.
- System-themed queue, Settings, and Diagnostics.
- Compact hover-enlarging action icons; flat optional queues with author/repository metadata.
- Local allowlisted logs, export, storage display, Explorer access, and confirmed clearing.

## Validation and limitations

The local regression suite has 107 passing tests (67 core and 40 Windows).
Both x86 and x64 self-contained applications passed local startup smoke tests.
The x64 app has been installed and exercised with a live account, including
system dark theme, queue grouping, action-icon hover sizing, diagnostics controls,
and settings-preserving upgrades.

Native ARM64 execution and comprehensive accessibility, high-contrast, light-theme,
and multi-DPI acceptance are not complete. GitHub Actions results must be checked
separately after publishing; local tests are not evidence of a CI run.

Scout is disabled. GitHub PRs are not supported. ReviewQ never changes PRs.
Required approvals alone do not prove all merge policies are satisfied.
Windows logs rotate across four 1 MiB files, without age-based expiry.

For setup, troubleshooting, and storage details, see the
[Windows guide](../windows/README.md).
