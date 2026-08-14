using BeetsBackup.Models;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace BeetsBackup.Services;

/// <summary>
/// Snapshot of the scheduler's running-job state at the moment a <see cref="SchedulerService.RunningJobChanged"/>
/// event fires. Consumers (e.g. MainViewModel) cache this on the UI thread and use it to drive
/// computed properties — without the snapshot they'd re-query <see cref="SchedulerService.IsRunningAnyJob"/>
/// inside a BeginInvoke closure, by which time a fast job may have already finished and the
/// re-read would show false right after an event meant to indicate "started", flickering the UI.
/// </summary>
public readonly record struct SchedulerRunningState(bool IsRunningAnyJob, bool IsRunningJobPaused);

/// <summary>
/// Background scheduler that checks for due backup jobs every minute and executes them.
/// Also provides job CRUD operations, pause/resume control, and retry logic.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly TransferService _transfer;
    private readonly BackupLogService _log;
    private readonly List<ScheduledJob> _jobs = new();
    private readonly PeriodicTimer _ticker;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _jobsLock = new();
    private readonly Dictionary<Guid, ManualResetEventSlim> _pauseGates = new();
    /// <summary>Per-running-job cancellation sources, keyed by log entry id. Lets the homepage
    /// pause/stop controls cancel an in-progress scheduled run — previously scheduled backups
    /// could not be cancelled at all because no token was passed to the transfer call.</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _runningCts = new();

    /// <summary>Job ids currently dispatched (Task.Run launched but ExecuteJobAsync not yet
    /// finished). Used by the minute tick and missed-job dispatch to skip jobs that are still
    /// running — without this, a long-running backup would be re-dispatched every tick (the
    /// cross-process mutex catches it, but it's wasteful churn). NextRun is now advanced in
    /// <see cref="ExecuteJobAsync"/>'s finally so it reflects actual completion time, not the
    /// dispatch time, which means we can no longer rely on "NextRun is in the future" as the
    /// claim mechanism.</summary>
    private readonly HashSet<Guid> _dispatchedJobIds = new();
    private Task? _runTask;

    /// <summary>UTC ticks of the most recent self-initiated write to <c>scheduled_jobs.json</c>.
    /// The cross-process watcher uses this to suppress reloads triggered by our own SaveJobs.
    /// Stored as a long and accessed via Volatile.Read/Write so the watcher thread reliably
    /// observes the writer's update — the .NET memory model does not promise cross-thread
    /// visibility without a barrier.</summary>
    private long _lastSelfWriteTicks;

    /// <summary>Window after a self-save during which file-change events are ignored. File.Replace
    /// fires multiple events; this avoids self-thrashing on the watcher.</summary>
    private static readonly TimeSpan SelfWriteCooldown = TimeSpan.FromSeconds(2);

    /// <summary>Quiet-period the watcher waits before reloading on external writes — same
    /// rationale as BackupLogService.WatcherCoalesceDelay.</summary>
    private static readonly TimeSpan WatcherCoalesceDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long Dispose waits for the scheduler tick loop to exit before abandoning.</summary>
    private static readonly TimeSpan SchedulerLoopShutdownTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Coalesces a burst of file-change events into a single reload after the watcher quiets.</summary>
    private CancellationTokenSource? _reloadCts;

    /// <summary>Watcher on <c>scheduled_jobs.json</c>. The headless --run-job path advances NextRun
    /// and writes the file from a separate process; without this watcher the foreground dashboard
    /// keeps showing the pre-run NextRun forever (until app restart).</summary>
    private FileSystemWatcher? _watcher;

    /// <summary>Set the moment <see cref="Dispose"/> begins. Watcher callbacks already in flight
    /// on the thread pool short-circuit on this flag rather than touching <see cref="_reloadCts"/>
    /// (which is being torn down).</summary>
    private volatile bool _disposed;

    /// <summary>When true, <see cref="Load"/> skips <see cref="StartWatcher"/>. The headless
    /// --run-job path is a one-shot process that advances NextRun once and exits — a watcher
    /// there serves no purpose and only adds a shutdown failure mode.</summary>
    private bool _headlessMode;

    /// <summary>Marks this scheduler as headless. Must be called before <see cref="Load"/>.</summary>
    public void MarkHeadless() => _headlessMode = true;

    /// <summary>Last error message produced by the scheduler loop, if any.</summary>
    public string? LastSchedulerError { get; private set; }

    private static readonly string JobsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "scheduled_jobs.json");

    /// <summary>Thread-safe snapshot of all scheduled jobs.</summary>
    public IReadOnlyList<ScheduledJob> Jobs
    {
        get { lock (_jobsLock) { return _jobs.ToList().AsReadOnly(); } }
    }

    /// <summary>Raised when the job list is modified (add, remove, or status change).</summary>
    public event Action? JobsChanged;

    /// <summary>Raised when the scheduler encounters an error executing a job.</summary>
    public event Action<string>? SchedulerError;

    /// <summary>Raised whenever the running-scheduled-job set transitions: a job starts,
    /// ends, is paused, or is resumed. Lets the main window's toolbar pause/stop buttons
    /// stay in sync with scheduled runs even though those runs originate from the scheduler
    /// rather than from the manual transfer flow. The <see cref="SchedulerRunningState"/>
    /// payload is captured under <see cref="_jobsLock"/> at fire time so consumers don't race
    /// the next state transition when their handler runs on the dispatcher.</summary>
    public event Action<SchedulerRunningState>? RunningJobChanged;

    /// <summary>Captures the current running-job state under the lock — used as the payload
    /// for every <see cref="RunningJobChanged"/> firing.</summary>
    private SchedulerRunningState SnapshotRunningState()
    {
        lock (_jobsLock)
        {
            bool anyPaused = false;
            foreach (var gate in _pauseGates.Values)
                if (!gate.IsSet) { anyPaused = true; break; }
            return new SchedulerRunningState(_pauseGates.Count > 0, anyPaused);
        }
    }

    /// <summary>True if at least one scheduled job is currently executing.</summary>
    public bool IsRunningAnyJob
    {
        get { lock (_jobsLock) { return _pauseGates.Count > 0; } }
    }

    /// <summary>True if a scheduled job is currently executing AND its pause gate is reset.
    /// Mirrors what the user sees: "something is running and it's paused right now".</summary>
    public bool IsRunningJobPaused
    {
        get
        {
            lock (_jobsLock)
            {
                if (_pauseGates.Count == 0) return false;
                foreach (var gate in _pauseGates.Values)
                    if (!gate.IsSet) return true;
                return false;
            }
        }
    }

    /// <summary>Snapshot of the log-entry ids for every scheduled job currently running.
    /// The home dashboard uses this to mirror the running entry's progress percent.</summary>
    public IReadOnlyList<Guid> RunningJobLogIds
    {
        get { lock (_jobsLock) { return _pauseGates.Keys.ToList(); } }
    }

    /// <summary>
    /// Lock-protected membership check that avoids the per-tick List allocation in
    /// <see cref="RunningJobLogIds"/>. Use this from hot paths like progress-tick handlers;
    /// reserve <see cref="RunningJobLogIds"/> for callers that genuinely need the full snapshot.
    /// </summary>
    public bool IsLogIdRunning(Guid logEntryId)
    {
        lock (_jobsLock) { return _pauseGates.ContainsKey(logEntryId); }
    }

    /// <summary>Pauses every scheduled job that is currently running. Used by the main
    /// window toolbar's pause button, which can't know per-job log-entry ids in advance.</summary>
    public void PauseRunning()
    {
        bool changed = false;
        lock (_jobsLock)
        {
            foreach (var gate in _pauseGates.Values)
            {
                if (gate.IsSet) { gate.Reset(); changed = true; }
            }
        }
        if (changed) RunningJobChanged?.Invoke(SnapshotRunningState());
    }

    /// <summary>Resumes every paused scheduled job.</summary>
    public void ResumeRunning()
    {
        bool changed = false;
        lock (_jobsLock)
        {
            foreach (var gate in _pauseGates.Values)
            {
                if (!gate.IsSet) { gate.Set(); changed = true; }
            }
        }
        if (changed) RunningJobChanged?.Invoke(SnapshotRunningState());
    }

    /// <summary>Cancels every running scheduled job. Each job's <see cref="CancellationToken"/>
    /// is observed by the transfer service, which throws <see cref="OperationCanceledException"/>;
    /// <see cref="ExecuteJobAsync"/> catches it and marks the entry Skipped with "Cancelled by user".
    /// Pause gates are released first so a paused worker observes the cancellation immediately.
    /// Cancel runs under <see cref="_jobsLock"/> so it can't race the worker's finally block,
    /// which removes-then-disposes the same CTS under the same lock — without this, Cancel could
    /// be invoked on a CTS whose Dispose was mid-flight and produce undocumented behavior.</summary>
    public void CancelRunning()
    {
        bool any;
        lock (_jobsLock)
        {
            // Release any pause so workers observing pauseToken.Wait() unblock and see the
            // cancellation request rather than sleeping forever.
            foreach (var gate in _pauseGates.Values)
                gate.Set();
            foreach (var cts in _runningCts.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* defensive */ }
            }
            any = _runningCts.Count > 0;
        }
        if (any) RunningJobChanged?.Invoke(SnapshotRunningState());
    }

    /// <summary>
    /// Creates the scheduler. Call <see cref="Load"/> to read persisted jobs from disk,
    /// then <see cref="Start"/> to begin the polling loop.
    /// </summary>
    public SchedulerService(TransferService transfer, BackupLogService log)
    {
        _transfer = transfer;
        _log = log;
        _ticker = new PeriodicTimer(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Reads persisted jobs from disk and starts the cross-process file watcher.
    /// Idempotent — safe to call once at startup. Kept out of the constructor so unit
    /// tests can build a scheduler without touching disk.
    /// </summary>
    public void Load()
    {
        LoadJobs();
        if (!_headlessMode) StartWatcher();
    }

    /// <summary>Starts the background scheduler loop (idempotent — safe to call from multiple threads).</summary>
    public void Start()
    {
        // Lock against a second concurrent Start() observing _runTask == null and launching
        // a duplicate RunAsync. Today only App.OnStartup calls this once, but the
        // documented contract is "idempotent" and the previous read+assign pattern would
        // orphan one of two competing tasks if the contract were ever exercised concurrently.
        lock (_jobsLock)
        {
            _runTask ??= RunAsync(_cts.Token);
        }
    }

    /// <summary>Returns enabled jobs whose next-run time is in the past (missed while app was closed).</summary>
    public List<ScheduledJob> GetMissedJobs()
    {
        lock (_jobsLock)
        {
            return _jobs.Where(j => j.IsEnabled && j.NextRun < DateTime.Now).ToList();
        }
    }

    /// <summary>Fires off missed backup jobs on background threads. Each task's finally block in
    /// <see cref="ExecuteJobAsync"/> advances NextRun and persists — no longer done here so the
    /// persisted timing reflects when the run actually ended, not when it was queued.</summary>
    public void RunMissedJobs(List<ScheduledJob> jobs)
    {
        // Fire JobsChanged BEFORE the Task.Run launches so the foreground UI sees the
        // "scheduled jobs are about to run" state before any RunningJobChanged event
        // arrives from the worker thread. The previous order produced a brief toolbar
        // flicker on startup (off → on from the worker, then off again when JobsChanged
        // synchronously fired with a stale snapshot, then on again when the next event
        // landed). Empty jobs list still fires it — consumers that care about "did
        // anything need to run" can re-query IsRunningAnyJob themselves.
        if (jobs.Count > 0) JobsChanged?.Invoke();

        foreach (var job in jobs)
        {
            FileLogger.Info($"Running missed job: '{job.Name}'");
            var snapshot = SnapshotJob(job);
            lock (_jobsLock) { _dispatchedJobIds.Add(job.Id); }
            _ = Task.Run(async () =>
            {
                try { await ExecuteJobAsync(snapshot, job); }
                catch (Exception ex) { FileLogger.LogException($"Missed job failed: '{job.Name}'", ex); }
            });
        }
    }

    /// <summary>Advances the next-run time for a missed job without executing it.</summary>
    public void SkipMissedJob(Guid id)
    {
        ScheduledJob? job;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job != null)
            {
                job.UpdateNextRun();
                SaveJobs();
            }
        }

        if (job != null)
        {
            FileLogger.Info($"Skipped missed job: '{job.Name}' — next run advanced to {job.NextRun:g}");
            _log.Add(PlaceholderFor(job, BackupStatus.Skipped,
                $"Missed backup skipped by user. Next run: {job.NextRun:g}"));
        }

        JobsChanged?.Invoke();
    }

    /// <summary>Adds a new job to the scheduler and logs a "Scheduled" entry.</summary>
    public void AddJob(ScheduledJob job)
    {
        lock (_jobsLock)
        {
            _jobs.Add(job);
            SaveJobs();
        }

        _log.Add(PlaceholderFor(job, BackupStatus.Scheduled,
            $"Scheduled for {job.NextRun:g}{(job.IsRecurring ? $", recurring {job.RecurInterval}" : "")}"));

        // Best-effort: also register with Windows Task Scheduler so the job fires even when
        // the app isn't running. Failures are non-fatal — the in-process timer is still active.
        if (job.IsEnabled)
        {
            var ok = WindowsTaskSchedulerService.Register(job);
            if (ok) FileLogger.Info($"Registered Windows scheduled task for '{job.Name}'.");
        }

        JobsChanged?.Invoke();
    }

    /// <summary>Removes a job by ID from the scheduler and persists the change.</summary>
    public void RemoveJob(Guid id)
    {
        lock (_jobsLock)
        {
            _jobs.RemoveAll(j => j.Id == id);
            SaveJobs();
        }
        WindowsTaskSchedulerService.Unregister(id);
        // Drop the "Scheduled for X" placeholder that AddJob inserted — without this it
        // hangs around in the log forever, advertising a run that will never happen.
        _log.RemoveScheduledPlaceholdersForJob(id);
        JobsChanged?.Invoke();
    }

    /// <summary>
    /// Replaces an existing job in-place by matching on <see cref="ScheduledJob.Id"/>.
    /// The <paramref name="updated"/> job must carry the same Id as the job being replaced.
    /// Re-registers the Windows scheduled task so the OS-level trigger reflects the edits.
    /// </summary>
    public void UpdateJob(ScheduledJob updated)
    {
        lock (_jobsLock)
        {
            var idx = _jobs.FindIndex(j => j.Id == updated.Id);
            if (idx < 0)
            {
                // Fall through to AddJob semantics so a stale edit doesn't silently drop.
                _jobs.Add(updated);
            }
            else
            {
                // Mutate the existing instance in place rather than replacing the list slot.
                // An ExecuteJobAsync task that's already in flight captured this reference as
                // originalJob and writes LastRun/NextRun to it in its finally block. Replacing
                // the slot would orphan that reference and lose the run's bookkeeping.
                _jobs[idx].CopyFieldsFrom(updated);
            }
            SaveJobs();
        }

        // Refresh the OS-level task so new timing / command-line args take effect.
        WindowsTaskSchedulerService.Unregister(updated.Id);
        if (updated.IsEnabled)
            WindowsTaskSchedulerService.Register(updated);

        // The old "Scheduled for X" placeholder may now be wrong (timing or recurrence may
        // have changed). AddJob's flow re-creates the placeholder fresh on next add; for an
        // edit we just drop the stale one — the next run will write a Running entry anyway.
        _log.RemoveScheduledPlaceholdersForJob(updated.Id);

        JobsChanged?.Invoke();
    }

    /// <summary>
    /// Headless run entry-point used by the <c>--run-job {id}</c> CLI path. Locates the
    /// persisted job, runs it synchronously, updates LastRun/NextRun, and returns.
    /// </summary>
    /// <returns><c>true</c> if a job was found and executed (regardless of outcome); <c>false</c>
    /// if no matching job exists (caller should exit with a non-zero code).</returns>
    public async Task<bool> RunJobByIdAsync(Guid jobId)
    {
        ScheduledJob? job;
        lock (_jobsLock) { job = _jobs.FirstOrDefault(j => j.Id == jobId); }
        if (job == null)
        {
            FileLogger.Warn($"RunJobByIdAsync: no job found with id {jobId}");
            return false;
        }

        var snapshot = SnapshotJob(job);
        lock (_jobsLock) { _dispatchedJobIds.Add(job.Id); }
        await ExecuteJobAsync(snapshot, job).ConfigureAwait(false);
        // ExecuteJobAsync's finally advances NextRun, removes from _dispatchedJobIds, saves.
        return true;
    }

    /// <summary>
    /// Re-registers every enabled job with Windows Task Scheduler. Called on app startup so
    /// users aren't caught out if a task was manually deleted or lost across a Windows reset.
    /// </summary>
    public void ReconcileWindowsTasks()
    {
        List<ScheduledJob> snapshot;
        lock (_jobsLock) { snapshot = _jobs.ToList(); }
        WindowsTaskSchedulerService.ReconcileAll(snapshot);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (await _ticker.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                List<ScheduledJob> dueJobs;
                var now = DateTime.Now;
                lock (_jobsLock)
                {
                    // Skip jobs still running from a prior tick — _dispatchedJobIds is the
                    // claim flag. Without this filter the tick re-dispatches every minute and
                    // the cross-process mutex absorbs the duplicate (wasteful but correct).
                    dueJobs = _jobs.Where(j => j.IsEnabled
                                               && j.NextRun <= now
                                               && !_dispatchedJobIds.Contains(j.Id)).ToList();
                    foreach (var job in dueJobs)
                        _dispatchedJobIds.Add(job.Id);
                }

                foreach (var job in dueJobs)
                {
                    var snapshot = SnapshotJob(job);
                    _ = Task.Run(async () =>
                    {
                        try { await ExecuteJobAsync(snapshot, job); }
                        catch (Exception jobEx) { FileLogger.LogException($"Scheduled job failed: '{job.Name}'", jobEx); }
                    });
                }
            }
            catch (Exception ex)
            {
                LastSchedulerError = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Scheduler error: {ex.Message}";
                FileLogger.LogException("Scheduler loop error", ex);
                SchedulerError?.Invoke($"Scheduler error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Executes a job using snapshotted data for thread safety.
    /// The snapshot is used for all read operations; the original job is only
    /// updated (LastRun) under lock in the finally block.
    /// </summary>
    private async Task ExecuteJobAsync(ScheduledJob snapshot, ScheduledJob originalJob)
    {
        // Cross-process per-job lock. See JobMutex for the rationale (Task Scheduler
        // + in-process minute-tick + foreground "Back up now" can all race on the
        // same job). The lease releases the kernel mutex on Dispose at end of method.
        var jobLock = JobMutex.TryAcquire(snapshot.Id, snapshot.Name);
        if (jobLock.WasBusy)
        {
            FileLogger.Info($"Skipping '{snapshot.Name}' — another process or thread is already running this job.");
            jobLock.Dispose();
            // Mutex-skip path bypasses the main try-finally, so release the dispatch claim here
            // ourselves — otherwise the next tick would still see this job as "dispatched".
            lock (_jobsLock) { _dispatchedJobIds.Remove(originalJob.Id); }
            return;
        }

        // Drop the "Scheduled for X" placeholder before the Running entry lands. Without this,
        // the log shows two entries for the same run forever — one Scheduled, one Complete.
        // Done after the mutex check so a skipped tick doesn't strip the placeholder from
        // under the process that's actually doing the work.
        _log.RemoveScheduledPlaceholdersForJob(snapshot.Id);

        var logEntry = RunningFor(snapshot);
        _log.Add(logEntry);
        FileLogger.Info($"Scheduled job started: '{snapshot.Name}' — {string.Join("; ", snapshot.SourcePaths)} → {snapshot.DestinationPath}");

        var pauseGate = new ManualResetEventSlim(true);
        var runCts = new CancellationTokenSource();
        // Wall-clock for this run, so telemetry can report transfer speed (bytes/duration).
        // backup_log.json stores only a last-updated timestamp, not a duration, so this is
        // the only place that number is available.
        var runStopwatch = System.Diagnostics.Stopwatch.StartNew();
        lock (_jobsLock)
        {
            _pauseGates[logEntry.Id] = pauseGate;
            _runningCts[logEntry.Id] = runCts;
        }
        RunningJobChanged?.Invoke(SnapshotRunningState());

        try
        {
            var exclusions = snapshot.ExclusionFilters.Count > 0 ? snapshot.ExclusionFilters : null;

            var estimatedSize = await Task.Run(() => TransferService.EstimateTotalSize(snapshot.SourcePaths, exclusions), runCts.Token).ConfigureAwait(false);
            _log.UpdateStatus(logEntry.Id, BackupStatus.Running, "Backup in progress");
            FileLogger.Info($"Estimated size for '{snapshot.Name}': {FileSystemItem.FormatBytes(estimatedSize)}");

            var percentProgress = new Progress<int>(pct =>
                _log.UpdateProgress(logEntry.Id, pct));

            var versioning = snapshot.EnableVersioning
                ? new VersioningOptions { Enabled = true, MaxVersions = snapshot.MaxVersions }
                : null;

            TransferResult result;
            if (snapshot.EnableCompression)
            {
                result = await _transfer.CompressAsync(
                    snapshot.SourcePaths,
                    snapshot.DestinationPath,
                    archiveName: BuildArchiveName(snapshot.Name),
                    exclusions: exclusions,
                    cancellationToken: runCts.Token,
                    pauseToken: pauseGate,
                    progressPercent: percentProgress).ConfigureAwait(false);
            }
            else
            {
                result = await _transfer.CopyAsync(
                    snapshot.SourcePaths,
                    snapshot.DestinationPath,
                    snapshot.StripPermissions,
                    snapshot.TransferMode,
                    progressPercent: percentProgress,
                    cancellationToken: runCts.Token,
                    pauseToken: pauseGate,
                    verifyChecksums: snapshot.VerifyChecksums,
                    exclusions: exclusions,
                    throttleBytesPerSec: snapshot.ThrottleMBps > 0 ? (long)snapshot.ThrottleMBps * 1024 * 1024 : 0,
                    versioning: versioning).ConfigureAwait(false);
            }

            _log.UpdateStats(logEntry.Id, BackupStatus.Complete, result);
            runStopwatch.Stop();
            EmitBackupTelemetry(snapshot.Name, snapshot.TransferMode.ToString(), result,
                runStopwatch.Elapsed.TotalMilliseconds, snapshot.DestinationPath);
            FileLogger.Info($"Scheduled job completed: '{snapshot.Name}' — {TransferReporter.FormatSummary(result)}");
            TransferReporter.Notify($"Backup complete: {snapshot.Name}", result);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            // User pressed Stop on the homepage (or LogDialog). Treat as Skipped, not Failed —
            // it wasn't an error, it was an explicit user choice. Toast as info, not warning.
            FileLogger.Info($"Scheduled job cancelled: '{snapshot.Name}'");
            _log.UpdateStatus(logEntry.Id, BackupStatus.Skipped, "Cancelled by user");
            ToastNotifier.Notify($"Backup stopped: {snapshot.Name}", "Cancelled by user.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException)
        {
            FileLogger.Error($"Scheduled job failed: '{snapshot.Name}' — insufficient disk space");
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, "Not enough disk space on the destination drive.");
            SchedulerError?.Invoke($"Job '{snapshot.Name}' failed — insufficient disk space");
            ToastNotifier.Notify($"Backup failed: {snapshot.Name}", "Not enough disk space on the destination drive.", ToastKind.Error);
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Scheduled job failed: '{snapshot.Name}'", ex);
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, ex.Message);
            SchedulerError?.Invoke($"Job '{snapshot.Name}' failed — {ex.Message}");
            ToastNotifier.Notify($"Backup failed: {snapshot.Name}", ex.Message, ToastKind.Error);
        }
        finally
        {
            lock (_jobsLock)
            {
                _pauseGates.Remove(logEntry.Id);
                pauseGate.Dispose();
                _runningCts.Remove(logEntry.Id);
                runCts.Dispose();
                _dispatchedJobIds.Remove(originalJob.Id);
                originalJob.LastRun = DateTime.Now;
                // Advance NextRun (and disable one-shot jobs) *here*, after the run completes,
                // so the persisted timing matches the actual run timing. Doing it earlier — in
                // the caller before await — left a window where Task Scheduler could see a
                // post-advance NextRun while the foreground job was still running, causing
                // duplicate runs in some cross-process timing edge cases.
                originalJob.UpdateNextRun();
                if (!originalJob.IsRecurring)
                    originalJob.IsEnabled = false;
                SaveJobs();
            }
            RunningJobChanged?.Invoke(SnapshotRunningState());
            jobLock.Dispose();
        }
    }

    /// <summary>
    /// Emits a single additive telemetry event summarising a finished backup run, used by
    /// the external 3.0->4.0 verification tooling to measure transfer throughput and to
    /// prove the FAT/exFAT incremental re-copy fix (run-2 filesCopied should drop to ~0 on
    /// a FAT/exFAT destination). Best-effort and side-effect-free — a telemetry failure
    /// must never affect the backup outcome or the caller.
    /// Shared with the foreground "Back up now" / wizard "Run now" path
    /// (<see cref="ViewModels.MainViewModel.RunWizardBackupAsync"/>) so manual runs are
    /// captured as field data, not just scheduled and headless runs.
    /// </summary>
    internal static void EmitBackupTelemetry(
        string jobName, string mode, TransferResult result, double durationMs, string destinationPath)
    {
        try
        {
            string destFs = "unknown";
            try
            {
                var root = Path.GetPathRoot(destinationPath);
                if (!string.IsNullOrEmpty(root))
                    destFs = new DriveInfo(root).DriveFormat; // "NTFS" / "FAT32" / "exFAT" / ...
            }
            catch { /* drive may be unavailable; leave as "unknown" */ }

            Telemetry.BeetTelemetry.Log.BackupCompleted(
                jobName, mode, result.BytesTransferred, result.FilesCopied,
                result.FilesSkipped, result.FilesFailed, durationMs, destFs);
        }
        catch { /* telemetry is observe-only */ }
    }

    /// <summary>
    /// A log entry that records something ABOUT a job without a transfer running — the
    /// "Scheduled" and "Skipped" markers. Sets Timestamp explicitly because these are
    /// point-in-time notes rather than the start of a run.
    /// </summary>
    private static BackupLogEntry PlaceholderFor(ScheduledJob job, BackupStatus status, string message) => new()
    {
        JobId = job.Id,
        JobName = job.Name,
        SourcePath = string.Join("; ", job.SourcePaths),
        DestinationPath = job.DestinationPath,
        Status = status,
        Timestamp = DateTime.Now,
        Message = message
    };

    /// <summary>
    /// The entry that fronts an actual run. Carries the transfer inputs (SourcePaths,
    /// StripPermissions, TransferMode) because the run is driven from this entry, and
    /// deliberately does NOT set Timestamp — the entry's own default stands, matching the
    /// behaviour before these helpers existed.
    /// </summary>
    private static BackupLogEntry RunningFor(ScheduledJob job) => new()
    {
        JobId = job.Id,
        JobName = job.Name,
        SourcePath = string.Join("; ", job.SourcePaths),
        DestinationPath = job.DestinationPath,
        SourcePaths = new List<string>(job.SourcePaths),
        StripPermissions = job.StripPermissions,
        TransferMode = job.TransferMode,
        Status = BackupStatus.Running,
        Message = "Estimating size..."
    };

    /// <summary>
    /// A fresh Running entry cloned from a FAILED one, for the retry path. Takes its inputs
    /// from the failed entry rather than from a job, because a retry can outlive edits to the
    /// job that spawned it — the retry must repeat what actually ran.
    /// </summary>
    private static BackupLogEntry RetryOf(BackupLogEntry failed) => new()
    {
        JobId = failed.JobId,
        JobName = failed.JobName + " (retry)",
        SourcePath = failed.SourcePath,
        DestinationPath = failed.DestinationPath,
        SourcePaths = new List<string>(failed.SourcePaths),
        StripPermissions = failed.StripPermissions,
        TransferMode = failed.TransferMode,
        Status = BackupStatus.Running,
        Message = "Retrying transfer..."
    };

    private static ScheduledJob SnapshotJob(ScheduledJob job)
    {
        // Routes through ScheduledJob.CopyFieldsFrom so adding a new persisted property to
        // ScheduledJob is a one-touch change rather than something that has to be reflected
        // here too. The Id is set up front (CopyFieldsFrom treats it as immutable identity).
        var snapshot = new ScheduledJob { Id = job.Id };
        snapshot.CopyFieldsFrom(job);
        return snapshot;
    }

    /// <summary>Pauses a currently running job by resetting its pause gate.</summary>
    public void PauseJob(Guid logEntryId)
    {
        bool changed = false;
        lock (_jobsLock)
        {
            if (_pauseGates.TryGetValue(logEntryId, out var gate) && gate.IsSet)
            {
                gate.Reset();
                changed = true;
            }
        }
        if (changed) RunningJobChanged?.Invoke(SnapshotRunningState());
    }

    /// <summary>Resumes a paused job by signaling its pause gate.</summary>
    public void ResumeJob(Guid logEntryId)
    {
        bool changed = false;
        lock (_jobsLock)
        {
            if (_pauseGates.TryGetValue(logEntryId, out var gate) && !gate.IsSet)
            {
                gate.Set();
                changed = true;
            }
        }
        if (changed) RunningJobChanged?.Invoke(SnapshotRunningState());
    }

    /// <summary>Returns whether the specified running job is currently paused.</summary>
    public bool IsJobPaused(Guid logEntryId)
    {
        lock (_jobsLock)
        {
            return _pauseGates.TryGetValue(logEntryId, out var gate) && !gate.IsSet;
        }
    }

    /// <summary>Returns whether the specified job has an active pause gate (i.e. is currently executing).</summary>
    public bool IsJobRunning(Guid logEntryId)
    {
        lock (_jobsLock)
        {
            return _pauseGates.ContainsKey(logEntryId);
        }
    }

    /// <summary>
    /// Retries a failed backup entry by re-running the transfer with the same parameters.
    /// </summary>
    /// <param name="failedEntry">The failed log entry to retry.</param>
    public async Task RetryAsync(BackupLogEntry failedEntry)
    {
        if (failedEntry.SourcePaths.Count == 0) return;

        // Take the cross-process per-job lock so a user clicking Retry in the LogDialog while
        // Windows Task Scheduler fires the same JobId can't race. JobId may be Guid.Empty for
        // pre-JobId log entries — in that case fall through without the lock (matching the
        // mutex-creation-failure policy).
        var jobLock = failedEntry.JobId != Guid.Empty
            ? JobMutex.TryAcquire(failedEntry.JobId, failedEntry.JobName)
            : null;
        if (jobLock?.WasBusy == true)
        {
            FileLogger.Info($"Retry skipped: '{failedEntry.JobName}' — already running in another process or thread.");
            ToastNotifier.Notify($"Retry skipped: {failedEntry.JobName}", "Another process or thread is already running this job.", ToastKind.Warning);
            jobLock.Dispose();
            return;
        }

        var logEntry = RetryOf(failedEntry);
        _log.Add(logEntry);
        FileLogger.Info($"Retry started: '{logEntry.JobName}' — {logEntry.SourcePath} → {logEntry.DestinationPath}");

        var retryStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var percentProgress = new Progress<int>(pct =>
                _log.UpdateProgress(logEntry.Id, pct));

            var result = await _transfer.CopyAsync(
                logEntry.SourcePaths,
                logEntry.DestinationPath,
                logEntry.StripPermissions,
                logEntry.TransferMode,
                progressPercent: percentProgress).ConfigureAwait(false);

            _log.UpdateStats(logEntry.Id, BackupStatus.Complete, result);
            retryStopwatch.Stop();
            EmitBackupTelemetry(logEntry.JobName, logEntry.TransferMode.ToString(), result,
                retryStopwatch.Elapsed.TotalMilliseconds, logEntry.DestinationPath);
            FileLogger.Info($"Retry completed: '{logEntry.JobName}' — {TransferReporter.FormatSummary(result)}");
        }
        catch (InsufficientSpaceException)
        {
            FileLogger.Error($"Retry failed: '{logEntry.JobName}' — insufficient disk space");
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, "Not enough disk space on the destination drive.");
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Retry failed: '{logEntry.JobName}'", ex);
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, ex.Message);
        }
        finally
        {
            jobLock?.Dispose();
        }
    }

    private void LoadJobs()
    {
        try
        {
            if (!File.Exists(JobsPath)) return;
            var json = File.ReadAllText(JobsPath);
            var loaded = JsonSerializer.Deserialize<List<ScheduledJob>>(json);
            if (loaded != null)
            {
                lock (_jobsLock)
                {
                    // Merge by Id rather than Clear+AddRange — in-flight ExecuteJobAsync tasks
                    // hold references to existing ScheduledJob instances (captured as
                    // originalJob) and mutate LastRun/NextRun in their finally blocks. If we
                    // replaced the list outright, those references would no longer be in _jobs
                    // when SaveJobs ran, and the headless process's NextRun update could be
                    // clobbered. Mutating existing instances in place keeps every reference
                    // pointing at a live entry.
                    var existingById = _jobs.ToDictionary(j => j.Id);
                    _jobs.Clear();
                    foreach (var incoming in loaded)
                    {
                        if (existingById.TryGetValue(incoming.Id, out var existing))
                        {
                            existing.CopyFieldsFrom(incoming);
                            _jobs.Add(existing);
                        }
                        else
                        {
                            _jobs.Add(incoming);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Silent failure here used to disappear the user's entire schedule on a corrupt
            // scheduled_jobs.json. Logging gives them (and us) a trail in operational.log.
            FileLogger.LogException($"Failed to load scheduled jobs from {JobsPath}", ex);
        }
    }

    private void SaveJobs()
    {
        // Mark before the write begins, not after — watcher events from this write may fire
        // before File.Replace returns, and we want every one of them to land inside the cooldown.
        Volatile.Write(ref _lastSelfWriteTicks, DateTime.UtcNow.Ticks);
        try
        {
            var dir = Path.GetDirectoryName(JobsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_jobs);
            var tmpPath = JobsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(JobsPath))
                File.Replace(tmpPath, JobsPath, JobsPath + ".bak");
            else
                File.Move(tmpPath, JobsPath);
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Failed to save scheduled jobs to {JobsPath}", ex);
        }
    }

    /// <summary>
    /// Starts the <see cref="FileSystemWatcher"/> on <c>scheduled_jobs.json</c>. The headless
    /// <c>--run-job</c> path runs in a separate process and writes this file when it advances
    /// NextRun — without this watcher the foreground app's job list stays stuck on pre-run
    /// timestamps until restart.
    /// </summary>
    private void StartWatcher()
    {
        if (_watcher != null) return;
        try
        {
            var dir = Path.GetDirectoryName(JobsPath)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(JobsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnJobsFileChanged;
            _watcher.Created += OnJobsFileChanged;
            _watcher.Renamed += OnJobsFileChanged;
        }
        catch (Exception ex)
        {
            FileLogger.LogException("SchedulerService: could not start file watcher", ex);
        }
    }

    /// <summary>
    /// Watcher callback. Fires on a thread-pool thread; debounces the burst that File.Replace
    /// produces and ignores events caused by this process's own writes.
    /// </summary>
    private void OnJobsFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        if (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSelfWriteTicks) < SelfWriteCooldown.Ticks) return;

        // Atomically swap in a new reload CTS and retire the previous one. FileSystemWatcher
        // fires its callbacks on thread-pool workers and can deliver multiple events for one
        // File.Replace burst concurrently — a plain read-cancel-replace races and either
        // orphans a CTS or breaks coalescing. See BackupLogService.OnLogFileChanged for the
        // matching write-up.
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _reloadCts, cts);
        try { prev?.Cancel(); }
        catch (ObjectDisposedException) { /* prior CTS already disposed by Dispose() — benign */ }
        prev?.Dispose();
        if (_disposed) { cts.Dispose(); return; }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WatcherCoalesceDelay, cts.Token).ConfigureAwait(false);
                LoadJobs();
                JobsChanged?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { FileLogger.LogException("SchedulerService: reload failed", ex); }
        });
    }

    // Archive name builder lives in NameSanitizer so the manual ("Back up now") path and
    // the scheduled-run path share the same sanitisation rules. Method-form alias kept so
    // call sites read naturally.
    private static string BuildArchiveName(string jobName) => NameSanitizer.BuildArchiveName(jobName);

    public void Dispose()
    {
        // Flip the flag and silence the watcher before tearing anything else down — any
        // callback already queued on the thread pool will short-circuit before touching
        // _reloadCts. EnableRaisingEvents=false throws if the watcher is already disposed,
        // which can't happen here but is defensively wrapped anyway.
        _disposed = true;
        if (_watcher != null)
        {
            try { _watcher.EnableRaisingEvents = false; } catch { }
            _watcher.Dispose();
            _watcher = null;
        }
        try { _reloadCts?.Cancel(); } catch (ObjectDisposedException) { }
        _reloadCts?.Dispose();
        _reloadCts = null;

        // Cancel in-flight jobs BEFORE waiting on the scheduler loop. The old order waited
        // up to 3s for the scheduler tick task (which only dispatches; doesn't run jobs) and
        // only THEN signalled the actual ExecuteJobAsync workers. That meant a scheduled run
        // active at quit time kept copying bytes through the entire 3s wait window, and
        // App.OnExit's 5s overall budget frequently fired before the worker could unwind.
        // Cancelling here gives the worker the full wait window to observe the token, close
        // its file handles, and run its finally block cleanly. Pause gates are pulsed first
        // so a paused worker observes the CT immediately rather than via the throw path.
        lock (_jobsLock)
        {
            foreach (var gate in _pauseGates.Values)
                gate.Set();
            foreach (var cts in _runningCts.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        _cts.Cancel();
        _ticker.Dispose(); // Causes WaitForNextTickAsync to return false immediately
        // Wait briefly for the scheduler loop to exit — don't block indefinitely
        // as that can deadlock when Dispose is called from the UI thread
        if (_runTask != null)
        {
            // _cts was cancelled two lines up, so the loop ends in the Canceled state and Wait()
            // republishes that as AggregateException(TaskCanceledException). That is the EXPECTED
            // outcome of a clean shutdown, but it used to escape Dispose, and the damage was not
            // just a stray log line:
            //   1. everything below this block (per-job CTS disposal, pause-gate disposal,
            //      _cts.Dispose) was skipped, leaking those handles; and
            //   2. ServiceProvider disposes its singletons in ONE unguarded reverse-order loop,
            //      so the throw aborted that loop and every service constructed before this one
            //      (BackupLogService, TransferService, FileSystemService, ThemeService,
            //      SettingsService) was never disposed at all.
            // Swallow cancellation only — a genuine fault out of the loop still propagates.
            try
            {
                if (!_runTask.Wait(SchedulerLoopShutdownTimeout))
                    FileLogger.Warn($"Scheduler task did not exit within {SchedulerLoopShutdownTimeout.TotalSeconds}s — abandoning wait");
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
                // Clean, requested cancellation — the loop did exactly what Cancel() asked of it.
            }
        }

        // Now that workers have had their cancellation window, dispose their resources.
        // A worker's own finally block may have already removed its entries and disposed
        // its CTS — the guards here cover the harmless double-dispose path.
        lock (_jobsLock)
        {
            foreach (var cts in _runningCts.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                cts.Dispose();
            }
            _runningCts.Clear();
            foreach (var gate in _pauseGates.Values)
            {
                gate.Set();
                gate.Dispose();
            }
            _pauseGates.Clear();
        }
        _cts.Dispose();
    }
}
