using BeetsBackup.Models;
using BeetsBackup.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Microsoft.Win32;

namespace BeetsBackup.ViewModels;

public partial class ScheduleDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _jobName = "My Backup";
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
    public ObservableCollection<string> ExclusionFilters { get; } = new();
    [ObservableProperty] private string _newExclusion = string.Empty;
    [ObservableProperty] private string? _selectedExclusion;
    [ObservableProperty] private string _sizeEstimate = string.Empty;
    [ObservableProperty] private bool _isEstimating;

    public List<string> IntervalOptions { get; } = new()
    {
        "Daily", "Weekly", "Every 6 Hours", "Every 12 Hours"
    };

    public List<TransferMode> TransferModeOptions { get; } = new()
    {
        TransferMode.SkipExisting, TransferMode.KeepBoth, TransferMode.Replace, TransferMode.Mirror
    };

    public bool ShowKeepBothWarning => SelectedTransferMode == TransferMode.KeepBoth && IsRecurring;
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

    [RelayCommand]
    private void RemoveSource()
    {
        if (SelectedSourcePath != null)
            SourcePaths.Remove(SelectedSourcePath);
    }

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

    [RelayCommand]
    private void RemoveExclusion()
    {
        if (SelectedExclusion != null)
            ExclusionFilters.Remove(SelectedExclusion);
    }

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

        var sources = SourcePaths.ToList();
        var exclusions = ExclusionFilters.Count > 0 ? ExclusionFilters.ToList() : null;

        var totalBytes = await Task.Run(() =>
            TransferService.EstimateTotalSize(sources, exclusions));

        SizeEstimate = $"{FormatBytes(totalBytes)} across {CountFiles(sources, exclusions):N0} files";
        IsEstimating = false;
    }

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

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }

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
            IsRecurring = IsRecurring,
            NextRun = nextRun,
            RecurInterval = IsRecurring ? GetInterval() : null,
            IsEnabled = true
        };
    }

    private static int ToTwelveHour(int hour24) =>
        hour24 % 12 == 0 ? 12 : hour24 % 12;

    private static int ToTwentyFourHour(int hour12, string amPm)
    {
        if (amPm == "AM") return hour12 == 12 ? 0 : hour12;
        else return hour12 == 12 ? 12 : hour12 + 12;
    }

    private TimeSpan GetInterval() => SelectedInterval switch
    {
        "Daily"          => TimeSpan.FromDays(1),
        "Weekly"         => TimeSpan.FromDays(7),
        "Every 6 Hours"  => TimeSpan.FromHours(6),
        "Every 12 Hours" => TimeSpan.FromHours(12),
        _                => TimeSpan.FromDays(1)
    };

    public bool IsValid =>
        SourcePaths.Count > 0 &&
        !string.IsNullOrWhiteSpace(DestinationPath) &&
        !string.IsNullOrWhiteSpace(JobName);
}
