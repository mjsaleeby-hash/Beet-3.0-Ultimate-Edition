using BeetsBackup.Models;
using System.IO;
using System.Text.Json;

namespace BeetsBackup.Services;

public class SchedulerService : IDisposable
{
    private readonly TransferService _transfer;
    private readonly BackupLogService _log;
    private readonly List<ScheduledJob> _jobs = new();
    private readonly PeriodicTimer _ticker;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _jobsLock = new();
    private Task? _runTask;

    public string? LastSchedulerError { get; private set; }

    private static readonly string JobsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "scheduled_jobs.json");

    public IReadOnlyList<ScheduledJob> Jobs
    {
        get { lock (_jobsLock) { return _jobs.ToList().AsReadOnly(); } }
    }

    public event Action? JobsChanged;

    public SchedulerService(TransferService transfer, BackupLogService log)
    {
        _transfer = transfer;
        _log = log;
        LoadJobs();
        _ticker = new PeriodicTimer(TimeSpan.FromMinutes(1));
    }

    public void Start()
    {
        if (_runTask == null)
            _runTask = RunAsync(_cts.Token);
    }

    public List<ScheduledJob> GetMissedJobs()
    {
        lock (_jobsLock)
        {
            return _jobs.Where(j => j.IsEnabled && j.NextRun < DateTime.Now).ToList();
        }
    }

    public void RunMissedJobs(List<ScheduledJob> jobs)
    {
        foreach (var job in jobs)
        {
            FileLogger.Info($"Running missed job: '{job.Name}'");
            _ = ExecuteJobAsync(job);
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

    public void SkipMissedJob(Guid id)
    {
        lock (_jobsLock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job != null)
            {
                job.UpdateNextRun();
                SaveJobs();
            }
        }
        JobsChanged?.Invoke();
    }

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
                    _ = ExecuteJobAsync(job);
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
            }
        }
    }

    private async Task ExecuteJobAsync(ScheduledJob job)
    {
        var logEntry = new BackupLogEntry
        {
            JobName = job.Name,
            SourcePath = string.Join("; ", job.SourcePaths),
            DestinationPath = job.DestinationPath,
            SourcePaths = new List<string>(job.SourcePaths),
            StripPermissions = job.StripPermissions,
            TransferMode = job.TransferMode,
            Status = BackupStatus.Running,
            Message = "Transfer in progress..."
        };
        _log.Add(logEntry);
        FileLogger.Info($"Scheduled job started: '{job.Name}' — {string.Join("; ", job.SourcePaths)} → {job.DestinationPath}");

        try
        {
            var percentProgress = new Progress<int>(pct =>
                _log.UpdateProgress(logEntry.Id, pct));

            var result = await _transfer.CopyAsync(
                job.SourcePaths,
                job.DestinationPath,
                job.StripPermissions,
                job.TransferMode,
                progressPercent: percentProgress);

            _log.UpdateStats(logEntry.Id, BackupStatus.Complete,
                result.FilesCopied, result.FilesSkipped, result.BytesTransferred, result.TotalFiles);
            FileLogger.Info($"Scheduled job completed: '{job.Name}' — {result.FilesCopied} copied, {result.FilesSkipped} skipped");
        }
        catch (InsufficientSpaceException)
        {
            FileLogger.Error($"Scheduled job failed: '{job.Name}' — insufficient disk space");
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, "Not enough disk space on the destination drive.");
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Scheduled job failed: '{job.Name}'", ex);
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, ex.Message);
        }
        finally
        {
            lock (_jobsLock)
            {
                job.LastRun = DateTime.Now;
                SaveJobs();
            }
        }
    }

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

            _log.UpdateStats(logEntry.Id, BackupStatus.Complete,
                result.FilesCopied, result.FilesSkipped, result.BytesTransferred, result.TotalFiles);
            FileLogger.Info($"Retry completed: '{logEntry.JobName}' — {result.FilesCopied} copied, {result.FilesSkipped} skipped");
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

    public void Dispose()
    {
        _cts.Cancel();
        _ticker.Dispose();
        _cts.Dispose();
    }
}
