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

    /// <summary>Starts the background scheduler loop (idempotent).</summary>
    public void Start()
    {
        if (_runTask == null)
            _runTask = RunAsync(_cts.Token);
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
        JobsChanged?.Invoke();
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
            _log.Add(new BackupLogEntry
            {
                JobId = job.Id,
                JobName = job.Name,
                SourcePath = string.Join("; ", job.SourcePaths),
                DestinationPath = job.DestinationPath,
                Status = BackupStatus.Skipped,
                Message = $"Missed backup skipped by user. Next run: {job.NextRun:g}"
            });
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

        _log.Add(new BackupLogEntry
        {
            JobId = job.Id,
            JobName = job.Name,
            SourcePath = string.Join("; ", job.SourcePaths),
            DestinationPath = job.DestinationPath,
            Status = BackupStatus.Scheduled,
            Message = $"Scheduled for {job.NextRun:g}{(job.IsRecurring ? $", recurring {job.RecurInterval}" : "")}"
        });

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
                _jobs[idx] = updated;
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
        // Cross-process per-job lock. The same job can race two ways:
        //   1. Windows Task Scheduler launches `BeetsBackup.exe --run-job` while the
        //      foreground app is already open, and that app's in-process minute-tick
        //      ticker also fires the job before its file watcher picks up the headless
        //      process's NextRun update.
        //   2. Two foreground threads dispatch a Task.Run for the same job back-to-back.
        // A named mutex (Global\ scope so it spans sessions) lets the first runner win
        // and the second exit immediately without doing duplicate work.
        var mutexName = $@"Global\BeetsBackup_Job_{snapshot.Id:N}";
        Mutex? jobMutex = null;
        bool gotLock = false;
        try
        {
            jobMutex = new Mutex(initiallyOwned: false, name: mutexName);
            try { gotLock = jobMutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { gotLock = true; } // prior holder crashed; safe to take
        }
        catch (Exception ex)
        {
            // If creating the mutex itself fails (extremely rare — out of handles, ACL issue),
            // fall through and run anyway. Better a possible duplicate run than a missed run.
            FileLogger.LogException($"Could not create job mutex for '{snapshot.Name}'; running without cross-process lock", ex);
            jobMutex?.Dispose();
            jobMutex = null;
        }

        if (jobMutex != null && !gotLock)
        {
            FileLogger.Info($"Skipping '{snapshot.Name}' — another process or thread is already running this job.");
            jobMutex.Dispose();
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

        var logEntry = new BackupLogEntry
        {
            JobId = snapshot.Id,
            JobName = snapshot.Name,
            SourcePath = string.Join("; ", snapshot.SourcePaths),
            DestinationPath = snapshot.DestinationPath,
            SourcePaths = new List<string>(snapshot.SourcePaths),
            StripPermissions = snapshot.StripPermissions,
            TransferMode = snapshot.TransferMode,
            Status = BackupStatus.Running,
            Message = "Estimating size..."
        };
        _log.Add(logEntry);
        FileLogger.Info($"Scheduled job started: '{snapshot.Name}' — {string.Join("; ", snapshot.SourcePaths)} → {snapshot.DestinationPath}");

        var pauseGate = new ManualResetEventSlim(true);
        var runCts = new CancellationTokenSource();
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
            FileLogger.Info($"Estimated size for '{snapshot.Name}': {FormatBytes(estimatedSize)}");

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
            if (jobMutex != null)
            {
                try { jobMutex.ReleaseMutex(); } catch { /* never owned, or already released */ }
                jobMutex.Dispose();
            }
        }
    }

    private static ScheduledJob SnapshotJob(ScheduledJob job)
    {
        return new ScheduledJob
        {
            Id = job.Id,
            Name = job.Name,
            SourcePaths = new List<string>(job.SourcePaths),
            DestinationPath = job.DestinationPath,
            StripPermissions = job.StripPermissions,
            VerifyChecksums = job.VerifyChecksums,
            TransferMode = job.TransferMode,
            ExclusionFilters = new List<string>(job.ExclusionFilters),
            ThrottleMBps = job.ThrottleMBps,
            EnableVersioning = job.EnableVersioning,
            MaxVersions = job.MaxVersions,
            EnableCompression = job.EnableCompression,
            IsRecurring = job.IsRecurring,
            RecurInterval = job.RecurInterval,
            NextRun = job.NextRun,
            LastRun = job.LastRun,
            IsEnabled = job.IsEnabled
        };
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

        var logEntry = new BackupLogEntry
        {
            JobId = failedEntry.JobId,
            JobName = failedEntry.JobName + " (retry)",
            SourcePath = failedEntry.SourcePath,
            DestinationPath = failedEntry.DestinationPath,
            SourcePaths = new List<string>(failedEntry.SourcePaths),
            StripPermissions = failedEntry.StripPermissions,
            TransferMode = failedEntry.TransferMode,
            Status = BackupStatus.Running,
            Message = "Retrying transfer..."
        };
        _log.Add(logEntry);
        FileLogger.Info($"Retry started: '{logEntry.JobName}' — {logEntry.SourcePath} → {logEntry.DestinationPath}");

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
                            // Copy every field except Id, which is the merge key.
                            existing.Name = incoming.Name;
                            existing.SourcePaths = incoming.SourcePaths;
                            existing.DestinationPath = incoming.DestinationPath;
                            existing.StripPermissions = incoming.StripPermissions;
                            existing.VerifyChecksums = incoming.VerifyChecksums;
                            existing.TransferMode = incoming.TransferMode;
                            existing.ExclusionFilters = incoming.ExclusionFilters;
                            existing.ThrottleMBps = incoming.ThrottleMBps;
                            existing.EnableVersioning = incoming.EnableVersioning;
                            existing.MaxVersions = incoming.MaxVersions;
                            existing.EnableCompression = incoming.EnableCompression;
                            existing.IsRecurring = incoming.IsRecurring;
                            existing.RecurInterval = incoming.RecurInterval;
                            existing.NextRun = incoming.NextRun;
                            existing.LastRun = incoming.LastRun;
                            existing.IsEnabled = incoming.IsEnabled;
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

        // Cancel may race with Dispose; ObjectDisposedException is benign here.
        try { _reloadCts?.Cancel(); }
        catch (ObjectDisposedException) { return; }
        if (_disposed) return;
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token).ConfigureAwait(false);
                LoadJobs();
                JobsChanged?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { FileLogger.LogException("SchedulerService: reload failed", ex); }
        });
    }

    /// <summary>Builds a timestamped archive filename for a compressed scheduled job run.</summary>
    private static string BuildArchiveName(string jobName)
    {
        var safe = string.Concat(jobName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        // Windows rejects filenames ending in '.' or whitespace — strip those before appending the
        // timestamp so a job like "Nightly ." doesn't produce a path Explorer can't open.
        safe = safe.TrimEnd(' ', '.', '\t');
        if (string.IsNullOrWhiteSpace(safe)) safe = "backup";
        return $"{safe}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }

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
        _cts.Cancel();
        _ticker.Dispose(); // Causes WaitForNextTickAsync to return false immediately
        // Wait briefly for the scheduler loop to exit — don't block indefinitely
        // as that can deadlock when Dispose is called from the UI thread
        if (_runTask != null)
        {
            if (!_runTask.Wait(TimeSpan.FromSeconds(3)))
                FileLogger.Warn("Scheduler task did not exit within 3 seconds — abandoning wait");
        }
        lock (_jobsLock)
        {
            // Cancel running jobs first, then release their pause gates so workers can observe
            // the cancellation. Order matters: a cancelled-but-paused job would otherwise block
            // here forever waiting on the gate.
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
