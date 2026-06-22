using System.Text.Json;

namespace BeetsBackup.PerfMon.Services;

/// <summary>
/// Locations of the data Beet writes about itself, plus a resolver for the build
/// identity that buckets a monitoring session as baseline vs candidate. Centralised
/// here so the build-tag resolver and the Stream-3 ingestors agree on where to look.
/// </summary>
public static class BeetDataPaths
{
    /// <summary>%LocalAppData%\Beet's Backup — Beet's per-user data root.</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beet's Backup");

    /// <summary>The append-only backup history Beet maintains.</summary>
    public static string BackupLogPath => Path.Combine(DataRoot, "backup_log.json");

    /// <summary>Folder of BeetTelemetry JSONL files (one per day).</summary>
    public static string TelemetryDir => Path.Combine(DataRoot, "telemetry");
}

/// <summary>The build identity Beet stamps on its telemetry: which cohort, version, commit.</summary>
public sealed record BeetBuildIdentity(string BuildTag, string Version, string GitCommit)
{
    public static readonly BeetBuildIdentity Unknown = new("unknown", "unknown", string.Empty);
}

/// <summary>
/// Resolves the build identity for a monitoring session. Prefers the authoritative value
/// Beet itself wrote (the most recent AppStarted telemetry record), and falls back to
/// deriving a tag from the exe's version string when telemetry isn't present yet.
/// </summary>
public static class BuildIdentityResolver
{
    /// <summary>Reads the newest AppStarted telemetry record, if any.</summary>
    public static BeetBuildIdentity FromTelemetry()
    {
        try
        {
            var dir = BeetDataPaths.TelemetryDir;
            if (!Directory.Exists(dir)) return BeetBuildIdentity.Unknown;

            // Newest file first; within it, the LAST AppStarted wins (most recent launch).
            foreach (var file in Directory.EnumerateFiles(dir, "telemetry_*.jsonl")
                         .OrderByDescending(f => f))
            {
                BeetBuildIdentity? found = ScanFileForAppStarted(file);
                if (found is not null) return found;
            }
        }
        catch { /* best-effort */ }
        return BeetBuildIdentity.Unknown;
    }

    /// <summary>Derives a coarse tag from a version string (e.g. "4.0.0.0" -> candidate).</summary>
    public static string DeriveTagFromVersion(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            var firstDot = version.IndexOf('.');
            var majorText = firstDot > 0 ? version[..firstDot] : version;
            if (int.TryParse(majorText, out var major))
                return major >= 4 ? "4.0-candidate" : "3.0-baseline";
        }
        return "unknown";
    }

    /// <summary>Resolves identity: telemetry if available, else version-derived.</summary>
    public static BeetBuildIdentity Resolve(string? exeVersion)
    {
        var fromTelemetry = FromTelemetry();
        if (fromTelemetry.BuildTag != "unknown") return fromTelemetry;
        return new BeetBuildIdentity(DeriveTagFromVersion(exeVersion), exeVersion ?? "unknown", string.Empty);
    }

    private static BeetBuildIdentity? ScanFileForAppStarted(string file)
    {
        BeetBuildIdentity? latest = null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains("AppStarted")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "AppStarted") continue;
                    var tag = root.TryGetProperty("buildTag", out var bt) ? bt.GetString() ?? "unknown" : "unknown";
                    var data = root.TryGetProperty("data", out var d) ? d : default;
                    var version = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("version", out var v)
                        ? v.GetString() ?? "unknown" : "unknown";
                    var commit = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("gitCommit", out var g)
                        ? g.GetString() ?? string.Empty : string.Empty;
                    latest = new BeetBuildIdentity(tag, version, commit);
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* file unreadable */ }
        return latest;
    }
}
