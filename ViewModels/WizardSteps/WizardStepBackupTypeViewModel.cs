using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

public enum BackupType { OneTimeNow, Scheduled, Recurring }

public partial class WizardStepBackupTypeViewModel : ObservableObject
{
    [ObservableProperty] private BackupType _selectedType = BackupType.Recurring;
}
