using BeetsBackup.Models;
using System.Windows;

namespace BeetsBackup.Views;

public partial class TransferModeDialog : Window
{
    public TransferMode? SelectedMode { get; private set; }

    public TransferModeDialog()
    {
        InitializeComponent();
    }

    private void KeepBoth_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = TransferMode.KeepBoth;
        DialogResult = true;
    }

    private void SkipExisting_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = TransferMode.SkipExisting;
        DialogResult = true;
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = TransferMode.Replace;
        DialogResult = true;
    }

    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Mirror mode will permanently delete files from the destination that are not present in the source — including files you placed there manually.\n\nDo you want to continue?",
            "Confirm Mirror Mode",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        SelectedMode = TransferMode.Mirror;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = null;
        DialogResult = false;
    }
}
