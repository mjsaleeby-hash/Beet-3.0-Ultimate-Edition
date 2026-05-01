using BeetsBackup.Models;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace BeetsBackup.Services;

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
    private Task? _runTask;

    /// <summary>UTC timestamp of the most recent self-initiated write to <c>scheduled_jobs.json</c>.
    /// The cross-process watcher uses this to suppress reloads triggered by our own SaveJobs.</summary>
    private DateTime _lastSelfWrite = DateTime.MinValue;

    /// <summary>Window after a self-save during which file-change events are ignored. File.Replace
    /// fires multiple events; this avoids self-thrashing on the watcher.</summary>
    private static readonly TimeSpan SelfWriteCooldown = TimeSpan.FromSeconds(2);

    /// <summary>Coalesces a burst of file-change events into a single reload after the watcher quiets.</summary>
    private CancellationTokenSource? _reloadCts;

    /// <summary>Watcher on <c>scheduled_jobs.json</c>. The headless --run-job path advances NextRun
    /// and writes the file from a separate process; without this watcher the foreground dashboard
    /// keeps showing the pre-run NextRun forever (until app restart).</summary>
    private FileSystemWatcher? _watcher;

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

    /// <summary>
    /// Creates the scheduler, loading persisted jobs from disk.
    /// Call <see cref="Start"/> to begin the polling loop.
    /// </summary>
    public SchedulerService(TransferService transfer, BackupLogService log)
    {
        _transfer = transfer;
        _log = log;
        LoadJobs();
        StartWatcher();
        _ticker = new PeriodicTimer(TimeSpan.FromMinutes(1));
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

    /// <summary>Fires off missed backup jobs on background threads and advances their next-run times.</summary>
    public void RunMissedJobs(List<ScheduledJob> jobs)
    {
        foreach (var job in jobs)
        {
            FileLogger.Info($"Running missed job: '{job.Name}'");
            // Snapshot job data before launching background task to avoid race conditions
            var snapshot = SnapshotJob(job);
            _ = Task.Run(async () =>
            {
                try { await ExecuteJobAsync(snapshot, job); }
                catch (Exception ex) { FileLogger.LogException($"Missed job failed: '{job.Name}'", ex); }
            });
            lock (_jobsLock)
            {
                job.UpdateNextRun();
                if (!job.IsRecurring)
                    job.IsEnabled = false;
                SaveJobs();
            }
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
        await ExecuteJobAsync(snapshot, job).ConfigureAwait(false);

        // Advance timing so Task Scheduler and the in-process timer agree about the next run.
        lock (_jobsLock)
        {
            job.UpdateNextRun();
            if (!job.IsRecurring)
                job.IsEnabled = false;
            SaveJobs();
        }
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
                    dueJobs = _jobs.Where(j => j.IsEnabled && j.NextRun <= now).ToList();
                }

                foreach (var job in dueJobs)
                {
                    // Snapshot job data before launching background task to avoid race conditions
                    var snapshot = SnapshotJob(job);
                    _ = Task.Run(async () =>
                    {
                        try { await ExecuteJobAsync(snapshot, job); }
                        catch (Exception jobEx) { FileLogger.LogException($"Scheduled job failed: '{job.Name}'", jobEx); }
                    });
                    lock (_jobsLock)
                    {
                        job.UpdateNextRun();
                        if (!job.IsRecurring)
                            job.IsEnabled = false;
                        SaveJobs();
                    }
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
        lock (_jobsLock) { _pauseGates[logEntry.Id] = pauseGate; }

        try
        {
            var exclusions = snapshot.ExclusionFilters.Count > 0 ? snapshot.ExclusionFilters : null;

            var estimatedSize = await Task.Run(() => TransferService.EstimateTotalSize(snapshot.SourcePaths, exclusions)).ConfigureAwait(false);
            _log.UpdateStatus(logEntry.Id, BackupStatus.Running, $"Transfer in progress — {FormatBytes(estimatedSize)} estimated");
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
                    pauseToken: pauseGate,
                    verifyChecksums: snapshot.VerifyChecksums,
                    exclusions: exclusions,
                    throttleBytesPerSec: snapshot.ThrottleMBps > 0 ? (long)snapshot.ThrottleMBps * 1024 * 1024 : 0,
                    versioning: versioning).ConfigureAwait(false);
            }

            var totalFailed = result.FilesFailed + result.DiskFullErrors + result.FilesLocked;
            _log.UpdateStats(logEntry.Id, BackupStatus.Complete,
                result.FilesCopied, result.FilesSkipped, result.BytesTransferred, result.TotalFiles,
                totalFailed, result.FileErrors);
            FileLogger.Info($"Scheduled job completed: '{snapshot.Name}' — {result.FilesCopied} copied, {result.FilesSkipped} skipped{(totalFailed > 0 ? $", {totalFailed} failed" : "")}");

            var toastKind = totalFailed > 0 ? ToastKind.Warning : ToastKind.Success;
            var toastBody = totalFailed > 0
                ? $"{result.FilesCopied} copied, {totalFailed} failed."
                : $"{result.FilesCopied} copied, {result.FilesSkipped} skipped.";
            ToastNotifier.Notify($"Backup complete: {snapshot.Name}", toastBody, toastKind);
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
                originalJob.LastRun = DateTime.Now;
                SaveJobs();
            }
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
        lock (_jobsLock)
        {
            if (_pauseGates.TryGetValue(logEntryId, out var gate))
                gate.Reset();
        }
    }

    /// <summary>Resumes a paused job by signaling its pause gate.</summary>
    public void ResumeJob(Guid logEntryId)
    {
        lock (_jobsLock)
        {
            if (_pauseGates.TryGetValue(logEntryId, out var gate))
                gate.Set();
        }
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

            var totalFailed = result.FilesFailed + result.DiskFullErrors + result.FilesLocked;
            _log.UpdateStats(logEntry.Id, BackupStatus.Complete,
                result.FilesCopied, result.FilesSkipped, result.BytesTransferred, result.TotalFiles,
                totalFailed, result.FileErrors);
            FileLogger.Info($"Retry completed: '{logEntry.JobName}' — {result.FilesCopied} copied, {result.FilesSkipped} skipped{(totalFailed > 0 ? $", {totalFailed} failed" : "")}");
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
                    // Replace, not append — this method is also the reload path for the file
                    // watcher when another process updates scheduled_jobs.json.
                    _jobs.Clear();
                    _jobs.AddRange(loaded);
                }
            }
        }
        catch { }
    }

    private void SaveJobs()
    {
        // Mark before the write begins, not after — watcher events from this write may fire
        // before File.Replace returns, and we want every one of them to land inside the cooldown.
        _lastSelfWrite = DateTime.UtcNow;
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
        catch { }
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
        if (DateTime.UtcNow - _lastSelfWrite < SelfWriteCooldown) return;

        _reloadCts?.Cancel();
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
        _watcher?.Dispose();
        _watcher = null;
        _reloadCts?.Cancel();
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
            foreach (var gate in _pauseGates.Values)
            {
                gate.Set(); // Unblock any paused jobs so they can observe cancellation
                gate.Dispose();
            }
            _pauseGates.Clear();
        }
        _cts.Dispose();
    }
}
