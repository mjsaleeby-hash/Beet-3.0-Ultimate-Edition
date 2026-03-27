using BeetsBackup.Models;
using BeetsBackup.Services;
using System.Windows;

namespace BeetsBackup.Views;

public partial class MissedBackupsDialog : Window
{
    private readonly SchedulerService _scheduler;
    private readonly List<ScheduledJob> _missedJobs;

    public bool RunNow { get; private set; }

    public MissedBackupsDialog(SchedulerService scheduler, List<ScheduledJob> missedJobs)
    {
        InitializeComponent();
        _scheduler = scheduler;
        _missedJobs = missedJobs;
        MissedList.ItemsSource = _missedJobs;
    }

    private void RunAll_Click(object sender, RoutedEventArgs e)
    {
        RunNow = true;
        Close();
    }

    private void SkipAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in _missedJobs)
            _scheduler.SkipMissedJob(job.Id);

        RunNow = false;
        Close();
    }
}
