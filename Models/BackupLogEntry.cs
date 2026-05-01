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

    /// <summary>Number of files successfully copied.</summary>
    public int FilesCopied { get; set; }

    /// <summary>Number of files skipped (already up-to-date).</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Number of files that failed to transfer.</summary>
    public int FilesFailed { get; set; }

    /// <summary>Total bytes written to the destination.</summary>
    public long BytesTransferred { get; set; }

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

    /// <summary>Summary of transfer statistics, shown only when the backup is complete.</summary>
    public string StatsDisplay => Status == BackupStatus.Complete
        ? $"{FilesCopied} copied, {FilesSkipped} skipped{(FilesFailed > 0 ? $", {FilesFailed} failed" : "")}, {FormatBytes(BytesTransferred)}"
        : string.Empty;

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
