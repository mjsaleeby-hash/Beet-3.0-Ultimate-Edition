using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.ViewModels.WizardSteps;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace BeetsBackup.ViewModels;

public partial class BackupWizardViewModel : ObservableObject
{
    private readonly SchedulerService _scheduler;
    private List<ObservableObject> _activeSteps = new();

    // Step ViewModels
    public WizardStepBackupTypeViewModel StepType { get; } = new();
    public WizardStepScheduleViewModel StepSchedule { get; } = new();
    public WizardStepSourceViewModel StepSource { get; } = new();
    public WizardStepDestinationViewModel StepDestination { get; } = new();
    public WizardStepTransferModeViewModel StepMode { get; } = new();
    public WizardStepAdvancedViewModel StepAdvanced { get; } = new();
    public WizardStepSummaryViewModel StepSummary { get; } = new();

    [ObservableProperty] private ObservableObject _currentStep;
    [ObservableProperty] private int _currentStepIndex;
    [ObservableProperty] private int _totalSteps;
    [ObservableProperty] private bool _isFirstStep = true;
    [ObservableProperty] private bool _isLastStep;
    [ObservableProperty] private string _finishLabel = "Schedule Backup";
    [ObservableProperty] private string _stepTitle = string.Empty;
    [ObservableProperty] private string _stepDescription = string.Empty;

    // Stepper dots
    public ObservableCollection<StepIndicator> Steps { get; } = new();

    // Events for dialog
    public event Action? RequestClose;
    public event Action? RequestFinish;

    // Result
    public ScheduledJob? BuiltJob { get; private set; }
    public bool RunNow { get; private set; }

    public BackupWizardViewModel(SchedulerService scheduler)
    {
        _scheduler = scheduler;
        RebuildStepList();
        _currentStep = _activeSteps[0];
        UpdateStepState();
    }

    private void RebuildStepList()
    {
        _activeSteps = new List<ObservableObject> { StepType };

        if (StepType.SelectedType != BackupType.OneTimeNow)
            _activeSteps.Add(StepSchedule);

        _activeSteps.Add(StepSource);
        _activeSteps.Add(StepDestination);
        _activeSteps.Add(StepMode);
        _activeSteps.Add(StepAdvanced);
        _activeSteps.Add(StepSummary);

        TotalSteps = _activeSteps.Count;

        Steps.Clear();
        for (int i = 0; i < _activeSteps.Count; i++)
            Steps.Add(new StepIndicator { Number = i + 1, Label = GetStepLabel(_activeSteps[i]) });
    }

    [RelayCommand]
    private void Next()
    {
        // Validate current step
        if (CurrentStep == StepSource && !StepSource.IsValid)
            return;
        if (CurrentStep == StepDestination && !StepDestination.IsValid)
            return;

        // When leaving step 1, rebuild steps (schedule step might be added/removed)
        if (CurrentStep == StepType)
            RebuildStepList();

        var idx = _activeSteps.IndexOf(CurrentStep);
        if (idx < 0 || idx >= _activeSteps.Count - 1) return;

        CurrentStepIndex = idx + 1;
        CurrentStep = _activeSteps[CurrentStepIndex];
        UpdateStepState();

        // Populate summary when entering last step
        if (CurrentStep == StepSummary)
            PopulateSummary();
    }

