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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedMode = null;
        DialogResult = false;
    }
}
