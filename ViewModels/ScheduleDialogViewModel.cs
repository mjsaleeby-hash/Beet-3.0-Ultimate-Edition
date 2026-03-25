using BeetsBackup.Models;
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
    [ObservableProperty] private TransferMode _selectedTransferMode = TransferMode.SkipExisting;

    public List<string> IntervalOptions { get; } = new()
    {
        "Daily", "Weekly", "Every 6 Hours", "Every 12 Hours"
    };

    public List<TransferMode> TransferModeOptions { get; } = new()
    {
        TransferMode.SkipExisting, TransferMode.KeepBoth, TransferMode.Replace
    };

    public bool ShowKeepBothWarning => SelectedTransferMode == TransferMode.KeepBoth && IsRecurring;

    partial void OnSelectedTransferModeChanged(TransferMode value) => OnPropertyChanged(nameof(ShowKeepBothWarning));
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
            TransferMode = SelectedTransferMode,
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
