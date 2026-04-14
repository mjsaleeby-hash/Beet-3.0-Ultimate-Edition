using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.ViewModels;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace BeetsBackup.Views;

/// <summary>Manual schedule dialog that collects job details and returns a <see cref="ScheduledJob"/>.</summary>
public partial class ScheduleDialog : Window
{
    public ScheduledJob? Result { get; private set; }
    private readonly ScheduleDialogViewModel _vm = new();

    public ScheduleDialog()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    // async void is correct for WPF event handlers — exceptions propagate to the dispatcher.
    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsValid)
        {
            MessageBox.Show("Please fill in the job name, source, and destination.",
                            "Missing Information", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.SelectedTransferMode == TransferMode.Mirror)
        {
            var confirm = MessageBox.Show(
                "Mirror mode will permanently delete files from the destination that are not present in the source — including files you placed there manually.\n\nDo you want to continue?",
                "Confirm Mirror Mode", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        // Always run the pre-flight disk-space check at save time — don't rely on the user
        // having clicked "Estimate Size". We run the check on a background thread so the UI
        // stays responsive on large source trees, then come back to prompt if needed.
        var sources = _vm.SourcePaths.ToList();
        var exclusions = _vm.ExclusionFilters.Count > 0 ? (IReadOnlyList<string>)_vm.ExclusionFilters.ToList() : null;
        var willCompress = _vm.EnableCompression;
        var destination = _vm.DestinationPath;

        var preview = await Task.Run(() =>
            DiskSpaceService.Preview(sources, destination, exclusions, willCompress));

        // Update the VM so the inline banner reflects the fresh result.
        _vm.LastDiskSpacePreview = preview;
        _vm.DiskSpaceMessage = preview.Summary;
        _vm.HasDiskSpaceMessage = !string.IsNullOrEmpty(preview.Summary);
        _vm.IsInsufficientSpace = preview.Status == DiskSpaceStatus.Insufficient;
        _vm.IsTightSpace = preview.Status == DiskSpaceStatus.Tight;

        if (preview.Status == DiskSpaceStatus.Insufficient)
        {
            var body =
                $"The estimated backup size ({preview.RequiredDisplay}) is larger than the free space " +
                $"available on {preview.DriveRoot} ({preview.AvailableDisplay}).\n\n" +
                "If you schedule this job, it will likely run out of space partway through. Continue anyway?";
            var spaceConfirm = MessageBox.Show(body,
                "Not Enough Disk Space", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (spaceConfirm != MessageBoxResult.Yes) return;
        }

        Result = _vm.BuildJob();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
