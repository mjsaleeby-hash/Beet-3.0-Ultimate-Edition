## Reliability & Optimization Roadmap

Focus: bug-free before feature-complete. No new features unless critical.

> **Current work is NOT tracked in this file.** The active 3.0→4.0 plan and its
> execution checklist live in `improvements/IMPLEMENTATION-PLAN.txt` (§9 is ticked
> and records outcomes, not just checkboxes). As of 2026-07-24: Wave 1 done and
> field-stable; Wave 2.1 done; the stale `v3.0.0` label is fixed (binds to
> `BuildInfo.Version`, commit `4330fdd`). The 4.0-candidate cohort window closed 7/23;
> its A/B report tooling had three bugs now fixed (date-windowing, crash attribution,
> workload normalization) — a final verdict still needs FAT/exFAT candidate data (run
> a backup to the USB stick twice). Two field defects were triaged from the report:
> the shutdown-teardown crash is FIXED; the scheduled-job double-run / phantom "Failed"
> is DIAGNOSED-only and deferred (touches the proven scheduler core — see
> `notes/bugs.md` + `notes/decisions.md`). Next code item is Wave 2.2. This file
> remains the roadmap for the items below, which that plan does not cover (VSS
> elevation, launcher rename, deferred features).

---

### Tier 1 — Visibility (do first, enables everything else)

