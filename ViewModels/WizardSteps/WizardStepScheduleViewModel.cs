using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsBackup.ViewModels.WizardSteps;

public partial class WizardStepScheduleViewModel : ObservableObject
{
    [ObservableProperty] private string _jobName = "My Backup";
    [ObservableProperty] private DateTime _scheduledDate = DateTime.Now.AddHours(1);
    [ObservableProperty] private int _scheduledHour;
    [ObservableProperty] private int _scheduledMinute = 0;
    [ObservableProperty] private string _selectedAmPm = "AM";
    [ObservableProperty] private string _selectedRecurrence = "Daily";

    public List<string> RecurrenceOptions { get; } = new() { "Every 6 Hours", "Every 12 Hours", "Daily", "Weekly", "Monthly" };
    public List<string> AmPmOptions { get; } = new() { "AM", "PM" };

    public WizardStepScheduleViewModel()
    {
        var nextHour = DateTime.Now.AddHours(1);
        ScheduledHour = nextHour.Hour > 12 ? nextHour.Hour - 12 : (nextHour.Hour == 0 ? 12 : nextHour.Hour);
        SelectedAmPm = nextHour.Hour >= 12 ? "PM" : "AM";
    }

    public DateTime ComputedNextRun
    {
        get
        {
            var hour24 = ScheduledHour;
            if (SelectedAmPm == "PM" && hour24 != 12) hour24 += 12;
            else if (SelectedAmPm == "AM" && hour24 == 12) hour24 = 0;

            var dt = ScheduledDate.Date.AddHours(hour24).AddMinutes(ScheduledMinute);
            if (dt < DateTime.Now) dt = dt.AddDays(1);
            return dt;
        }
    }

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
