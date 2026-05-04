# Beet's Backup — Windows Practices Audit
Date: 2026-04-18
Auditor: windows-expert (via general-purpose)
Target host: Intel i7-4770K (4C/8T Haswell), 32 GB RAM, 1 SSD + 4 HDDs, Windows 11 26100

## Summary
- **13 findings**: 5 HIGH, 5 MEDIUM, 3 LOW.
- **Top resource underutilization**: the copy engine is **strictly serial** on all files — one file, one thread, 4 KB read buffer through `File.Copy`/`FileStream`. On this 8-logical-core host the observed max `ProcessCpuPercent` of 13% during a (non-copy) session is consistent with exactly the "one busy core / seven idle" shape this codebase will produce during a real backup. For multi-drive sources or SSD-to-SSD copies the effective throughput ceiling is a single queue-depth-1 stream rather than the 717 MB/s the host's I/O subsystem can sustain.
- **Key themes**:
  1. Transfer pipeline has no per-file parallelism, no Win32 `CopyFileEx`, no SSD-tuned buffering, no prefetch/overlapped I/O. Whole copies run on the thread-pool worker that `Task.Run` hands out.
  2. Hash verification does a **full extra read pass** of the destination (`ComputeSha256(dest)`) even though the source hash is computed during the copy — doubling I/O for `verifyChecksums = true`.
  3. GC mode is default (workstation) for a workload that allocates heavy byte buffers; `<ServerGarbageCollection>` and `<ConcurrentGarbageCollection>` are not set in `BeetsBackup.csproj`.
  4. No Windows API hygiene: no `SetThreadExecutionState` (long backups let the machine sleep mid-transfer); no `PROCESS_MODE_BACKGROUND_BEGIN` / priority-class tuning; no `\\?\` long-path handling.
  5. Thread count of 15–27 on idle is explained by (a) the forever-running 1-second polling loop in `StartShowSignalListener`, (b) `PeriodicTimer` + its scheduler worker, (c) WinForms + WPF dispatchers, (d) the HttpClient connection pool. Most of this is fixable.

## Findings

### [HIGH] Copy engine is strictly single-threaded per transfer
**Category:** Utilization
**File:** `Services/TransferService.cs:87-133` (`CopyAsync` → `Task.Run` → serial `foreach`) and `TransferService.cs:476-644` (`CopyItem` recursive, sequential)
**Observation:** Every transfer wraps the whole tree in one `Task.Run` and walks `Directory.EnumerateFiles` / `EnumerateDirectories` serially. There is no `Parallel.ForEachAsync`, no `Channel<T>` producer/consumer, no per-drive fan-out. The only parallelism in the whole application is the *sidebar folder-size pie chart* (`MainViewModel.cs:478-486`, 504). On an 8-logical-core, multi-spindle host like the auditee's (5 physical disks), a large copy that spans e.g. `D:\` → `E:\` will use one core and queue-depth-1 to one drive at a time.
**Impact:** For 10,000 small files on SSD, throughput is capped by per-file `File.Copy` syscall latency — typically 200–400 files/sec vs. 2,000–5,000/sec achievable with 8-way parallel copies. For mixed HDD source + HDD destination the serial model is acceptable (head seeks make parallelism counter-productive), but we have no mechanism to detect that.
**Recommendation:** Introduce a bounded `Channel<FileCopyItem>` fed by a single enumeration task, with `Math.Min(Environment.ProcessorCount, 8)` consumer tasks pulling copies. Detect destination drive type with `System.Management` or `DRIVE_INFO` and scale down to 1 worker when both source and destination are rotational (`MediaType == HDD`). Sketch:
```csharp
var options = new ParallelOptions {
    MaxDegreeOfParallelism = IsSsd(destRoot) ? Environment.ProcessorCount : 1,
    CancellationToken = ct
};
await Parallel.ForEachAsync(EnumerateFiles(source, excl), options, async (file, t) => {
    await CopyOneAsync(file, destFor(file), ...);
});
```

### [HIGH] `verifyChecksums` re-reads the destination, doubling I/O
**Category:** I/O
**File:** `Services/TransferService.cs:739-752` (`ExecuteCopy` verify branch); `FileSystemService.cs:99-131` (`CopyFileWithHash`)
**Observation:** `CopyFileWithHash` computes SHA-256 of the *source* bytes as it streams them to the destination. Then `ExecuteCopy` does:
```csharp
var sourceHash = _fs.CopyFileWithHash(source, dest, stripPermissions);
…
var destHash = ComputeSha256(dest);   // <-- full extra read
if (!sourceHash.SequenceEqual(destHash)) { … }
```
`ComputeSha256(dest)` re-opens the destination and reads it end-to-end again.
**Impact:** Checksum-verified backups take ~2x the wall time and ~2x the disk read on the destination. On a 100 GB job that's 100 GB of unnecessary reads.
**Recommendation:** Either (a) trust the source hash + filesystem (NTFS/ReFS are checksum-sound) and drop the destination re-read entirely, or (b) pre-compute hash on source in a separate pass only when the user explicitly asks for "paranoid" mode. The current code appears to be hedging against bit-rot during the write — the correct fix is to `FlushFileBuffers` / `FILE_FLAG_WRITE_THROUGH` on close and trust the OS. If you keep a destination re-read, at least skip it on identical-size files that were just written (`stripPermissions` + `SetLast*` calls don't alter data).

### [HIGH] `FileStream` buffers and copy path are not SSD-tuned
**Category:** I/O
**File:** `Services/FileSystemService.cs:85` (`File.Copy`), `FileSystemService.cs:108` (80 KB buffer, no FileOptions), `TransferService.cs:772-777` (64 KB throttled copy, no FileOptions)
**Observation:** `File.Copy` uses default 4 KB FileStream buffering under the hood (.NET 8 improved this on some paths but the default constructor still does). The hand-rolled variants use 64 KB and 80 KB buffers respectively, but **none** pass `FileOptions.SequentialScan`, `FileOptions.Asynchronous`, or the `WRITE_THROUGH`/`NO_BUFFERING` flags. They are created with `File.OpenRead` / `File.Create`, which default to synchronous mode.
**Impact:** Sequential large-file copy throughput is bounded at ~150–250 MB/s on a modern NVMe that can sustain 3+ GB/s, because the I/O is synchronous and not hinted as sequential (so Windows doesn't do aggressive readahead). For the auditee's Samsung 840 EVO SSD (peak ~500 MB/s), the observed 717 MB/s burst in the PerfMon log came from *Windows' own cache* — not from Beet's copy path.
**Recommendation:** Replace `File.OpenRead` with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024*1024, FileOptions.SequentialScan | FileOptions.Asynchronous)` and use `await srcStream.ReadAsync(buffer, ct)` / `destStream.WriteAsync(...)`. Use a **1 MB buffer rented from `ArrayPool<byte>.Shared`** — see the next finding. Even better, delegate to the Win32 `CopyFileEx` API which handles all of this plus sparse files, alternate data streams, and sends a progress callback:
```csharp
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CopyFileEx(string src, string dst, CopyProgressRoutine? cb,
    IntPtr data, ref int cancel, CopyFileFlags flags);
```

