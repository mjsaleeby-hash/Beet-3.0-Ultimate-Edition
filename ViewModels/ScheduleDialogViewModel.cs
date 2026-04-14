using BeetsBackup.Models;
using BeetsBackup.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Microsoft.Win32;

namespace BeetsBackup.ViewModels;

/// <summary>
/// ViewModel for the manual schedule dialog. Collects source/destination paths,
/// timing, transfer mode, and advanced options, then builds a <see cref="ScheduledJob"/>.
/// </summary>
public partial class ScheduleDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _jobName = "My Backup";

    /// <summary>User-selected source folder paths for the backup job.</summary>
    public ObservableCollection<string> SourcePaths { get; } = new();

    [ObservableProperty] private string? _selectedSourcePath;
    [ObservableProperty] private string _destinationPath = string.Empty;
    [ObservableProperty] private DateTime _scheduledDate = DateTime.Now.AddHours(1);
    [ObservableProperty] private int _scheduledHour = ToTwelveHour(DateTime.Now.AddHours(1).Hour);
    [ObservableProperty] private int _scheduledMinute = 0;
    [ObservableProperty] private string _selectedAmPm = DateTime.Now.AddHours(1).Hour >= 12 ? "PM" : "AM";
    [ObservableProperty] private bool _isRecurring = false;
    [ObservableProperty] private string _selectedInterval = "Daily";
    [ObservableProperty] private bool _stripPermissions = false;
    [ObservableProperty] private bool _verifyChecksums = false;
    [ObservableProperty] private TransferMode _selectedTransferMode = TransferMode.SkipExisting;

    /// <summary>Glob-style exclusion patterns (e.g. *.tmp, Thumbs.db).</summary>
    public ObservableCollection<string> ExclusionFilters { get; } = new();

    [ObservableProperty] private string _newExclusion = string.Empty;
    [ObservableProperty] private string? _selectedExclusion;
    [ObservableProperty] private bool _enableThrottle;
    [ObservableProperty] private string _selectedThrottleSpeed = "10 MB/s";
    [ObservableProperty] private string _sizeEstimate = string.Empty;
    [ObservableProperty] private bool _isEstimating;

    // --- Pre-flight disk-space preview (populated by EstimateSize) ---
    [ObservableProperty] private string _diskSpaceMessage = string.Empty;
    [ObservableProperty] private bool _hasDiskSpaceMessage;
    [ObservableProperty] private bool _isInsufficientSpace;
    [ObservableProperty] private bool _isTightSpace;

    /// <summary>Last pre-flight preview (for the Save command to re-check before adding the job).</summary>
    public DiskSpacePreview? LastDiskSpacePreview { get; private set; }

    [ObservableProperty] private bool _enableVersioning;
    [ObservableProperty] private int _maxVersions = 5;
    [ObservableProperty] private bool _enableCompression;

    /// <summary>Versioning and compression are mutually exclusive — compressed jobs produce a single archive with no in-place overwrites.</summary>
    partial void OnEnableCompressionChanged(bool value)
    {
        if (value) EnableVersioning = false;
    }
    partial void OnEnableVersioningChanged(bool value)
    {
        if (value) EnableCompression = false;
    }

    public List<string> ThrottleSpeedOptions { get; } = new()
    {
        "1 MB/s", "5 MB/s", "10 MB/s", "25 MB/s", "50 MB/s", "100 MB/s"
    };

    public List<string> IntervalOptions { get; } = new()
    {
        "Daily", "Weekly", "Every 6 Hours", "Every 12 Hours"
    };

    public List<TransferMode> TransferModeOptions { get; } = new()
    {
        TransferMode.SkipExisting, TransferMode.KeepBoth, TransferMode.Replace, TransferMode.Mirror
    };

    /// <summary>Shows a warning when KeepBoth + recurring could cause exponential file growth.</summary>
    public bool ShowKeepBothWarning => SelectedTransferMode == TransferMode.KeepBoth && IsRecurring;

    /// <summary>Shows a warning that Mirror mode deletes destination files not in the source.</summary>
    public bool ShowMirrorWarning => SelectedTransferMode == TransferMode.Mirror;

    partial void OnSelectedTransferModeChanged(TransferMode value)
    {
        OnPropertyChanged(nameof(ShowKeepBothWarning));
        OnPropertyChanged(nameof(ShowMirrorWarning));
    }
    partial void OnIsRecurringChanged(bool value) => OnPropertyChanged(nameof(ShowKeepBothWarning));

    public List<int> Hours { get; } = Enumerable.Range(1, 12).ToList();  // 1–12
    public List<int> Minutes { get; } = new() { 0, 15, 30, 45 };
    public List<string> AmPmOptions { get; } = new() { "AM", "PM" };

    /// <summary>Opens a folder picker and adds selected folders to <see cref="SourcePaths"/>.</summary>
    [RelayCommand]
    private void AddSource()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select source folder(s) or drive(s)",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (var folder in dialog.FolderNames)
            {
                if (!SourcePaths.Contains(folder))
                    SourcePaths.Add(folder);
            }
        }
    }

    /// <summary>Removes the currently selected source path from the list.</summary>
    [RelayCommand]
    private void RemoveSource()
    {
        if (SelectedSourcePath != null)
            SourcePaths.Remove(SelectedSourcePath);
    }

    /// <summary>Adds the current <see cref="NewExclusion"/> text as an exclusion filter.</summary>
    [RelayCommand]
    private void AddExclusion()
    {
        var filter = NewExclusion.Trim();
        if (!string.IsNullOrEmpty(filter) && !ExclusionFilters.Contains(filter))
        {
            ExclusionFilters.Add(filter);
            NewExclusion = string.Empty;
        }
    }

    /// <summary>Removes the currently selected exclusion filter from the list.</summary>
    [RelayCommand]
    private void RemoveExclusion()
    {
        if (SelectedExclusion != null)
            ExclusionFilters.Remove(SelectedExclusion);
    }

    /// <summary>Calculates and displays the estimated total size and file count for the selected sources.</summary>
    [RelayCommand]
    private async Task EstimateSize()
    {
        if (SourcePaths.Count == 0)
        {
            SizeEstimate = "Add source folders first.";
            return;
        }

        IsEstimating = true;
        SizeEstimate = "Calculating...";
        DiskSpaceMessage = string.Empty;
        HasDiskSpaceMessage = false;
        IsInsufficientSpace = false;
        IsTightSpace = false;

        var sources = SourcePaths.ToList();
        var exclusions = ExclusionFilters.Count > 0 ? ExclusionFilters.ToList() : null;
        var destination = DestinationPath;
        var willCompress = EnableCompression;

        // Count + preview in one background pass so we don't double-enumerate the source tree.
        var (preview, fileCount) = await Task.Run(() =>
        {
            var p = DiskSpaceService.Preview(sources, destination, exclusions, willCompress);
            return (p, CountFiles(sources, exclusions));
        });

        LastDiskSpacePreview = preview;
        SizeEstimate = $"{preview.RequiredDisplay} across {fileCount:N0} files";

        // Only surface the disk-space message when the destination is known — while the user is
        // still editing the dialog the destination may be blank, and showing "check skipped" on
        // every keystroke would be noisy.
        if (!string.IsNullOrWhiteSpace(destination))
        {
            DiskSpaceMessage = preview.Summary;
            HasDiskSpaceMessage = !string.IsNullOrEmpty(DiskSpaceMessage);
            IsInsufficientSpace = preview.Status == DiskSpaceStatus.Insufficient;
            IsTightSpace = preview.Status == DiskSpaceStatus.Tight;
        }

        IsEstimating = false;
    }

    /// <summary>Counts total files across all source paths, respecting exclusion filters.</summary>
    private static int CountFiles(List<string> sources, List<string>? exclusions)
    {
        int count = 0;
        foreach (var source in sources)
        {
            try
            {
                var attr = System.IO.File.GetAttributes(source);
                if (attr.HasFlag(System.IO.FileAttributes.Directory))
                {
                    var files = System.IO.Directory.EnumerateFiles(source, "*",
                        new System.IO.EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true });
                    if (exclusions != null)
                        files = files.Where(f => !IsExcluded(System.IO.Path.GetFileName(f), exclusions));
                    count += files.Count();
                }
                else
                {
                    if (exclusions == null || !IsExcluded(System.IO.Path.GetFileName(source), exclusions))
                        count++;
                }
            }
            catch { }
        }
        return count;
    }

    /// <summary>Returns <c>true</c> if the file name matches any exclusion pattern (extension or exact name).</summary>
    private static bool IsExcluded(string name, List<string> exclusions)
    {
        foreach (var pattern in exclusions)
        {
            if (pattern.StartsWith("*."))
            {
                if (name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Formats a byte count into a human-readable string (e.g. "1.5 GB").</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }

    /// <summary>Opens a folder picker for the backup destination.</summary>
    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select destination folder or drive"
        };
        if (dialog.ShowDialog() == true)
            DestinationPath = dialog.FolderName;
    }

    /// <summary>Assembles a <see cref="ScheduledJob"/> from the current dialog state.</summary>
    public ScheduledJob BuildJob()
    {
        var hour24 = ToTwentyFourHour(ScheduledHour, SelectedAmPm);
        var nextRun = ScheduledDate.Date
            .AddHours(hour24)
            .AddMinutes(ScheduledMinute);

        if (nextRun < DateTime.Now)
            nextRun = nextRun.AddDays(1);

        return new ScheduledJob
        {
            Name = JobName,
            SourcePaths = SourcePaths.ToList(),
            DestinationPath = DestinationPath,
            StripPermissions = StripPermissions,
            VerifyChecksums = VerifyChecksums,
            TransferMode = SelectedTransferMode,
            ExclusionFilters = ExclusionFilters.ToList(),
            ThrottleMBps = EnableThrottle ? ParseThrottleMBps(SelectedThrottleSpeed) : 0,
            // Compression and versioning are mutually exclusive (compressed runs produce a single
            // archive with no in-place overwrites). UI guards already prevent this, but belt-and-braces.
            EnableVersioning = EnableVersioning && !EnableCompression,
            MaxVersions = Math.Max(1, MaxVersions),
            EnableCompression = EnableCompression,
            IsRecurring = IsRecurring,
            NextRun = nextRun,
            RecurInterval = IsRecurring ? GetInterval() : null,
            IsEnabled = true
        };
    }

    /// <summary>Converts 24-hour format to 12-hour (e.g. 0 and 12 both become 12).</summary>
    private static int ToTwelveHour(int hour24) =>
        hour24 % 12 == 0 ? 12 : hour24 % 12;

    /// <summary>Converts 12-hour + AM/PM to 24-hour format.</summary>
    private static int ToTwentyFourHour(int hour12, string amPm)
    {
        if (amPm == "AM") return hour12 == 12 ? 0 : hour12;
        else return hour12 == 12 ? 12 : hour12 + 12;
    }

    /// <summary>Maps the selected interval label to a <see cref="TimeSpan"/>.</summary>
    private TimeSpan GetInterval() => SelectedInterval switch
    {
        "Daily"          => TimeSpan.FromDays(1),
        "Weekly"         => TimeSpan.FromDays(7),
        "Every 6 Hours"  => TimeSpan.FromHours(6),
        "Every 12 Hours" => TimeSpan.FromHours(12),
        _                => TimeSpan.FromDays(1)
    };

    private static int ParseThrottleMBps(string option) =>
        int.TryParse(option.Split(' ')[0], out var mb) ? mb : 10;

    /// <summary>Returns <c>true</c> when all required fields have been filled in.</summary>
    public bool IsValid =>
        SourcePaths.Count > 0 &&
        !string.IsNullOrWhiteSpace(DestinationPath) &&
        !string.IsNullOrWhiteSpace(JobName);
}
