# Beets Backup -- Commercial Viability Assessment

**Assessed:** 2026-03-24
**Assessor:** Architect review
**Verdict:** Foundation is solid. Estimated **3-5 developer days** to reach commercial quality.

**Stack confirmed clean:** MVVM, standard .NET BCL only (File.Copy, Directory.EnumerateFiles, DriveInfo, System.Text.Json), 3 NuGet packages, zero kernel drivers or P/Invoke. No risk of breakage from Windows Updates.

---

## CRITICAL FIXES — Day 1-2 (Non-negotiable before any release)

### 1. Move Data-Loss Bug
- **File:** `TransferService.MoveAsync`, lines 94-95
- **Problem:** Deletes the source directory after copy regardless of whether individual files succeeded. 3 of 500 files fail = those 3 are permanently lost.
- **Fix:** Track which top-level source items completed with zero failures; only delete those. Alternatively, move the delete inside `CopyAndVerify` after verified success.
- **Effort:** 1 afternoon
- **Status:** RESOLVED (2026-03-24) — per-item failure tracking added; source only deleted when `failedDuring == 0`; partial failures preserve source and report to user

### 2. Atomic JSON Saves
- **Files:** `BackupLogService.Save`, `SettingsService.Save`, `SchedulerService.SaveJobs`
- **Problem:** All use `File.WriteAllText` directly. Power loss mid-write = truncated/corrupted file. User loses entire log history and scheduled jobs.
- **Fix:** Write to `.tmp` first, then `File.Replace(tmpPath, targetPath, backupPath)`. Atomic on NTFS.
- **Effort:** 30 minutes
- **Status:** RESOLVED (2026-03-24) — all three services write to `.tmp` then use `File.Replace` with `.bak` backup

### 3. Scheduler Race Condition
- **File:** `SchedulerService.cs`
- **Problem:** `_jobs` is a plain `List<T>` accessed from UI thread and timer thread with no lock. Can crash the scheduler silently via `InvalidOperationException` during enumeration.
- **Fix:** Add `lock(_jobsLock)` around all reads/writes/iterations, or use `ImmutableList` with interlocked replace.
- **Effort:** 1 hour
- **Status:** RESOLVED (2026-04-01) — `SnapshotJob()` clones job data before handing to background tasks; only `LastRun` written back under lock

### 4. Silent Scheduler Death
- **File:** `SchedulerService.cs`, line 28
- **Problem:** `_ = RunAsync(_cts.Token)` fire-and-forgets. If `RunAsync` throws, the scheduler dies permanently with no UI indication.
- **Fix:** Store the task; add top-level try/catch inside `RunAsync` that logs to `BackupLogService`, optionally restarts the loop, and surfaces a warning in the status bar.
- **Effort:** 30 minutes
- **Status:** RESOLVED (2026-04-01) — `SchedulerError` event fires on scheduler loop errors and job failures; `MainViewModel` surfaces message in status bar

### 5. Single-Instance Mutex
- **Problem:** No mutex check on startup. Two instances can run simultaneously, both running schedulers and racing on JSON files.
- **Fix:** Add named `Mutex` in `App.OnStartup`. Show "Already running" message if owned, then exit.
- **Effort:** 20 minutes
- **Status:** RESOLVED (2026-03-24) — named Mutex in `App.OnStartup`; second instance signals first to show window via `EventWaitHandle`, then exits

### 6. Professional Error Messages
- **Problem:** Placeholder text in user-facing messages: "Not enough space on destination dummy!", "Can't do that dummy!"
- **Fix:** Replace with professional messages including relevant data (e.g., required vs. available space).
- **Effort:** 15 minutes
- **Status:** RESOLVED (2026-03-24) — all user-facing messages replaced with professional text

---

## HIGH PRIORITY — Day 3-5

