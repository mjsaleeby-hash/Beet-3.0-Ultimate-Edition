using System.Collections.ObjectModel;
using System.IO;
using BeetsBackup.Models;
using BeetsBackup.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsBackup.ViewModels;

/// <summary>
/// ViewModel for the "Previous Versions" dialog. Lists archived versions of a live file and
/// exposes Restore/Delete/Refresh commands that wrap <see cref="VersioningService"/>.
/// </summary>
public partial class PreviousVersionsViewModel : ObservableObject
{
    /// <summary>The live file whose history is being displayed.</summary>
    public string LiveFilePath { get; }

    /// <summary>Human-readable heading shown above the list (just the leaf filename).</summary>
    public string FileName => Path.GetFileName(LiveFilePath);

    /// <summary>Discovered archived versions, newest first. Empty collection means no history found.</summary>
    public ObservableCollection<ArchivedVersion> Versions { get; } = new();

    [ObservableProperty] private ArchivedVersion? _selectedVersion;

    /// <summary>User-facing status line (empty state, last action result, errors).</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    public PreviousVersionsViewModel(string liveFilePath)
    {
        LiveFilePath = liveFilePath;
        Refresh();
    }

    /// <summary>Re-enumerates the .versions folder and repopulates the list.</summary>
    [RelayCommand]
    private void Refresh()
    {
        Versions.Clear();
        var found = VersioningService.GetArchivedVersions(LiveFilePath);
        foreach (var v in found) Versions.Add(v);

        StatusMessage = Versions.Count == 0
            ? "No previous versions found for this file."
            : $"{Versions.Count} version(s) archived.";
    }

    /// <summary>
    /// Restores the currently selected version, archiving the live copy first so the action is reversible.
    /// </summary>
    [RelayCommand]
    private void Restore()
    {
        if (SelectedVersion == null)
        {
            StatusMessage = "Select a version to restore.";
            return;
        }

        var chosen = SelectedVersion;
        var ok = VersioningService.RestoreVersion(chosen);
        if (ok)
        {
            StatusMessage = $"Restored version from {chosen.DisplayTimestamp}. Your previous copy was archived.";
            Refresh();
        }
        else
        {
            StatusMessage = "Restore failed — see the log for details.";
        }
    }

    /// <summary>Permanently deletes the currently selected archived version.</summary>
    [RelayCommand]
    private void Delete()
    {
        if (SelectedVersion == null)
        {
            StatusMessage = "Select a version to delete.";
            return;
        }

        var chosen = SelectedVersion;
        var ok = VersioningService.DeleteArchivedVersion(chosen);
        if (ok)
        {
            Versions.Remove(chosen);
            StatusMessage = $"Deleted archived version from {chosen.DisplayTimestamp}.";
            if (Versions.Count == 0)
                StatusMessage = "No previous versions remain.";
        }
        else
        {
            StatusMessage = "Delete failed — the file may be in use.";
        }
    }
}
