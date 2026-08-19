# Beet's Backup

A lightweight, portable dual-pane file manager and backup tool for Windows. Ships as a single `.exe` with no installer required.

Built with WPF and .NET 8, Beet's Backup is designed strictly for managing, backing up, and transferring files — not launching them. It gives you a backup-health dashboard, side-by-side drive browsing, scheduled backups that run through Windows Task Scheduler, checksum verification, file versioning, and a full transfer log, all in one self-contained executable.

**Current version:** 4.0.0 (`4.0-candidate` build tag)

---

## Features

### Dashboard

- **Protection status banner** — a green "Protected" / crimson "At Risk" banner across the top of the window. Green requires the most recent *terminal* backup to have completed with status `Complete` within the last 7 days; anything else reads At Risk. The banner also shows last backup, next scheduled backup, and a "View details" button into the log.
- **Protection Summary card** — files backed up, total size, backups run, and a verification health indicator, aggregated across all history.
- **Backup Schedule card** — the next scheduled run and its recurrence.
- **Recent Backups card** — the most recent runs with job name, timestamp, size, and status.
- **Drive sidebar** — every connected drive as a tile with a circular usage ring, plus a **Total Capacity** footer tile showing aggregate used/free across all drives.
- **Crash recovery banner** — if the previous session ended uncleanly, a one-shot warning banner appears at launch with **Export Diagnostics** and **Dismiss**.

### File Management

- **Dual-pane file browser** with independent drive selection (SOURCE / DESTINATION). The **Split Pane** button opens a separate `SplitPaneWindow` that shares the main view model, so the two windows stay in sync — top half (source tree + file list), full-width nav bar, bottom half (destination tree + file list).
- **Single-pane mode** with full drag-and-drop support
- **Right-click context menu** — Open in Explorer, Copy to Bottom / Copy to Top, Cut to Bottom / Cut to Top, Rename, Delete, Previous Versions…, Extract Here, Extract To…. Copy and Cut transfer straight to the other pane; there is no clipboard paste step and no New Folder command. Deletes go to the **Windows Recycle Bin**, not permanent deletion.
- **Previous Versions** — right-click any file to open a timestamped list of archived copies; double-click an entry to restore it (the current copy is archived first, so a restore is itself reversible); delete individual archived versions; populated automatically by the versioning system whenever a file would be overwritten
- **Open in Explorer** — right-click any file or folder to open its location in Windows Explorer (folders open directly; files are pre-selected in Explorer). Launched via `explorer.exe` so the target opens unelevated even though Beet's Backup itself runs elevated.
- **Drag-and-drop transfers** in both single and split pane modes
- **File and folder rename** via right-click context menu
- **Back / forward / up navigation** with full history tracking for both panes
- **Deep recursive file search** — press Enter or click the magnifying glass in the top nav bar to search the current folder tree recursively; extension-aware (`.exe` matches by extension, `exe` matches by name substring); results populate inline with a cancel button to return to normal view; live status messages report search progress and result count
- **Search path column** — a Path column appears automatically in search results showing the parent directory of each matching file
- **Search and filter** textbox provides a secondary live filter on top of search results or the current folder listing
- **Bottom pane navigation bar** — split pane mode includes a dedicated full-width nav bar between the two halves with Back, Forward, Up, and path display, symmetric with the top nav bar
- **Async folder size calculation** paced to the drive rather than the CPU, with progress indicators; Refresh reloads pane contents and recalculates folder sizes
- **Long path support** (>260 characters) via the application manifest
- **Hidden file support** — visible, transferable, and attributes preserved
- **GridSplitter resizable panes** for flexible layout
- **No file launching** — double-clicking a file shows a reminder that this is a backup tool, not a file explorer

### Transfers

