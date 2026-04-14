using System.Windows;
using BeetsBackup.ViewModels;
// Disambiguate: the WinForms reference (used for NotifyIcon) also defines MessageBox.
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace BeetsBackup.Views;

/// <summary>
/// Modal dialog listing archived copies of a single file found in its destination's
/// <c>.versions</c> folder, with Restore / Delete actions.
/// </summary>
public partial class PreviousVersionsDialog : Window
{
    private readonly PreviousVersionsViewModel _vm;

    public PreviousVersionsDialog(string liveFilePath)
    {
        InitializeComponent();
        _vm = new PreviousVersionsViewModel(liveFilePath);
        DataContext = _vm;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedVersion == null)
        {
            MessageBox.Show(this, "Select a version to restore from the list first.",
                "Previous Versions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Restore is a writable operation — confirm before overwriting the current live file.
        var confirm = MessageBox.Show(this,
            $"Restore the version from {_vm.SelectedVersion.DisplayTimestamp}?\n\n" +
            "Your current copy will be archived to .versions first, so this can be undone.",
            "Restore Previous Version", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _vm.RestoreCommand.Execute(null);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedVersion == null)
        {
            MessageBox.Show(this, "Select a version to delete from the list first.",
                "Previous Versions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Permanently delete the archived version from {_vm.SelectedVersion.DisplayTimestamp}?\n\n" +
            "This only removes the archived copy — the live file is not touched.",
            "Delete Archived Version", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        _vm.DeleteCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Double-click a row = Restore, matching the Explorer "Previous Versions" tab convention.
        if (_vm.SelectedVersion != null)
            Restore_Click(sender, e);
    }
}
