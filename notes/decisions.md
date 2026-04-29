## 2026-04-18 — Performance Phase 1 & Zombie Fix

### Server GC enabled
- Added `<ServerGarbageCollection>true</ServerGarbageCollection>` and `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` to `BeetsBackup.csproj`.
- **Rationale:** Default workstation GC serializes collection with the calling thread. Server GC allocates one heap per logical core (8 on this host) and collects in parallel — roughly halves GC pause time for a long-running process that churns large byte buffers.

### ArrayPool buffers — 1 MB pooled, not per-file heap allocations
- `FileSystemService.CopyFileWithHash` and `TransferService.ThrottledCopy` now rent from `ArrayPool<byte>.Shared` and return in `finally`.
- Buffer size raised to 1 MB in both paths (was 80 KB and 64 KB respectively).
- **Rationale:** Per-file `new byte[N]` churns Gen 0 at scale (800 MB of allocations across a 10,000-file backup). Pooling eliminates that entirely.

### PowerManagement helper — prevent sleep during transfers
- New `Services/PowerManagement.cs` wraps `SetThreadExecutionState` via PInvoke.
- `using var _awake = PowerManagement.KeepSystemAwake();` applied in `CopyAsync`, `CompressAsync`, `ExtractAsync`, `MoveAsync`.
- **Rationale:** Without this, a long overnight backup on an unattended machine will silently fail when Windows hits the sleep timeout.

### Cancellation-responsive throttle delay
- `TransferService.ThrottledCopy` replaced `Thread.Sleep(delay)` with `ct.WaitHandle.WaitOne(delay)`.
- **Rationale:** `Thread.Sleep` ignores cancellation. A user hitting Cancel during a throttled transfer was stuck waiting up to the full delay interval.

### RegisterWaitForSingleObject — no polling thread
- `App.xaml.cs` `StartShowSignalListener` replaced the `Task.Run` + 1-second `WaitOne` polling loop with `ThreadPool.RegisterWaitForSingleObject`.
- Removed the now-unnecessary `_showSignalCts` field.
- **Rationale:** The polling loop held a thread-pool worker permanently blocked, waking every second. OS-level callback has zero thread cost until the signal fires.

### PerformanceMonitor excluded from main build
- Added `PerformanceMonitor\**` to `DefaultItemExcludes` in `BeetsBackup.csproj`.
- **Rationale:** The monitor is a standalone console tool; it was being pulled into the main build inadvertently.

---

### Zombie process — sync-over-async deadlock (root cause + fix)

**Decision: headless job path must not block the UI thread on an async call.**

- **Root cause:** `App.RunHeadlessJob` called `scheduler.RunJobByIdAsync(jobId).GetAwaiter().GetResult()` on the WPF UI thread during `OnStartup`. Awaits inside the async chain captured `DispatcherSynchronizationContext`. On completion, continuations posted back to the dispatcher — which was blocked in `GetResult()`. Deadlock: Task never completed, `Environment.Exit` in `finally` never executed, process lived forever with no window.
- **Evidence:** `dotnet-stack` dump on a zombie PID showed UI thread frozen at `TaskAwaiter<Boolean>.GetResult()` → `App.RunHeadlessJob` → `App.OnStartup`, with idle thread-pool workers (backup was done).

**Fixes applied to `App.xaml.cs`:**
1. **Task.Run wrapper:** `Task.Run(() => scheduler.RunJobByIdAsync(jobId)).GetAwaiter().GetResult()` — async state machine runs on thread pool, no dispatcher context captured.
2. **Bounded dispose:** `Services.Dispose()` in headless `finally` runs via `Task.Run(...).Wait(5s)` with a timeout log.
3. **Shutdown watchdog:** New `ArmShutdownWatchdog(TimeSpan, string)` spawns a background thread that calls `Process.GetCurrentProcess().Kill()` after the timeout. Armed at 30s in `RunHeadlessJob`, 15s in `OnExit`. Guarantees termination even if `Environment.Exit` itself hangs.

**Note:** Phase 4's `ConfigureAwait(false)` sweep across `Services/` would eliminate this entire class of deadlock at the source.

---

## 2026-04-29 — Performance Phase 3 plan: parallel copy engine

### Goal
Fan out file copies across worker tasks bounded by drive-type-aware concurrency. Target: 2–5×
throughput on SSD-to-SSD, no regression on HDD where parallelism is counter-productive.

