using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;

namespace BeetsBackup.ViewModels.WizardSteps;

/// <summary>How the user selects what to back up: common folders, a whole drive, or custom paths.</summary>
public enum SourceMode { QuickPick, EntireDrive, Custom }

/// <summary>
/// Wizard step: lets the user pick backup sources via quick-pick (Documents/Pictures/etc.),
/// an entire drive, or custom folder selection.
/// </summary>
public partial class WizardStepSourceViewModel : ObservableObject
{
    [ObservableProperty] private SourceMode _selectedMode = SourceMode.EntireDrive;
    [ObservableProperty] private string? _selectedDrive;
    [ObservableProperty] private bool _includeDocuments = true;
    [ObservableProperty] private bool _includePictures = true;
    [ObservableProperty] private bool _includeDesktop = true;
    [ObservableProperty] private bool _includeDownloads;

    public ObservableCollection<string> CustomPaths { get; } = new();
    [ObservableProperty] private string? _selectedCustomPath;

    public List<DriveDisplayItem> AvailableDrives { get; } = new();

    public WizardStepSourceViewModel()
    {
        RefreshDrives();
        if (AvailableDrives.Count > 0)
            SelectedDrive = AvailableDrives[0].RootPath;
    }

    public void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            AvailableDrives.Add(new DriveDisplayItem(d));
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder to back up" };
        if (dialog.ShowDialog() == true && !CustomPaths.Contains(dialog.FolderName))
            CustomPaths.Add(dialog.FolderName);
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedCustomPath != null)
            CustomPaths.Remove(SelectedCustomPath);
    }

    /// <summary>Resolves the selected mode into concrete file-system paths for the backup engine.</summary>
    public List<string> ResolvedSourcePaths
    {
        get
        {
            return SelectedMode switch
            {
                SourceMode.QuickPick => GetQuickPickPaths(),
                SourceMode.EntireDrive => string.IsNullOrEmpty(SelectedDrive) ? new() : new() { SelectedDrive },
                SourceMode.Custom => CustomPaths.ToList(),
                _ => new()
            };
        }
    }

    private List<string> GetQuickPickPaths()
    {
        var paths = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IncludeDocuments) paths.Add(Path.Combine(profile, "Documents"));
        if (IncludePictures)
        {
            paths.Add(Path.Combine(profile, "Pictures"));
            paths.Add(Path.Combine(profile, "Videos"));
        }
        if (IncludeDesktop) paths.Add(Path.Combine(profile, "Desktop"));
        if (IncludeDownloads) paths.Add(Path.Combine(profile, "Downloads"));
        return paths.Where(Directory.Exists).ToList();
    }

    public bool IsValid => ResolvedSourcePaths.Count > 0;
}

/// <summary>Display-friendly wrapper around <see cref="DriveInfo"/> for drive picker lists.</summary>
public class DriveDisplayItem
{
    public string RootPath { get; }
    public string Label { get; }
    public string FreeSpace { get; }
    public long AvailableBytes { get; }

    public DriveDisplayItem(DriveInfo d)
    {
        RootPath = d.RootDirectory.FullName;
        Label = string.IsNullOrWhiteSpace(d.VolumeLabel)
            ? $"{d.RootDirectory} ({d.DriveType})"
            : $"{d.VolumeLabel} ({d.RootDirectory})";
        AvailableBytes = d.AvailableFreeSpace;
        FreeSpace = FormatBytes(d.AvailableFreeSpace) + " free";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {suffixes[i]}";
    }
}
