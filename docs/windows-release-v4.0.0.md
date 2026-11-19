# ReviewQ for Windows 4.0.0

Windows feature parity with the original ReviewQ v4.0.0, including the archive
features introduced in v3.3.0, plus a redesigned native Settings experience.

## New and improved

- **Archive and restore:** hide PRs locally from any queue, then restore them
  from the collapsed Archives section. Archived PRs do not contribute to active
  lists, pending counts, or active change indicators. Choices are scoped to the
  account and configured sources; Azure DevOps PRs are never modified.
  Archive actions use blue file-folder artwork on white tiles.
- **Optional review reminders:** choose weekdays, up to eight local times per
  day, and optional sound in Setup or Settings. Reminders default to off.
  Other reminder controls stay visible but greyed out and disabled until the
  checkbox is selected; toggling preserves the unsaved choices.
- **Native Windows delivery:** a per-user scheduled task can deliver generic
  reminders while ReviewQ is closed, without fetching or exposing PR data.
  Clicking a reminder opens the queue and requests a normal refresh. Test and
  notification-permission controls report explicit status and errors.
- **Mac-inspired Settings:** grouped cards, segmented authentication, separate
  assistant instruction editors and character counts, weekday chips, compact
  time selectors, and a fixed footer with Diagnostics and Save and Refresh.
  Light/dark styling and narrow-window layouts retain native Windows behavior.
- **Windows release notifications:** cached daily checks of public Windows
  releases, a per-version dismissible View Updates banner, and a manual check
  in Settings. Installation remains manual; ReviewQ never automatically
  downloads or runs an installer.
- **Refined queue layout and private diagnostics:** Mac-aligned header/footer
  actions, safe numeric failure codes, reminder-stage reporting, and assistant
  action timings without exporting credentials, prompts, or private PR data.

Existing authentication, Scout handoff, repository grouping, startup caching,
and optional followed/authored queues are retained. Upgrades preserve saved
settings and credentials. Uninstall removes only ReviewQ's owned reminder
registration and leaves settings, archives, caches, and credentials intact.

## Downloads

Per-user installers and self-contained portable ZIPs are provided for **x64**,
**ARM64**, and **x86**. Most Intel/AMD PCs should use
`ReviewQ-4.0.0-win-x64-setup.exe`. Windows 11 22H2 or newer is required.
The x86 package is a 32-bit process on supported Windows, not support for a
32-bit Windows operating system. No separate .NET runtime is required.

Packages are **unsigned** and may trigger SmartScreen warnings. Compare
downloads against `SHA256SUMS.txt`; checksums are not publisher signatures.
Extract the entire portable ZIP before starting ReviewQ.

## Verification and limitations

- All **248 local tests passed**: 146 core and 102 Windows tests, including
  explicitly enabled native Task Scheduler registration and removal.
- Native UI regression checks cover light/dark Settings and Setup, disabled
  reminder controls, preserved drafts, archive/restore, responsive footers,
  authentication switching, and prompt validation.
- All three architecture packages were built locally and checked for the
  correct executable architecture; x86/x64 startup smokes and six package
  checksum checks passed. ARM64 was not executed locally.
- The [Windows workflow](https://github.com/hritvikpatel1999/reviewq-win/actions/workflows/windows.yml)
  tests, packages, and startup-smokes x86, x64, and native ARM64. The published
  release identifies the exact successful workflow run used for its downloads.
- Installed x64 checks verified a real generic test-notification request,
  notification identity, warm/cold queue activation, one coalesced warm
  refresh, unsaved Settings preservation, and actual owned-registration
  cleanup during uninstall.
- A successful notification API call does not prove that a banner appeared.
  Real clock-triggered delivery, permission-denial combinations, time-zone/DST,
  sleep/Focus behavior, full interactive ARM64 acceptance, and complete
  accessibility/high-contrast/DPI acceptance remain outstanding.

Reminders do not wake the PC or replay missed reminders. Windows notification
settings and Focus can suppress banners. Defaults are weekdays at 10:30 and
15:30, disabled and silent. Editing a draft does not change the saved schedule
until Settings or Setup is saved.

See the [Windows guide](../windows/README.md) for setup, authentication,
notification permissions, privacy, and troubleshooting.
