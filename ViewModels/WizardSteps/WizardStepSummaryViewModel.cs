using BeetsBackup.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

/// <summary>
/// Wizard step: displays a read-only summary of all chosen settings, an estimated backup size,
/// and a pre-flight disk-space preview warning users before they finish the wizard.
/// </summary>
public partial class WizardStepSummaryViewModel : ObservableObject
{
    [ObservableProperty] private string _backupTypeDisplay = string.Empty;
    [ObservableProperty] private string _scheduleDisplay = string.Empty;
    [ObservableProperty] private string _sourceDisplay = string.Empty;
    [ObservableProperty] private string _destinationDisplay = string.Empty;
    [ObservableProperty] private string _transferModeDisplay = string.Empty;
    [ObservableProperty] private string _optionsDisplay = string.Empty;
    [ObservableProperty] private string _sizeEstimate = "Calculating...";
    [ObservableProperty] private bool _isEstimating;

    // --- Pre-flight disk-space preview ---
    [ObservableProperty] private string _diskSpaceMessage = string.Empty;
    [ObservableProperty] private bool _hasDiskSpaceMessage;
    [ObservableProperty] private bool _isInsufficientSpace;
    [ObservableProperty] private bool _isTightSpace;

    /// <summary>The last computed preview, so callers (wizard Finish) can consult it without re-running.</summary>
    public DiskSpacePreview? LastPreview { get; private set; }

    /// <summary>
    /// Asynchronously estimates the total backup size and runs the pre-flight disk-space check,
    /// updating <see cref="SizeEstimate"/> plus the disk-space message properties.
    /// </summary>
    public async Task EstimateSizeAsync(
        List<string> sourcePaths,
        string destinationPath,
        IReadOnlyList<string>? exclusions = null,
        bool willCompress = false)
    {
        IsEstimating = true;
        SizeEstimate = "Calculating...";
        DiskSpaceMessage = string.Empty;
        HasDiskSpaceMessage = false;
        IsInsufficientSpace = false;
        IsTightSpace = false;
        LastPreview = null;

        try
        {
            var preview = await Task.Run(() =>
                DiskSpaceService.Preview(sourcePaths, destinationPath, exclusions, willCompress));

            LastPreview = preview;
            SizeEstimate = preview.RequiredDisplay;

            // Always surface a message on the summary screen so the user isn't left guessing
            // whether the check ran — even a neutral "plenty of room" line is informative.
            DiskSpaceMessage = preview.Summary;
            HasDiskSpaceMessage = !string.IsNullOrEmpty(DiskSpaceMessage);
            IsInsufficientSpace = preview.Status == DiskSpaceStatus.Insufficient;
            IsTightSpace = preview.Status == DiskSpaceStatus.Tight;
        }
        catch
        {
            SizeEstimate = "Unable to estimate";
        }
        finally
        {
            IsEstimating = false;
        }
    }
}
