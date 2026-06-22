using System.Text.Json;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Analysis;

/// <summary>One parsed PerfMon session: its build tag, start time, and resource samples.</summary>
public sealed record LoadedSession(
    string FileName,
    SessionMetadata? Metadata,
    IReadOnlyList<PerformanceSample> Samples)
{
    public string BuildTag => Metadata?.BuildTag ?? "unknown";
    public DateTimeOffset StartedAt => Metadata?.StartedAt ?? DateTimeOffset.MinValue;
    public DateTimeOffset? LastSampleAt => Samples.Count > 0 ? Samples[^1].Timestamp : null;
}

/// <summary>
/// Loads session_*.jsonl resource logs. Shared by the cohort report so the build-tagged
/// session data is parsed in one tolerant place (truncated tails, files being written).
/// </summary>
public static class SessionLoader
{
    public static IReadOnlyList<LoadedSession> Load(string logDirectory)
    {
        var sessions = new List<LoadedSession>();
        if (!Directory.Exists(logDirectory)) return sessions;

        foreach (var file in Directory.EnumerateFiles(logDirectory, "session_*.jsonl"))
        {
            try { sessions.Add(Parse(file)); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionLoader] Failed to parse {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return sessions;
    }

    private static LoadedSession Parse(string path)
    {
        var samples = new List<PerformanceSample>();
        SessionMetadata? metadata = null;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                switch (typeEl.GetString())
                {
                    case "session_start":
                        metadata = doc.RootElement.GetProperty("metadata").Deserialize<SessionMetadata>();
                        break;
                    case "sample":
                        var s = doc.RootElement.GetProperty("data").Deserialize<PerformanceSample>();
                        if (s is not null) samples.Add(s);
                        break;
                }
            }
        }
        return new LoadedSession(Path.GetFileName(path), metadata, samples);
    }
}