### [HIGH] LOH allocations per file and no `ArrayPool<byte>`
**Category:** Memory
**File:** `Services/FileSystemService.cs:108` (`new byte[81920]`), `TransferService.cs:782` (`new byte[65536]`)
**Observation:** `CopyFileWithHash` does `var buffer = new byte[81920];` *inside* the per-file method, so each file copied allocates a fresh 80 KB array. 80 KB is under the 85 KB LOH threshold, but it still churns Gen 0 heavily on a large copy. `ThrottledCopy` allocates `new byte[65536]` per file in the same pattern. A 10,000-file backup allocates **800 MB** of short-lived buffer memory for no reason.
**Impact:** Extra Gen 0/Gen 1 collection pressure, which on workstation GC (the default) runs on the calling thread and serializes with the copy. Also measurable TLB/cache thrash.
**Recommendation:** Use `ArrayPool<byte>.Shared.Rent(1 << 20)` / `Return`, and bump the buffer to 1 MB to better match SSD command granularity:
```csharp
var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
try { /* copy */ } finally { ArrayPool<byte>.Shared.Return(buffer); }
```

### [HIGH] No Windows power-state hint during long backups
**Category:** API
**File:** `Services/TransferService.cs:87-130` (any `CopyAsync` call); `Services/SchedulerService.cs:251-347` (`ExecuteJobAsync`)
**Observation:** Scheduled and manual backups run for potentially hours (the auditee has 4× 4 TB spindles). Nothing in the code calls `SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_CONTINUOUS)`, so if the user is away and Windows hits the configured sleep timeout **the machine will go to sleep mid-backup**, resulting in a "File locked" / "Disk full" error when the NAS drive spins down or the destination is unavailable on resume.
**Impact:** User-visible reliability failure. Backups silently fail on unattended runs overnight.
**Recommendation:** Pinvoke `SetThreadExecutionState` at the start of any transfer and restore it in `finally`:
```csharp
[Flags] enum EXECUTION_STATE : uint {
    ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001,
    ES_AWAYMODE_REQUIRED = 0x00000040
}
[DllImport("kernel32.dll", SetLastError = true)]
static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

var prev = SetThreadExecutionState(
    EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
try { /* run backup */ }
finally { SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS); }
```
Optionally also `ES_AWAYMODE_REQUIRED` for desktop/server SKUs so the display can still sleep.

