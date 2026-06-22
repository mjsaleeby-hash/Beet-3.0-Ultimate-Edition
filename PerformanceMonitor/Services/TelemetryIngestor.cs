using System.Globalization;
using System.Text.Json;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

/// <summary>
/// Reads Beet's BeetTelemetry JSONL files into typed records. These events are the
/// precise, build-tagged, per-operation timings the cohort report uses to prove the
/// UI-latency and throughput deltas (navigation, search, folder-size, backup).
/// Tolerant of truncated tail lines and the file being actively written by Beet.
/// </summary>
public sealed class TelemetryIngestor
{
    private readonly string _telemetryDir;

    public TelemetryIngestor(string? telemetryDir = null)
        => _telemetryDir = telemetryDir ?? BeetDataPaths.TelemetryDir;

    public IReadOnlyList<TelemetryRecord> Load()
    {
        var records = new List<TelemetryRecord>();
        if (!Directory.Exists(_telemetryDir)) return records;

        foreach (var file in Directory.EnumerateFiles(_telemetryDir, "telemetry_*.jsonl"))
        {
            try { ParseFile(file, records); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Telemetry] Failed to parse {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return records;
    }

    /// <summary>The timeline of build launches (from AppStarted events), so timestamped
    /// backups/crashes can be assigned to the build that was running at the time.</summary>
    public IReadOnlyList<BuildWindowMarker> BuildTimeline()
        => Load()
            .Where(r => r.EventName == "AppStarted")
            .Select(r => new BuildWindowMarker(r.Timestamp, r.BuildTag))
            .OrderBy(m => m.StartedAt)
            .ToList();

    private static void ParseFile(string path, List<TelemetryRecord> into)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; } // truncated/corrupt tail line — skip

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var evEl)) continue;
                var eventName = evEl.GetString() ?? "";
                var buildTag = root.TryGetProperty("buildTag", out var bt) ? bt.GetString() ?? "unknown" : "unknown";
                var ts = root.TryGetProperty("ts", out var tsEl) && tsEl.GetString() is { } tss
                    && DateTimeOffset.TryParse(tss, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed : DateTimeOffset.MinValue;

                var numbers = new Dictionary<string, double>();
                var strings = new Dictionary<string, string>();
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in data.EnumerateObject())
                    {
                        switch (prop.Value.ValueKind)
                        {
                            case JsonValueKind.Number:
                                if (prop.Value.TryGetDouble(out var d)) numbers[prop.Name] = d;
                                break;
                            case JsonValueKind.String:
                                strings[prop.Name] = prop.Value.GetString() ?? "";
                                break;
                        }
                    }
                }
                into.Add(new TelemetryRecord(buildTag, ts, eventName, numbers, strings));
            }
        }
    }
}
