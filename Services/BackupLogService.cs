using BeetsBackup.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace BeetsBackup.Services;

/// <summary>
/// Manages the backup history log — an observable collection of <see cref="BackupLogEntry"/> objects
/// that are persisted to JSON and displayed in the Log dialog.
/// All mutations are marshalled to the UI thread for safe WPF binding.
/// </summary>
public sealed class BackupLogService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "backup_log.json");

    /// <summary>Whether a deferred save is already queued on the dispatcher.</summary>
    private bool _savePending;

    /// <summary>UTC timestamp of the last completed disk write.</summary>
    private DateTime _lastSave = DateTime.MinValue;

    /// <summary>
    /// Minimum interval between disk writes for non-terminal status updates (Running, Scheduled).
    /// Terminal statuses (Complete, Failed, Cancelled) bypass debounce via <see cref="SaveNow"/>.
    /// </summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(5);

    /// <summary>Observable collection of log entries, bound to the Log dialog's list view.</summary>
    public ObservableCollection<BackupLogEntry> Entries { get; } = new();

    /// <summary>
    /// Loads the backup log from disk. Marks stale "Running" entries as failed
    /// and fixes completed entries that have leftover percentage messages.
    /// </summary>
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

            // Fix completed entries that still show a percentage as their message
            foreach (var entry in Entries.Where(e => e.Status == BackupStatus.Complete && e.Message != null && e.Message.EndsWith("%")))
            {
                entry.Message = "Complete";
                entry.ProgressPercent = 100;
                anyStale = true;
            }

            if (anyStale) Save();
        }
        catch (Exception ex)
        {
            FileLogger.LogException("Failed to load backup log", ex);
        }
    }

    /// <summary>
    /// Adds a new entry to the front of the log, capping at 500 entries.
    /// </summary>
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

    /// <summary>Updates the status and message for the entry with the specified ID.</summary>
    public void UpdateStatus(Guid id, BackupStatus status, string message = "")
    {
        RunOnUiThread(() =>
        {
            var entry = Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.Status = status;
            entry.Message = message;
            entry.Timestamp = DateTime.Now;
            if (status is BackupStatus.Running or BackupStatus.Scheduled)
                Save();
            else
                SaveNow();
        });
    }

    /// <summary>Updates the progress percentage for a running backup entry.</summary>
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

    /// <summary>Updates the final transfer statistics for a completed backup entry.</summary>
    public void UpdateStats(Guid id, BackupStatus status, int filesCopied, int filesSkipped, long bytesTransferred, int totalFiles = 0, int filesFailed = 0, IReadOnlyList<FileError>? fileErrors = null)
    {
        RunOnUiThread(() =>
        {
            var entry = Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.Status = status;
            entry.FilesCopied = filesCopied;
            entry.FilesSkipped = filesSkipped;
            entry.FilesFailed = filesFailed;
            entry.BytesTransferred = bytesTransferred;
            entry.TotalFiles = totalFiles;
            entry.ProgressPercent = 100;
            entry.Message = filesFailed > 0 ? $"Complete with {filesFailed} error(s)" : "Complete";
            if (fileErrors != null && fileErrors.Count > 0)
                entry.FileErrors = fileErrors.ToList();
            entry.Timestamp = DateTime.Now;
            SaveNow();
        });
    }

    /// <summary>Removes all entries from the log and persists the empty state.</summary>
    public void Clear()
    {
        RunOnUiThread(() =>
        {
            Entries.Clear();
            SaveNow();
        });
    }

    /// <summary>Dispatches an action to the UI thread, executing synchronously if already on it.</summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Debounced save: avoids excessive disk I/O when rapid-fire progress updates arrive.
    /// If called within the debounce window, queues a single deferred save at Background
    /// dispatcher priority. All callers run on the UI thread (via <see cref="RunOnUiThread"/>),
    /// so <see cref="_savePending"/> does not need synchronization.
    /// </summary>
    private void Save()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSave < SaveDebounce)
        {
            // Coalesce: schedule one deferred save that will capture the latest state
            if (!_savePending)
            {
                _savePending = true;
                var dispatcher = Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _savePending = false;
                    SaveNow();
                });
            }
            return;
        }
        SaveNow();
    }

    /// <summary>
    /// Performs the actual atomic write (temp file + replace) to disk immediately.
    /// Called directly for terminal statuses (Complete, Failed, Cancelled) to ensure
    /// final results are never lost to debounce coalescing.
    /// </summary>
    private void SaveNow()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Entries.ToList());
            // Atomic write: write to temp, then replace. Prevents corruption if the app
            // crashes mid-write (the old file or its .bak copy survives).
            var tmpPath = LogPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(LogPath))
                File.Replace(tmpPath, LogPath, LogPath + ".bak");
            else
                File.Move(tmpPath, LogPath);
        }
        catch (Exception ex)
        {
            FileLogger.LogException("Failed to persist backup log", ex);
        }
    }
}
