# Beets Backup -- Changes for 2026-04-03

## New Features

### System Tray Support

- Closing or minimizing the main window now hides the app to the Windows system tray instead of quitting
- A tray icon is always present while the app is running
- Right-clicking the tray icon shows a context menu with **Show/Hide** and **Quit** options
- The only way to fully exit the app is via the tray menu **Quit** action
- Double-clicking the tray icon toggles the window visible/hidden

### Single-Instance Show (Upgraded from Error)

- Launching a second instance of the app now signals the already-running first instance to show and bring its window to the foreground (e.g., if it was hidden in the tray)
- The second instance then exits cleanly
- Previously this just showed an "already running" error and exited without restoring the window

### Smart Startup Behavior (`--startup` flag)

- The Windows startup folder shortcut created by **Launch at Startup** now includes the `--startup` flag
- When launched with `--startup` (i.e., at Windows login), the app hides immediately to the system tray — no window shown
- When launched manually (without the flag), the window always opens normally
- If missed backups are detected on a `--startup` launch, the window opens at normal size so the missed-backup dialog is visible
- This gives clean background behavior for the common case without sacrificing discoverability on manual launch

### Light Mode Polish

- Warmer gray tones replace the previous cool/blue-tinted grays throughout the light theme
- Dedicated brush resource keys added for:
  - Toggle/pill controls — correct fill and thumb colors in light mode
  - Drive usage rings — accurate ring color and background arc in light mode
  - Donut chart center fill — correct background for the hollow center of the pie chart in light mode
- All new keys follow the existing `DynamicResource` pattern so they respond instantly to theme switches

### Namespace Disambiguation (Internal Fix)

- Resolved ambiguous type references caused by `System.Windows.Forms` and WPF coexisting in the same assembly (leftover from the `FolderBrowserDialog` era)
- Explicit `using` aliases and fully-qualified type names added where needed
- No user-visible behavior change; fixes compilation warnings and prevents future collision bugs

---

# Beets Backup -- Changes for 2026-04-01

## Bug Fixes — Critical

- **Scheduler race condition** — `SnapshotJob()` clones job data before background execution; `ExecuteJobAsync` reads snapshot only; `LastRun` written back under lock
- **`_pauseGate` ObjectDisposedException** — fresh `ManualResetEventSlim` created per transfer, old one disposed after signaling
- **SaveJobs serialization race** — resolved by snapshot fix above

## Bug Fixes — High

- **Infinite loop on `TimeSpan.Zero`** — `UpdateNextRun` now guards `> TimeSpan.Zero`
- **BackupLogService I/O jank** — `Save()` debounced with 1-second delay
- **FileSystemItem.Size cross-thread** — `CalculateDirectorySizeAsync` marshals Size setter to UI thread via `Dispatcher.InvokeAsync`
- **CheckFreeSpace UNC crash** — `DriveInfo` wrapped in try/catch; check skipped for UNC/relative paths
- **Timestamp preservation** — `FileSystemService.CopyFile` now calls `File.SetLastWriteTimeUtc` after copy; fixes incremental backups re-copying unchanged files

## Bug Fixes — Medium / Low

- **Null `Application.Current` on shutdown** — null guard added in `CalculateFolderSizesWithProgressAsync`
- **SchedulerService never disposed** — `App.OnExit` disposes `ServiceProvider`
- **TimeSpan locale sensitivity** — `NullableTimeSpanConverter` uses `CultureInfo.InvariantCulture`
- **CancellationTokenSource leak** — old CTS instances disposed before creating new ones
- **Drive ejection race** — `LoadDrives` catches `IOException` per drive
- **CSV newline escaping** — `Escape()` strips `\r`, replaces `\n` with space

## New Features

