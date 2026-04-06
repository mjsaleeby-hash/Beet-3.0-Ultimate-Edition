using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

/// <summary>
/// Wizard step: configures when the backup runs — date, time, and optional recurrence interval.
/// </summary>
public partial class WizardStepScheduleViewModel : ObservableObject
{
    [ObservableProperty] private string _jobName = "My Backup";
    [ObservableProperty] private DateTime _scheduledDate = DateTime.Now.AddHours(1);
    [ObservableProperty] private int _scheduledHour = 12;
    [ObservableProperty] private int _scheduledMinute = 0;
    [ObservableProperty] private string _selectedAmPm = "AM";
    [ObservableProperty] private string _selectedRecurrence = "Daily";
    [ObservableProperty] private bool _showRecurrence;

    public List<string> RecurrenceOptions { get; } = new() { "Every 6 Hours", "Every 12 Hours", "Daily", "Weekly", "Monthly" };
    public List<string> AmPmOptions { get; } = new() { "AM", "PM" };

    public WizardStepScheduleViewModel()
    {
        var nextHour = DateTime.Now.AddHours(1);
        ScheduledHour = nextHour.Hour > 12 ? nextHour.Hour - 12 : (nextHour.Hour == 0 ? 12 : nextHour.Hour);
        SelectedAmPm = nextHour.Hour >= 12 ? "PM" : "AM";
    }

    partial void OnScheduledHourChanged(int value)
    {
        if (value < 1) ScheduledHour = 1;
        else if (value > 12) ScheduledHour = 12;
    }

    partial void OnScheduledMinuteChanged(int value)
    {
        if (value < 0) ScheduledMinute = 0;
        else if (value > 59) ScheduledMinute = 59;
    }

    /// <summary>Computes the next run time from the date, hour, minute, and AM/PM fields.</summary>
    public DateTime ComputedNextRun
    {
        get
        {
            var hour24 = Math.Clamp(ScheduledHour, 1, 12);
            if (SelectedAmPm == "PM" && hour24 != 12) hour24 += 12;
            else if (SelectedAmPm == "AM" && hour24 == 12) hour24 = 0;

            var minute = Math.Clamp(ScheduledMinute, 0, 59);
            var dt = ScheduledDate.Date.AddHours(hour24).AddMinutes(minute);
            while (dt < DateTime.Now) dt = dt.AddDays(1);
            return dt;
        }
    }

    /// <summary>Maps the selected recurrence label to a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ComputedInterval => SelectedRecurrence switch
    {
        "Every 6 Hours" => TimeSpan.FromHours(6),
        "Every 12 Hours" => TimeSpan.FromHours(12),
        "Daily" => TimeSpan.FromDays(1),
        "Weekly" => TimeSpan.FromDays(7),
        "Monthly" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1)
    };
}
