using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace BeetsBackup.ViewModels.WizardSteps;

/// <summary>
/// Wizard step: optional advanced settings — checksum verification, permission stripping,
/// transfer throttling, and file exclusion patterns.
/// </summary>
public partial class WizardStepAdvancedViewModel : ObservableObject
{
    [ObservableProperty] private bool _verifyChecksums;
    [ObservableProperty] private bool _stripPermissions;
    [ObservableProperty] private bool _enableThrottle;
    [ObservableProperty] private string _selectedThrottleSpeed = "10 MB/s";
    [ObservableProperty] private string _newExclusion = string.Empty;
    [ObservableProperty] private bool _enableVersioning;
    [ObservableProperty] private int _maxVersions = 5;
    [ObservableProperty] private bool _enableCompression;

    /// <summary>Versioning and compression are mutually exclusive — compressed jobs produce a single archive with no in-place overwrites to version.</summary>
    partial void OnEnableCompressionChanged(bool value)
    {
        if (value) EnableVersioning = false;
    }
    partial void OnEnableVersioningChanged(bool value)
    {
        if (value) EnableCompression = false;
    }

    public ObservableCollection<string> ExclusionFilters { get; } = new();
    [ObservableProperty] private string? _selectedExclusion;

    public List<string> ThrottleSpeedOptions { get; } = new()
    {
        "1 MB/s", "5 MB/s", "10 MB/s", "25 MB/s", "50 MB/s", "100 MB/s"
    };

    [RelayCommand]
    private void AddExclusion()
    {
        var pattern = NewExclusion.Trim();
        if (!string.IsNullOrEmpty(pattern) && !ExclusionFilters.Contains(pattern))
        {
            ExclusionFilters.Add(pattern);
            NewExclusion = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveExclusion()
    {
        if (SelectedExclusion != null)
            ExclusionFilters.Remove(SelectedExclusion);
    }

    public int ThrottleMBps => EnableThrottle ? ParseSpeed(SelectedThrottleSpeed) : 0;

    private static int ParseSpeed(string speed)
    {
        var num = speed.Replace(" MB/s", "");
        return int.TryParse(num, out var val) ? val : 10;
    }
}
