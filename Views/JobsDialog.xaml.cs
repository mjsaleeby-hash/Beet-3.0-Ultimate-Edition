using BeetsBackup.Models;
using BeetsBackup.Services;
using System.Windows;
using System.Windows.Input;

namespace BeetsBackup.Views;

/// <summary>Dialog that lists all scheduled backup jobs and supports editing or deleting them.</summary>
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

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelectedJob();

    private void JobsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedJob();

    /// <summary>
    /// Opens <see cref="ScheduleDialog"/> in edit mode for the selected job. If the user saves,
    /// routes the result through <see cref="SchedulerService.UpdateJob"/> so the original Id
    /// and its Windows scheduled task are preserved in place.
    /// </summary>
    private void EditSelectedJob()
    {
        if (JobsList.SelectedItem is not ScheduledJob job) return;

        var dlg = new ScheduleDialog(job) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is ScheduledJob updated)
        {
            _scheduler.UpdateJob(updated);
            RefreshList();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
