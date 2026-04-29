namespace BeetsBackup.Models;

/// <summary>
/// Decision the enumerator made about how to process a single source file. Carried on a
/// <see cref="FileWorkItem"/> so the parallel copy worker can act on it without re-checking
/// the destination — the enumerator runs once, decisions are frozen, and workers don't race
/// to make different choices for the same file.
/// </summary>
public enum FileAction
{
    /// <summary>Destination didn't exist (or was renamed for KeepBoth) — straight copy.</summary>
    Copy,

    /// <summary>Destination exists; will be archived (if versioning enabled) and overwritten.
    /// Decided here because the old contents matter — a worker that re-checks at copy time would
    /// race a sibling worker that already replaced the destination.</summary>
    Replace,

    /// <summary>Source matches destination (same size + same-or-older mtime). Skip the copy
    /// entirely; counted under <see cref="TransferResult.FilesSkipped"/>.</summary>
    SkipIdentical,
}

/// <summary>
/// One source directory that needs to exist at the destination before any file inside it can
/// land. Pre-created sequentially by the orchestrator before workers start copying so workers
/// never need to call <see cref="System.IO.Directory.CreateDirectory(string)"/> themselves —
/// both for performance (one call instead of many redundant ones) and to avoid the case where
/// two workers race on the same parent directory.
/// </summary>
/// <param name="SourcePath">Absolute path of the source directory; used to read the hidden attribute.</param>
/// <param name="DestPath">Absolute path where the directory should exist at the destination.</param>
/// <param name="IsHidden">When true, set the hidden attribute on the destination after creation.</param>
public sealed record DirectoryWorkItem(
    string SourcePath,
    string DestPath,
    bool IsHidden);

/// <summary>
/// One source file that needs to be copied (or skipped). All decisions about destination
/// path uniqueness (KeepBoth) and overwrite vs skip have been made by the enumerator — the
/// copy worker just executes <see cref="Action"/>.
/// </summary>
/// <param name="SourcePath">Absolute path of the source file.</param>
/// <param name="DestPath">Absolute path of the planned destination file (already KeepBoth-renamed if applicable).</param>
/// <param name="Action">What the worker should do with this item.</param>
/// <param name="SourceSize">File size at enumeration time, in bytes. Used for byte-progress reporting.</param>
public sealed record FileWorkItem(
    string SourcePath,
    string DestPath,
    FileAction Action,
    long SourceSize);