### "Open Log Folder" Button (Log Dialog)
- Opens `%LocalAppData%\Beet's Backup\` in Explorer from the Log dialog
- `FileLogger.LogDir` renamed to `FileLogger.LogDirectory` (now `public`)

### Scheduler Error Surfacing (Status Bar)
- `SchedulerService.SchedulerError` event fires on loop errors and job failures
- `MainViewModel` subscribes and displays the message in the status bar (UI-thread marshaled)
- Unsubscribed in `Dispose()`

### Per-File Error Tracking
- `TransferResult.FileErrors` — `List<FileError>` capped at 200 via `AddFileError(path, reason)`
- `FileError` class: `Path` + `Reason` properties
- Every catch point in `TransferService.CopyItem` records a `FileError`
- `BackupLogEntry.FilesFailed` count + `BackupLogEntry.FileErrors` list persisted to JSON
- `StatsDisplay` shows "Complete with N error(s)" when failures present
- Red **"View Errors"** button in Log dialog — enabled when selected entry has errors; shows MessageBox with full file list

## Test Suite
- **132 tests passing** (unit, service, viewmodel, edge case, stress, data integrity)

---

# Beets Backup -- Changes for 2026-03-25

## New Features

### 1. Data Distribution Visual Mode (Pie Chart)

A new **Visual Mode** can be toggled from the toolbar (button sits next to the Dark/Light theme toggle). The toolbar button shows a gradient pie symbol and its label dynamically reads "Visual" in list mode and "List View" in visual mode.

When active, the file list is replaced with a **donut pie chart** visualizing the top 10 largest items in the current folder:
- **10 distinct colors** — blue, rose, green, amber, violet, red, sky, orange, emerald, fuchsia
- **"Other" slice** (muted gray) covers all items beyond the top 10
- **Legend panel** to the right lists each item with its name, file/folder icon, size, and percentage
- **Cross-highlighting** — hovering a pie slice highlights the matching legend row, and hovering a legend row highlights the matching slice
- **Auto-rebuilds** when async folder size calculations finish (cancellation-safe via `TaskContinuationOptions.OnlyOnRanToCompletion`)
- Works in both **single-pane and split-pane** modes

**Implementation notes:**
- Pure WPF — `PathGeometry` + `ArcSegment` (same pattern as `UsageToArcConverter`); no third-party charting library
- `TryFindResource` used for all brush lookups (theme-safe)
- Event handler cleanup on each rebuild prevents listener accumulation
- `FormatBytes` in `FileSystemItem.cs` promoted from `private` to `internal static` for reuse by `PieSlice`

**New files:** `Models/PieSlice.cs`, `Views/PieChartControl.xaml`, `Views/PieChartControl.xaml.cs`, `mockups/data-distribution-chart.html`

**Modified files:** `Models/FileSystemItem.cs`, `ViewModels/MainViewModel.cs`, `Views/MainWindow.xaml`

---

# Beets Backup -- Changes for 2026-03-21

This document covers all gaps fixed, new features added, and bugs resolved in the 2026-03-21 session.

---

## Gaps Fixed

### 1. All Drives View Wired Up

The `DriveItemTemplate` with circular usage arcs existed in XAML but was never actually applied to any `TreeView`. All three tree views now share a single `HierarchicalDataTemplate` (`FolderTreeTemplate`) that renders drive usage rings (used/total space) for root drive nodes and plain folder names for subfolders. This means the drive capacity visualizations that were already designed are now visible everywhere they should be.

### 2. Scheduled Jobs Persisted to Disk

Scheduled jobs previously lived only in memory and were lost on app close. `SchedulerService` now saves and loads jobs to `%LocalAppData%\Beet's Backup\scheduled_jobs.json`. A custom `NullableTimeSpanConverter` was added to handle `TimeSpan?` serialization, since the default JSON serializer does not round-trip nullable `TimeSpan` values cleanly.

### 3. Delete Confirmation Dialog

`DeleteItem` used to recurse-delete immediately with no user prompt. It now shows a confirmation dialog with Yes/No buttons before proceeding. When multiple items are selected, the dialog displays the count of items that will be deleted.

