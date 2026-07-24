using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

/// <summary>
/// Scans the Windows Application event log for Beet-related hard failures — process
/// crashes (Application Error / Windows Error Reporting) and unhandled .NET exceptions
/// (.NET Runtime). These are the failures that DON'T make it into backup_log.json
/// because the process died, so they're essential to a fair before/after error count.
///
/// TWO THINGS THIS HAS TO GET RIGHT, both learned the hard way (2026-07-23):
///
///   1. WHICH PROCESS CRASHED. This used to test whether the message merely CONTAINED
///      the substring "BeetsBackup" — which is just as true of BeetsBackup.PerfMon.exe,
///      the monitor itself. The monitor crashed repeatedly during the baseline window
///      (a stale collector binary), so the report blamed the app for 20 crashes it did
///      not have. A verification tool counting its OWN crashes against the thing it is
///      verifying is worse than no crash metric at all. We now parse the faulting
///      process out of the message and require it to be Beet's exe exactly.
///
///   2. ONE CRASH IS NOT THREE EVENTS. A single fault typically writes three records:
///      "Application Error" (1000), ".NET Runtime" (1026) and "Windows Error Reporting"
///      (1001), all within a second or two. Counting records counts each crash roughly
///      three times. We collapse a burst into one INCIDENT.
///
/// Best-effort: the Application log is normally readable by a standard user, but a
/// locked-down machine or a cleared log must degrade to "no entries", never crash.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogIngestor
{
    // Event-log sources that report a process-level fault.
    private static readonly string[] FaultSources =
        { "Application Error", ".NET Runtime", "Windows Error Reporting", ".NET Runtime Error" };

    /// <summary>The ONLY process whose crashes count as Beet crashing. Explicitly not
    /// BeetsBackup.PerfMon.exe or BeetsBackupLauncher.exe — those are separate binaries
    /// with separate failure modes, and attributing them to Beet corrupts the verdict.</summary>
    private const string BeetProcess = "BeetsBackup.exe";

    /// <summary>Records this far apart or closer are treated as the same crash. The three
    /// records Windows writes for one fault land within a second or two; 60s is generous
    /// enough to absorb a slow WER upload without merging two genuinely separate crashes
    /// (Beet is not restarted and re-crashed inside a minute in any observed data).</summary>
    private static readonly TimeSpan IncidentWindow = TimeSpan.FromSeconds(60);

    // How each fault source names the process that died. First match wins.
    //   Application Error       -> "Faulting application name: BeetsBackup.exe, version: ..."
    //   .NET Runtime            -> "Application: BeetsBackup.exe"
    //   Windows Error Reporting -> "P1: BeetsBackup.exe"
    private static readonly Regex[] ProcessPatterns =
    {
        new(@"Faulting application name:\s*([^\s,]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*Application:\s*(\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"^\s*P1:\s*(\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),
    };

    /// <summary>
    /// Reads Beet crash INCIDENTS at or after <paramref name="since"/>, one entry per
    /// crash rather than one per event-log record.
    /// </summary>
    public IReadOnlyList<BeetEventLogEntry> Load(DateTime since)
    {
        var matched = new List<BeetEventLogEntry>();
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
                if (!FaultSources.Any(s => source.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!IsBeetCrash(message)) continue;

                matched.Add(new BeetEventLogEntry(
                    entry.TimeGenerated,
                    entry.EntryType.ToString(),
                    source,
                    Truncate(message, 500)) { ProcessName = BeetProcess });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventLog] Could not read Application log: {ex.Message}");
        }
        return CollapseToIncidents(matched);
    }

    /// <summary>
    /// True when this fault record is about Beet's own executable.
    ///
    /// The named process wins whenever we can parse one — that is the authoritative
    /// answer and it is what excludes the PerfMon collector. When no pattern matches
    /// (an unfamiliar message layout), we fall back to requiring the exact exe token
    /// AND the absence of ".PerfMon", so an unparseable record about the app is still
    /// counted while an unparseable one about the monitor is not.
    /// </summary>
    private static bool IsBeetCrash(string message)
    {
        var named = ExtractProcessName(message);
        if (named is not null)
            return named.Equals(BeetProcess, StringComparison.OrdinalIgnoreCase);

        return message.Contains(BeetProcess, StringComparison.OrdinalIgnoreCase)
               && !message.Contains("BeetsBackup.PerfMon", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractProcessName(string message)
    {
        foreach (var rx in ProcessPatterns)
        {
            var m = rx.Match(message);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Collapses the burst of records Windows writes for a single fault into one entry.
    /// Records are grouped by proximity in time; the representative kept is the richest
    /// source available, because "Application Error" carries the faulting MODULE (e.g.
    /// KERNELBASE.dll) that makes a crash diagnosable, while WER carries only a bucket id.
    /// </summary>
    private static List<BeetEventLogEntry> CollapseToIncidents(List<BeetEventLogEntry> records)
    {
        var incidents = new List<BeetEventLogEntry>();
        if (records.Count == 0) return incidents;

        foreach (var group in GroupByProximity(records.OrderBy(r => r.Timestamp).ToList()))
            incidents.Add(group.OrderBy(SourceRank).ThenBy(r => r.Timestamp).First());

        return incidents;
    }

    private static IEnumerable<List<BeetEventLogEntry>> GroupByProximity(List<BeetEventLogEntry> ordered)
    {
        var current = new List<BeetEventLogEntry> { ordered[0] };
        for (int i = 1; i < ordered.Count; i++)
        {
            // Compare against the incident's FIRST record, not the previous one, so a long
            // trail of near-misses cannot chain into one arbitrarily long "incident".
            if (ordered[i].Timestamp - current[0].Timestamp <= IncidentWindow)
            {
                current.Add(ordered[i]);
            }
            else
            {
                yield return current;
                current = new List<BeetEventLogEntry> { ordered[i] };
            }
        }
        yield return current;
    }

    private static int SourceRank(BeetEventLogEntry e)
        => e.Source.Contains("Application Error", StringComparison.OrdinalIgnoreCase) ? 0
         : e.Source.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase) ? 1
         : 2;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s.Replace("\r", " ").Replace("\n", " ")
                           : s[..max].Replace("\r", " ").Replace("\n", " ") + "…";
}
