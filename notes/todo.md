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
- [ ] Backup wizard (full implementation; placeholder exists)
- [ ] Dry run / preview mode
- [ ] Encryption
- [ ] Pre/post backup scripts
- [ ] Delta / block-level copying