- **Transfer mode selection:** Skip Existing, Keep Both, Replace, or Mirror (Sync)
- **Keep Both naming** — a colliding file is written as `name-1.ext`, `name-2.ext`, … (folders as `name-1`); the counter also avoids names reserved earlier in the same transfer plan, so two same-named sources cannot land on each other
- **Mirror mode** — copies new/changed files then deletes destination files not present in source (to the Recycle Bin); shows a prominent confirmation warning before proceeding, and skips the cleanup phase entirely if the source scans as empty (e.g. a disconnected drive)
- **NTFS permission stripping** — Remove Permissions toggle strips ACLs so files work cleanly on other machines
- **SHA-256 checksum verification** — Verify Checksums toggle confirms integrity after every copy
- **Pause, resume, and stop** controls during active transfers
- **Transfer throttling** — the "Throttle (10 MB/s)" toggle in the Options popup caps bandwidth (bound to `ThrottleTransfer` in `MainViewModel`); scheduled jobs additionally support a per-job speed picker (1–100 MB/s) in the schedule dialog and wizard
- **VSS Shadow Copy fallback** — locked or in-use files (e.g. open Outlook PSTs, live database files) are retried 3 times with 500 ms delays; if still inaccessible, a Volume Shadow Copy snapshot is created via P/Invoke to `vssapi.dll` (no external packages) so the file can be read without interrupting the owning process; snapshots are cached per volume for the duration of the transfer session and cleaned up automatically afterward; the transfer summary reports how many files were copied via shadow copy
- **Pre-flight disk space preview** — before every backup, `DiskSpaceService` calculates required vs. available space; result shown as a colored banner (red = Insufficient, amber = Tight) in the wizard summary and schedule dialog; Insufficient status requires confirmation before the job is committed; applies a 0.7× estimate for compressed (zip) jobs; UNC/network destinations return a non-fatal Unknown status
- **Archive Now** — zip the current selection (or the whole current folder) to a folder you pick, as `archive_YYYY-MM-DD_HH-mm-ss.zip`
- **Extract archive** — right-click any `.zip` file and choose **Extract Here** (sibling folder) or **Extract To…** (folder picker); full zip-slip protection; cancel/pause/progress support; sharing-violation handling
- **Overall progress bar with ETA** displayed in the status bar
- **Transfer progress dialog** — docked circular progress indicator shown during active transfers; DPI-aware multi-monitor positioning

### Backup Wizard

A guided, fully implemented 7-step flow (6 steps when the backup runs immediately), reachable from the **Backup Wizard** button in the command bar:

1. **Type** — One-time backup (run now), Scheduled (one-time), or Recurring. Choosing "run now" skips step 2.
2. **When** — job name, first run date/time, and recurrence for recurring jobs.
3. **Source** — Quick Pick (Documents / Pictures and Videos / Desktop / Downloads), Entire Drive (the default), or Custom (any number of hand-picked folders).
4. **Destination** — pick a drive plus optional subfolder, or browse to any folder. Warns when the destination sits on the same drive as the source.
5. **Mode** — the four transfer modes above.
6. **Options** — checksums, permission stripping, per-job speed limit, versioning with max-versions, compression, and exclusion filters.
7. **Review** — full summary, estimated size, and the disk-space forecast banner before you commit.

### Scheduled Backups