### [MEDIUM] No `ConfigureAwait(false)` anywhere in library code
**Category:** Threading
**File:** entire `Services/` + `ViewModels/` (0 matches for `ConfigureAwait` across the whole repo)
**Observation:** Every `await` in library services (`TransferService`, `SchedulerService`, `UpdateService`, `DiskSpaceService`, `VersioningService`) posts back to the captured SynchronizationContext. Since most callers start on the UI thread, the continuations hop back to the Dispatcher after each `await Task.Run` — adding a context switch per awaited boundary.
**Impact:** Marginal on modern .NET (thread-pool continuations already skip sync context in many cases), but it measurably raises thread-pool queue latency when many transfers run concurrently (e.g., 3 scheduled jobs fire at the same minute). It also makes deadlocks possible if a caller ever does `.Result` on one of these methods — and `App.xaml.cs:168` already does `scheduler.RunJobByIdAsync(jobId).GetAwaiter().GetResult()` in the headless path.
**Recommendation:** Add `.ConfigureAwait(false)` to every `await` inside `Services/*.cs`. Libraries that don't touch UI never need the sync context back.

### [MEDIUM] `BackupLogService.Save` serializes + writes on every progress tick
**Category:** I/O
**File:** `Services/BackupLogService.cs:95-103` (UpdateProgress) + 150-188 (Save/SaveNow)
**Observation:** `UpdateProgress` does not call `Save()` — good. But `UpdateStatus` and `UpdateStats` do, and they're called on every `progress.Report(pct)` boundary downstream. The debounce at line 153 (`now - _lastSave < SaveDebounce`) is only 1 second, and when the debounce expires a *background Dispatcher callback* still calls `SaveNow` which does `JsonSerializer.Serialize(Entries.ToList())` — a full O(N) allocation of every log entry, capped at 500 items — on every tick. Across a multi-hour backup with percent granularity that's thousands of 50+ KB JSON serializations.
**Impact:** Constant Gen-1 allocation pressure; every second of transfer allocates a list-copy and a JSON buffer; measurable in the 220 MB working-set baseline.
**Recommendation:** (a) Only `Save()` on terminal status changes (Complete / Failed / Scheduled), not on UpdateStatus during an in-progress run. (b) Use `JsonSerializer.SerializeAsync(fileStream, Entries)` to a `BufferedStream` to avoid the full-buffer copy. (c) Increase debounce to 5–10 seconds during Running state.

### [MEDIUM] `StartShowSignalListener` polls every 1 second forever
**Category:** Threading / Handles
**File:** `App.xaml.cs:243-270`
**Observation:**
```csharp
Task.Run(() => {
    while (!cts.IsCancellationRequested) {
        bool signaled = signal.WaitOne(1000);
        …
    }
});
```
This holds a dedicated thread-pool worker **blocked on a 1-second wait** for the process lifetime. That's one of the "extra" threads contributing to the 27-thread idle-count.
**Impact:** One permanently-hot thread in `WaitOne`, and it wakes the process every second to re-evaluate the cancellation token — which prevents the process from going into deep idle and keeps the GC from fully quiescing.
**Recommendation:** `signal.WaitOne(Timeout.Infinite)` is safe because `Dispose()` on the wait handle will break the wait; combine with `ThreadPool.RegisterWaitForSingleObject` so no thread is blocked at all:
```csharp
_registered = ThreadPool.RegisterWaitForSingleObject(
    signal, OnSignaled, null, Timeout.Infinite, executeOnlyOnce: false);
```
Then there is zero thread cost for this listener.

