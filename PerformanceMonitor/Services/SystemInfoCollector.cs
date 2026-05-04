using System.Management;
using System.Runtime.Versioning;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

[SupportedOSPlatform("windows")]
public static class SystemInfoCollector
{
    public static SystemInfo Collect()
    {
        var memStatus = new NativeMethods.MEMORYSTATUSEX();
        NativeMethods.GlobalMemoryStatusEx(memStatus);

        var cpuName = "Unknown";
        var physicalCores = 0;
        var maxClock = 0.0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, MaxClockSpeed FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                cpuName = obj["Name"]?.ToString()?.Trim() ?? cpuName;
                if (obj["NumberOfCores"] is not null)
                    physicalCores += Convert.ToInt32(obj["NumberOfCores"]);
                if (obj["MaxClockSpeed"] is not null)
                    maxClock = Convert.ToDouble(obj["MaxClockSpeed"]);
            }
        }
        catch { /* WMI not available — fall back to defaults */ }

        var disks = new List<DiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, MediaType, Size, InterfaceType FROM Win32_DiskDrive");
            foreach (var obj in searcher.Get())
            {
                disks.Add(new DiskInfo
                {
                    Model = obj["Model"]?.ToString()?.Trim() ?? "Unknown",
                    MediaType = obj["MediaType"]?.ToString()?.Trim() ?? "Unknown",
                    SizeBytes = obj["Size"] is null ? 0 : Convert.ToInt64(obj["Size"]),
                    InterfaceType = obj["InterfaceType"]?.ToString()?.Trim() ?? "Unknown",
                });
            }
        }
        catch { /* WMI not available */ }

        // Try to identify SSD vs HDD via MSFT_PhysicalDisk (more reliable on modern Windows).
        TryEnrichDiskMediaTypes(disks);

        return new SystemInfo
        {
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            CpuName = cpuName,
            PhysicalCores = physicalCores == 0 ? Environment.ProcessorCount : physicalCores,
            LogicalCores = Environment.ProcessorCount,
            CpuMaxClockMhz = maxClock,
            TotalMemoryBytes = (long)memStatus.ullTotalPhys,
            DotNetVersion = Environment.Version.ToString(),
            Disks = disks,
        };
    }

    private static void TryEnrichDiskMediaTypes(List<DiskInfo> disks)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT FriendlyName, MediaType FROM MSFT_PhysicalDisk"));

            var i = 0;
            foreach (var obj in searcher.Get())
            {
                if (i >= disks.Count) break;
                var mediaTypeCode = obj["MediaType"] is null ? 0 : Convert.ToInt32(obj["MediaType"]);
                var mediaName = mediaTypeCode switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => disks[i].MediaType,
                };
                disks[i] = disks[i] with { MediaType = mediaName };
                i++;
            }
        }
        catch { /* MSFT_PhysicalDisk may be restricted — keep WMI media type */ }
    }
}
