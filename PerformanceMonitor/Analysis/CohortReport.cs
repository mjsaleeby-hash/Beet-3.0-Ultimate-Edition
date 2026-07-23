using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using BeetsBackup.PerfMon.Models;
using BeetsBackup.PerfMon.Services;

namespace BeetsBackup.PerfMon.Analysis;

/// <summary>
/// The headline output of the verification system: a build-tagged A/B comparison that
/// answers "did Beet improve, and by how much?". It groups every data source by build
/// cohort (3.0-baseline vs 4.0-candidate) and reports, per metric, the baseline vs
/// candidate distribution, the delta and % change, and a confidence note so sampling
/// noise is not mistaken for a win.
///
/// Sources joined here:
///   - session_*.jsonl  : resource samples (CPU/memory/handles) + system context
///   - telemetry JSONL   : per-operation timings + backup throughput (build-tagged)
///   - backup_log.json   : per-run error detail (tagged by build-window timeline)
///   - Application log    : Beet process crashes (tagged by build-window timeline)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CohortReport
{
    private const int MinTimingSamples = 15; // below this, a timing delta is "low confidence"

    private readonly string _logDirectory;

    public CohortReport(string logDirectory) => _logDirectory = logDirectory;

    private enum Dir { LowerIsBetter, HigherIsBetter, Neutral }

    public string Build()
    {
        var allSessions = SessionLoader.Load(_logDirectory);
        var allTelemetry = new TelemetryIngestor().Load();
        var outcomes = new BackupLogIngestor().Load();

        // Build the timeline and assign tags from the UNFILTERED data. Tag assignment
        // answers "which build was actually running when this backup/crash happened?",
        // which is a question about reality, not about which data we intend to report
        // on. Filtering first would delete the very markers that resolve the excluded
        // period, and records inside it would then silently inherit the previous
        // cohort's tag — reintroducing the exact contamination we are removing.
        var timeline = BuildTimeline(allTelemetry, allSessions);
        var since = timeline.Count > 0 ? timeline[0].LocalStart.AddDays(-1) : DateTime.Now.AddDays(-30);
        var crashes = SafeLoadCrashes(since);
        AssignBuildTags(outcomes, crashes, timeline);

        // NOW apply each cohort's valid-data window, uniformly, to every source. Doing
        // it once here (rather than per section) is what guarantees the resource,
        // latency, throughput, error and leak tables all describe the SAME days.
        var sessions = allSessions
            .Where(s => CohortWindows.Includes(s.BuildTag, s.StartedAt.ToLocalTime().DateTime)).ToList();
        var telemetry = allTelemetry
            .Where(r => CohortWindows.Includes(r.BuildTag, r.Timestamp.ToLocalTime().DateTime)).ToList();
        var windowedOutcomes = outcomes.Where(o => CohortWindows.Includes(o.BuildTag, o.Timestamp)).ToList();
        var windowedCrashes = crashes.Where(x => CohortWindows.Includes(x.BuildTag, x.Timestamp)).ToList();
        int droppedEvents = allTelemetry.Count - telemetry.Count;
        int droppedRuns = outcomes.Count - windowedOutcomes.Count;

        var tags = DetermineCohorts(sessions, telemetry);
        var sb = new StringBuilder();
        sb.AppendLine("# Beet's Backup — 3.0 → 4.0 Cohort Comparison");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Sessions: {sessions.Count}  |  Telemetry events: {telemetry.Count}  |  "
                      + $"Backup runs: {windowedOutcomes.Count}  |  Crashes scanned since: {since:yyyy-MM-dd}");
        if (droppedEvents > 0 || droppedRuns > 0)
        {
            // State the exclusion out loud. A verdict that quietly drops a quarter of the
            // data is indistinguishable from one computed over all of it, and the reader
            // has no way to audit a filter they cannot see.
            sb.AppendLine();
            sb.AppendLine($"> **Excluded as out-of-window:** {droppedEvents} telemetry events, "
                          + $"{droppedRuns} backup runs. Data is only counted inside its cohort's "
                          + "window (see below); records outside one belong to a build whose tag "
                          + "did not match the code that was actually deployed.");
        }
        sb.AppendLine();

        if (tags.Baseline is null)
        {
            sb.AppendLine("_No build-tagged data found yet. Run the baseline build with telemetry on._");
            return sb.ToString();
        }

        sb.AppendLine($"**Baseline:** `{tags.Baseline}` ({CohortWindows.Describe(tags.Baseline)})   "
                      + $"**Candidate:** `{tags.Candidate ?? "(not measured yet)"}` "
                      + $"({CohortWindows.Describe(tags.Candidate)})");
        sb.AppendLine();

        AppendResourceSection(sb, sessions, tags);
        AppendTimingSection(sb, telemetry, tags);
        AppendThroughputSection(sb, telemetry, tags);
        AppendFatProofSection(sb, telemetry, tags);
        AppendErrorSection(sb, windowedOutcomes, windowedCrashes, tags);
        AppendLeakSection(sb, sessions, tags);
        AppendContentionNote(sb, sessions, tags);

        sb.AppendLine("---");
        sb.AppendLine("Confidence: \"meaningful\" = adequate samples and >5% change; \"negligible\" = "
                      + "<5% change; \"low\" = too few samples to conclude. A regression whose samples "
                      + "coincide with high external disk/CPU load (see contention note) should be re-run.");
        return sb.ToString();
    }

    // ---- Sections -----------------------------------------------------------------

    private void AppendResourceSection(StringBuilder sb, IReadOnlyList<LoadedSession> sessions, Cohorts t)
    {
        sb.AppendLine("## Resource usage (per-second samples)");
        sb.AppendLine();
        var b = SamplesFor(sessions, t.Baseline!);
        var c = t.Candidate is null ? new List<PerformanceSample>() : SamplesFor(sessions, t.Candidate);

        StartTable(sb);
        Row(sb, "Process CPU %", b.Select(x => x.ProcessCpuPercent), c.Select(x => x.ProcessCpuPercent), Dir.LowerIsBetter, "F2");
        Row(sb, "Working Set (MB)", b.Select(x => x.WorkingSetBytes / 1048576.0), c.Select(x => x.WorkingSetBytes / 1048576.0), Dir.LowerIsBetter, "F1");
        Row(sb, "Private Bytes (MB)", b.Select(x => x.PrivateBytes / 1048576.0), c.Select(x => x.PrivateBytes / 1048576.0), Dir.LowerIsBetter, "F1");
        Row(sb, "Handle Count", b.Select(x => (double)x.HandleCount), c.Select(x => (double)x.HandleCount), Dir.LowerIsBetter, "F0");
        Row(sb, "Thread Count", b.Select(x => (double)x.ThreadCount), c.Select(x => (double)x.ThreadCount), Dir.LowerIsBetter, "F0");
        Row(sb, "IO Write KB/s", b.Select(x => x.IoWriteBytesPerSec / 1024.0), c.Select(x => x.IoWriteBytesPerSec / 1024.0), Dir.Neutral, "F1");
        sb.AppendLine();
    }

    private void AppendTimingSection(StringBuilder sb, IReadOnlyList<TelemetryRecord> tel, Cohorts t)
    {
        sb.AppendLine("## Operation latency (telemetry)");
        sb.AppendLine();
        StartTable(sb);
        AddTimingRow(sb, tel, t, "NavigationCompleted", "elapsedMs", "Navigation (ms)");
        AddTimingRow(sb, tel, t, "SearchCompleted", "elapsedMs", "Deep search (ms)");
        AddTimingRow(sb, tel, t, "FolderSizeCompleted", "elapsedMs", "Folder sizing (ms)");
        AddTimingRow(sb, tel, t, "FilterRefreshed", "elapsedMs", "Filter refresh (ms)");
        sb.AppendLine();
    }

    private void AddTimingRow(StringBuilder sb, IReadOnlyList<TelemetryRecord> tel, Cohorts t,
        string evt, string field, string label)
    {
        var b = tel.Where(r => r.EventName == evt && r.BuildTag == t.Baseline).Select(r => r.Number(field)).Where(v => !double.IsNaN(v));
        var c = t.Candidate is null ? Enumerable.Empty<double>()
            : tel.Where(r => r.EventName == evt && r.BuildTag == t.Candidate).Select(r => r.Number(field)).Where(v => !double.IsNaN(v));
        Row(sb, label, b, c, Dir.LowerIsBetter, "F1");
    }

    private void AppendThroughputSection(StringBuilder sb, IReadOnlyList<TelemetryRecord> tel, Cohorts t)
    {
        sb.AppendLine("## Backup throughput (telemetry)");
        sb.AppendLine();
        StartTable(sb);
        var b = ThroughputMbps(tel, t.Baseline!);
        var c = t.Candidate is null ? Enumerable.Empty<double>() : ThroughputMbps(tel, t.Candidate);
        Row(sb, "Throughput (MB/s)", b, c, Dir.HigherIsBetter, "F1");

        var bd = tel.Where(r => r.EventName == "BackupCompleted" && r.BuildTag == t.Baseline).Select(r => r.Number("durationMs")).Where(v => !double.IsNaN(v) && v > 0);
        var cd = t.Candidate is null ? Enumerable.Empty<double>()
            : tel.Where(r => r.EventName == "BackupCompleted" && r.BuildTag == t.Candidate).Select(r => r.Number("durationMs")).Where(v => !double.IsNaN(v) && v > 0);
        Row(sb, "Backup duration (ms)", bd, cd, Dir.LowerIsBetter, "F0");
        sb.AppendLine();
    }

    private static IEnumerable<double> ThroughputMbps(IReadOnlyList<TelemetryRecord> tel, string tag)
        => tel.Where(r => r.EventName == "BackupCompleted" && r.BuildTag == tag)
              .Select(r => (bytes: r.Number("bytesTransferred"), ms: r.Number("durationMs")))
              .Where(x => x.bytes > 0 && x.ms > 0)
              .Select(x => x.bytes / 1048576.0 / (x.ms / 1000.0));

    private void AppendFatProofSection(StringBuilder sb, IReadOnlyList<TelemetryRecord> tel, Cohorts t)
    {
        sb.AppendLine("## FAT/exFAT incremental re-copy proof (Wave 1.1 headline)");
        sb.AppendLine();
        sb.AppendLine("Files copied on backups whose destination is a FAT/exFAT volume. The bug re-copies "
                      + "unchanged files every run; the fix drops this toward zero on a no-change re-run.");
        sb.AppendLine();

        bool IsFat(TelemetryRecord r)
        {
            var fs = r.Text("destFilesystem");
            return fs.Contains("FAT", StringComparison.OrdinalIgnoreCase); // FAT32 / exFAT
        }
        var b = tel.Where(r => r.EventName == "BackupCompleted" && r.BuildTag == t.Baseline && IsFat(r)).Select(r => r.Number("filesCopied")).Where(v => !double.IsNaN(v));
        var c = t.Candidate is null ? Enumerable.Empty<double>()
            : tel.Where(r => r.EventName == "BackupCompleted" && r.BuildTag == t.Candidate && IsFat(r)).Select(r => r.Number("filesCopied")).Where(v => !double.IsNaN(v));

        var bl = b.ToList(); var cl = c.ToList();
        if (bl.Count == 0 && cl.Count == 0)
        {
            sb.AppendLine("_No FAT/exFAT backups recorded. Run scenario S1 against a FAT32/exFAT USB to populate this._");
            sb.AppendLine();
            return;
        }
        StartTable(sb);
        Row(sb, "Files re-copied (FAT/exFAT)", bl, cl, Dir.LowerIsBetter, "F0");
        sb.AppendLine();
    }

    private void AppendErrorSection(StringBuilder sb, IReadOnlyList<BackupOutcome> outcomes,
        IReadOnlyList<BeetEventLogEntry> crashes, Cohorts t)
    {
        sb.AppendLine("## Errors (before vs after)");
        sb.AppendLine();

        var bRuns = outcomes.Where(o => o.BuildTag == t.Baseline && o.Status is "Complete" or "Failed").ToList();
        var cRuns = t.Candidate is null ? new List<BackupOutcome>()
            : outcomes.Where(o => o.BuildTag == t.Candidate && o.Status is "Complete" or "Failed").ToList();

        StartTable(sb);
        Row(sb, "Errors per run", bRuns.Select(o => (double)o.TotalErrors), cRuns.Select(o => (double)o.TotalErrors), Dir.LowerIsBetter, "F2");
        Row(sb, "Files failed per run", bRuns.Select(o => (double)o.FilesFailed), cRuns.Select(o => (double)o.FilesFailed), Dir.LowerIsBetter, "F2");
        Row(sb, "Files locked per run", bRuns.Select(o => (double)o.FilesLocked), cRuns.Select(o => (double)o.FilesLocked), Dir.LowerIsBetter, "F2");
        Row(sb, "Checksum mismatch/run", bRuns.Select(o => (double)o.ChecksumMismatches), cRuns.Select(o => (double)o.ChecksumMismatches), Dir.LowerIsBetter, "F2");
        sb.AppendLine();

        int bFail = bRuns.Count(o => o.Status == "Failed");
        int cFail = cRuns.Count(o => o.Status == "Failed");
        int bCrash = crashes.Count(x => x.BuildTag == t.Baseline);
        int cCrash = t.Candidate is null ? 0 : crashes.Count(x => x.BuildTag == t.Candidate);
        sb.AppendLine($"- Failed runs: baseline **{bFail}** / {bRuns.Count}, candidate **{cFail}** / {cRuns.Count}");
        sb.AppendLine($"- Process crashes (Application event log): baseline **{bCrash}**, candidate **{cCrash}**");
        sb.AppendLine();
    }

    private void AppendLeakSection(StringBuilder sb, IReadOnlyList<LoadedSession> sessions, Cohorts t)
    {
        sb.AppendLine("## Memory/handle stability (leak check within each cohort)");
        sb.AppendLine();
        AppendLeakLine(sb, sessions, t.Baseline!, "Baseline");
        if (t.Candidate is not null) AppendLeakLine(sb, sessions, t.Candidate, "Candidate");
        sb.AppendLine();
    }

    private static void AppendLeakLine(StringBuilder sb, IReadOnlyList<LoadedSession> sessions, string tag, string label)
    {
        var ordered = sessions.Where(s => s.BuildTag == tag && s.Samples.Count > 0).OrderBy(s => s.StartedAt).ToList();
        if (ordered.Count < 2) { sb.AppendLine($"- {label}: need ≥2 sessions for a trend ({ordered.Count} found)."); return; }
        double firstWs = ordered.First().Samples.Average(x => x.WorkingSetBytes) / 1048576.0;
        double lastWs = ordered.Last().Samples.Average(x => x.WorkingSetBytes) / 1048576.0;
        int firstHandles = ordered.First().Samples.Max(x => x.HandleCount);
        int lastHandles = ordered.Last().Samples.Max(x => x.HandleCount);
        string flag = lastWs > firstWs * 1.5 ? " ⚠ possible WS growth" : (lastHandles > firstHandles * 1.5 ? " ⚠ possible handle growth" : " ✓ stable");
        sb.AppendLine($"- {label}: WS {firstWs:F0}→{lastWs:F0} MB, peak handles {firstHandles}→{lastHandles}.{flag}");
    }

    private void AppendContentionNote(StringBuilder sb, IReadOnlyList<LoadedSession> sessions, Cohorts t)
    {
        sb.AppendLine("## External contention (attribution check)");
        sb.AppendLine();
        sb.AppendLine("Share of samples where the machine was under heavy EXTERNAL load (another process "
                      + "dominating CPU/disk). A candidate that looks slower mostly during these samples is "
                      + "likely a victim of contention, not a regression.");
        sb.AppendLine();
        AppendContentionLine(sb, sessions, t.Baseline!, "Baseline");
        if (t.Candidate is not null) AppendContentionLine(sb, sessions, t.Candidate, "Candidate");
        sb.AppendLine();
    }

    private static void AppendContentionLine(StringBuilder sb, IReadOnlyList<LoadedSession> sessions, string tag, string label)
    {
        var samples = SamplesForStatic(sessions, tag).Where(s => s.Context is not null).ToList();
        if (samples.Count == 0) { sb.AppendLine($"- {label}: no system-context samples."); return; }
        int busyDisk = samples.Count(s => s.Context!.DiskQueueLength > 2.0);
        int extCpu = samples.Count(s => s.Context!.TopProcesses.Any(p => p.CpuPercent > 25));
        sb.AppendLine($"- {label}: {Pct(busyDisk, samples.Count)} samples with disk queue >2; "
                      + $"{Pct(extCpu, samples.Count)} with an external process >25% CPU.");
    }

    // ---- Helpers ------------------------------------------------------------------

    private static string Pct(int n, int total) => total == 0 ? "0%" : $"{100.0 * n / total:F0}%";

    private static IReadOnlyList<PerformanceSample> SamplesFor(IReadOnlyList<LoadedSession> sessions, string tag)
        => SamplesForStatic(sessions, tag);

    private static List<PerformanceSample> SamplesForStatic(IReadOnlyList<LoadedSession> sessions, string tag)
        => sessions.Where(s => s.BuildTag == tag).SelectMany(s => s.Samples).ToList();

    private static void StartTable(StringBuilder sb)
    {
        sb.AppendLine("| Metric | Base median | Cand median | Base p95 | Cand p95 | Δ% (median) | Better? | Confidence |");
        sb.AppendLine("|--------|------------:|------------:|---------:|---------:|------------:|:-------:|------------|");
    }

    private static void Row(StringBuilder sb, string label, IEnumerable<double> baseVals,
        IEnumerable<double> candVals, Dir dir, string fmt)
    {
        var b = baseVals.ToArray();
        var c = candVals.ToArray();
        var ci = CultureInfo.InvariantCulture;
        if (b.Length == 0) { sb.AppendLine($"| {label} | _no data_ | | | | | | |"); return; }

        double bMed = Stats.Percentile(b, 0.50), bP95 = Stats.Percentile(b, 0.95);
        if (c.Length == 0)
        {
            sb.AppendLine($"| {label} | {bMed.ToString(fmt, ci)} | _pending_ | {bP95.ToString(fmt, ci)} | | | | n={b.Length} |");
            return;
        }
        double cMed = Stats.Percentile(c, 0.50), cP95 = Stats.Percentile(c, 0.95);
        double pct = bMed == 0 ? double.NaN : (cMed - bMed) / Math.Abs(bMed) * 100.0;

        string better = dir switch
        {
            Dir.LowerIsBetter => cMed < bMed ? "✓" : (cMed > bMed ? "✗" : "="),
            Dir.HigherIsBetter => cMed > bMed ? "✓" : (cMed < bMed ? "✗" : "="),
            _ => "·",
        };
        string pctText = double.IsNaN(pct) ? "n/a" : $"{(pct >= 0 ? "+" : "")}{pct.ToString("F1", ci)}%";
        sb.AppendLine($"| {label} | {bMed.ToString(fmt, ci)} | {cMed.ToString(fmt, ci)} | "
            + $"{bP95.ToString(fmt, ci)} | {cP95.ToString(fmt, ci)} | {pctText} | {better} | {Confidence(b.Length, c.Length, pct)} |");
    }

    private static string Confidence(int nB, int nC, double pct)
    {
        if (nB < MinTimingSamples || nC < MinTimingSamples) return $"low (n={nB}/{nC})";
        if (double.IsNaN(pct)) return "n/a";
        return Math.Abs(pct) < 5 ? "negligible" : "meaningful";
    }

    // ---- Cohort + timeline plumbing ----------------------------------------------

    private sealed record Cohorts(string? Baseline, string? Candidate);

    private static Cohorts DetermineCohorts(IReadOnlyList<LoadedSession> sessions, IReadOnlyList<TelemetryRecord> tel)
    {
        // Collect distinct, real tags with their first-seen time.
        var firstSeen = new Dictionary<string, DateTimeOffset>();
        void See(string tag, DateTimeOffset at)
        {
            if (string.IsNullOrEmpty(tag) || tag == "unknown") return;
            if (!firstSeen.TryGetValue(tag, out var existing) || at < existing) firstSeen[tag] = at;
        }
        foreach (var s in sessions) See(s.BuildTag, s.StartedAt);
        foreach (var r in tel) See(r.BuildTag, r.Timestamp);

        if (firstSeen.Count == 0) return new Cohorts(null, null);

        string? baseline = firstSeen.Keys.FirstOrDefault(k => k.Contains("baseline", StringComparison.OrdinalIgnoreCase));
        string? candidate = firstSeen.Keys.FirstOrDefault(k => k.Contains("candidate", StringComparison.OrdinalIgnoreCase));

        if (baseline is null || candidate is null)
        {
            // Fall back to earliest = baseline, latest = candidate by first-seen time.
            var ordered = firstSeen.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
            baseline ??= ordered.First();
            candidate ??= ordered.Count > 1 ? ordered.Last() : null;
            if (candidate == baseline) candidate = null;
        }
        return new Cohorts(baseline, candidate);
    }

    private static List<(DateTime LocalStart, string Tag)> BuildTimeline(
        IReadOnlyList<TelemetryRecord> tel, IReadOnlyList<LoadedSession> sessions)
    {
        var markers = new List<(DateTime LocalStart, string Tag)>();
        foreach (var r in tel.Where(r => r.EventName == "AppStarted" && r.BuildTag != "unknown"))
            markers.Add((r.Timestamp.ToLocalTime().DateTime, r.BuildTag));
        // Fall back to session starts so outcomes can still be bucketed if telemetry was off.
        foreach (var s in sessions.Where(s => s.BuildTag != "unknown"))
            markers.Add((s.StartedAt.ToLocalTime().DateTime, s.BuildTag));
        return markers.OrderBy(m => m.LocalStart).ToList();
    }

    private static void AssignBuildTags(IReadOnlyList<BackupOutcome> outcomes,
        IReadOnlyList<BeetEventLogEntry> crashes, List<(DateTime LocalStart, string Tag)> timeline)
    {
        string Resolve(DateTime ts)
        {
            string tag = "unknown";
            foreach (var m in timeline)
            {
                if (m.LocalStart <= ts) tag = m.Tag;
                else break;
            }
            return tag;
        }
        foreach (var o in outcomes) o.BuildTag = Resolve(o.Timestamp);
        foreach (var c in crashes) c.BuildTag = Resolve(c.Timestamp);
    }

    private IReadOnlyList<BeetEventLogEntry> SafeLoadCrashes(DateTime since)
    {
        try { return new EventLogIngestor().Load(since); }
        catch { return Array.Empty<BeetEventLogEntry>(); }
    }
}
