namespace BeetsBackup.PerfMon.Models;

public sealed record PerformanceSample
{
    public required DateTimeOffset Timestamp { get; init; }
    public required long UptimeMs { get; init; }

    public required double ProcessCpuPercent { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long PrivateBytes { get; init; }
    public required long VirtualBytes { get; init; }
    public required int HandleCount { get; init; }
    public required int ThreadCount { get; init; }

    public required long IoReadBytesTotal { get; init; }
    public required long IoWriteBytesTotal { get; init; }
    public required long IoReadBytesPerSec { get; init; }
    public required long IoWriteBytesPerSec { get; init; }

    public required double SystemCpuPercent { get; init; }
    public required long SystemAvailableMemoryBytes { get; init; }
}
