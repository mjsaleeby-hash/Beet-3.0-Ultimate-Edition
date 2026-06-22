namespace BeetsBackup.PerfMon.Models;

/// <summary>
/// One parsed BeetTelemetry event. Numeric and string payload fields are kept in
/// separate maps so the cohort report can pull whichever it needs per event type
/// (e.g. BackupCompleted.durationMs, NavigationCompleted.elapsedMs) without a rigid
/// schema per event. Every record is already build-tagged by Beet at emit time.
/// </summary>
public sealed record TelemetryRecord(
    string BuildTag,
    DateTimeOffset Timestamp,
    string EventName,
    IReadOnlyDictionary<string, double> Numbers,
    IReadOnlyDictionary<string, string> Strings)
{
    public double Number(string key) => Numbers.TryGetValue(key, out var v) ? v : double.NaN;
    public string Text(string key) => Strings.TryGetValue(key, out var v) ? v : string.Empty;
}

/// <summary>
/// One backup run as recorded in backup_log.json — the authoritative source of error
/// detail (locked files, disk-full, checksum mismatches, per-file errors). It carries
/// no build tag of its own, so the report assigns one by which build was running at
/// <see cref="Timestamp"/> (the build-window timeline from telemetry AppStarted records).
/// </summary>
public sealed record BackupOutcome
{
    public required DateTime Timestamp { get; init; }
    public string BuildTag { get; set; } = "unknown"; // assigned by timeline correlation
    public required string JobName { get; init; }
    public required string Status { get; init; }
    public int FilesCopied { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesFailed { get; init; }
    public int FilesLocked { get; init; }
    public int DirectoriesFailed { get; init; }
    public int DiskFullErrors { get; init; }
    public int ChecksumMismatches { get; init; }
    public long BytesTransferred { get; init; }
    public int FileErrorCount { get; init; }

    /// <summary>Total error-ish events for the per-run error-rate metric.</summary>
    public int TotalErrors => FilesFailed + DirectoriesFailed + DiskFullErrors + ChecksumMismatches;
}

/// <summary>A Beet-related crash / unhandled-exception entry from the Windows Application log.</summary>
public sealed record BeetEventLogEntry(
    DateTime Timestamp,
    string Level,
    string Source,
    string Message)
{
    public string BuildTag { get; set; } = "unknown"; // assigned by timeline correlation
}

/// <summary>A point in time when a particular build started running (from telemetry AppStarted).
/// Used to decide which cohort a timestamped backup/crash belongs to.</summary>
public sealed record BuildWindowMarker(DateTimeOffset StartedAt, string BuildTag);
