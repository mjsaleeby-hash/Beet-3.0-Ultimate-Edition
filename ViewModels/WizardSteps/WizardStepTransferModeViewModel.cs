using BeetsBackup.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

public partial class WizardStepTransferModeViewModel : ObservableObject
{
    [ObservableProperty] private TransferMode _selectedMode = TransferMode.SkipExisting;
}
