using BeetsBackup.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

/// <summary>Wizard step: lets the user choose how file conflicts are resolved during transfer.</summary>
public partial class WizardStepTransferModeViewModel : ObservableObject
{
    [ObservableProperty] private TransferMode _selectedMode = TransferMode.SkipExisting;
}