### 7. Recycle Bin for Deletes
- **File:** `MainViewModel.cs` `DeleteItemCommand`, `MainWindow.xaml.cs` `Delete_Click`
- **Problem:** `Directory.Delete(path, true)` is permanent and unrecoverable. Confirmation only in code-behind, not ViewModel.
- **Fix:** Use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` with `RecycleOption.SendToRecycleBin`. Move confirmation logic into ViewModel.
- **Status:** RESOLVED (2026-03-24) — `FileSystemService.DeleteItem` uses `FileSystem.DeleteFile/DeleteDirectory` with `RecycleOption.SendToRecycleBin`; confirmation dialog in `MainWindow.Delete_Click`

### 8. SkipExisting Size-Only Comparison
- **File:** `TransferService.cs`, lines 180-192
- **Problem:** Only compares file size. A same-size corrupted file is silently skipped.
- **Fix:** Add timestamp comparison (`FileInfo.LastWriteTime` is already loaded — zero cost).
- **Status:** RESOLVED (2026-03-24) — compares both `Length` and `LastWriteTimeUtc`

### 9. Async Tree Expansion
- **File:** `FolderTreeItem.cs`, lines 97-111
- **Problem:** `IsExpanded` setter calls `LoadChildren()` synchronously. Network drives cause multi-second UI freezes.
- **Fix:** Convert to async; use `Task.Run` for `Directory.EnumerateDirectories`, populate via dispatcher.
- **Status:** RESOLVED (2026-03-24) — `LoadChildrenAsync()` uses `Task.Run` for enumeration

### 10. Deep Search UI Thread Hammering
- **File:** `MainViewModel.cs`, `SearchDirectoryRecursive`, line 426
- **Problem:** Each matched file does a synchronous `Dispatcher.Invoke`. 5,000 results = 5,000 cross-thread round-trips.
- **Fix:** Buffer results into a `List<T>`, dispatch in batches of 50-100 using `BeginInvoke`.
- **Status:** RESOLVED (2026-03-24) — batched `BeginInvoke` dispatch with flush at end

### 11. Stale "Running" Log Entries
- **File:** `BackupLogService.cs`
- **Problem:** A crash during transfer leaves the log entry permanently at "Running" status.
- **Fix:** On `Load`, find entries with `Status == Running`, rewrite as `Failed` with message "Interrupted (app was closed)".
- **Status:** RESOLVED (2026-03-24) — `Load()` marks stale Running entries as Failed with "Interrupted" message, then saves

### 12. Unbounded Log Growth
- **File:** `BackupLogService.cs`
- **Problem:** No max entry count. Daily backups for 2 years = 730+ entries, all re-serialized on every save.
- **Fix:** Cap at 500 entries, drop oldest on overflow.
- **Status:** RESOLVED (2026-03-24) — `Add()` caps at 500 entries, drops oldest on overflow

### 13. Disk-Full Mid-Transfer
- **File:** `TransferService.cs`
- **Problem:** Pre-check passes but drive fills up mid-copy. Per-file errors scroll past and are not persisted to the log.
- **Fix:** Catch `IOException` with HResult `0x80070070` (`ERROR_DISK_FULL`); add `DiskFullErrors` counter to `TransferResult`.
- **Status:** RESOLVED (2026-04-01) — per-file errors (disk full, locked file, checksum mismatch, general I/O) recorded in `TransferResult.FileErrors` and persisted to `BackupLogEntry.FileErrors`; "View Errors" button in Log dialog

---

## MEDIUM / LOW PRIORITY

### 14. Locked File Counter (VSS Alternative)
- **Problem:** Locked files (Outlook `.ost`, databases) throw `IOException` and are silently skipped.
- **Fix:** Catch locked-file `IOException` (HResult `0x80070020`), track `FilesLocked` counter in `TransferResult`, show prominently in completion message.
- **Status:** Open

### 15. Crash Logger
- **File:** `App.xaml.cs`
- **Problem:** No `DispatcherUnhandledException`, `AppDomain.UnhandledException`, or `TaskScheduler.UnobservedTaskException` handlers.
- **Fix:** Add all three; write to `%LocalAppData%\Beet's Backup\crash_log.txt`.
- **Status:** Open

### 16. Operational Log File
- **Problem:** No developer-facing trace of which files failed; no exception stack traces.
- **Fix:** Add `FileLogger` service appending to `operational.log` (10 MB cap, single rotation).
- **Status:** Open

### 17. SettingsService Self-Serialization
- **File:** `SettingsService.cs`, line 33
- **Problem:** Serializes `this` — fragile if fields are ever added.
- **Fix:** Extract a `SettingsData` record/POCO.
- **Status:** Open

### 18. Replace WinForms FolderBrowserDialog
- **File:** `ScheduleDialogViewModel.cs`, line 46
- **Problem:** Uses `System.Windows.Forms.FolderBrowserDialog`, pulling in the entire WinForms assembly.
- **Fix:** Use .NET 8's `Microsoft.Win32.OpenFolderDialog`.
- **Status:** Open

