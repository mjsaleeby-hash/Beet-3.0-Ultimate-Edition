# Beet's Backup

A lightweight, portable dual-pane file manager built for backup workflows on Windows. Ships as a single `.exe` with no installer required.

Built with WPF and .NET 8, Beet's Backup is designed strictly for managing and transferring files — not launching them. It gives you side-by-side drive browsing, scheduled backups, checksum verification, and a full transfer log, all in one self-contained executable.

---

## Features

### File Management

- **Dual-pane file browser** with independent drive selection (SOURCE / DESTINATION)
- **Single-pane mode** with full drag-and-drop support
- **Drive browser** with circular usage rings showing used, total, and free space per drive
- **Copy, cut, paste, delete** via right-click context menus
- **Open in Explorer** — right-click any file or folder to open its location in Windows Explorer (folders open directly; files are pre-selected in Explorer); also works on search results, making it easy to jump to a file found deep in the directory tree
- **Drag-and-drop transfers** in both single and split pane modes
- **File and folder rename** via right-click context menu
- **Back / forward / up navigation** with full history tracking for both panes
- **Deep recursive file search** — press Enter or click the magnifying glass in the top nav bar to search the current folder tree recursively; extension-aware (`.exe` matches by extension, `exe` matches by name substring); results populate inline with a cancel button to return to normal view; live status messages report search progress and result count
- **Search path column** — a Path column appears automatically in search results showing the parent directory of each matching file, making it easy to locate results without navigating manually
- **Search and filter** textbox provides a secondary live filter on top of search results or the current folder listing
- **Bottom pane navigation bar** — split pane mode includes a dedicated full-width nav bar between the two halves with Back, Forward, Up, and path display, symmetric with the top nav bar
- **Async folder size calculation** with progress indicators; Refresh button reloads pane contents and recalculates folder sizes
- **Hidden file support** — visible, transferable, and attributes preserved
- **GridSplitter resizable panes** for flexible layout
- **No file launching** — double-clicking a file shows a reminder that this is a backup tool, not a file explorer

### Transfers

- **Transfer mode selection:** Skip Existing, Keep Both, Replace, or Mirror (Sync)
- **Mirror mode** — copies new/changed files then deletes destination files not present in source; shows a prominent confirmation warning before proceeding
- **NTFS permission stripping** — Remove Permissions checkbox strips ACLs so files work cleanly on other machines
- **SHA-256 checksum verification** — Verify Checksums checkbox confirms integrity after every copy
- **Pause, resume, and stop** controls during active transfers
- **Transfer throttling** — "Limit Speed" toolbar toggle caps bandwidth at 10 MB/s (bound to `ThrottleTransfer` in `MainViewModel`); scheduled jobs additionally support a per-job speed picker (1–100 MB/s) in the schedule dialog
- **Insufficient disk space detection** with a warning before transfer begins
- **Overall progress bar with ETA** displayed in the status bar

### Scheduled Backups

- **One-time or recurring** schedules (Daily, Weekly, Every 6 Hours, Every 12 Hours)
- **Jobs persist to disk** and survive app restarts
- **Missed backup detection** — on startup, detects jobs that were missed while the app was closed and prompts to run them immediately or skip
- **Runs in the background** via a `PeriodicTimer` while the app is open
- **Scheduler errors surfaced in status bar** — job failures and scheduler loop errors are reported immediately in the main window status bar
- **Per-job settings** for transfer mode, permission stripping, checksum verification, and exclusion filters
- **Exclusion filters** — skip files by extension pattern (e.g. `*.tmp`, `*.log`) or exact name (e.g. `Thumbs.db`, `node_modules`)
- **Backup size estimation** — "Estimate Size" button calculates total source size and file count respecting active filters; auto-runs at job start
- **Pause / resume** for running scheduled jobs via the log dialog

### Backup Log

- **Persistent JSON log** of all backup operations
- **Real-time progress bars** for currently running jobs
- **Color-coded status** indicators: Scheduled, Running, Complete, Failed
- **Detailed stats** including file counts, bytes transferred, and failure count
- **Per-file error tracking** — each failed file records its path and reason (disk full, locked, checksum mismatch, etc.), capped at 200 entries per job
- **"View Errors" button** — enabled when the selected log entry has file errors; shows a list of all failed files with reasons
- **Pause button** for running jobs, always visible in the log dialog
- **Retry button** for failed jobs
- **Export to CSV** for external reporting
- **"Open Log Folder" button** — opens `%LocalAppData%\Beet's Backup\` in Explorer for direct access to log files
- **Clear log** to reset history

### UI / UX

- **Dark and light theme** toggle
- **Update checker** — `UpdateService` queries the GitHub Releases API on startup; if a newer version is found, an accent-colored banner appears in the status bar with **Download** and **Dismiss** buttons; "Check for Updates" is also available in the Options menu; skipped versions are persisted to settings so dismissed releases are not surfaced again
- **Launch at Startup** — Options menu toggle that creates or removes a Windows startup folder shortcut (with the `--startup` flag); when the shortcut fires at login the app hides directly to the system tray unless missed backups require attention, in which case the window opens at normal size
- **Data Distribution Visual Mode** — toolbar button toggles between List view and a donut pie chart of the top 10 largest items in the current folder; color-coded with 10 distinct colors plus a muted "Other" slice; legend shows item name, icon, size, and percentage; hovering a slice highlights the matching legend entry and vice versa; chart auto-rebuilds when folder size calculations complete; works in both single-pane and split-pane modes
- **Custom logo** with gradient "Beet's Backup" branding
- **Navigation bars** — top pane nav bar with back, forward, up, path display, search, and refresh; bottom pane nav bar in split mode with symmetric back, forward, up, and path display
- **Restructured split pane layout** — top half (source tree + file list), full-width bottom nav bar, bottom half (destination tree + file list)
- **Themed toolbar** with all primary controls
- **System tray support** — closing or minimizing the window hides the app to the system tray rather than quitting; the tray icon right-click menu provides Show/Hide and Quit options; the app can only be fully exited through the tray menu
- **Single-instance enforcement** — launching a second copy signals the already-running instance to show its window (bringing it to the foreground if it was hidden in the tray), then exits cleanly
- **Smart startup behavior** — launching manually always shows the window; launching via the Windows startup shortcut (with the `--startup` flag) hides directly to tray so the app stays out of the way until needed
- **Light mode polish** — warmer gray tones throughout the light theme; dedicated brush resource keys for toggle controls, drive usage rings, and the donut chart center fill, ensuring crisp rendering in both themes

---

## Requirements

- **OS:** Windows 10 or later (x64)
- **.NET 8.0** — included automatically in the self-contained build

---

## Installation

### Option A: Download the executable

1. Download `BeetsBackup.exe` from the repository root (or the [Releases](../../releases) page if available).
2. Run it. No installer, no dependencies, no setup.

### Option B: Build from source

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone the repository:
   ```bash
   git clone https://github.com/adam1767/Test-2.0.git
   cd Test-2.0
   ```
3. Publish a self-contained single-file build:
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```