    [RelayCommand]
    private void Back()
    {
        var idx = _activeSteps.IndexOf(CurrentStep);
        if (idx <= 0) return;

        CurrentStepIndex = idx - 1;
        CurrentStep = _activeSteps[CurrentStepIndex];
        UpdateStepState();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Finish()
    {
        BuiltJob = BuildJob();
        RunNow = StepType.SelectedType == BackupType.OneTimeNow;

        if (!RunNow)
            _scheduler.AddJob(BuiltJob);

        RequestFinish?.Invoke();
    }

    private void UpdateStepState()
    {
        IsFirstStep = CurrentStepIndex == 0;
        IsLastStep = CurrentStepIndex == _activeSteps.Count - 1;
        FinishLabel = StepType.SelectedType == BackupType.OneTimeNow
            ? "Start Backup Now" : "Schedule Backup";

        var (title, desc) = GetStepCopy(CurrentStep);
        StepTitle = title;
        StepDescription = desc;

        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].State = i < CurrentStepIndex ? StepState.Done
                           : i == CurrentStepIndex ? StepState.Active
                           : StepState.Pending;
        }
    }

    private void PopulateSummary()
    {
        var s = StepSummary;
        s.BackupTypeDisplay = StepType.SelectedType switch
        {
            BackupType.OneTimeNow => "One-time backup (starting now)",
            BackupType.Scheduled => "Scheduled (one-time)",
            BackupType.Recurring => $"Recurring — {StepSchedule.SelectedRecurrence}",
            _ => ""
        };

        s.ScheduleDisplay = StepType.SelectedType == BackupType.OneTimeNow
            ? "Immediately"
            : StepSchedule.ComputedNextRun.ToString("MMM d, yyyy h:mm tt");

        s.SourceDisplay = string.Join("\n", StepSource.ResolvedSourcePaths);
        s.DestinationDisplay = StepDestination.DestinationPath;

        s.TransferModeDisplay = StepMode.SelectedMode switch
        {
            TransferMode.SkipExisting => "Skip files already there",
            TransferMode.KeepBoth => "Keep both copies",
            TransferMode.Replace => "Replace with newer version",
            TransferMode.Mirror => "Mirror (make destination identical)",
            _ => ""
        };

        var opts = new List<string>();
        if (StepAdvanced.VerifyChecksums) opts.Add("Verify checksums");
        if (StepAdvanced.StripPermissions) opts.Add("Remove permissions");
        if (StepAdvanced.EnableThrottle) opts.Add($"Speed limit: {StepAdvanced.SelectedThrottleSpeed}");
        if (StepAdvanced.ExclusionFilters.Count > 0) opts.Add($"{StepAdvanced.ExclusionFilters.Count} exclusion(s)");
        s.OptionsDisplay = opts.Count > 0 ? string.Join(", ", opts) : "None (defaults)";

        _ = s.EstimateSizeAsync(StepSource.ResolvedSourcePaths);
    }

    private ScheduledJob BuildJob()
    {
        var nextRun = StepType.SelectedType == BackupType.OneTimeNow
            ? DateTime.Now
            : StepSchedule.ComputedNextRun;

        return new ScheduledJob
        {
            Name = StepType.SelectedType == BackupType.OneTimeNow
                ? $"Wizard Backup {DateTime.Now:yyyy-MM-dd}"
                : StepSchedule.JobName,
            SourcePaths = StepSource.ResolvedSourcePaths,
            DestinationPath = StepDestination.DestinationPath,
            TransferMode = StepMode.SelectedMode,
            StripPermissions = StepAdvanced.StripPermissions,
            VerifyChecksums = StepAdvanced.VerifyChecksums,
            ThrottleMBps = StepAdvanced.ThrottleMBps,
            ExclusionFilters = StepAdvanced.ExclusionFilters.ToList(),
            IsRecurring = StepType.SelectedType == BackupType.Recurring,
            NextRun = nextRun,
            RecurInterval = StepType.SelectedType == BackupType.Recurring
                ? StepSchedule.ComputedInterval : null,
            IsEnabled = true
        };
    }

    private static string GetStepLabel(ObservableObject step) => step switch
    {
        WizardStepBackupTypeViewModel => "Type",
        WizardStepScheduleViewModel => "When",
        WizardStepSourceViewModel => "Source",
        WizardStepDestinationViewModel => "Destination",
        WizardStepTransferModeViewModel => "Mode",
        WizardStepAdvancedViewModel => "Options",
        WizardStepSummaryViewModel => "Review",
        _ => ""
    };

    private static (string title, string description) GetStepCopy(ObservableObject step) => step switch
    {
        WizardStepBackupTypeViewModel => (
            "What kind of backup do you want to set up?",
            "Don't worry \u2014 you can change this later. Pick the option that sounds most like what you need."),
        WizardStepScheduleViewModel => (
            "When should the backup run?",
            "Tell us the first time you want it to run. For automatic backups, it will repeat on this schedule."),
        WizardStepSourceViewModel => (
            "What do you want to back up?",
            "Pick specific folders, or back up an entire drive. If you're not sure where your files are, backing up the entire drive is the safest option."),
        WizardStepDestinationViewModel => (
            "Where should the backup be saved?",
            "Choose a location that's different from your source \u2014 an external hard drive, a USB stick, or a folder on another drive."),
        WizardStepTransferModeViewModel => (
            "What should happen if a file already exists?",
            "This controls what happens when the destination already has a file with the same name."),
        WizardStepAdvancedViewModel => (
            "Optional: fine-tune your backup",
            "These settings are all turned off by default. Most people don't need to change anything here \u2014 skip this step if you're not sure."),
        WizardStepSummaryViewModel => (
            "Everything looks good \u2014 here's your summary",
            "Review what you've set up. If anything doesn't look right, use the Back button to change it."),
        _ => ("", "")
    };
}

public enum StepState { Pending, Active, Done }

public partial class StepIndicator : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private StepState _state = StepState.Pending;
}
