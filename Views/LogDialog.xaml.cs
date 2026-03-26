using BeetsBackup.Models;
using BeetsBackup.Services;
using System.IO;
using System.Text;
using System.Windows;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace BeetsBackup.Views;

public partial class LogDialog : Window
{
    private readonly BackupLogService _log;
    private readonly SchedulerService _scheduler;

    public LogDialog(BackupLogService log, SchedulerService scheduler)
    {
        InitializeComponent();
        _log = log;
        _scheduler = scheduler;
        LogList.ItemsSource = _log.Entries;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not BackupLogEntry entry) return;
        if (entry.Status != BackupStatus.Failed) return;
        if (!HasRetryInfo(entry))
        {
            MessageBox.Show("This entry does not have enough information to retry.", "Retry", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Back-fill SourcePaths from display string for older entries
        if (entry.SourcePaths.Count == 0 && !string.IsNullOrEmpty(entry.SourcePath))
            entry.SourcePaths = entry.SourcePath.Split("; ", StringSplitOptions.RemoveEmptyEntries).ToList();
        await _scheduler.RetryAsync(entry);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"BeetsBackup_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Job Name,Status,Source,Destination,Files Copied,Files Skipped,Bytes Transferred,Message");
        foreach (var entry in _log.Entries)
        {
            sb.AppendLine($"\"{entry.TimestampDisplay}\",\"{Escape(entry.JobName)}\",\"{entry.StatusDisplay}\",\"{Escape(entry.SourcePath)}\",\"{Escape(entry.DestinationPath)}\",{entry.FilesCopied},{entry.FilesSkipped},{entry.BytesTransferred},\"{Escape(entry.Message)}\"");
        }
        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show($"Log exported to {dialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LogList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RetryButton.IsEnabled = LogList.SelectedItem is BackupLogEntry entry
            && entry.Status == BackupStatus.Failed
            && HasRetryInfo(entry);
    }

    private static bool HasRetryInfo(BackupLogEntry entry)
        => (entry.SourcePaths.Count > 0 || !string.IsNullOrEmpty(entry.SourcePath))
           && !string.IsNullOrEmpty(entry.DestinationPath);

    private static string Escape(string value) => value.Replace("\"", "\"\"");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
