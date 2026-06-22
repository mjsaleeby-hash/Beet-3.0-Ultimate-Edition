using System.Diagnostics;
using System.Runtime.Versioning;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

/// <summary>
/// Scans the Windows Application event log for Beet-related hard failures — process
/// crashes (Application Error / Windows Error Reporting) and unhandled .NET exceptions
/// (.NET Runtime). These are the failures that DON'T make it into backup_log.json
/// because the process died, so they're essential to a fair before/after error count.
///
/// Best-effort: the Application log is normally readable by a standard user, but a
/// locked-down machine or a cleared log must degrade to "no entries", never crash.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogIngestor
{
    // Event-log sources that report a process-level fault. We additionally require the
    // message to mention Beet so we don't pick up unrelated apps' crashes.
    private static readonly string[] FaultSources =
        { "Application Error", ".NET Runtime", "Windows Error Reporting", ".NET Runtime Error" };

    private const string BeetMarker = "BeetsBackup";

    /// <summary>Reads Beet fault entries at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<BeetEventLogEntry> Load(DateTime since)
    {
        var results = new List<BeetEventLogEntry>();
        try
        {
            using var log = new EventLog("Application");
            // Iterate newest-first and stop once we pass the window — the log is ordered
            // oldest-to-newest, so walk it backwards.
            var entries = log.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                EventLogEntry entry;
                try { entry = entries[i]; }
                catch { continue; }

                if (entry.TimeGenerated < since) break; // older than the window — done

                if (entry.EntryType is not (EventLogEntryType.Error or EventLogEntryType.Warning))
                    continue;

                var source = entry.Source ?? "";
                var message = entry.Message ?? "";
                bool sourceMatches = FaultSources.Any(s => source.Contains(s, StringComparison.OrdinalIgnoreCase));
                bool mentionsBeet = message.Contains(BeetMarker, StringComparison.OrdinalIgnoreCase)
                                    || source.Contains(BeetMarker, StringComparison.OrdinalIgnoreCase);

                if ((sourceMatches && mentionsBeet) || (mentionsBeet && entry.EntryType == EventLogEntryType.Error))
                {
                    results.Add(new BeetEventLogEntry(
                        entry.TimeGenerated,
                        entry.EntryType.ToString(),
                        source,
                        Truncate(message, 500)));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventLog] Could not read Application log: {ex.Message}");
        }
        return results;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s.Replace("\r", " ").Replace("\n", " ")
                           : s[..max].Replace("\r", " ").Replace("\n", " ") + "…";
}
