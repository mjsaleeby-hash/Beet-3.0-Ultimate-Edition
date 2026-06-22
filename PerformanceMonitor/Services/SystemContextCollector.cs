using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

/// <summary>
/// Captures machine-wide context each tick so a Beet slowdown can be attributed to
/// Beet vs. an external cause. Two sources:
///   - Disk saturation (queue length, busy %, throughput) via WMI's PerfFormattedData,
///     which exposes the same counters as PerfMon/Resource Monitor.
///   - The top processes competing for CPU and disk, computed from process snapshots
///     with a remembered previous reading (CPU% and I/O bytes/sec are both DELTAS, so
///     they need two samples to compute).
///
/// All collection is best-effort: access-denied on a protected process or a transient
/// WMI hiccup must never take down the monitor. Beet's own PID is excluded so it never
/// shows up as its own competitor.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemContextCollector : IDisposable
{
    private readonly int _beetPid;
    private readonly int _logicalCores;

    // Per-PID previous readings, so CPU% and IO/sec can be computed as deltas.
    private readonly Dictionary<int, (TimeSpan Cpu, ulong IoBytes, DateTime At)> _prev = new();

    // A single reused WMI searcher for the disk perf counters.
    private readonly ManagementObjectSearcher _diskSearcher = new(
        "SELECT Name, AvgDiskQueueLength, PercentDiskTime, DiskBytesPersec " +
        "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name = '_Total'");

    private const int TopN = 3; // top-N by CPU and top-N by IO

    public SystemContextCollector(int beetPid)
    {
        _beetPid = beetPid;
        _logicalCores = Environment.ProcessorCount;
    }

    public SystemContext Collect()
    {
        var (queue, busy, diskBytes) = CollectDiskMetrics();
        var top = CollectTopProcesses();
        return new SystemContext
        {
            DiskQueueLength = queue,
            DiskBusyPercent = busy,
            DiskBytesPerSec = diskBytes,
            TopProcesses = top,
        };
    }

    private (double queue, double busy, long bytes) CollectDiskMetrics()
    {
        try
        {
            foreach (ManagementBaseObject mo in _diskSearcher.Get())
            {
                using (mo)
                {
                    double queue = ToDouble(mo["AvgDiskQueueLength"]);
                    double busy = ToDouble(mo["PercentDiskTime"]);
                    long bytes = (long)ToDouble(mo["DiskBytesPersec"]);
                    return (queue, busy, bytes);
                }
            }
        }
        catch { /* WMI unavailable or denied — fall through to zeros */ }
        return (0, 0, 0);
    }

    private IReadOnlyList<ProcessUsage> CollectTopProcesses()
    {
        var now = DateTime.UtcNow;
        var current = new List<(int Pid, string Name, double Cpu, long IoPerSec)>();
        var seenPids = new HashSet<int>();

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return Array.Empty<ProcessUsage>(); }

        foreach (var p in processes)
        {
            try
            {
                int pid = p.Id;
                seenPids.Add(pid);
                if (pid == _beetPid || pid == 0) continue;

                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; }
                catch { continue; } // protected/exited process — skip

                ulong ioBytes = 0;
                try
                {
                    if (NativeMethods.GetProcessIoCounters(p.Handle, out var io))
                        ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                }
                catch { /* handle access denied — leave ioBytes at 0 */ }

                if (_prev.TryGetValue(pid, out var prev))
                {
                    var elapsed = (now - prev.At).TotalSeconds;
                    if (elapsed <= 0) elapsed = 1;
                    var cpuPct = (cpu - prev.Cpu).TotalMilliseconds / (elapsed * 1000.0 * _logicalCores) * 100.0;
                    if (cpuPct < 0) cpuPct = 0;
                    long ioPerSec = ioBytes >= prev.IoBytes ? (long)((ioBytes - prev.IoBytes) / elapsed) : 0;
                    current.Add((pid, SafeName(p), Math.Round(cpuPct, 1), ioPerSec));
                }

                _prev[pid] = (cpu, ioBytes, now);
            }
            catch { /* anything odd about this process — skip it */ }
            finally { p.Dispose(); }
        }

        // Drop readings for processes that have exited so the map can't grow forever.
        foreach (var stalePid in _prev.Keys.Where(k => !seenPids.Contains(k)).ToList())
            _prev.Remove(stalePid);

        // Union of the top-N by CPU and the top-N by IO, so a disk hog with low CPU
        // (or vice versa) is still surfaced.
        var byCpu = current.OrderByDescending(c => c.Cpu).Take(TopN);
        var byIo = current.OrderByDescending(c => c.IoPerSec).Take(TopN);
        return byCpu.Concat(byIo)
            .DistinctBy(c => c.Pid)
            .Where(c => c.Cpu > 1.0 || c.IoPerSec > 256 * 1024) // ignore idle noise
            .Select(c => new ProcessUsage { Name = c.Name, Pid = c.Pid, CpuPercent = c.Cpu, IoBytesPerSec = c.IoPerSec })
            .ToList();
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    private static double ToDouble(object? value)
    {
        try { return value is null ? 0 : Convert.ToDouble(value); }
        catch { return 0; }
    }

    public void Dispose() => _diskSearcher.Dispose();
}
