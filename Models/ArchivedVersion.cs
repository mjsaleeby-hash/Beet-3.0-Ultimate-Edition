namespace BeetsBackup.Models;

/// <summary>
/// Represents a single archived copy of a file found in a <c>.versions</c> folder.
/// Surfaces a timestamp parsed from the archive filename, the size of the archived copy,
/// and the full path back to the live file the version belongs to.
/// </summary>
/// <param name="ArchivedPath">Full path to the archived copy inside the <c>.versions</c> tree.</param>
/// <param name="OriginalPath">Full path to the live file this version corresponds to.</param>
/// <param name="ArchivedAt">Timestamp parsed from the archived filename (local time).</param>
/// <param name="Size">Size of the archived copy, in bytes.</param>
public sealed record ArchivedVersion(string ArchivedPath, string OriginalPath, System.DateTime ArchivedAt, long Size)
{
    /// <summary>Human-readable timestamp shown in the Previous Versions list.</summary>
    public string DisplayTimestamp => ArchivedAt.ToString("MMM d, yyyy h:mm:ss tt");

    /// <summary>Human-readable size shown in the Previous Versions list.</summary>
    public string DisplaySize => FileSystemItem.FormatBytes(Size);
}
