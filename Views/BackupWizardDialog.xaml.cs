using BeetsBackup.Models;
using BeetsBackup.ViewModels;
using System.Windows;

namespace BeetsBackup.Views;

public partial class BackupWizardDialog : Window
{
    private readonly BackupWizardViewModel _vm;

    public ScheduledJob? Result => _vm.BuiltJob;
    public bool RunNow => _vm.RunNow;

    public BackupWizardDialog(BackupWizardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.RequestClose += () => { DialogResult = false; };
        vm.RequestFinish += () => { DialogResult = true; };
    }
}
