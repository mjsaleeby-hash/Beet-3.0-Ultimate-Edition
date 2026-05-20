namespace BeetsBackup.Services;

/// <summary>
/// Single source of truth for matching a file or directory name against the user's
/// exclusion patterns. Used by the size-estimate previews (ScheduleDialogViewModel,
/// wizard summary) and the actual transfer (TransferService) so the preview can't
/// disagree with what the real backup excludes.
/// </summary>
/// <remarks>
/// Supported patterns:
/// <list type="bullet">
///   <item><c>*.ext</c> — extension match, case-insensitive (e.g. <c>*.tmp</c>, <c>*.log</c>).</item>
///   <item>Any other string — exact name match, case-insensitive (e.g. <c>Thumbs.db</c>,
///     <c>node_modules</c>). Used for both files and directory names.</item>
/// </list>
/// More-elaborate glob support (mid-string wildcards, path-segment patterns) can be added
/// here without touching the call sites.
/// </remarks>
public static class ExclusionMatcher
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="name"/> matches any pattern in
    /// <paramref name="exclusions"/>. The match is performed on a single path component —
    /// callers should pass <c>Path.GetFileName(...)</c>, not a full path.
    /// </summary>
    public static bool IsExcluded(string name, IReadOnlyList<string> exclusions)
    {
        foreach (var pattern in exclusions)
        {
            if (pattern.StartsWith("*."))
            {
                // Extension match — *.tmp, *.log
                if (name.EndsWith(pattern.Substring(1), System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (name.Equals(pattern, System.StringComparison.OrdinalIgnoreCase))
            {
                // Exact name match — Thumbs.db, node_modules
                return true;
            }
        }
        return false;
    }
}
