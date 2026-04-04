using BeetsBackup.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

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

    public async Task EstimateSizeAsync(List<string> sourcePaths)
    {
        IsEstimating = true;
        SizeEstimate = "Calculating...";
        try
        {
            var bytes = await Task.Run(() => TransferService.EstimateTotalSize(sourcePaths));
            SizeEstimate = FormatBytes(bytes);
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