- [x] **Crash logger (#15)** — All three handlers (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) wired in `App.OnStartup`. `FileLogger.WriteCrashDump` writes to `%LocalAppData%\Beet's Backup\crash_dump.log` with timestamp, source, app/OS/.NET version, memory, uptime, exception type + HResult + stack trace, full inner-exception chain, and AggregateException unrolling. Auto-rotates at 10 MB. Handler registration was moved above the `--run-job` headless branch so scheduled backups crashing unattended also produce a dump.

---

### Tier 2 — Fragile code that will eventually break

- [x] **SettingsService self-serialization (#17)** — `SettingsService` already uses a `SettingsData` POCO (`Save()` serializes `Data`, `Load()` deserializes `SettingsData`). The other two persistence services (`SchedulerService`, `BackupLogService`) were already serializing clean POCO collections (`List<ScheduledJob>`, `List<BackupLogEntry>`). No `JsonSerializer.Serialize(this)` anywhere in the codebase.

- [x] **UI thread blocking on navigate (#21)** — Both `NavigateTop` and `NavigateBottom` already wrap `_fs.GetChildren(path).ToList()` in `Task.Run` with cancellation tokens that supersede stale navigations. Recursive search (top + bottom) likewise runs inside `Task.Run` with batched `dispatcher.BeginInvoke` updates. Remaining `Directory.Exists`/`File.Exists` calls on the UI thread are single-syscall checks on user-chosen paths — not freeze hazards.

---

### Tier 3 — Reporting accuracy

- [x] **Locked file counter (#14)** — `TransferResult.FilesLocked` is populated by `TransferService` and now persisted to `BackupLogEntry.FilesLocked`. `BackupLogEntry.StatsDisplay` and the new `TransferReporter.FormatSummary` surface "X locked" separately from "X failed". `SchedulerService` no longer lumps locked files into `FilesFailed`.

- [x] **Directory failure count (#22)** — `TransferResult.DirectoriesFailed` is populated by `TransferService` (4 sites) and now persisted to `BackupLogEntry.DirectoriesFailed`. Surfaced as "X folders failed" in `StatsDisplay` and toast/log summaries via `TransferReporter`. File failure ratios now reflect only file-level errors.

**Refactor by-product**: extracted `Services/TransferReporter.cs` so manual transfers (`MainViewModel`) and scheduled jobs (`SchedulerService`) share one consistent surface for `FormatSummary`, `Notify`, and `TotalFailed`. Replaced `BackupLogService.UpdateStats` long-param signature with `(id, status, TransferResult)` that auto-extends as new counters land. Old `BackupLogEntry.StatsDisplay` format `"X copied, Y skipped, Z failed, bytes"` replaced with one that lists every non-zero counter (e.g. `"100 copied, 50 skipped, 12 locked, 2 folders failed, 1.2 GB"`).

---

### Tier 4 — UX correctness (after counters are accurate)

- [x] **Homepage pause/stop control over scheduled backups** — Previously the toolbar pause/stop buttons only worked for manual file-pane transfers and went disabled while a scheduled run was active. Scheduled backups had no cancel path at all (no `CancellationToken` was passed to the transfer call), so a runaway scheduled run could only be stopped by killing the process. `SchedulerService` now tracks per-job CTSs in `_runningCts`, exposes `IsRunningAnyJob`, `IsRunningJobPaused`, `RunningJobLogIds`, plus `PauseRunning()` / `ResumeRunning()` / `CancelRunning()`, and fires a `RunningJobChanged` event. `MainViewModel.IsTransferring` and `IsPaused` are now combined getters over manual + scheduler state; pause/stop commands route to whichever flow is active. The progress banner mirrors the running scheduled entry's `ProgressPercent`. Cancelled scheduled runs are logged as Skipped with "Cancelled by user".

- [x] **Clearer dashboard/completion text** — When a backup completes with 0 copies, 0 failures, and only skips, the summary now reads `"Up to date — N files verified"` instead of `"0 copied, N skipped"`. Applied consistently across `TransferReporter.FormatSummary` (status bar + `FileLogger`), `TransferReporter.Notify` (Windows toast body), and `BackupLogEntry.StatsDisplay` (LogDialog grid). Empty-input case reads `"Done — nothing to do"` so the previous bare `"Done."` doesn't get confused with a real run that did nothing. Locked-file case correctly does NOT use the up-to-date phrasing — locked files are real failures, not a clean run.

---

### Tier 5 — Architecture (no rush)

- [ ] **VSS elevation — Option A (elevated helper subprocess)** — Keep main app non-elevated. When a locked file is hit, spawn a small helper exe via `ShellExecute("runas")` that does only the VSS snapshot, returns the shadow path via named pipe or temp file, then exits. One UAC prompt, only when needed. See architecture notes below.

- [ ] **Launcher rename for shipping** — `BeetsBackupLauncher.exe` → `BeetsBackup.exe` (user-facing); current elevated WPF app → `BeetsBackup.Core.exe` (internal). See details below.

---

### Deferred features (not being worked — parked for reference)

- [ ] Dry run / preview mode
- [ ] Encryption
- [ ] Pre/post backup scripts
- [ ] Delta / block-level copying

---

## Architecture Notes

### VSS — Remove elevation requirement (2026-04-21)

Currently `VssSnapshotService` requires the app to be running as Administrator because Windows
only allows elevated processes (or Backup Operators group members) to create VSS snapshots.
This is an OS-level restriction — `CreateVssBackupComponents` checks the caller's token and
returns `E_ACCESSDENIED` for non-elevated processes.

Three options, in preference order:

**Option A — Elevated helper subprocess (recommended)**
Keep the main app non-elevated. When a locked file is hit during transfer, spawn a small
separate helper exe via `ShellExecute("runas")`. The helper does only the VSS snapshot,
writes the resulting shadow path back to the main process via a named pipe or temp file,
then exits. Result: one UAC prompt, only when the user actually hits a locked file.

**Option B — Detect and offer restart-as-admin**
Run normally. If VSS fails with `E_ACCESSDENIED`, catch it and show a one-time prompt:
"Some files are locked. Restart Beet's Backup as Administrator to copy them?" Then relaunch
with `ShellExecute("runas")`. Quick to implement; UX is slightly clunky (full app restarts).

**Option C — Require administrator at launch (simplest, most intrusive)**
Add `requestedExecutionLevel level="requireAdministrator"` to the app manifest. UAC prompts
every time the app starts regardless of whether VSS is needed. Approach used by Macrium,
Veeam, etc. Acceptable for a dedicated backup tool; annoying for casual users.

---

### Launcher / main-exe rename for shipping (2026-04-29)

The current dual-exe layout works but is dev-only — `BeetsBackup.exe` is `requireAdministrator`
(VSS) and `BeetsBackupLauncher.exe` is the `asInvoker` stub the user pins to skip UAC on every
taskbar click. End users would be confused by two exes; the pinned one (launcher) doesn't match
the obvious "main app" name.

**Plan for final build (preferred — Option 1):**

- Rename `BeetsBackupLauncher.exe` → `BeetsBackup.exe`. This is the user-facing exe, the one
  pinned, the one in Start Menu, the one in installer shortcuts.
- Rename current `BeetsBackup.exe` (the elevated WPF app) → `BeetsBackup.Core.exe` (or similar
  internal name). Implementation detail; users never click it directly.
- Update the launcher's `Process.Start` target to the new core exe name.
- Update `WindowsTaskSchedulerService` (the headless `--run-job` path, commit `d993311`) to
  point at the renamed core exe. Headless still needs elevation; runs without UAC because it's
  invoked by Task Scheduler.
- Update the ONLOGON scheduled task target (commit `4676bb8`) to point at the launcher (so
  Beet starts at logon without UAC).
- Re-skin the launcher's `.csproj` `Description`/`Product` so file properties read sensibly.
- Update build artifacts / `CopyExeToRoot` targets accordingly.

**Fallback (Option 2 — if rename is too disruptive):**

Leave the dual-exe names. The installer creates a single Start Menu shortcut "Beet's Backup"
whose Target is `BeetsBackupLauncher.exe`. End users only ever see "Beet's Backup" in Start
Menu and can right-click → Pin from there. The dual-exe layout is hidden unless someone
browses the install folder.

**Files known to reference the exe names:**
- `BeetsBackupLauncher/Program.cs` — `BeetsBackup.exe` constant in `Path.Combine`
- `BeetsBackupLauncher/BeetsBackupLauncher.csproj` — assembly name + `CopyLauncherToRoot` target
- `BeetsBackup.csproj` — assembly name + `CopyExeToRoot` target
- `Services/WindowsTaskSchedulerService.cs` — schtasks `/TR` argument (BeetsBackup.exe path)
- `Services/StartupService.cs` (or wherever ONLOGON task creation lives) — task target path
- Anywhere `Environment.ProcessPath` is consumed and compared against a hardcoded name
