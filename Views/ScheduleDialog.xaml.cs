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

    /// <summary>
    /// Opens the dialog in edit mode, pre-populated from the given job. On save, the VM keeps the
    /// original Id so the caller can route the result through <c>SchedulerService.UpdateJob</c>.
    /// </summary>
    public ScheduleDialog(ScheduledJob existing) : this()
    {
        _vm.LoadFromJob(existing);
        Title = "Edit Backup";
    }

    /// <summary>
    /// Tracks an in-flight Schedule_Click handler so we don't re-enter on a second click while
    /// the disk-space pre-flight is still running. Without this, a fast double-click drops the
    /// user's filter edits because the second handler sets DialogResult on a window the first
    /// handler has already started closing.
    /// </summary>
    private bool _scheduling;

    // async void is correct for WPF event handlers — exceptions propagate to the dispatcher.
    private async void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduling) return;

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

        // Mark in-flight before the await so a second Schedule click while we're awaiting
        // bounces immediately. Cleared in finally so a Cancel/Insufficient-Space exit lets the
        // user retry without restarting the dialog.
        _scheduling = true;
        try
        {
            var preview = await Task.Run(() =>
                DiskSpaceService.Preview(sources, destination, exclusions, willCompress));

            // The dialog may have been closed during the await — by the X button, ESC, the
            // Cancel button, or even an unhandled exception in another binding. Setting
            // DialogResult on a no-longer-shown dialog throws InvalidOperationException, which
            // (under our global handler) silently eats the user's save. Bail before that.
            if (!IsLoaded) return;

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

            // One final check — the message box above pumps messages, so the dialog could have
            // been closed while it was up. Belt-and-braces.
            if (!IsLoaded) return;

            Result = _vm.BuildJob();
            try { DialogResult = true; }
            catch (InvalidOperationException) { /* dialog was closed mid-set; the caller will see a null Result */ }
        }
        finally
        {
            _scheduling = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // If the disk-space pre-flight is still running, ignore the cancel — the in-flight
        // Schedule click will guard against the closed-window race when it resumes.
        try { DialogResult = false; }
        catch (InvalidOperationException) { /* already closed */ }
    }
}
