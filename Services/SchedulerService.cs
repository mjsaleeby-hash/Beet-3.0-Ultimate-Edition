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
            JobName = job.Name,
            SourcePath = string.Join("; ", job.SourcePaths),
            DestinationPath = job.DestinationPath,
            Status = BackupStatus.Scheduled,
            Message = $"Scheduled for {job.NextRun:g}{(job.IsRecurring ? $", recurring {job.RecurInterval}" : "")}"
        });

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
        JobsChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (await _ticker.WaitForNextTickAsync(ct))
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
        var logEntry = new BackupLogEntry
        {
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

            var estimatedSize = await Task.Run(() => TransferService.EstimateTotalSize(snapshot.SourcePaths, exclusions));
            _log.UpdateStatus(logEntry.Id, BackupStatus.Running, $"Transfer in progress — {FormatBytes(estimatedSize)} estimated");
            FileLogger.Info($"Estimated size for '{snapshot.Name}': {FormatBytes(estimatedSize)}");

            var percentProgress = new Progress<int>(pct =>
                _log.UpdateProgress(logEntry.Id, pct));

            var result = await _transfer.CopyAsync(
                snapshot.SourcePaths,
                snapshot.DestinationPath,
                snapshot.StripPermissions,
                snapshot.TransferMode,
                progressPercent: percentProgress,
                pauseToken: pauseGate,
                verifyChecksums: snapshot.VerifyChecksums,
                exclusions: exclusions,
                throttleBytesPerSec: snapshot.ThrottleMBps > 0 ? (long)snapshot.ThrottleMBps * 1024 * 1024 : 0);

            var totalFailed = result.FilesFailed + result.DiskFullErrors + result.FilesLocked;
            _log.UpdateStats(logEntry.Id, BackupStatus.Complete,
                result.FilesCopied, result.FilesSkipped, result.BytesTransferred, result.TotalFiles,
                totalFailed, result.FileErrors);
            FileLogger.Info($"Scheduled job completed: '{snapshot.Name}' — {result.FilesCopied} copied, {result.FilesSkipped} skipped{(totalFailed > 0 ? $", {totalFailed} failed" : "")}");
        }
        catch (InsufficientSpaceException)
        {
            FileLogger.Error($"Scheduled job failed: '{snapshot.Name}' — insufficient disk space");
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, "Not enough disk space on the destination drive.");
            SchedulerError?.Invoke($"Job '{snapshot.Name}' failed — insufficient disk space");
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Scheduled job failed: '{snapshot.Name}'", ex);
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, ex.Message);
            SchedulerError?.Invoke($"Job '{snapshot.Name}' failed — {ex.Message}");
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
                progressPercent: percentProgress);

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
                    _jobs.AddRange(loaded);
                }
            }
        }
        catch { }
    }

    private void SaveJobs()
    {
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
