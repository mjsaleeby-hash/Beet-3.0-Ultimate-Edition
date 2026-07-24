namespace BeetsBackup.PerfMon.Analysis;

/// <summary>
/// The VALID-DATA WINDOW for each build cohort.
///
/// WHY THIS FILE EXISTS (read before changing any date below):
///   The cohort report used to bucket data by the <c>BuildTag</c> stamped on each
///   record and NOTHING ELSE. That is correct only if a build's tag was flipped at
///   the exact moment that build started running. Ours was not, once:
///
///     2026-07-08 11:34  Wave 1 was DEPLOYED, but BuildTag was still "3.0-baseline".
///     2026-07-16 13:26  The tag was finally flipped to "4.0-candidate" (commit e75accc).
///
///   So every record from 2026-07-08 to 2026-07-15 is WAVE-1 CODE WEARING A BASELINE
///   LABEL. Tag-only bucketing silently folded ~21 backups of already-improved code
///   into the "before" side of the comparison, which flatters the baseline and
///   understates the candidate. The exclusion was written down in the project notes
///   from the start — but a decision that lives only in notes is not applied to
///   anything. This file is that decision, encoded where it actually runs.
///
/// HOW IT WORKS:
///   Start is INCLUSIVE, End is EXCLUSIVE, both in LOCAL time (the windows are
///   human day boundaries, and every consumer converts to local before asking).
///   A null End means "still open — include everything up to now". A tag with no
///   entry here is UNWINDOWED: all of its records are included. That default matters
///   — a future cohort keeps working without touching this file, and the only tags
///   that need an entry are ones whose deploy and tag-flip drifted apart.
///
/// IF YOU FLIP A TAG AT DEPLOY TIME, YOU NEVER NEED AN ENTRY HERE. The real fix for
/// this class of bug is the discipline recorded in the plan: the tag flip belongs in
/// the same action as the deploy. These windows are the cleanup for the one time it
/// was not.
/// </summary>
public static class CohortWindows
{
    /// <summary>
    /// Per-tag valid windows. Edit here when a cohort opens or closes; nothing else
    /// in the report needs to change, because every data source is filtered through
    /// <see cref="Includes"/> at load time.
    /// </summary>
    private static readonly Dictionary<string, (DateTime Start, DateTime? End)> Windows =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // CLOSED. 14 days, 56 BackupCompleted events — double the 7-day target, so
            // there is no reason to reach for the contaminated days to pad the sample.
            // End is 07-08 exclusive: the Wave-1 deploy landed that morning (11:34), and
            // rather than split a day around a deploy timestamp we drop the whole day.
            ["3.0-baseline"] = (new DateTime(2026, 6, 24), new DateTime(2026, 7, 8)),

            // CLOSED at 07-24 exclusive (captures 07-16..07-23). The window ran its full
            // ~7 days clean; closing it here fixes the cohort against a stable set of days.
            // The boundary is also a deploy fence: commit 164d720 (the shutdown-crash fix)
            // shipped 07-24, so anything from that day on is a different build and must not
            // fold into the candidate — the same deploy/tag drift that contaminated 07-08..07-15.
            ["4.0-candidate"] = (new DateTime(2026, 7, 16), new DateTime(2026, 7, 24)),
        };

    /// <summary>
    /// True when a record carrying <paramref name="tag"/> at <paramref name="localTimestamp"/>
    /// belongs to that cohort's valid window. Unknown tags are always included — see the
    /// class remarks on why "no entry" means "no filtering" rather than "exclude".
    /// </summary>
    public static bool Includes(string tag, DateTime localTimestamp)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        if (!Windows.TryGetValue(tag, out var w)) return true; // unwindowed tag — keep everything
        if (localTimestamp < w.Start) return false;
        return w.End is null || localTimestamp < w.End.Value;
    }

    /// <summary>
    /// Human-readable window for the report header, e.g. "2026-06-24 .. 2026-07-07" or
    /// "2026-07-16 .. open". Printing this is the point: a reader must be able to see
    /// which days a verdict is based on without reading this source file.
    /// </summary>
    public static string Describe(string? tag)
    {
        if (tag is null || !Windows.TryGetValue(tag, out var w)) return "all data";
        // End is exclusive; show the last day actually included so the label matches
        // how a human would describe the window ("through the 7th", not "before the 8th").
        string end = w.End is null ? "open" : w.End.Value.AddDays(-1).ToString("yyyy-MM-dd");
        return $"{w.Start:yyyy-MM-dd} .. {end}";
    }
}
