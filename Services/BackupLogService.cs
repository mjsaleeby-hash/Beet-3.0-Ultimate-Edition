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

            // Mark any entries left in Running state as interrupted
            bool anyStale = false;
            foreach (var entry in Entries.Where(e => e.Status == BackupStatus.Running).ToList())
            {
                entry.Status = BackupStatus.Failed;
                entry.Message = "Interrupted — the application closed while this job was running.";
                anyStale = true;
            }
            if (anyStale) Save();
        }
        catch { }
    }

    public void Add(BackupLogEntry entry)
    {
        RunOnUiThread(() =>
        {
            Entries.Insert(0, entry);
            // Cap log at 500 entries
            while (Entries.Count > 500)
                Entries.RemoveAt(Entries.Count - 1);
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

    public void Clear()
    {
        Entries.Clear();
        Save();
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
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Entries.ToList());
            var tmpPath = LogPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(LogPath))
                File.Replace(tmpPath, LogPath, LogPath + ".bak");
            else
                File.Move(tmpPath, LogPath);
        }
        catch { }
    }
}
