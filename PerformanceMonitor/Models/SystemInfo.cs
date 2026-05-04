namespace BeetsBackup.PerfMon.Models;

public sealed record SystemInfo
{
    public required string MachineName { get; init; }
    public required string OsVersion { get; init; }
    public required string CpuName { get; init; }
    public required int PhysicalCores { get; init; }
    public required int LogicalCores { get; init; }
    public required double CpuMaxClockMhz { get; init; }
    public required long TotalMemoryBytes { get; init; }
    public required string DotNetVersion { get; init; }
    public required IReadOnlyList<DiskInfo> Disks { get; init; }
}

public sealed record DiskInfo
{
    public required string Model { get; init; }
    public required string MediaType { get; init; }
    public required long SizeBytes { get; init; }
    public required string InterfaceType { get; init; }
}