### [MEDIUM] `App.OnStartup` is eager — every service is instantiated up-front
**Category:** Startup
**File:** `App.xaml.cs:86-148` + `ConfigureServices` at 290-306
**Observation:** `BuildServiceProvider()` is followed immediately by `GetRequiredService<SettingsService>()`, `BackupLogService`, `ThemeService`, `MainWindow`, `SchedulerService`. All are `AddSingleton`. Because `MainViewModel` takes all of them as ctor parameters (`MainViewModel.cs:139`), the entire DI graph instantiates before the window shows. `SchedulerService` ctor loads jobs from JSON synchronously; `SettingsService.Load()` is synchronous.
**Impact:** Cold-start time is paid on the UI thread. With `PublishSingleFile=true` + `EnableCompressionInSingleFile=true` (`.csproj:16-17`), first-launch decompression already adds ~300-500 ms; eager DI adds another 100-200 ms.
**Recommendation:** (a) Move `SchedulerService.LoadJobs()` off the ctor and call it on a background task after the window is shown. (b) Make `UpdateService` lazy — it's only used 3 seconds post-startup and on demand. (c) Consider removing `EnableCompressionInSingleFile` — the ~30 MB the app saves on disk costs 300–500 ms on every launch.

### [MEDIUM] Handle baseline of 880–904 partly explained by WinForms + WPF + tray + HttpClient
**Category:** Handles
**File:** `Views/MainWindow.xaml.cs:55-83` (tray); `App.xaml.cs` (WPF + mutex + waithandle); `Services/UpdateService.cs:18` (static HttpClient)
**Observation:** The live session shows a stable 880–904 handle count. Components contributing:
- WinForms + WPF running simultaneously (project enables both: `BeetsBackup.csproj:8-9`) — each has its own message loop, timers, theme handles.
- `NotifyIcon` + `ContextMenuStrip` + the `ToolStripMenuItem` children (~15-20 HICONs and HMENUs).
- Single-instance Mutex + `EventWaitHandle` + CTS + internal `PeriodicTimer` waitable handle.
- Static `HttpClient` keeps the SocketsHttpHandler's connection pool open; 10+ handles even with no in-flight request.
**Impact:** Not a leak — the value is stable in-window — but the 900 baseline is ~2-3x what a minimal WPF app needs. Reducing it cuts GDI pressure and slightly improves Alt-Tab / DWM latency.
**Recommendation:** (a) Drop `UseWindowsForms` if possible and replace `NotifyIcon` with a WPF TaskbarIcon (e.g., `H.NotifyIcon.Wpf`) — saves the Forms message loop entirely. (b) Dispose `HttpClient` between update checks instead of keeping it static; update checks happen once per launch. (c) Use a named `Semaphore` instead of `Mutex` + `EventWaitHandle` for single-instance (one handle instead of two).

### [LOW] `Thread.Sleep` in throttled copy loop
**Category:** Threading
**File:** `Services/TransferService.cs:800` (`Thread.Sleep((int)(expectedMs - elapsedMs))`)
**Observation:** The throttled copy path blocks its thread-pool worker on `Thread.Sleep` to achieve bandwidth limits. Because the copy is inside `Task.Run`, this ties up a thread-pool worker for the entire throttled duration.
**Impact:** With 10 MB/s throttle on a 1 GB file, that's ~100 seconds of thread-pool worker time held by `Thread.Sleep`. If the user starts three simultaneous throttled transfers, three workers are stuck sleeping.
**Recommendation:** Replace with `await Task.Delay(ms, ct).ConfigureAwait(false)` and make `ThrottledCopy` `async`. Frees the worker during the sleep.

### [LOW] `Directory.EnumerateFiles` is called twice — once to count, once to copy
**Category:** I/O
**File:** `Services/TransferService.cs:93` + `:952-994` (`CountFiles` / `CountFilesPruned`) then recursive copy at `:518`
**Observation:** Before any copy happens, `CountFiles` walks the entire source tree once to compute `TotalFiles`. Then `CopyItem` walks it again to do the work. For 100,000-file sources each walk can take several seconds on cold-cached rotational media.
**Impact:** Up to 2x metadata I/O for every backup — measurable when source is a slow network share or a spun-down HDD.
**Recommendation:** Switch to a single-pass copy that updates `TotalFiles` incrementally using a sentinel "indeterminate" progress mode until the enumeration pipe empties, or cache the enumeration in `List<string>` once and iterate it twice in memory.

