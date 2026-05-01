using BeetsBackup.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace BeetsBackup.Services;

/// <summary>
/// Manages the backup history log — an observable collection of <see cref="BackupLogEntry"/> objects
/// that are persisted to JSON and displayed in the Log dialog.
/// All mutations are marshalled to the UI thread for safe WPF binding.
/// </summary>
public sealed class BackupLogService : IDisposable
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

    /// <summary>UTC timestamp of the most recent self-initiated write. The cross-process file
    /// watcher uses this to suppress reloads triggered by our own SaveNow.</summary>
    private DateTime _lastSelfWrite = DateTime.MinValue;

    /// <summary>Window after a self-save during which file-change events are ignored. File.Replace
    /// fires multiple events (rename, modify, delete on temp/.bak) and we don't want to thrash a
    /// pointless reload while our own write is settling.</summary>
    private static readonly TimeSpan SelfWriteCooldown = TimeSpan.FromSeconds(2);

    /// <summary>Coalesces a burst of file-change events into a single reload after the watcher quiets.</summary>
    private CancellationTokenSource? _reloadCts;

    /// <summary>Watcher on the log directory; reloads <see cref="Entries"/> when the JSON file is
    /// modified by another process (the headless --run-job path writes here independently of the
    /// foreground app, and without this watch the dashboard shows stale Last/Next data forever).</summary>
    private FileSystemWatcher? _watcher;

    /// <summary>When true, log mutations run synchronously under <see cref="_headlessLock"/> instead
    /// of being posted to the WPF dispatcher. The headless --run-job path blocks the UI thread in
    /// <c>GetAwaiter().GetResult()</c>, so dispatcher.BeginInvoke posts queue forever and are then
    /// dropped by Environment.Exit — silently losing every Add/UpdateStatus/UpdateStats write.</summary>
    private bool _headlessMode;

    /// <summary>Serializes Entries mutations when <see cref="_headlessMode"/> is set. In foreground
    /// mode the WPF dispatcher serializes via the UI thread; here we have no UI thread.</summary>
    private readonly object _headlessLock = new();

    /// <summary>Observable collection of log entries, bound to the Log dialog's list view.</summary>
    public ObservableCollection<BackupLogEntry> Entries { get; } = new();

    /// <summary>
    /// Switches this service into headless mode: log mutations run synchronously under a lock
    /// instead of being posted to the WPF dispatcher. Must be called before <see cref="Load"/>
    /// in any process that won't pump the WPF dispatcher (i.e. the <c>--run-job</c> CLI path).
    /// </summary>
    public void MarkHeadless() => _headlessMode = true;

    /// <summary>
    /// Loads the backup log from disk. Marks stale "Running" entries as failed
    /// and fixes completed entries that have leftover percentage messages.
    /// Also starts the cross-process file watcher so subsequent external writes (e.g. by the
    /// headless --run-job process) get reflected in the foreground app without a restart.
    /// </summary>
    public void Load()
    {
        ReloadFromDisk(applyHousekeeping: true);
        StartWatcher();
    }

    /// <summary>
    /// Re-reads the log file into <see cref="Entries"/>, replacing the current contents. Called
    /// at startup (with housekeeping that fixes stale Running entries) and on every external
    /// file change detected by the watcher (without housekeeping — the housekeeping was already
    /// applied by whichever process loaded the file initially).
    /// </summary>
    private void ReloadFromDisk(bool applyHousekeeping)
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var json = File.ReadAllText(LogPath);
            var loaded = JsonSerializer.Deserialize<List<BackupLogEntry>>(json);
            if (loaded == null) return;

            RunOnUiThread(() =>
            {
                Entries.Clear();
                foreach (var entry in loaded.OrderByDescending(e => e.Timestamp))
                    Entries.Add(entry);

                if (!applyHousekeeping) return;

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
            });
        }
        catch (Exception ex)
        {
            FileLogger.LogException("Failed to load backup log", ex);
        }
    }

    /// <summary>
    /// Starts the <see cref="FileSystemWatcher"/> on the log directory. Coalesces bursts of
    /// events (File.Replace fires several) and suppresses events caused by our own SaveNow.
    /// </summary>
    private void StartWatcher()
    {
        if (_watcher != null) return;
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(LogPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnLogFileChanged;
            _watcher.Created += OnLogFileChanged;
            _watcher.Renamed += OnLogFileChanged;
        }
        catch (Exception ex)
        {
            FileLogger.LogException("BackupLogService: could not start file watcher", ex);
        }
    }

    /// <summary>
    /// Watcher callback. Fires on a thread-pool thread; debounces and ignores events caused
    /// by this process's own writes.
    /// </summary>
    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow - _lastSelfWrite < SelfWriteCooldown) return;

        // Cancel any pending reload and schedule a new one — coalesces the rename/write/delete
        // burst that File.Replace produces into a single ReloadFromDisk call.
        _reloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token).ConfigureAwait(false);
                ReloadFromDisk(applyHousekeeping: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { FileLogger.LogException("BackupLogService: reload failed", ex); }
        });
    }

    /// <summary>Stops the file watcher and cancels any pending debounced reload.</summary>
    public void Dispose()
    {
        _reloadCts?.Cancel();
        _reloadCts?.Dispose();
        _reloadCts = null;
        _watcher?.Dispose();
        _watcher = null;
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

    /// <summary>
    /// Dispatches an action to the UI thread, executing synchronously if already on it.
    /// In headless mode (<see cref="MarkHeadless"/>) runs the action synchronously under
    /// <see cref="_headlessLock"/> instead — the WPF dispatcher cannot pump while the
    /// <c>--run-job</c> path blocks the UI thread waiting for its async work to finish.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (_headlessMode)
        {
            lock (_headlessLock) action();
            return;
        }
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
        // Mark before the write begins, not after — the watcher event for our own write may fire
        // before File.Replace returns. Setting this first ensures the cooldown check on the
        // watcher callback path catches every event from our own write burst.
        _lastSelfWrite = DateTime.UtcNow;
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
