using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

/// <summary>Determines how and when the backup runs.</summary>
public enum BackupType { OneTimeNow, Scheduled, Recurring }

/// <summary>Wizard step 1: lets the user choose between one-time, scheduled, or recurring backup.</summary>
public partial class WizardStepBackupTypeViewModel : ObservableObject
{
    [ObservableProperty] private BackupType _selectedType = BackupType.Recurring;
}