- **One-time or recurring** schedules — Every 6 Hours, Every 12 Hours, Daily, Weekly, or Monthly (30 days)
- **Jobs persist to disk** and survive app restarts
- **Windows Task Scheduler integration** — each job is registered as a Windows Task (`BeetsBackup_{Guid}`) so backups run even if the app is not open; past-due times are bumped to now + 1 min; the in-process `PeriodicTimer` is kept as a safety net when the app is open
- **Headless CLI mode** — Windows Task Scheduler launches the app with `--run-job {guid}`; the app runs the job with no window and exits with code 0 (success), 1 (not found), or 2 (failed). The headless path is wrapped so a sync-over-async stall can't leave a zombie process behind, and a watchdog backstops it.
- **Single-run guard** — `JobMutex` prevents the in-process timer and a Task Scheduler launch from running the same job twice concurrently
- **Missed backup detection** — on startup, detects jobs that were missed while the app was closed and prompts to run them immediately or skip
- **Scheduler errors surfaced in status bar** — job failures and scheduler loop errors are reported immediately in the main window status bar
- **Toast notifications** — Windows balloon-tip notifications on job completion or failure (Success / Warning / Error)
- **Per-job settings** for transfer mode, permission stripping, checksum verification, exclusion filters, speed limit, versioning, and compression
- **File versioning** — enable per job to archive existing destination files before overwriting. Archived copies land in a hidden `.versions\` tree at the destination root, mirroring the relative folder layout, named `name__yyyy-MM-dd_HH-mm-ss.ext`. Retention defaults to 5 and prunes by the timestamp in the filename, not file mtime. If archiving fails, the overwrite is skipped rather than destroying an unprotected copy. Mutually exclusive with compression.
- **Compression** — compress backup output to a single timestamped `.zip` per job; mutually exclusive with versioning
- **Exclusion filters** — skip files by extension pattern (e.g. `*.tmp`, `*.log`) or exact name (e.g. `Thumbs.db`, `node_modules`)
- **Backup size estimation** — "Estimate Size" calculates total source size and file count respecting active filters; auto-runs at job start
- **Pause / resume** for running scheduled jobs via the log dialog

### Backup Log

- **Persistent JSON log** of all backup operations (capped at 500 entries)
- **Real-time progress bars** for currently running jobs
- **Color-coded status** indicators: Scheduled, Running, Complete, Failed, Interrupted
- **Detailed stats** including file counts, bytes transferred, and failure count
- **Per-file error tracking** — each failed file records its path and reason (disk full, locked, checksum mismatch, etc.), capped at 200 entries per job. Reasons come from `IOExceptionClassifier`, which maps the underlying Win32 errors rather than raw HRESULTs.
- **"View Errors" button** — enabled when the selected log entry has file errors; shows a list of all failed files with reasons
- **Pause button** for running jobs, always visible in the log dialog
- **Retry button** for failed jobs
- **Export to CSV** for external reporting
- **"Open Log Folder" button** — opens `%LocalAppData%\Beet's Backup\` in Explorer
- **Clear log** to reset history

### UI / UX

- **Dark and light theme** toggle, persisted across launches
- **Unified command bar** — all controls always visible, no mode toggle: **Back up now**, Pause/Resume, Stop, Split Pane, Schedule, Backup Wizard, Jobs, Log, **Options ▾**, Theme, and Visual/List. Most buttons are pre-rendered sprite art cropped from `Assets/Icons/Toolbar/`.
- **Options popup** — a menu that stays open until the mouse leaves; contains Remove Permissions, Verify Checksums, Throttle (10 MB/s), Archive Now, Launch at Startup, Start Minimized to Tray, Check for Updates, **Export Diagnostics for Support…**, and **View User Guide (FAQ)**
- **Export Diagnostics** — writes `BeetsBackup-Diagnostics-<timestamp>.zip` to the Desktop containing `operational.log`, `crash_dump.log`, `settings.json`, `backup_log.json`, and system info, ready to attach to a support email
- **Bundled user guide** — `FAQ/beetsbackup_user_guide.pdf` is published next to the exe and opened by "View User Guide (FAQ)"
- **Update checker** — `UpdateService` queries the GitHub Releases API ~3 s after startup; if a newer version is found, an accent-colored banner appears in the status bar with **Download** and **Dismiss** buttons; "Check for Updates" is also in the Options menu; skipped versions are persisted so dismissed releases are not surfaced again
- **Launch at Startup** — Options toggle that registers or removes an **ONLOGON Windows scheduled task** (not a Startup-folder shortcut; any legacy shortcut is migrated automatically on first run). Because the task runs elevated, auto-start does not re-prompt for UAC.
- **Start Minimized to Tray** — when set alongside Launch at Startup, an auto-start launch (`--startup`) goes straight to the tray with no window, unless missed backups need attention
- **Data Distribution Visual Mode** — toggles between List view and a donut chart of the top 10 largest items in the current folder; color-coded with 10 distinct colors plus a muted "Other" slice; the legend is the accessible surface — rows are keyboard-focusable, announce name/size/percentage as one string, and stay in sync with slice hover; chart rebuilds atomically when folder size calculations complete; works in both single-pane and split-pane modes
- **Custom logo** with gradient "Beet's Backup" branding and the running version read from assembly metadata
- **Navigation bars** — top pane nav bar with back, forward, up, path display, search, and refresh; bottom pane nav bar in split mode with symmetric controls
- **System tray support** — closing or minimizing hides the app to the tray rather than quitting; the tray icon's right-click menu provides Show/Hide and Quit; the app can only be fully exited through the tray menu
- **Single-instance enforcement** — launching a second copy signals the already-running instance to show its window, then exits cleanly
- **Light mode polish** — warmer gray tones throughout the light theme; dedicated brush resource keys for toggle controls, drive usage rings, and the donut chart center fill

