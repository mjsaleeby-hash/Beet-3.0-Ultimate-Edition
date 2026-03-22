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

    public LogDialog(BackupLogService log)
    {
        InitializeComponent();
        _log = log;
        LogList.ItemsSource = _log.Entries;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Entries.Clear();
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

    private static string Escape(string value) => value.Replace("\"", "\"\"");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
