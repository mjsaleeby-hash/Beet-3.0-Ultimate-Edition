using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.Models;

/// <summary>
/// Represents the lifecycle state of a backup operation.
/// </summary>
public enum BackupStatus { Scheduled, Running, Complete, Failed, Skipped }

/// <summary>
/// Tracks a single backup operation's metadata, progress, and outcome.
/// Observable properties enable live UI updates for running jobs.
/// </summary>
public partial class BackupLogEntry : ObservableObject
{
    /// <summary>Unique identifier for this log entry.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Identifier of the originating <see cref="ScheduledJob"/>, if this entry came from one.
    /// Used to correlate the "Scheduled for X" placeholder with the eventual run entry so the
    /// placeholder can be cleaned up when the run starts (or the job is deleted/edited).
    /// <see cref="Guid.Empty"/> for entries created outside the scheduler (manual backups,
    /// retries of pre-JobId log entries) and for log entries written before this field existed.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>Display name of the backup job.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Primary source path (kept for backward compatibility with older JSON logs).</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Root destination directory for the backup.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>All source paths included in this backup.</summary>
    public List<string> SourcePaths { get; set; } = new();

    /// <summary>Whether NTFS permissions are stripped from copied files.</summary>
    public bool StripPermissions { get; set; }

    /// <summary>Conflict resolution strategy used for this backup.</summary>
    public TransferMode TransferMode { get; set; } = TransferMode.SkipExisting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    [NotifyPropertyChangedFor(nameof(SizeOrStatusDisplay))]
    private BackupStatus _status;

    [ObservableProperty] private string _message = string.Empty;

    /// <summary>Transfer progress as a percentage (0-100).</summary>
    [ObservableProperty] private int _progressPercent;

    [ObservableProperty] private int _filesProcessed;
    [ObservableProperty] private int _totalFiles;

    // Counter fields below back StatsDisplay. They're declared as observable with
    // NotifyPropertyChangedFor so the log dialog refreshes the moment any counter
    // changes — without this, StatsDisplay only updated when Status changed, which
    // forced UpdateStats to assign Status last (a brittle ordering requirement).

    /// <summary>Number of files successfully copied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _filesCopied;

    /// <summary>Number of files skipped (already up-to-date).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _filesSkipped;

    /// <summary>Number of files that failed to transfer with a non-lock, non-disk-full error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _filesFailed;

    /// <summary>Number of files skipped because they were locked by another process.
    /// Tracked separately from <see cref="FilesFailed"/> so the user can see which "failures"
    /// are actually OS-level lock contention versus genuine errors.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _filesLocked;

    /// <summary>Number of directories that could not be created at the destination.
    /// Tracked separately from <see cref="FilesFailed"/> so file-level failure ratios aren't
    /// skewed by upstream directory creation errors.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _directoriesFailed;

    /// <summary>Number of files that fell back to a VSS shadow copy after lock retries.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _filesCopiedViaVss;

    /// <summary>Number of files that failed because the destination disk filled up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _diskFullErrors;

    /// <summary>Number of files whose post-copy SHA-256 hash did not match the source.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private int _checksumMismatches;

    /// <summary>Total bytes written to the destination.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    [NotifyPropertyChangedFor(nameof(BytesTransferredDisplay))]
    [NotifyPropertyChangedFor(nameof(SizeOrStatusDisplay))]
    private long _bytesTransferred;

    /// <summary>Detailed per-file error records.</summary>
    public List<FileError> FileErrors { get; set; } = new();

    private DateTime _timestamp = DateTime.Now;

    /// <summary>When this entry was last updated.</summary>
    public DateTime Timestamp
    {
        get => _timestamp;
        set
        {
            if (_timestamp != value)
            {
                _timestamp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimestampDisplay));
            }
        }
    }

    /// <summary>Human-readable status text for UI binding.</summary>
    public string StatusDisplay => Status.ToString();

    /// <summary>Formatted timestamp for display in the log grid.</summary>
    public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd  HH:mm:ss");

    /// <summary>Summary of transfer statistics, shown only when the backup is complete.
    /// Surfaces locked / directory / disk-full / checksum counters separately so the user can
    /// distinguish lock contention from genuine errors. When the backup didn't actually
    /// change anything (every file already up-to-date), reads "Up to date — N verified"
    /// instead of "0 copied, N skipped" so the user doesn't read the row as a failed run.</summary>
    public string StatsDisplay
    {
        get
        {
            if (Status != BackupStatus.Complete) return string.Empty;

            var totalFailed = FilesFailed + DirectoriesFailed + DiskFullErrors + FilesLocked + ChecksumMismatches;
            bool nothingChanged = FilesCopied == 0 && FilesCopiedViaVss == 0 && totalFailed == 0;
            if (nothingChanged)
            {
                return FilesSkipped > 0
                    ? $"Up to date — {FilesSkipped} verified"
                    : "Up to date";
            }

            var parts = new List<string>();
            if (FilesCopied > 0) parts.Add($"{FilesCopied} copied");
            if (FilesCopiedViaVss > 0) parts.Add($"{FilesCopiedViaVss} via shadow copy");
            if (FilesSkipped > 0) parts.Add($"{FilesSkipped} skipped");
            if (FilesFailed > 0) parts.Add($"{FilesFailed} failed");
            if (FilesLocked > 0) parts.Add($"{FilesLocked} locked");
            if (DirectoriesFailed > 0) parts.Add($"{DirectoriesFailed} folders failed");
            if (DiskFullErrors > 0) parts.Add($"{DiskFullErrors} disk-full");
            if (ChecksumMismatches > 0) parts.Add($"{ChecksumMismatches} checksum mismatches");
            parts.Add(FormatBytes(BytesTransferred));
            return string.Join(", ", parts);
        }
    }

    /// <summary>Human-readable bytes transferred (e.g. "7.41 GB").</summary>
    public string BytesTransferredDisplay => FormatBytes(BytesTransferred);

    /// <summary>Shows transferred bytes for complete entries, status text otherwise — used by the Recent Backups card.</summary>
    public string SizeOrStatusDisplay => Status == BackupStatus.Complete
        ? FormatBytes(BytesTransferred)
        : Status.ToString();

    /// <summary>
    /// Formats a byte count into a human-readable string with appropriate unit suffix.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }
}
