using BeetsBackup.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Application = System.Windows.Application;

namespace BeetsBackup.Services;

public class BackupLogService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "backup_log.json");

    public ObservableCollection<BackupLogEntry> Entries { get; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var json = File.ReadAllText(LogPath);
            var loaded = JsonSerializer.Deserialize<List<BackupLogEntry>>(json);
            if (loaded == null) return;
            foreach (var entry in loaded.OrderByDescending(e => e.Timestamp))
                Entries.Add(entry);
        }
        catch { }
    }

    public void Add(BackupLogEntry entry)
    {
        RunOnUiThread(() =>
        {
            Entries.Insert(0, entry); // newest first
            Save();
        });
    }

    public void UpdateStatus(Guid id, BackupStatus status, string message = "")
    {
        RunOnUiThread(() =>
        {
            var entry = Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.Status = status;
            entry.Message = message;
            entry.Timestamp = DateTime.Now;
            Save();
        });
    }

    public void UpdateProgress(Guid id, int percent)
    {
        RunOnUiThread(() =>
        {
            var entry = Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.ProgressPercent = percent;
            entry.Message = $"{percent}%";
        });
    }

    public void UpdateStats(Guid id, BackupStatus status, int filesCopied, int filesSkipped, long bytesTransferred, int totalFiles = 0)
    {
        RunOnUiThread(() =>
        {
            var entry = Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.Status = status;
            entry.FilesCopied = filesCopied;
            entry.FilesSkipped = filesSkipped;
            entry.BytesTransferred = bytesTransferred;
            entry.TotalFiles = totalFiles;
            entry.ProgressPercent = 100;
            entry.Timestamp = DateTime.Now;
            Save();
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, JsonSerializer.Serialize(Entries.ToList()));
        }
        catch { }
    }
}
