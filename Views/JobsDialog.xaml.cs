using BeetsBackup.Models;
using BeetsBackup.Services;
using System.Windows;

namespace BeetsBackup.Views;

/// <summary>Dialog that lists all scheduled backup jobs and allows deleting them.</summary>
public partial class JobsDialog : Window
{
    private readonly SchedulerService _scheduler;

    public JobsDialog(SchedulerService scheduler)
    {
        InitializeComponent();
        _scheduler = scheduler;
        RefreshList();
    }

    private void RefreshList()
    {
        JobsList.ItemsSource = null;
        JobsList.ItemsSource = _scheduler.Jobs.ToList();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is ScheduledJob job)
        {
            _scheduler.RemoveJob(job.Id);
            RefreshList();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
