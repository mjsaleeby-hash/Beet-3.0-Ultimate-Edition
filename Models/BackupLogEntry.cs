using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.Models;

public enum BackupStatus { Scheduled, Running, Complete, Failed }

public partial class BackupLogEntry : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public List<string> SourcePaths { get; set; } = new();
    public bool StripPermissions { get; set; }
    public TransferMode TransferMode { get; set; } = TransferMode.SkipExisting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(StatsDisplay))]
    private BackupStatus _status;

    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private int _progressPercent;   // 0–100
    [ObservableProperty] private int _filesProcessed;
    [ObservableProperty] private int _totalFiles;

    public int FilesCopied { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public long BytesTransferred { get; set; }
    public List<FileError> FileErrors { get; set; } = new();

    private DateTime _timestamp = DateTime.Now;
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

    public string StatusDisplay => Status.ToString();
    public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd  HH:mm:ss");
    public string StatsDisplay => Status == BackupStatus.Complete
        ? $"{FilesCopied} copied, {FilesSkipped} skipped{(FilesFailed > 0 ? $", {FilesFailed} failed" : "")}, {FormatBytes(BytesTransferred)}"
        : string.Empty;

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }
}