### 19. ClearLog Not Persisting
- **File:** `LogDialog.xaml.cs`, line 22
- **Problem:** `Entries.Clear()` does not save to disk afterward.
- **Fix:** Call `Save()` after `Clear()`, or expose a `Clear()` method on `BackupLogService`.
- **Status:** Open

### 20. Assembly Version Metadata
- **Problem:** No `AssemblyVersion`, `AssemblyFileVersion`, `AssemblyProduct`, etc.
- **Fix:** Add attributes for Windows Properties dialog and installer metadata.
- **Status:** Open

### 21. Navigate UI Thread Blocking
- **File:** `MainViewModel.cs`, lines 222-224
- **Problem:** `_fs.GetChildren(path)` enumerates directory synchronously on the UI thread.
- **Fix:** Wrap in `Task.Run`, populate via dispatcher.
- **Status:** Open

### 22. TransferService Directory Failure Count
- **File:** `TransferService.cs`, lines 150, 164
- **Problem:** Directory failures increment `FilesFailed` but `TotalFiles` only counts files, so the failure percentage can exceed 100%.
- **Fix:** Track directory failures in a separate counter.
- **Status:** Open

---

## VSS Decision

Not adding VSS at this time. Instead, detect locked files specifically (see item 14) and report them clearly. Add VSS only if customers request backup of live databases or mailboxes.

---

## Work Split Reference

| Priority | Items | Target Days |
|----------|-------|-------------|
| Critical | 1-6 | Day 1-2 |
| High | 7-13 | Day 3-5 |
| Medium/Low | 14-22 | Backlog |

---

## 2026-03-24 — Implementation Session Results

All 21 fixes from the architect assessment were implemented across **14 files** by **8 parallel developer agents**.

### 4 Additional Bugs Found and Fixed by Debugger Agent

1. **MoveAsync safety check incomplete** — The fix for item 1 (Move data-loss bug) missed resetting `DiskFullErrors` and `FilesLocked` counters, meaning a disk-full or locked-file failure during a move would not trigger source preservation. Data loss risk. Fixed.
2. **Mutex ReleaseMutex throws on second instance exit** — The single-instance mutex (item 5) called `ReleaseMutex()` on a mutex the second instance never acquired, causing a runtime crash on exit. Fixed.
3. **Progress bar never reaches 100%** — `DiskFullErrors` and `FilesLocked` were not included in the progress calculation denominator, so the bar could not reach 100% when those error types occurred. Fixed.
4. **BackupLogService.Load marks stale entries as Failed but never saves** — The stale-entry fix (item 11) correctly rewrote in-memory entries but did not call `Save()` afterward, so the fix was lost on the next crash. Fixed.

### 6 Awareness Items (No Fix Needed Now)

- **Recycle Bin COM on non-STA thread** — Delete dispatches to a thread pool thread; COM dialog could fail in rare edge cases. Acceptable for now.
- **BackupLogEntry.Id get-only breaks JSON round-trip** — Deserialized entries get new GUIDs. Does not affect current functionality.
- **ExecuteJobAsync fire-and-forget** — Scheduler job execution is fire-and-forget. Already mitigated by the top-level try/catch added in item 4.
- **SaveJobs I/O inside lock** — File write happens while holding the jobs lock. Low risk given current usage patterns.
- **CheckFreeSpace fails on UNC paths** — `DriveInfo` only accepts drive letters. Network backup targets may skip the free space pre-check. **RESOLVED (2026-04-01)** — wrapped in try/catch; check silently skipped for UNC/relative paths.
- **Search batch flush after cancellation** — A final partial batch may dispatch after cancellation is signaled. Cosmetic; no data impact.

### Build Outcome

- **0 warnings, 0 errors**
- App launches successfully

### Notable Changes

- **New service:** `FileLogger.cs` — operational logging to `%LocalAppData%\Beet's Backup\operational.log` with 10 MB rotation
- **`UseWindowsForms` removed** from `.csproj`; replaced with native .NET 8 `OpenFolderDialog` (`Microsoft.Win32`)
- **Assembly metadata added:** version 3.0.0, product name, company

### UI Mockup

An HTML mockup was created at `mockups/beets-backup-reimagined.html` showing a modernized UI vision. All design changes shown are achievable within WPF.

### Next Session

**Build `BeetsBackup.Tests/` — an xUnit test project** to automate testing of all critical paths identified in the architect review. Test scope documented in `notes/beets-backup-features-and-testing.txt`.
