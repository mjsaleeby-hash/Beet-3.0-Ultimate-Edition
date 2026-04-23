## 2026-04-18 — Performance roadmap (post-Phase 1)

Performance work is organized in phases from the 2026-04-18 Windows practices audit (`PerformanceMonitor/audit/windows-practices-audit-2026-04-18.md`). Phase 1 is complete. Phases 2–4 remain.

---

### Phase 2 — I/O Tuning (~1–2 hours, low risk)

- [x] **FileStream options** — Added `FileOptions.SequentialScan` and 1 MB internal buffers to `FileStream` constructors in `CopyFileWithHash` and `ThrottledCopy`. Also applied to `ComputeSha256`. `CopyFile` uses `File.Copy` (kernel-optimized, no change needed).
- [x] **BackupLogService debounce** — Raised debounce from 1s to 5s. Terminal statuses (Complete/Failed) now call `SaveNow()` directly, bypassing debounce. Running/Scheduled transitions use the debounced path.
- [x] **Flush-to-disk in verify mode** — Both `CopyFileWithHash` and `ThrottledCopy` now flush to disk (`Flush(flushToDisk: true)`) before the destination re-read. The re-read is retained — flush alone cannot detect silent corruption, USB failures, or bit flips. The flush reduces the chance of re-reading stale OS cache data.

---

### Phase 3 — Parallel Copy Engine (~half day, medium risk)

- [ ] **`Parallel.ForEachAsync` with drive-type-aware concurrency** — Fan out file copies across workers. SSD-to-SSD: 4–8 workers (2–5× throughput gain). HDD source or dest: 1 worker (seek overhead makes parallelism counter-productive). Detect drive type via `System.Management` or `IOCTL_STORAGE_QUERY_PROPERTY`.
- [ ] **Thread-safe counters** — Replace mutable shared state with `Interlocked` operations on `long` counters once parallelism is introduced.
- [ ] **Ordered directory creation** — Parallel enumeration must pre-create destination directories before workers start copying into them.
- [ ] **Thread-safe mirror cleanup** — Mirror mode's deletion sets must be protected (concurrent writes from multiple workers).
- [ ] **Regression test all paths** — Mirror, VSS, file permissions, hidden attributes, versioning, compression, cancellation mid-run.

---

### Phase 4 — Hygiene (low risk, sweeping)

- [x] **`ConfigureAwait(false)` sweep** — Added to every `await` in `TransferService.cs` (4), `SchedulerService.cs` (6), and `UpdateService.cs` (1). Eliminates the dispatcher-context deadlock class that caused the zombie process bug.
- [ ] **Lazy DI in `App.OnStartup`** — Move `SchedulerService.LoadJobs()` off the constructor; make `UpdateService` lazy (only needed 3 seconds post-launch). Reduces cold-start time on the UI thread.
- [x] **Long-path support** — Added `app.manifest` with `longPathAware` and Windows 10/11 compatibility. Paths >260 characters now work without `\\?\` prefix normalization.
- [ ] **`CopyFileEx` PInvoke** — Replace the hand-rolled buffered copy with the Win32 `CopyFileEx` API. Handles sparse files, alternate data streams, and provides a kernel-level progress callback.

---

### Other open items (pre-existing, from 2026-04-01 assessment)

- [ ] Move data-loss: source delete on partial failure
- [ ] Atomic JSON saves
- [ ] Single-instance mutex
- [ ] Professional error messages (remove "dummy!" text)
- [ ] Items 7–12 from 2026-03-24 viability assessment
- [x] Backup wizard (fully implemented — 7-step wizard with stepper UI, disk-space preflight, wizard validates source/destination)
- [ ] Dry run / preview mode
- [ ] Encryption
- [ ] Pre/post backup scripts
- [ ] Delta / block-level copying

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
