using System.Globalization;
using System.Text;
using System.Text.Json;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Analysis;

public sealed class LogAnalyzer
{
    private readonly string _logDirectory;

    public LogAnalyzer(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public string BuildReport()
    {
        var sessions = LoadSessions().ToList();
        if (sessions.Count == 0)
            return "No session logs found.\n";

        var sb = new StringBuilder();
        sb.AppendLine("# Beet's Backup — Performance Trend Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Sessions analyzed: {sessions.Count}");
        sb.AppendLine($"Log directory: `{_logDirectory}`");
        sb.AppendLine();

        AppendOverallSummary(sb, sessions);
        AppendPerSessionTable(sb, sessions);
        AppendOutliers(sb, sessions);
        AppendTrends(sb, sessions);

        return sb.ToString();
    }

    private IEnumerable<SessionData> LoadSessions()
    {
        if (!Directory.Exists(_logDirectory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(_logDirectory, "session_*.jsonl"))
        {
            SessionData? data = null;
            try
            {
                data = ParseSession(file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Analyzer] Failed to parse {Path.GetFileName(file)}: {ex.Message}");
            }

            if (data is not null)
                yield return data;
        }
    }

    private static SessionData ParseSession(string path)
    {
        var samples = new List<PerformanceSample>();
        SessionMetadata? metadata = null;
        string endReason = "unknown";

        // Share Write + Delete so we can read a session file that PerfMon is currently writing to.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        var lineNumber = 0;
        var badLines = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                // Tolerate truncated tail lines (writer crashed / killed) and corrupt mid-stream lines.
                badLines++;
                continue;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();

                switch (type)
                {
                    case "session_start":
                        metadata = doc.RootElement.GetProperty("metadata").Deserialize<SessionMetadata>();
                        break;
                    case "sample":
                        var sample = doc.RootElement.GetProperty("data").Deserialize<PerformanceSample>();
                        if (sample is not null) samples.Add(sample);
                        break;
                    case "session_end":
                        endReason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "unknown" : "unknown";
                        break;
                }
            }
        }

        if (badLines > 0)
            Console.Error.WriteLine($"[Analyzer] Skipped {badLines} malformed line(s) in {Path.GetFileName(path)}");

        return new SessionData(Path.GetFileName(path), metadata, samples, endReason);
    }

    private static void AppendOverallSummary(StringBuilder sb, List<SessionData> sessions)
    {
        var allSamples = sessions.SelectMany(s => s.Samples).ToList();
        if (allSamples.Count == 0) return;

        sb.AppendLine("## Aggregate — all samples across all sessions");
        sb.AppendLine();
        AppendMetricTable(sb, allSamples);
        sb.AppendLine();
    }

    private static void AppendPerSessionTable(StringBuilder sb, List<SessionData> sessions)
    {
        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine("| Started | Duration | Samples | Avg CPU% | Peak CPU% | Avg WS (MB) | Peak WS (MB) | Peak Handles | End Reason |");
        sb.AppendLine("|---------|----------|--------:|---------:|----------:|------------:|-------------:|-------------:|------------|");

        foreach (var s in sessions.OrderBy(s => s.Metadata?.StartedAt))
        {
            if (s.Samples.Count == 0) continue;
            var avgCpu = s.Samples.Average(x => x.ProcessCpuPercent);
            var peakCpu = s.Samples.Max(x => x.ProcessCpuPercent);
            var avgWs = s.Samples.Average(x => x.WorkingSetBytes) / (1024.0 * 1024);
            var peakWs = s.Samples.Max(x => x.WorkingSetBytes) / (1024.0 * 1024);
            var peakHandles = s.Samples.Max(x => x.HandleCount);
            var started = s.Metadata?.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "?";
            var duration = FormatDuration(s.Samples);

            sb.AppendLine(
                $"| {started} | {duration} | {s.Samples.Count} | {avgCpu:F2} | {peakCpu:F2} | {avgWs:F1} | {peakWs:F1} | {peakHandles} | {s.EndReason} |");
        }
        sb.AppendLine();
    }

    private static void AppendOutliers(StringBuilder sb, List<SessionData> sessions)
    {
        sb.AppendLine("## Outliers (one-off spikes)");
        sb.AppendLine();

        var all = sessions.SelectMany(s => s.Samples.Select(x => (s, x))).ToList();
        if (all.Count == 0) { sb.AppendLine("_No data._"); return; }

        var cpuValues = all.Select(t => t.x.ProcessCpuPercent).ToArray();
        var wsValues = all.Select(t => (double)t.x.WorkingSetBytes).ToArray();
        var handleValues = all.Select(t => (double)t.x.HandleCount).ToArray();

        var cpuP99 = Percentile(cpuValues, 0.99);
        var wsP99 = Percentile(wsValues, 0.99);
        var handleP99 = Percentile(handleValues, 0.99);

        sb.AppendLine($"- P99 CPU%: **{cpuP99:F2}** — spikes above this may indicate pathological workloads.");
        sb.AppendLine($"- P99 Working Set: **{wsP99 / (1024 * 1024):F1} MB** — sustained values above this suggest a memory leak.");
        sb.AppendLine($"- P99 Handle Count: **{handleP99:F0}** — trending growth indicates handle leak.");
        sb.AppendLine();

        var cpuSpikes = all
            .Where(t => t.x.ProcessCpuPercent > cpuP99 && t.x.ProcessCpuPercent > 5)
            .OrderByDescending(t => t.x.ProcessCpuPercent)
            .Take(10)
            .ToList();
        if (cpuSpikes.Count > 0)
        {
            sb.AppendLine("**Top CPU spikes:**");
            foreach (var (s, x) in cpuSpikes)
                sb.AppendLine($"- {x.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {x.ProcessCpuPercent:F1}% (session {s.Metadata?.SessionId})");
            sb.AppendLine();
        }
    }