### Telemetry (local only)

`Telemetry/` writes structured records to a local file sink, stamped with the assembly's `BuildTag` (`3.0-baseline` vs `4.0-candidate`) so the external **PerformanceMonitor** tool can bucket resource and backup data by build. Nothing is sent off the machine, and telemetry failures never block startup or a backup.

---

## Requirements

- **OS:** Windows 10 version 1607 or later, or Windows 11 (x64)
- **.NET 8.0** — included automatically in the self-contained build; no runtime install needed to *run* the exe
- **Administrator rights** — the app manifest requests `requireAdministrator`, so Windows shows a UAC prompt at launch. This is required for VSS shadow copies of locked files.

---

## Installation

### Option A: Download the executable

1. Download `BeetsBackup.exe` from the repository root (or the [Releases](../../releases) page if available).
2. Run it and accept the UAC prompt. No installer, no dependencies, no setup.

### Option B: Build from source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). `global.json` pins the SDK to **8.0.419** with `rollForward: latestFeature`, so you need an 8.0 SDK at 8.0.419 or newer — a .NET 9/10 SDK alone will not satisfy it.
2. Clone the repository:
   ```bash
   git clone https://github.com/mjsaleeby-hash/Beet-3.0-Ultimate-Edition.git
   cd Beet-3.0-Ultimate-Edition
   ```
3. Publish. All publish settings (single file, self-contained, win-x64, ReadyToRun, compression) live in the csproj, so no extra flags are needed:
   ```bash
   dotnet publish BeetsBackup.csproj -c Release
   ```

