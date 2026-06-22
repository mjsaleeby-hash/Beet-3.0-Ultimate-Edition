using System.Diagnostics.Tracing;

namespace BeetsBackup.Telemetry;

/// <summary>
/// Lightweight, additive performance-telemetry channel for Beet's Backup.
///
/// WHY THIS EXISTS (for future maintainers):
///   The 3.0 -> 4.0 verification effort needs to prove, with real numbers, that
///   specific operations got faster (navigation, search, folder sizing, backups).
///   Inferring those timings from OUTSIDE the process (CPU/IO curves) is imprecise
///   and hard to attribute. Emitting one structured event at the END of each
///   operation — carrying the duration and a couple of counts that the code ALREADY
///   has on hand — gives exact, attributable measurements at essentially zero cost.
///
/// WHY AN EventSource (and not just a log line):
///   - When NO listener is attached, an EventSource event is almost free (a flag
///     check), so leaving these calls in a shipping build costs nothing.
///   - The in-process <see cref="TelemetryFileSink"/> is our only listener; it
///     writes each event to a JSON-Lines file the external PerformanceMonitor and
///     BenchmarkHarness ingest. The same events can also be captured by PerfView/ETW
///     later without any code change.
///
/// SAFETY:
///   This class is purely additive. It touches NONE of the "leave alone" mechanisms
///   (copy/scheduler/IPC/VSS/atomic-writes/watchdog). Call sites only wrap an
///   existing operation in a Stopwatch and emit numbers that operation produced.
/// </summary>
[EventSource(Name = "BeetsBackup-Telemetry")]
public sealed class BeetTelemetry : EventSource
{
    /// <summary>The process-wide singleton. Referencing it is what creates the
    /// underlying EventSource and lets <see cref="TelemetryFileSink"/> subscribe.</summary>
    public static readonly BeetTelemetry Log = new();

    private BeetTelemetry() { }

    /// <summary>
    /// Emitted once at application start. Stamps every telemetry file with the build
    /// identity so the PerformanceMonitor can bucket all downstream data as either the
    /// 3.0 baseline or the 4.0 candidate WITHOUT any manual bookkeeping.
    /// </summary>
    [Event(1, Level = EventLevel.Informational)]
    public void AppStarted(string version, string gitCommit, string buildTag)
        => WriteEvent(1, version, gitCommit, buildTag);

    /// <summary>
    /// Emitted when a backup run finishes. <paramref name="durationMs"/> closes a real
    /// gap: backup_log.json records only a last-updated timestamp, not how long a run
    /// took, so "transfer speed = bytes / duration" is not computable from the log
    /// alone. <paramref name="destFilesystem"/> (e.g. "FAT32", "exFAT", "NTFS") lets the
    /// harness prove the FAT/exFAT incremental re-copy fix specifically.
    /// </summary>
    [Event(2, Level = EventLevel.Informational)]
    public void BackupCompleted(
        string jobName, string mode, long bytesTransferred, int filesCopied,
        int filesSkipped, int filesFailed, double durationMs, string destFilesystem)
        => WriteEvent(2, jobName, mode, bytesTransferred, filesCopied,
                      filesSkipped, filesFailed, durationMs, destFilesystem);

    /// <summary>Emitted when a pane finishes loading a directory's children. Measures the
    /// navigation latency that Wave 2.2 (RangeObservableCollection) is meant to improve.</summary>
    [Event(3, Level = EventLevel.Informational)]
    public void NavigationCompleted(string pane, int itemCount, double elapsedMs)
        => WriteEvent(3, pane, itemCount, elapsedMs);

    /// <summary>Emitted when a deep search finishes. Measures the search time that Wave 1.3
    /// (drop the per-subdir GetAttributes syscall) is meant to improve.</summary>
    [Event(4, Level = EventLevel.Informational)]
    public void SearchCompleted(int matchCount, int directoriesScanned, double elapsedMs)
        => WriteEvent(4, matchCount, directoriesScanned, elapsedMs);

    /// <summary>Emitted when a folder-size pass finishes. Measures the sizing time that
    /// Wave 2.4 (drive-aware parallelism) is meant to improve, split by drive type.</summary>
    [Event(5, Level = EventLevel.Informational)]
    public void FolderSizeCompleted(int directoryCount, string driveType, double elapsedMs)
        => WriteEvent(5, directoryCount, driveType, elapsedMs);

    /// <summary>Emitted when the live filter re-runs over the panes. Measures the refresh
    /// churn that Wave 2.3 (debounce) is meant to reduce.</summary>
    [Event(6, Level = EventLevel.Informational)]
    public void FilterRefreshed(int itemCount, double elapsedMs)
        => WriteEvent(6, itemCount, elapsedMs);
}
