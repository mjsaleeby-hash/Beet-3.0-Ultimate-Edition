namespace BeetsBackup.Models;

/// <summary>
/// Accumulates statistics from a file transfer operation (copy, move, or mirror).
/// </summary>
public sealed class TransferResult
{
    /// <summary>Number of files successfully copied to the destination.</summary>
    public int FilesCopied { get; set; }

    /// <summary>Number of files skipped because they already existed unchanged.</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Number of files that failed to copy due to errors.</summary>
    public int FilesFailed { get; set; }

    /// <summary>Total bytes written to the destination.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Total number of files enumerated before the transfer began.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Number of files whose post-copy SHA-256 hash did not match the source.</summary>
    public int ChecksumMismatches { get; set; }

    /// <summary>Number of files that failed because the destination disk was full.</summary>
    public int DiskFullErrors { get; set; }

    /// <summary>Number of files skipped because they were locked by another process.</summary>
    public int FilesLocked { get; set; }

    /// <summary>Number of files copied via VSS shadow copy fallback after lock retries were exhausted.</summary>
    public int FilesCopiedViaVss { get; set; }

    /// <summary>Number of directories that failed to copy.</summary>
    public int DirectoriesFailed { get; set; }

    /// <summary>Number of files deleted during mirror cleanup.</summary>
    public int FilesDeleted { get; set; }

    /// <summary>Number of directories deleted during mirror cleanup.</summary>
    public int DirectoriesDeleted { get; set; }

    /// <summary>Per-file error details. Capped at <see cref="MaxFileErrors"/> entries to avoid unbounded growth.</summary>
    public List<FileError> FileErrors { get; } = new();

    private const int MaxFileErrors = 200;

    /// <summary>
    /// Records an individual file error, up to the configured cap.
    /// </summary>
    /// <param name="path">Full path of the file that failed.</param>
    /// <param name="reason">Human-readable description of the failure.</param>
    public void AddFileError(string path, string reason)
    {
        if (FileErrors.Count < MaxFileErrors)
            FileErrors.Add(new FileError { Path = path, Reason = reason });
    }
}

/// <summary>
/// Represents a single file-level error that occurred during a transfer operation.
/// </summary>
public sealed class FileError
{
    /// <summary>Full path of the file that encountered the error.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Human-readable description of the error.</summary>
    public string Reason { get; set; } = string.Empty;
}