The exe lands in `bin\Release\net8.0-windows\win-x64\publish\`, and a `CopyExeToRoot` post-publish target also copies `BeetsBackup.exe` and the user-guide PDF one level *above* the repository folder.

For a step-by-step walkthrough aimed at non-developers, see **[READ THIS FIRST. HOW TO BUILD.txt](READ%20THIS%20FIRST.%20HOW%20TO%20BUILD.txt)** (mirrored at `docs/guides/how-to-build.txt`).

### Running the tests

```bash
dotnet test BeetsBackup.Tests/BeetsBackup.Tests.csproj
```

**244 tests, all passing** as of 2026-08-18. The test project is pinned to invariant globalization so culture-sensitive assertions hold on any locale.

---

## Usage

1. **Launch** `BeetsBackup.exe` and accept the UAC prompt.
2. **Read the status banner** — green "Protected" means a backup completed successfully in the last 7 days; "At Risk" means it did not.
3. **Set up your first backup** with the **Backup Wizard**, or hit **Back up now** to run every enabled job immediately.
4. **Select a drive** from the sidebar to browse its contents.
5. **Enable split mode** to open a second pane and select a destination drive.
6. **Navigate** using back, forward, and up buttons or by double-clicking folders.
7. **Search recursively** by typing a term in the search box on the top nav bar and pressing Enter. A **Path** column appears in results. Use the "X" button to cancel. Right-click any result and choose **Open in Explorer** to jump to its location. The filter textbox applies a secondary live filter on top of any search results.
8. **Transfer files** between panes using toolbar buttons, the right-click context menu, or drag-and-drop.
9. **Choose a transfer mode** (Skip Existing, Keep Both, Replace, or Mirror) before starting a transfer. Mirror mode will delete destination files not in the source — confirm the warning before proceeding.
10. **Enable checksum verification**, **permission stripping**, or the **10 MB/s throttle** via the **Options** dropdown.
11. **Monitor progress** in the status bar, and use pause, resume, or stop controls during transfers. Locked files are handled automatically via the VSS fallback — no action required; the transfer summary reports how many files required it.
12. **Review backup history** in the log dialog. Use **View Errors** on any entry with failures to see which files failed and why, **Open Log Folder** for direct access to all log files, and Export to CSV if needed.
13. **Restore previous versions** — right-click any file and choose **Previous Versions…** to browse and restore archived copies created by the versioning system.
14. **Extract archives** — right-click a `.zip` file and choose **Extract Here** or **Extract To…**.
15. **Get help** — **Options ▸ View User Guide (FAQ)** opens the bundled PDF; **Options ▸ Export Diagnostics for Support…** drops a support bundle on your Desktop.

> **Tip:** Scheduled backups are registered with **Windows Task Scheduler**, so they run even if the app is not open. Enable **Launch at Startup** in the Options menu to have the app start automatically at login — it registers an elevated ONLOGON task, so there is no UAC prompt at login. Add **Start Minimized to Tray** to keep it out of the way. To fully quit, right-click the tray icon and choose **Quit** — closing the window only hides it.

---

## Data locations

Everything the app writes lives in one folder: `%LocalAppData%\Beet's Backup\`

| File | Contents |
|------|----------|
| `operational.log` | Timestamped activity log (INFO/WARN/ERROR/FATAL), rotated at 10 MB |
| `crash_dump.log` | Unhandled-exception reports with environment and stack details |
| `backup_log.json` | Backup history (max 500 entries, 200 file errors per entry) |
| `settings.json` | Theme, Launch at Startup, Start Minimized, skipped update version |
| `scheduled_jobs.json` | All configured backup jobs |

Scheduled tasks are registered in Windows Task Scheduler as `BeetsBackup_*`.

---

## Project Structure

