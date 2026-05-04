## 2026-04-18 — Performance roadmap (post-Phase 1)

Performance work is organized in phases from the 2026-04-18 Windows practices audit (`PerformanceMonitor/audit/windows-practices-audit-2026-04-18.md`). Phase 1 is complete. Phases 2–4 remain.

---

### Phase 2 — I/O Tuning (~1–2 hours, low risk)

- [x] **FileStream options** — Added `FileOptions.SequentialScan` and 1 MB internal buffers to `FileStream` constructors in `CopyFileWithHash` and `ThrottledCopy`. Also applied to `ComputeSha256`. `CopyFile` uses `File.Copy` (kernel-optimized, no change needed).
- [x] **BackupLogService debounce** — Raised debounce from 1s to 5s. Terminal statuses (Complete/Failed) now call `SaveNow()` directly, bypassing debounce. Running/Scheduled transitions use the debounced path.
- [x] **Flush-to-disk in verify mode** — Both `CopyFileWithHash` and `ThrottledCopy` now flush to disk (`Flush(flushToDisk: true)`) before the destination re-read. The re-read is retained — flush alone cannot detect silent corruption, USB failures, or bit flips. The flush reduces the chance of re-reading stale OS cache data.

---

### Phase 3 — Parallel Copy Engine (~half day, medium risk)

- [x] **`Parallel.ForEachAsync` with drive-type-aware concurrency** — Stage 4 (`0bbd938`). Workers gated by `DriveTypeService.GetWorkerCount`: SSD↔SSD = `min(8, ProcessorCount)`; any HDD/removable in pair = 1; network = 4; unknown = 2.
- [x] **Thread-safe counters** — Stage 1 (`6492b24`). `TransferResult` counters use `Interlocked.Increment`/`Add`; `AddFileError` is locked. `TryAdvanceReportedPercent` enforces monotonic percent reports under parallelism.
- [x] **Ordered directory creation** — Stage 3b (`168b730`). Phase 2 of `CopyAsync` pre-creates all `DirectoryWorkItem` paths sorted by depth before any file workers start. `EnumerateWorkItems` produces the frozen plan.
- [x] **Thread-safe mirror cleanup** — Mirror cleanup is still sequential (runs after `Parallel.ForEachAsync` completes). Intentional: destruction logic deserves audit-friendly serial code, and mirror cleanup is a small fraction of total wall time. `VssSnapshotService.GetOrCreateSnapshotRoot` was made thread-safe in Stage 4 for the parallel copy phase itself.
- [ ] **Regression sweep on real hardware** — Stage 5. Automated test suite is 169 green; remaining items need a real backup run: SSD→HDD perf check, VSS fallback path on a locked file, mirror with non-empty destination, large-volume checksum verify, mid-run cancellation, pause/resume.

---

### Phase 4 — Hygiene (low risk, sweeping)

- [x] **`ConfigureAwait(false)` sweep** — Added to every `await` in `TransferService.cs` (4), `SchedulerService.cs` (6), and `UpdateService.cs` (1). Eliminates the dispatcher-context deadlock class that caused the zombie process bug.
- [x] **Lazy DI in `App.OnStartup`** — Pulled `LoadJobs()` out of the `SchedulerService` constructor into an explicit `Load()` (called from `App.OnStartup` and `RunHeadlessJob`). Wrapped `UpdateService` in `Lazy<UpdateService>` so it's not constructed until the post-launch update check actually runs.
- [x] **Long-path support** — Added `app.manifest` with `longPathAware` and Windows 10/11 compatibility. Paths >260 characters now work without `\\?\` prefix normalization.
- [x] **`CopyFileEx` PInvoke** — `FileSystemService.CopyFile` now calls Win32 `CopyFileEx` directly (kernel-level sparse-file/ADS handling). The throttled and checksum-verify paths still use buffered `FileStream` for chunk-level rate limiting and SHA-256 streaming. Paths are converted to `\\?\` extended form so the call is long-path-safe regardless of the host process's manifest.

---

### Other open items (pre-existing, from 2026-04-01 assessment)

- [x] Move data-loss: source delete on partial failure — Fixed: per-item failure tracking, source preserved on any error
- [x] Atomic JSON saves — Fixed: all three services use write-to-tmp + File.Replace pattern
- [x] Single-instance mutex — Fixed: named Mutex in App.OnStartup, second instance signals first to show
- [x] Professional error messages (remove "dummy!" text) — Fixed: all user-facing messages are professional
- [x] Items 7–12 from 2026-03-24 viability assessment — all resolved (recycle bin deletes, timestamp comparison, async tree, search batching, stale log entries, log cap at 500)
- [x] Backup wizard (fully implemented — 7-step wizard with stepper UI, disk-space preflight, wizard validates source/destination)
- [ ] Dry run / preview mode
- [ ] Encryption
- [ ] Pre/post backup scripts
- [ ] Delta / block-level copying

---

### Medium/low viability items still open (from 2026-03-24 assessment)

- [ ] **#14 Locked file counter** — Catch locked-file `IOException` (HResult `0x80070020`), track `FilesLocked` counter in `TransferResult`, show in completion message
- [ ] **#15 Crash logger** — Add `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` handlers; write to `crash_log.txt`
- [ ] **#17 SettingsService self-serialization** — Extract `SettingsData` POCO; currently serializes `this` which is fragile
- [ ] **#21 Navigate UI thread blocking** — `_fs.GetChildren(path)` on UI thread; wrap in `Task.Run` + dispatcher populate
- [ ] **#22 TransferService directory failure count** — Directory failures increment `FilesFailed` but skew the failure %; track separately

---

## Future Fixes / Additions

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
