using System.Threading;

namespace BeetsBackup.Models;

/// <summary>
/// Accumulates statistics from a file transfer operation (copy, move, or mirror).
/// Counter mutators are thread-safe (Interlocked); the file-error list is guarded by a lock.
/// Designed for use by the parallel copy engine where multiple workers update one shared instance.
/// </summary>
public sealed class TransferResult
{
    private int _filesCopied;
    /// <summary>Number of files successfully copied to the destination.</summary>
    public int FilesCopied { get => Volatile.Read(ref _filesCopied); internal set => _filesCopied = value; }
    /// <summary>Atomically increments <see cref="FilesCopied"/>.</summary>
    public void IncrementFilesCopied() => Interlocked.Increment(ref _filesCopied);

    private int _filesSkipped;
    /// <summary>Number of files skipped because they already existed unchanged.</summary>
    public int FilesSkipped { get => Volatile.Read(ref _filesSkipped); internal set => _filesSkipped = value; }
    /// <summary>Atomically increments <see cref="FilesSkipped"/>.</summary>
    public void IncrementFilesSkipped() => Interlocked.Increment(ref _filesSkipped);

    private int _filesFailed;
    /// <summary>Number of files that failed to copy due to errors.</summary>
    public int FilesFailed { get => Volatile.Read(ref _filesFailed); internal set => _filesFailed = value; }
    /// <summary>Atomically increments <see cref="FilesFailed"/>.</summary>
    public void IncrementFilesFailed() => Interlocked.Increment(ref _filesFailed);

    private long _bytesTransferred;
    /// <summary>Total bytes written to the destination.</summary>
    public long BytesTransferred { get => Interlocked.Read(ref _bytesTransferred); internal set => _bytesTransferred = value; }
    /// <summary>Atomically adds to <see cref="BytesTransferred"/>.</summary>
    public void AddBytesTransferred(long bytes) => Interlocked.Add(ref _bytesTransferred, bytes);

    /// <summary>Total number of files enumerated before the transfer began. Set once in the
    /// single-threaded setup phase, then read from workers — no atomic mutator needed.</summary>
    public int TotalFiles { get; set; }

    private int _checksumMismatches;
    /// <summary>Number of files whose post-copy SHA-256 hash did not match the source.</summary>
    public int ChecksumMismatches { get => Volatile.Read(ref _checksumMismatches); internal set => _checksumMismatches = value; }
    /// <summary>Atomically increments <see cref="ChecksumMismatches"/>.</summary>
    public void IncrementChecksumMismatches() => Interlocked.Increment(ref _checksumMismatches);

    private int _diskFullErrors;
    /// <summary>Number of files that failed because the destination disk was full.</summary>
    public int DiskFullErrors { get => Volatile.Read(ref _diskFullErrors); internal set => _diskFullErrors = value; }
    /// <summary>Atomically increments <see cref="DiskFullErrors"/>.</summary>
    public void IncrementDiskFullErrors() => Interlocked.Increment(ref _diskFullErrors);

    private int _filesLocked;
    /// <summary>Number of files skipped because they were locked by another process.</summary>
    public int FilesLocked { get => Volatile.Read(ref _filesLocked); internal set => _filesLocked = value; }
    /// <summary>Atomically increments <see cref="FilesLocked"/>.</summary>
    public void IncrementFilesLocked() => Interlocked.Increment(ref _filesLocked);

    private int _filesCopiedViaVss;
    /// <summary>Number of files copied via VSS shadow copy fallback after lock retries were exhausted.</summary>
    public int FilesCopiedViaVss { get => Volatile.Read(ref _filesCopiedViaVss); internal set => _filesCopiedViaVss = value; }
    /// <summary>Atomically increments <see cref="FilesCopiedViaVss"/>.</summary>
    public void IncrementFilesCopiedViaVss() => Interlocked.Increment(ref _filesCopiedViaVss);

    private int _directoriesFailed;
    /// <summary>Number of directories that failed to copy.</summary>
    public int DirectoriesFailed { get => Volatile.Read(ref _directoriesFailed); internal set => _directoriesFailed = value; }
    /// <summary>Atomically increments <see cref="DirectoriesFailed"/>.</summary>
    public void IncrementDirectoriesFailed() => Interlocked.Increment(ref _directoriesFailed);

    private int _filesDeleted;
    /// <summary>Number of files deleted during mirror cleanup.</summary>
    public int FilesDeleted { get => Volatile.Read(ref _filesDeleted); internal set => _filesDeleted = value; }
    /// <summary>Atomically increments <see cref="FilesDeleted"/>.</summary>
    public void IncrementFilesDeleted() => Interlocked.Increment(ref _filesDeleted);

    private int _directoriesDeleted;
    /// <summary>Number of directories deleted during mirror cleanup.</summary>
    public int DirectoriesDeleted { get => Volatile.Read(ref _directoriesDeleted); internal set => _directoriesDeleted = value; }
    /// <summary>Atomically increments <see cref="DirectoriesDeleted"/>.</summary>
    public void IncrementDirectoriesDeleted() => Interlocked.Increment(ref _directoriesDeleted);

    private const int MaxFileErrors = 200;
    private readonly object _errorLock = new();
    private readonly List<FileError> _fileErrors = new();

    /// <summary>Per-file error details. Capped at <c>MaxFileErrors</c> entries to avoid unbounded
    /// growth. Returns a snapshot copy taken under lock; safe to enumerate while workers are
    /// still adding errors.</summary>
    public IReadOnlyList<FileError> FileErrors
    {
        get { lock (_errorLock) { return _fileErrors.ToList(); } }
    }

    /// <summary>
    /// Records an individual file error, up to the configured cap. Thread-safe.
    /// </summary>
    /// <param name="path">Full path of the file that failed.</param>
    /// <param name="reason">Human-readable description of the failure.</param>
    public void AddFileError(string path, string reason)
    {
        lock (_errorLock)
        {
            if (_fileErrors.Count < MaxFileErrors)
                _fileErrors.Add(new FileError { Path = path, Reason = reason });
        }
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