### Architecture: enumerate-then-copy (two-pass), not recursive parallel

The current `CopyItem` is depth-first recursive — directories enumerate children and call
`CopyItem` on each. Parallelizing that would require parallel enumeration with shared mutable
state at each tree level, plus careful ordering to ensure parent dirs exist before child files
land. Easier to do it as two passes:

1. **Enumeration pass** (single thread, orchestrator) — walks every source path, applies all
   exclusion + mode logic (Skip/Replace/KeepBoth), and builds two flat lists:
   - `IReadOnlyList<DirectoryWorkItem>` — source dir, dest dir, hidden flag
   - `IReadOnlyList<FileWorkItem>` — source path, planned dest path (already KeepBoth-renamed if
     applicable), action enum (Copy / SkipIdentical / Replace)
2. **Pre-create directories** — sorted by depth, sequential. Cheap, avoids worker contention.
3. **Parallel copy pass** — `Parallel.ForEachAsync(fileList, opts, ...)` with bounded
   concurrency. Each worker copies one file using the existing `CopyAndVerify` / `ExecuteCopy` /
   `ThrottledCopy` machinery (already pure per-file, just needs thread-safe counters).
4. **Mirror cleanup** — stays sequential. Destruction logic is small and deserves careful
   auditing more than parallelism.

### Concurrency tuning
New helper, e.g. `Services/DriveTypeService.cs`, uses `IOCTL_STORAGE_QUERY_PROPERTY` with
`StorageDeviceSeekPenaltyProperty` to detect SSD vs HDD per drive, plus `DriveInfo.DriveType`
for Network/Removable. Worker count by source+dest pair:

| Source | Dest    | Workers                                         |
|--------|---------|-------------------------------------------------|
| SSD    | SSD     | `Math.Min(8, Environment.ProcessorCount)`       |
| SSD    | HDD     | 1 (HDD seeks dominate)                          |
| HDD    | SSD     | 1 (HDD seeks dominate)                          |
| HDD    | HDD     | 1                                               |
| Net    | any     | 4 (good middle ground for SMB)                  |
| any    | Net     | 4                                               |
| Unknown| any     | 2 (conservative)                                |

If multiple sources span different drive types, take the most-constraining of all source/dest
combinations.

### Thread-safety touchpoints
- `TransferResult` counters → switch to `Interlocked.Increment` / `Interlocked.Add`. Properties
  exposed read-only; mutator methods on the result class (`AddCopied()`, `AddBytes(long)`, etc.).
- `TransferResult.AddFileError` → bounded under a `lock` (or `ConcurrentQueue` with a counter).
- `VssSnapshotService.GetOrCreateSnapshotRoot` → must be thread-safe. Internal cache should use
  `ConcurrentDictionary` keyed by volume root, value = `Lazy<SnapshotRoot>` so the snapshot
  creation itself runs once even under contention.
- `IProgress<int>` percent reporting — already free-threaded by contract. Calls from workers OK.

### Stages (commit-able boundaries)
1. **Stage 1: TransferResult thread safety** — switch counters to `Interlocked`, lock around
   `AddFileError`. Behavior identical single-threaded. SAFE TO COMMIT ALONE.
2. **Stage 2: DriveTypeService** — new helper + unit-of-test. No callers yet. SAFE TO COMMIT.
3. **Stage 3: Enumerate-then-copy refactor (still sequential)** — extract `EnumerateWorkItems`,
   build `DirectoryWorkItem`/`FileWorkItem` records, replace recursive `CopyItem` orchestration
   with enumerate→pre-create→sequential-foreach. Should produce identical outputs to today's
   code. THE BIG ONE. Regression-test before commit.
4. **Stage 4: Add parallelism** — swap sequential `foreach` for `Parallel.ForEachAsync` with
   `MaxDegreeOfParallelism` from DriveTypeService. Make `VssSnapshotService` thread-safe.
5. **Stage 5: Regression sweep** — Mirror, VSS fallback, ACL strip, hidden attrs, versioning,
   compression, throttling, checksum verify, cancellation mid-run, pause/resume.

### Out-of-scope for Phase 3
- `CompressAsync` (zip archive creation) — `ZipArchive` is not thread-safe for concurrent writes;
  parallelizing would require a different archive library. Skip.
- `ExtractAsync` — same reason.
- `MoveAsync` — uses `CopyItem` then `DeleteItem`; will inherit parallel speedup once `CopyItem`
  is refactored. No additional work needed.
