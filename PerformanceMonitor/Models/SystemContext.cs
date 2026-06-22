namespace BeetsBackup.PerfMon.Models;

/// <summary>
/// Machine-wide context captured ALONGSIDE each per-process sample. This is the
/// "alibi check" for the 3.0->4.0 comparison: if Beet looks slow at a given second,
/// these numbers say whether the MACHINE was saturated — and by WHAT — at that exact
/// moment. Without it, an external process pinning the disk could be misread as a
/// Beet regression.
/// </summary>
public sealed record SystemContext
{
    /// <summary>Average disk queue length across all physical disks (_Total). Sustained
    /// values &gt; ~2 per spindle indicate the disk is a bottleneck.</summary>
    public double DiskQueueLength { get; init; }

    /// <summary>Percent of time the disk subsystem was busy (_Total). Can exceed 100 on
    /// multi-disk systems; treat as a saturation indicator, not a strict percentage.</summary>
    public double DiskBusyPercent { get; init; }

    /// <summary>System-wide disk throughput (bytes/sec, _Total) at this sample.</summary>
    public long DiskBytesPerSec { get; init; }

    /// <summary>The processes competing with Beet for CPU and disk at this sample
    /// (union of the top few by CPU and by I/O), Beet itself excluded. Empty when
    /// nothing notable is running.</summary>
    public IReadOnlyList<ProcessUsage> TopProcesses { get; init; } = Array.Empty<ProcessUsage>();
}

/// <summary>One competing process's resource use at a sample instant.</summary>
public sealed record ProcessUsage
{
    public required string Name { get; init; }
    public required int Pid { get; init; }
    public required double CpuPercent { get; init; }
    public required long IoBytesPerSec { get; init; }
}
