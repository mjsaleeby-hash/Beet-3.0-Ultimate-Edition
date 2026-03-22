using BeetsBackup.Models;
using BeetsBackup.ViewModels;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace BeetsBackup.Views;

public partial class ScheduleDialog : Window
{
    public ScheduledJob? Result { get; private set; }
    private readonly ScheduleDialogViewModel _vm = new();

    public ScheduleDialog()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsValid)
        {
            MessageBox.Show("Please fill in the job name, source, and destination.",
                            "Missing Information", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = _vm.BuildJob();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
