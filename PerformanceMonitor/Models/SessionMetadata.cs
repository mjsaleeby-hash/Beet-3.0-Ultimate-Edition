namespace BeetsBackup.PerfMon.Models;

public sealed record SessionMetadata
{
    public required string SessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ProcessPath { get; init; }
    public required string ProcessVersion { get; init; }
    public required SystemInfo System { get; init; }
}
