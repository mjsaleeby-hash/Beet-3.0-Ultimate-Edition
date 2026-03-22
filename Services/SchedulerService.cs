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

    private static readonly string JobsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "scheduled_jobs.json");

    public IReadOnlyList<ScheduledJob> Jobs => _jobs.AsReadOnly();
    public event Action? JobsChanged;

    public SchedulerService(TransferService transfer, BackupLogService log)
    {
        _transfer = transfer;
        _log = log;
        LoadJobs();
        _ticker = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _ = RunAsync(_cts.Token);
    }

    public void AddJob(ScheduledJob job)
    {
        _jobs.Add(job);
        SaveJobs();

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
        _jobs.RemoveAll(j => j.Id == id);
        SaveJobs();
        JobsChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (await _ticker.WaitForNextTickAsync(ct))
        {
            var now = DateTime.Now;
            foreach (var job in _jobs.Where(j => j.IsEnabled && j.NextRun <= now).ToList())
            {
                _ = ExecuteJobAsync(job);
                job.UpdateNextRun();
                if (!job.IsRecurring)
                    job.IsEnabled = false;
                SaveJobs();
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
            Status = BackupStatus.Running,
            Message = "Transfer in progress..."
        };
        _log.Add(logEntry);

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
        }
        catch (InsufficientSpaceException)
        {
            _log.UpdateStatus(logEntry.Id, BackupStatus.Failed, "Not enough space on destination dummy!");
        }
        catch (Exception ex)
        {
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
                _jobs.AddRange(loaded);
        }
        catch { }
    }

    private void SaveJobs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JobsPath)!);
            File.WriteAllText(JobsPath, JsonSerializer.Serialize(_jobs));
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