---

## Usage

1. **Launch** `BeetsBackup.exe`.
2. **Select a drive** from the sidebar to browse its contents.
3. **Enable split mode** to open a second pane and select a destination drive.
4. **Navigate** using back, forward, and up buttons or by double-clicking folders.
5. **Search recursively** by typing a term in the search box on the top nav bar and pressing Enter or clicking the magnifying glass. A **Path** column appears automatically in results showing each file's parent directory. Use the "X" button to cancel and return to the folder view. Right-click any result and choose **"Open in Explorer"** to jump directly to its location. The filter textbox applies a secondary live filter on top of any search results.
6. **Transfer files** between panes using toolbar buttons, the right-click context menu, or drag-and-drop.
7. **Choose a transfer mode** (Skip Existing, Keep Both, Replace, or Mirror) before starting a transfer. Mirror mode will delete destination files not in the source — confirm the warning before proceeding.
8. **Enable checksum verification** or **permission stripping** via the toolbar checkboxes as needed.
9. **Monitor progress** in the status bar, and use pause, resume, or stop controls during transfers. Enable the **"Limit Speed"** toolbar toggle to cap bandwidth at 10 MB/s when transfers should not saturate the drive.
10. **Schedule backups** through the schedule dialog — set a source folder, destination folder, frequency, transfer mode, permission options, checksum verification, and exclusion filters. Use "Estimate Size" to preview how much data will be transferred.
11. **Review backup history** in the log dialog to see past and active operations. Use **"View Errors"** on any entry with failures to see which files failed and why. Use **"Open Log Folder"** for direct access to all log files. Export to CSV if needed.

> **Tip:** The app must remain running for scheduled backups to execute. Enable **"Launch at Startup"** in the Options menu to have the app start automatically at login — it will hide to the system tray so it stays out of the way, and will open normally if any scheduled backups were missed. To fully quit the app, right-click the tray icon and choose **Quit** — closing the window only hides it.

---

## Project Structure

```
├── Views/               UI (MainWindow, PieChartControl, dialogs: Schedule, Jobs, Log, Rename, TransferMode, UpdateBanner)
├── ViewModels/          Presentation logic (MVVM)
├── Models/              Data types (FileSystemItem, DriveItem, ScheduledJob, TransferResult, FileError, PieSlice, etc.)
├── Services/            Core logic
│   ├── FileSystemService    Drive & file enumeration, rename, timestamp-preserving copy
│   ├── TransferService      Copy/move with dedup, permission stripping, checksum verification, per-file error tracking
│   ├── SchedulerService     Periodic backup job runner with disk persistence and SchedulerError event
│   ├── BackupLogService     JSON-based backup history with debounced saves
│   ├── FileLogger           Operational log + crash dump writer (LogDirectory: %LocalAppData%\Beet's Backup\)
│   ├── SettingsService      User preferences, dark/light mode flag, Launch at Startup shortcut management, skip-version persistence
│   ├── UpdateService        GitHub Releases API update checker with banner notification and skip-version support
│   └── ThemeService         Light/dark mode (Light.xaml & Dark.xaml; dedicated brush keys for toggles, rings, donut center)
├── Helpers/             Value converters for WPF bindings
├── Themes/              Light.xaml & Dark.xaml resource dictionaries
├── Assets/              App icon & logo
├── BeetsBackup.Tests/   xUnit test project (132 tests)
└── mockups/             HTML design mockups (data-distribution-chart.html, etc.)
```

---

## Tech Stack

| Component         | Technology                                  |
|-------------------|---------------------------------------------|
| Framework         | .NET 8.0 (WPF, Windows Desktop)             |
| Language          | C#                                           |
| Architecture      | MVVM                                         |
| MVVM Toolkit      | CommunityToolkit.Mvvm                        |
| DI Container      | Microsoft.Extensions.DependencyInjection     |
| Publish Target    | Single-file, self-contained (win-x64)        |

---

## License

This project is provided as-is for personal use.
