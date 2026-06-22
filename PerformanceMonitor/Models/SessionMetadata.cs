namespace BeetsBackup.PerfMon.Models;

public sealed record SessionMetadata
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ProcessPath { get; init; }
    public required string ProcessVersion { get; init; }

    /// <summary>"3.0-baseline" or "4.0-candidate" — the bucket this whole session belongs
    /// to in the A/B cohort report. Resolved from Beet's telemetry AppStarted record when
    /// available, else derived from the exe's major version.</summary>
    public string BuildTag { get; init; } = "unknown";

    /// <summary>Short git commit Beet stamped into its build, when present.</summary>
    public string GitCommit { get; init; } = string.Empty;

    public required SystemInfo System { get; init; }
}
