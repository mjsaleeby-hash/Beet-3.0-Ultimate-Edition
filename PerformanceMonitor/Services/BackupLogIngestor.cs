using System.Text.Json;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

/// <summary>
/// Reads Beet's backup_log.json — the authoritative record of each backup's OUTCOME,
/// including the error detail telemetry doesn't carry (locked files, disk-full,
/// checksum mismatches, per-file errors). Entries carry no build tag, so the caller
/// assigns one by correlating each entry's timestamp to the build-window timeline.
///
/// Matches Beet's serialization exactly: default System.Text.Json (PascalCase names,
/// enums as numeric indices). BackupStatus: 0=Scheduled 1=Running 2=Complete 3=Failed 4=Skipped.
/// </summary>
public sealed class BackupLogIngestor
{
    private static readonly string[] StatusNames =
        { "Scheduled", "Running", "Complete", "Failed", "Skipped" };

    private readonly string _logPath;

    public BackupLogIngestor(string? logPath = null)
        => _logPath = logPath ?? BeetDataPaths.BackupLogPath;

    public IReadOnlyList<BackupOutcome> Load()
    {
        var outcomes = new List<BackupOutcome>();
        if (!File.Exists(_logPath)) return outcomes;

        string json;
        try
        {
            // Shared read — Beet (or a headless run) may be writing the file.
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            json = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BackupLog] Could not read {_logPath}: {ex.Message}");
            return outcomes;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outcomes;

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                try { outcomes.Add(ParseEntry(e)); }
                catch { /* skip a malformed entry rather than fail the whole load */ }
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[BackupLog] Malformed JSON in {_logPath}: {ex.Message}");
        }
        return outcomes;
    }

    private static BackupOutcome ParseEntry(JsonElement e)
    {
        int statusIdx = GetInt(e, "Status");
        string status = statusIdx >= 0 && statusIdx < StatusNames.Length ? StatusNames[statusIdx] : "Unknown";

        DateTime ts = e.TryGetProperty("Timestamp", out var t) && t.ValueKind == JsonValueKind.String
            && DateTime.TryParse(t.GetString(), out var parsed) ? parsed : DateTime.MinValue;

        int fileErrors = e.TryGetProperty("FileErrors", out var fe) && fe.ValueKind == JsonValueKind.Array
            ? fe.GetArrayLength() : 0;

        return new BackupOutcome
        {
            Timestamp = ts,
            JobName = e.TryGetProperty("JobName", out var jn) ? jn.GetString() ?? "" : "",
            Status = status,
            FilesCopied = GetInt(e, "FilesCopied"),
            FilesSkipped = GetInt(e, "FilesSkipped"),
            FilesFailed = GetInt(e, "FilesFailed"),
            FilesLocked = GetInt(e, "FilesLocked"),
            DirectoriesFailed = GetInt(e, "DirectoriesFailed"),
            DiskFullErrors = GetInt(e, "DiskFullErrors"),
            ChecksumMismatches = GetInt(e, "ChecksumMismatches"),
            BytesTransferred = GetLong(e, "BytesTransferred"),
            FileErrorCount = fileErrors,
        };
    }

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
