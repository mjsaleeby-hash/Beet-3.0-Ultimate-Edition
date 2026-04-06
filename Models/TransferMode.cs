namespace BeetsBackup.Models;

/// <summary>
/// Determines how file conflicts are resolved during a backup or copy operation.
/// </summary>
public enum TransferMode
{
    /// <summary>Skip files that already exist at the destination (copy only new or changed files).</summary>
    SkipExisting,

    /// <summary>Keep both copies by renaming the incoming file with a numeric suffix.</summary>
    KeepBoth,

    /// <summary>Overwrite destination files unconditionally.</summary>
    Replace,

    /// <summary>Make the destination an exact replica of the source, deleting extra files.</summary>
    Mirror
}