    private static void AppendTrends(StringBuilder sb, List<SessionData> sessions)
    {
        var ordered = sessions
            .Where(s => s.Metadata is not null && s.Samples.Count > 0)
            .OrderBy(s => s.Metadata!.StartedAt)
            .ToList();

        if (ordered.Count < 2)
        {
            sb.AppendLine("## Trends");
            sb.AppendLine();
            sb.AppendLine("_Need at least 2 sessions to compute trends._");
            return;
        }

        sb.AppendLine("## Trends across sessions");
        sb.AppendLine();

        var first = ordered.First();
        var last = ordered.Last();
        var firstAvgCpu = first.Samples.Average(x => x.ProcessCpuPercent);
        var lastAvgCpu = last.Samples.Average(x => x.ProcessCpuPercent);
        var firstAvgWs = first.Samples.Average(x => x.WorkingSetBytes) / (1024.0 * 1024);
        var lastAvgWs = last.Samples.Average(x => x.WorkingSetBytes) / (1024.0 * 1024);
        var firstPeakHandles = first.Samples.Max(x => x.HandleCount);
        var lastPeakHandles = last.Samples.Max(x => x.HandleCount);

        sb.AppendLine($"- First session ({first.Metadata!.StartedAt.ToLocalTime():yyyy-MM-dd}): avg CPU {firstAvgCpu:F2}%, avg WS {firstAvgWs:F1} MB, peak handles {firstPeakHandles}");
        sb.AppendLine($"- Latest session ({last.Metadata!.StartedAt.ToLocalTime():yyyy-MM-dd}): avg CPU {lastAvgCpu:F2}%, avg WS {lastAvgWs:F1} MB, peak handles {lastPeakHandles}");
        sb.AppendLine();

        if (lastAvgWs > firstAvgWs * 1.5)
            sb.AppendLine($"> ⚠ Working set has grown {((lastAvgWs / firstAvgWs) - 1) * 100:F0}% since the first session — investigate for leaks.");
        if (lastPeakHandles > firstPeakHandles * 1.5)
            sb.AppendLine($"> ⚠ Peak handle count has grown {((lastPeakHandles / (double)firstPeakHandles) - 1) * 100:F0}% — handle leak suspected.");
        if (lastAvgCpu > firstAvgCpu * 2)
            sb.AppendLine($"> ⚠ Average CPU has more than doubled since the first session.");
        sb.AppendLine();
    }

    private static void AppendMetricTable(StringBuilder sb, List<PerformanceSample> samples)
    {
        sb.AppendLine("| Metric | Min | Avg | P50 | P95 | P99 | Max |");
        sb.AppendLine("|--------|----:|----:|----:|----:|----:|----:|");
        AppendRow(sb, "Process CPU %", samples.Select(x => x.ProcessCpuPercent), "F2");
        AppendRow(sb, "Working Set (MB)", samples.Select(x => x.WorkingSetBytes / (1024.0 * 1024)), "F1");
        AppendRow(sb, "Private Bytes (MB)", samples.Select(x => x.PrivateBytes / (1024.0 * 1024)), "F1");
        AppendRow(sb, "Handle Count", samples.Select(x => (double)x.HandleCount), "F0");
        AppendRow(sb, "Thread Count", samples.Select(x => (double)x.ThreadCount), "F0");
        AppendRow(sb, "IO Read KB/s", samples.Select(x => x.IoReadBytesPerSec / 1024.0), "F1");
        AppendRow(sb, "IO Write KB/s", samples.Select(x => x.IoWriteBytesPerSec / 1024.0), "F1");
        AppendRow(sb, "System CPU %", samples.Select(x => x.SystemCpuPercent), "F2");
    }

    private static void AppendRow(StringBuilder sb, string label, IEnumerable<double> valuesEnum, string fmt)
    {
        var values = valuesEnum.ToArray();
        if (values.Length == 0)
        {
            sb.AppendLine($"| {label} | - | - | - | - | - | - |");
            return;
        }

        var c = CultureInfo.InvariantCulture;
        sb.AppendLine(
            $"| {label} | {values.Min().ToString(fmt, c)} | {values.Average().ToString(fmt, c)} | " +
            $"{Percentile(values, 0.50).ToString(fmt, c)} | {Percentile(values, 0.95).ToString(fmt, c)} | " +
            $"{Percentile(values, 0.99).ToString(fmt, c)} | {values.Max().ToString(fmt, c)} |");
    }

    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(x => x).ToArray();
        var rank = p * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];
        return sorted[low] + (rank - low) * (sorted[high] - sorted[low]);
    }

    private static string FormatDuration(List<PerformanceSample> samples)
    {
        if (samples.Count < 2) return "-";
        var span = samples[^1].Timestamp - samples[0].Timestamp;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
            : $"{span.Minutes}m{span.Seconds:D2}s";
    }

    private sealed record SessionData(
        string FileName,
        SessionMetadata? Metadata,
        List<PerformanceSample> Samples,
        string EndReason);
}