### 4. Single-Pane Drag and Drop

The `TopPaneList` was missing `AllowDrop` and the corresponding drag/drop event handlers that the split-pane view already had. These have been added so drag-and-drop works identically in both single-pane and split-pane modes.

### 5. Refresh Button

A "Refresh" toolbar button was added, bound to `RefreshDrivesCommand`. It re-enumerates all drives, which is useful after hot-plugging a USB drive without restarting the app.

---

## New Features

### 6. Rename

Right-click context menu now includes a "Rename" option. Selecting it opens `RenameDialog`, a new dialog with a pre-filled text box (name pre-selected for quick typing). `FileSystemService.RenameItem` performs the rename via `Directory.Move` or `File.Move` depending on the item type. Both panes refresh after the operation completes.

**New files:** `Views/RenameDialog.xaml`, `Views/RenameDialog.xaml.cs`

### 7. Search / Filter

A search text box was added to the navigation bar. Typing filters the current folder listing by name using case-insensitive matching. This is implemented through `ICollectionView` filters exposed as `FilteredTopPaneItems` and `FilteredBottomPaneItems` on the view model. Clearing the text box restores the full listing.

### 8. Back / Forward / Up Navigation

The navigation bar now includes Back, Forward, and Up buttons along with a path display. Navigation history is tracked in a list with an index pointer. Navigating to a new folder prunes forward history (standard browser-style behavior). All three buttons have `CanExecute` guards so they disable appropriately when there is nowhere to go.

### 9. Overall Progress Bar with ETA

A `ProgressBar` and ETA label were added to the status bar. During active transfers, progress is reported as a percentage and the remaining time is estimated from elapsed time divided by current progress. Both elements are only visible while a transfer is in progress.

### 10. SHA-256 Checksum Verification

A "Verify Checksums" checkbox was added to the transfer options. When enabled, after each file is copied the app computes SHA-256 hashes of both the source and destination files and compares them. Any mismatches are counted in `TransferResult.ChecksumMismatches` and reported in the status bar summary at the end of the transfer.

### 11. Export Backup Log to CSV

The `LogDialog` now has an "Export CSV" button. Clicking it opens a `SaveFileDialog` and writes all log entries as a CSV file with proper field escaping (quoting fields that contain commas or quotes).

### 12. Rename Dialog (UI)

`RenameDialog` is a simple modal window. The text box is pre-filled with the current item name and the name portion is pre-selected so the user can immediately start typing a replacement. Cancel and Rename buttons close the dialog with the appropriate `DialogResult`.

---

## Bug Fixes

### 13. Cross-Thread ObservableCollection Crash

`BackupLogService.Add` was calling `Entries.Insert` from a background thread, which throws because `ObservableCollection` is not thread-safe for UI-bound collections. Fixed by adding a `Dispatcher.CheckAccess()` guard that marshals the insert onto the UI thread when needed.

### 14. Cross-Thread PropertyChanged in UpdateStatus

`UpdateStatus` was raising `PropertyChanged` from a background thread without dispatching, even though other methods in the same class already used `Dispatcher.BeginInvoke`. The same dispatcher wrapper was added here for consistency and correctness.

---

## Files Changed

| File | Status |
|------|--------|
| `Models/ScheduledJob.cs` | Modified |
| `Models/TransferResult.cs` | Modified |
| `Services/BackupLogService.cs` | Modified |
| `Services/FileSystemService.cs` | Modified |
| `Services/SchedulerService.cs` | Modified |
| `Services/TransferService.cs` | Modified |
| `ViewModels/MainViewModel.cs` | Modified |
| `Views/MainWindow.xaml` | Modified |
| `Views/MainWindow.xaml.cs` | Modified |
| `Views/LogDialog.xaml` | Modified |
| `Views/LogDialog.xaml.cs` | Modified |
| `Views/RenameDialog.xaml` | **New** |
| `Views/RenameDialog.xaml.cs` | **New** |
