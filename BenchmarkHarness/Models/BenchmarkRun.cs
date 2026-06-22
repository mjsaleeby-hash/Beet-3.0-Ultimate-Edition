namespace BeetsBackup.Benchmark.Models;

/// <summary>The result of one headless backup invocation, joined from the process wall-clock,
/// the new backup_log.json entry, and the matching BackupCompleted telemetry event.</summary>
public sealed record BenchmarkRun
{
    public required int Iteration { get; init; }
    public required string Scenario { get; init; }
    public required string BuildTag { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>End-to-end process time (includes Beet's ~1-2s headless startup).</summary>
    public required double ProcessWallMs { get; init; }
    /// <summary>Transfer-only time from telemetry (excludes startup), when available.</summary>
    public double TelemetryDurationMs { get; init; }

    public int FilesCopied { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesFailed { get; init; }
    public long BytesTransferred { get; init; }
    public string Status { get; init; } = "unknown";
    public string DestFilesystem { get; init; } = "unknown";

    /// <summary>MB/s computed from telemetry duration when present, else process wall-clock.</summary>
    public double ThroughputMbps
    {
        get
        {
            double ms = TelemetryDurationMs > 0 ? TelemetryDurationMs : ProcessWallMs;
            return ms > 0 && BytesTransferred > 0 ? BytesTransferred / 1048576.0 / (ms / 1000.0) : 0;
        }
    }
}
