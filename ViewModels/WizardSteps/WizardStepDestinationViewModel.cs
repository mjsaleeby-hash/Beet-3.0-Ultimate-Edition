using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;

namespace BeetsBackup.ViewModels.WizardSteps;

public partial class WizardStepDestinationViewModel : ObservableObject
{
    [ObservableProperty] private string _destinationPath = string.Empty;
    [ObservableProperty] private string? _selectedDrive;
    [ObservableProperty] private string _subfolder = string.Empty;
    [ObservableProperty] private bool _useCustomPath;
    [ObservableProperty] private bool _showSameDriveWarning;

    private List<string> _sourcePaths = new();

    public List<DriveDisplayItem> AvailableDrives { get; } = new();

    public WizardStepDestinationViewModel()
    {
        RefreshDrives();
    }

    public void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            AvailableDrives.Add(new DriveDisplayItem(d));
    }

    public void SetSourcePaths(List<string> sourcePaths) => _sourcePaths = sourcePaths;

    partial void OnSelectedDriveChanged(string? value)
    {
        UpdateResolvedPath();
    }

    partial void OnSubfolderChanged(string value)
    {
        UpdateResolvedPath();
    }

    partial void OnDestinationPathChanged(string value)
    {
        ShowSameDriveWarning = IsSameDriveAsSource(_sourcePaths);
    }

    private void UpdateResolvedPath()
    {
        if (!UseCustomPath && !string.IsNullOrEmpty(SelectedDrive))
        {
            DestinationPath = string.IsNullOrWhiteSpace(Subfolder)
                ? SelectedDrive
                : Path.Combine(SelectedDrive, Subfolder.Trim());
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select destination folder" };
        if (dialog.ShowDialog() == true)
        {
            UseCustomPath = true;
            DestinationPath = dialog.FolderName;
        }
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(DestinationPath);

    public bool IsSameDriveAsSource(List<string> sourcePaths)
    {
        if (string.IsNullOrEmpty(DestinationPath)) return false;
        var destRoot = Path.GetPathRoot(DestinationPath)?.ToUpperInvariant();
        return sourcePaths.Any(s =>
            Path.GetPathRoot(s)?.ToUpperInvariant() == destRoot);
    }
}
