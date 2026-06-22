using System.Diagnostics;
using System.Text.Json;
using BeetsBackup.Benchmark.Models;
using BeetsBackup.PerfMon.Services;

namespace BeetsBackup.Benchmark.Services;

/// <summary>
/// Drives Beet through its existing headless entry point — `BeetsBackup.exe --run-job &lt;id&gt;` —
/// so a fixed corpus → fixed destination backup can be run identically and repeatedly on both
/// the 3.0 baseline and the 4.0 candidate. After each run it joins three sources for one
/// <see cref="BenchmarkRun"/>: the process wall-clock, the new backup_log.json entry (outcome),
/// and the matching BackupCompleted telemetry event (transfer-only duration + filesystem).
/// </summary>
public sealed class BenchmarkRunner
{
    private static readonly string ScheduledJobsPath = Path.Combine(BeetDataPaths.DataRoot, "scheduled_jobs.json");

    private readonly string _exePath;
    private readonly int _timeoutMs;

    public BenchmarkRunner(string exePath, int timeoutSeconds)
    {
        _exePath = exePath;
        _timeoutMs = timeoutSeconds * 1000;
    }

    /// <summary>A scheduled job the harness can drive: its id, display name, and destination.</summary>
    public sealed record JobRef(Guid Id, string Name, string Destination);

    public static IReadOnlyList<JobRef> ListJobs()
    {
        var jobs = new List<JobRef>();
        if (!File.Exists(ScheduledJobsPath)) return jobs;
        try
        {
            using var fs = new FileStream(ScheduledJobsPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return jobs;
            foreach (var j in doc.RootElement.EnumerateArray())
            {
                var id = j.TryGetProperty("Id", out var idEl) && Guid.TryParse(idEl.GetString(), out var g) ? g : Guid.Empty;
                var name = j.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var dest = j.TryGetProperty("DestinationPath", out var d) ? d.GetString() ?? "" : "";
                if (id != Guid.Empty) jobs.Add(new JobRef(id, name, dest));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Bench] Could not read scheduled_jobs.json: {ex.Message}"); }
        return jobs;
    }

    public static JobRef? ResolveJob(string nameOrId)
    {
        var jobs = ListJobs();
        if (Guid.TryParse(nameOrId, out var g))
            return jobs.FirstOrDefault(j => j.Id == g);
        return jobs.FirstOrDefault(j => string.Equals(j.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? jobs.FirstOrDefault(j => j.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs the job once and returns the joined result.</summary>
    public BenchmarkRun RunOnce(JobRef job, int iteration, string scenario)
    {
        var startUtc = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        LaunchAndWait(job.Id);
        sw.Stop();

        // Give Beet's debounced log/telemetry writes a moment to flush after the process exits.
        Thread.Sleep(750);

        var outcome = NewestOutcomeSince(startUtc.LocalDateTime.AddSeconds(-2));
        var tel = NewestBackupTelemetrySince(startUtc);

        return new BenchmarkRun
        {
            Iteration = iteration,
            Scenario = scenario,
            BuildTag = tel?.BuildTag ?? "unknown",
            StartedAt = startUtc,
            ProcessWallMs = sw.Elapsed.TotalMilliseconds,
            TelemetryDurationMs = tel?.Number("durationMs") is { } dm && !double.IsNaN(dm) ? dm : 0,
            FilesCopied = outcome?.FilesCopied ?? (int)(tel?.Number("filesCopied") ?? 0),
            FilesSkipped = outcome?.FilesSkipped ?? (int)(tel?.Number("filesSkipped") ?? 0),
            FilesFailed = outcome?.FilesFailed ?? (int)(tel?.Number("filesFailed") ?? 0),
            BytesTransferred = outcome?.BytesTransferred ?? (long)(tel?.Number("bytesTransferred") ?? 0),
            Status = outcome?.Status ?? "unknown",
            DestFilesystem = tel?.Text("destFilesystem") ?? "unknown",
        };
    }

    private void LaunchAndWait(Guid jobId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = $"--run-job {jobId}",
            // UseShellExecute=true so Beet's manifest-based elevation works. Run this harness
            // from an ELEVATED terminal to avoid a UAC prompt on every iteration.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {_exePath}");
        if (!proc.WaitForExit(_timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException($"Headless run exceeded {_timeoutMs / 1000}s and was killed.");
        }
    }

    private static PerfMon.Models.BackupOutcome? NewestOutcomeSince(DateTime sinceLocal)
        => new BackupLogIngestor().Load()
            .Where(o => o.Timestamp >= sinceLocal && o.Status is "Complete" or "Failed" or "Skipped")
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefault();

    private static PerfMon.Models.TelemetryRecord? NewestBackupTelemetrySince(DateTimeOffset sinceUtc)
        => new TelemetryIngestor().Load()
            .Where(r => r.EventName == "BackupCompleted" && r.Timestamp >= sinceUtc)
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();
}