### [LOW] Long paths (`\\?\`) not handled
**Category:** API
**File:** `Services/FileSystemService.cs` and `TransferService.cs` throughout; no `\\?\` prefixing anywhere
**Observation:** .NET 8 on `net8.0-windows` respects `LongPathsEnabled` when set in the registry/app.manifest, but there is no `app.manifest` in this project, so paths > 260 chars will fail with `PathTooLongException` on default Windows 11 installs.
**Impact:** Users with deep backup trees (node_modules inside OneDrive inside D:\Projects\…) will hit silent failures counted as generic `FilesFailed`.
**Recommendation:** Add an `app.manifest` with `<ws2:longPathAware>true</ws2:longPathAware>` to opt in, or normalize any path > 248 chars with the `\\?\` prefix before passing to the Win32 APIs.

## System-spec observations

**Host from telemetry** (`session_start` event):
- Intel i7-4770K, 4 physical / **8 logical** cores, 32 GB RAM, Windows 11 26100.
- 5 disks: Samsung 840 EVO 500 GB SSD (reported as HDD by WMI — WMI `MediaType` is unreliable pre-NVMe), and 4× Seagate 4 TB drives.
- .NET 8.0.25 single-file publish.

**Where Beet is leaving resources on the table:**
- CPU: During the observed session max `ProcessCpuPercent` = 13.3%. That's consistent with a single-threaded (1/8 = 12.5%) workload plus some UI. Under real transfer load this would look identical — the copy engine has no way to consume > 1 core. **Target: 50-80% during a large copy, achieved by fanning out to 4-8 workers.**
- Disk queue depth: All copy paths are synchronous `Read`/`Write`. The SSD supports QD32+. We issue QD1. **Target: 8-16 in-flight I/Os per drive.**
- Memory: Working set 220 MB stable — fine. GC mode is workstation (default) even though this is a long-lived desktop process that allocates large byte buffers. **Target: switch to `<ServerGarbageCollection>true</ServerGarbageCollection>` + `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` in `BeetsBackup.csproj`.** Server GC uses one heap/core (up to 8 here) and collects in parallel; roughly halves GC pause time for this kind of workload.
- Threads: baseline 15-27. 4-6 are structural (WPF render, WPF dispatcher, finalizer, GC, timer queue, WinForms loop). The rest come from `StartShowSignalListener` (1), `PeriodicTimer` queue worker, the CopyOnWrite `Task.Run` backgrounded at `App.xaml.cs:138-142`, and `HttpClient`'s SocketsHttpHandler pool. **Target: idle steady-state ≤ 14 threads.**

**Windows API gaps specific to a backup tool on Windows 11:**
- `SetThreadExecutionState` — not called (HIGH above).
- `CopyFileEx` — not used; hand-rolled buffered copy instead (HIGH above).
- `GetFileInformationByHandleEx` (`FileIdBothDirectoryInfo`) — would let directory scans pull name+size+timestamps in one syscall per ~100 entries instead of one `FindFirst` + N `GetFileAttributes`. Currently `FileSystemService.GetChildren` round-trips per file.
- `SetFileInformationByHandle` with `FILE_DISPOSITION_POSIX_SEMANTICS` — would make concurrent copy + delete safer during mirror cleanup.
- `BackgroundCopy` (BITS) — the "right" Windows API for throttled, resumable, network-aware copy. Overkill for local transfers but would replace the hand-rolled throttle loop for network destinations.

## Appendix: files examined
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\BeetsBackup.csproj`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\App.xaml.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\TransferService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\FileSystemService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\SchedulerService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\BackupLogService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\VssSnapshotService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\DiagnosticsService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\FileLogger.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\UpdateService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\ToastNotifier.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\WindowsTaskSchedulerService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Services\DiskSpaceService.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\ViewModels\MainViewModel.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\Views\MainWindow.xaml.cs`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\PerformanceMonitor\bin\Release\net8.0-windows\win-x64\logs\session_2026-04-18_160041_11092.jsonl`
- `C:\Users\Owner\Documents\Beet-3.0-Ultimate-Edition\PerformanceMonitor\bin\Release\net8.0-windows\win-x64\audit\trend_report_2026-04-18_161458.md`