```
├── Views/               UI: MainWindow, SplitPaneWindow, PieChartControl, and dialogs
│   │                        (BackupWizard, Schedule, Jobs, Log, Rename, TransferMode,
│   │                         MissedBackups, PreviousVersions, TransferProgress)
│   └── WizardSteps/     Per-step wizard views
├── ViewModels/          Presentation logic (MVVM): MainViewModel, BackupWizardViewModel,
│   │                        ScheduleDialogViewModel, PreviousVersionsViewModel
│   └── WizardSteps/     Seven per-step view models (Type, Schedule, Source, Destination,
│                            TransferMode, Advanced, Summary)
├── Models/              FileSystemItem, DriveItem, FolderTreeItem, ScheduledJob, TransferMode,
│                            TransferResult, TransferWorkItem, BackupLogEntry, ArchivedVersion,
│                            PieSlice, RangeObservableCollection
├── Services/            Core logic
│   ├── FileSystemService           Drive & file enumeration, rename, delete-to-Recycle-Bin,
│   │                               timestamp-preserving copy
│   ├── TransferService             Copy/move with dedup, permission stripping, checksum
│   │                               verification, per-file error tracking, VSS fallback,
│   │                               zip create/extract (zip-slip safe), Keep Both uniqueness,
│   │                               Mirror cleanup, archive-before-overwrite versioning gate
│   ├── TransferReporter            Progress/ETA/summary reporting for the UI
│   ├── VersioningService           Archive-before-overwrite, version listing, restore, prune
│   ├── VssSnapshotService          P/Invoke wrapper for vssapi.dll — per-volume snapshots
│   ├── DiskSpaceService            Pre-flight preview (Sufficient / Tight / Insufficient / Unknown)
│   ├── DriveTypeService            Drive classification (fixed / removable / network / FAT vs NTFS)
│   ├── SchedulerService            Backup job runner; Task Scheduler integration; headless
│   │                               RunJobByIdAsync; SchedulerError event; toasts
│   ├── WindowsTaskSchedulerService schtasks.exe wrapper — Register, Unregister, ReconcileAll,
│   │                               BuildScheduleArgs, ONLOGON startup task
│   ├── JobMutex                    Cross-process single-run guard per job
│   ├── ExclusionMatcher            Extension-pattern and exact-name exclusion matching
│   ├── NameSanitizer               Safe file/folder names for jobs and archives
│   ├── IOExceptionClassifier       Maps Win32 I/O errors to user-facing failure reasons
│   ├── PowerManagement             Keeps the machine awake for the duration of a backup
│   ├── ToastNotifier               Windows balloon-tip notifications (Success / Warning / Error)
│   ├── BackupLogService            JSON-based backup history with debounced saves
│   ├── DiagnosticsService          Desktop support-bundle zip (logs, settings, system info)
│   ├── FileLogger                  Operational log + crash dump writer
│   ├── SettingsService             Preferences, theme flag, Launch at Startup task management,
│   │                               skip-version persistence, legacy-shortcut migration
│   ├── UpdateService               GitHub Releases API update checker with skip-version support
│   └── ThemeService                Light/dark mode
├── Telemetry/           BeetTelemetry, TelemetryFileSink, BuildInfo (BuildTag stamping)
├── Shared/              IpcNames — single source of truth for cross-process mutex/signal names
├── Helpers/             CliArgs (--run-job parser) + WPF value converters
├── Themes/              Controls.xaml, Light.xaml, Dark.xaml resource dictionaries
├── Assets/              App icon, logo, toolbar sprite sheet
├── FAQ/                 beetsbackup_user_guide.pdf (bundled and published beside the exe)
├── docs/                User guide, support reference, build guide, changelogs, plans/specs
├── BeetsBackup.Tests/   xUnit test project (244 tests, all passing)
├── BeetsBackupLauncher/ Work-in-progress unelevated launcher stub (signals the tray instance
│                            instead of triggering UAC) — not yet functional
├── PerformanceMonitor/  Standalone console tool that samples the running app's CPU/memory/
│                            handles/IO and analyzes cohorts (see its own README)
├── BenchmarkHarness/    Controlled throughput/latency benchmarks
├── improvements/        Audits, verdicts, and specs (incl. the 3.0 → 4.0 performance verdict)
├── notes/               Bugs, decisions, ideas, todo
└── mockups/             HTML design mockups
```

The solution (`Beet-3.0-Ultimate-Edition.sln`) contains three projects: `BeetsBackup`, `BeetsBackup.Tests`, and `BeetsBackupLauncher`. `PerformanceMonitor` and `BenchmarkHarness` are standalone and built separately.

---

## Tech Stack

| Component         | Technology                                       |
|-------------------|--------------------------------------------------|
| Framework         | .NET 8.0 (WPF + WinForms interop, Windows Desktop)|
| Language          | C# (nullable enabled, implicit usings)            |
| Architecture      | MVVM                                              |
| MVVM Toolkit      | CommunityToolkit.Mvvm 8.4.0                       |
| DI Container      | Microsoft.Extensions.DependencyInjection 10.0.5   |
| ACL support       | System.IO.FileSystem.AccessControl 5.0.0          |
| Tests             | xUnit 2.9.3 + FluentAssertions 7.0.0              |
| SDK pin           | `global.json` → 8.0.419, `rollForward: latestFeature` |
| GC                | Server GC, concurrent                             |
| Publish Target    | Single-file, self-contained, ReadyToRun (win-x64) |

---

## License

This project is provided as-is for personal use.
