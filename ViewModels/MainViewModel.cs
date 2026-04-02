using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;

namespace BeetsBackup.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ThemeService _theme;
    private readonly FileSystemService _fs;
    private readonly TransferService _transfer;
    private readonly SchedulerService _scheduler;
    private readonly BackupLogService _log;
    private readonly SettingsService _settings;
    private readonly UpdateService _update;

    // --- Theme ---
    [ObservableProperty] private bool _isDarkMode;

    // --- Toolbar ---
    [ObservableProperty] private bool _removePermissions;
    [ObservableProperty] private bool _verifyChecksums;
    [ObservableProperty] private bool _throttleTransfer;
    [ObservableProperty] private bool _isSplitPane;

    // --- Options ---
    [ObservableProperty] private bool _launchAtStartup;

    // --- Update notification ---
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateMessage = string.Empty;

    // --- Pane data ---
    public ObservableCollection<DriveItem> Drives { get; } = new();
    public ObservableCollection<FolderTreeItem> TopTreeItems { get; } = new();
    public ObservableCollection<FolderTreeItem> BottomTreeItems { get; } = new();
    public ObservableCollection<FileSystemItem> TopPaneItems { get; } = new();
    public ObservableCollection<FileSystemItem> BottomPaneItems { get; } = new();

    // Filtered views for search
    private ICollectionView? _filteredTopView;
    private ICollectionView? _filteredBottomView;
    public ICollectionView FilteredTopPaneItems => _filteredTopView ??= CreateFilteredView(TopPaneItems);
    public ICollectionView FilteredBottomPaneItems => _filteredBottomView ??= CreateFilteredView(BottomPaneItems);

    [ObservableProperty] private DriveItem? _selectedTopDrive;
    [ObservableProperty] private DriveItem? _selectedBottomDrive;
    [ObservableProperty] private FileSystemItem? _selectedTopItem;
    [ObservableProperty] private FileSystemItem? _selectedBottomItem;

    [ObservableProperty] private string _topCurrentPath = string.Empty;
    [ObservableProperty] private string _bottomCurrentPath = string.Empty;

    // --- Search (live filter) ---
    private string _searchFilter = string.Empty;
    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
            {
                _filteredTopView?.Refresh();
                _filteredBottomView?.Refresh();
            }
        }
    }

    // --- Deep search ---
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _deepSearchQuery = string.Empty;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private bool _showNoResults;
    private CancellationTokenSource? _searchCts;

    // --- Navigation history (top) ---
    private readonly List<string> _topHistory = new();
    private int _topHistoryIndex = -1;
    private bool _isNavigatingHistory;

    // --- Navigation history (bottom) ---
    private readonly List<string> _bottomHistory = new();
    private int _bottomHistoryIndex = -1;
    private bool _isNavigatingBottomHistory;

    // --- Visual mode (pie chart) ---
    [ObservableProperty] private bool _isTopVisualMode;
    [ObservableProperty] private bool _isBottomVisualMode;
    [ObservableProperty] private bool _isTopCalculating;
    [ObservableProperty] private bool _isBottomCalculating;
    [ObservableProperty] private string _topTotalSize = "";
    [ObservableProperty] private string _bottomTotalSize = "";
    [ObservableProperty] private ObservableCollection<PieSlice> _topPieSlices = new();
    [ObservableProperty] private ObservableCollection<PieSlice> _bottomPieSlices = new();

    // --- Status ---
    [ObservableProperty] private string _statusMessage = "Ready";

    // --- Transfer controls ---
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _transferProgressPercent;
    [ObservableProperty] private string _transferEta = string.Empty;

    private const long ManualThrottleBytesPerSec = 10L * 1024 * 1024; // 10 MB/s

    private CancellationTokenSource? _transferCts;
    private ManualResetEventSlim _pauseGate = new(true);
    private DateTime _transferStartTime;
    private TimeSpan _pausedDuration;
    private DateTime _pauseStartTime;

    private long ThrottleValue => ThrottleTransfer ? ManualThrottleBytesPerSec : 0;

    private CancellationTokenSource? _topSizeCts;
    private CancellationTokenSource? _bottomSizeCts;
    private CancellationTokenSource? _topNavCts;
    private CancellationTokenSource? _bottomNavCts;

    public MainViewModel(ThemeService theme, FileSystemService fs, TransferService transfer, SchedulerService scheduler, BackupLogService log, SettingsService settings, UpdateService update)
    {
        _theme = theme;
        _fs = fs;
        _transfer = transfer;
        _scheduler = scheduler;
        _log = log;
        _settings = settings;
        _update = update;
        IsDarkMode = theme.IsDark;
        LaunchAtStartup = settings.LaunchAtStartup;
        _scheduler.SchedulerError += OnSchedulerError;
        LoadDrives();
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        _settings.LaunchAtStartup = value;
    }

    // --- Update commands ---

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        StatusMessage = "Checking for updates...";
        var available = await Task.Run(() => _update.CheckForUpdateAsync());
        if (available)
        {
            IsUpdateAvailable = true;
            UpdateMessage = $"Update available: v{_update.LatestVersion}";
            StatusMessage = UpdateMessage;
        }
        else
        {
            IsUpdateAvailable = false;
            UpdateMessage = string.Empty;
            StatusMessage = $"You're on the latest version (v{_update.CurrentVersion})";
        }
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (_update.ReleaseUrl != null && _update.ReleaseUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _update.ReleaseUrl,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        _update.SkipVersion();
        IsUpdateAvailable = false;
        UpdateMessage = string.Empty;
        StatusMessage = "Ready";
    }

    private ICollectionView CreateFilteredView(ObservableCollection<FileSystemItem> source)
    {
        var view = CollectionViewSource.GetDefaultView(source);
        view.Filter = obj =>
        {
            if (string.IsNullOrEmpty(_searchFilter)) return true;
            return obj is FileSystemItem item &&
                   item.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
        };
        return view;
    }

    private void LoadDrives()
    {
        Drives.Clear();
        TopTreeItems.Clear();
        BottomTreeItems.Clear();
        foreach (var drive in _fs.GetDrives())
        {
            try
            {
                Drives.Add(drive);
                TopTreeItems.Add(new FolderTreeItem(drive));
                BottomTreeItems.Add(new FolderTreeItem(drive));
            }
            catch (IOException) { /* Drive became unavailable between enumeration and access */ }
        }
    }

    [RelayCommand]
    private void PauseResumeTransfer()
    {
        if (!IsTransferring) return;
        if (IsPaused)
        {
            _pausedDuration += DateTime.Now - _pauseStartTime;
            _pauseGate.Set();
            IsPaused = false;
            StatusMessage = "Resumed...";
        }
        else
        {
            _pauseStartTime = DateTime.Now;
            _pauseGate.Reset();
            IsPaused = true;
            StatusMessage = "Paused.";
        }
    }

    [RelayCommand]
    private void StopTransfer()
    {
        if (!IsTransferring) return;
        _pauseGate.Set();
        _transferCts?.Cancel();
        IsPaused = false;
        StatusMessage = "Stopping...";
    }

    private void BeginTransfer()
    {
        _transferCts?.Cancel();
        _transferCts = new CancellationTokenSource();
        // Create a fresh pause gate per transfer to avoid ObjectDisposedException
        // and prevent a cancelled transfer from briefly resuming
        var oldGate = _pauseGate;
        _pauseGate = new ManualResetEventSlim(true);
        oldGate.Set(); // unblock any old transfer so it can observe cancellation
        oldGate.Dispose();
        IsTransferring = true;
        IsPaused = false;
        TransferProgressPercent = 0;
        TransferEta = string.Empty;
        _transferStartTime = DateTime.Now;
        _pausedDuration = TimeSpan.Zero;
    }

    private void EndTransfer(string message)
    {
        IsTransferring = false;
        IsPaused = false;
        TransferProgressPercent = 0;
        TransferEta = string.Empty;
        StatusMessage = message;
    }

    public void Dispose()
    {
        _scheduler.SchedulerError -= OnSchedulerError;
        _pauseGate.Dispose();
        _transferCts?.Dispose();
        _topSizeCts?.Cancel(); _topSizeCts?.Dispose();
        _bottomSizeCts?.Cancel(); _bottomSizeCts?.Dispose();
        _topNavCts?.Cancel(); _topNavCts?.Dispose();
        _bottomNavCts?.Cancel(); _bottomNavCts?.Dispose();
        _searchCts?.Cancel(); _searchCts?.Dispose();
    }

    private void OnSchedulerError(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => StatusMessage = message);
        else
            StatusMessage = message;
    }

    private void OnTransferProgress(string msg) => StatusMessage = msg;

    private void OnTransferPercent(int percent)
    {
        TransferProgressPercent = percent;
        if (percent > 0)
        {
            var elapsed = DateTime.Now - _transferStartTime - _pausedDuration;
            if (elapsed.TotalSeconds < 1) return;
            var estimated = TimeSpan.FromTicks((long)(elapsed.Ticks * (100.0 / percent)));
            var remaining = estimated - elapsed;
            if (remaining.TotalSeconds > 0)
                TransferEta = remaining.TotalHours >= 1
                    ? $"~{remaining:h\\:mm\\:ss} remaining"
                    : $"~{remaining:m\\:ss} remaining";
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.Toggle();
        IsDarkMode = _theme.IsDark;
    }

    [RelayCommand]
    private void ToggleSplitPane() => IsSplitPane = !IsSplitPane;

    [RelayCommand]
    private async Task NavigateTop(string path)
    {
        // Cancel any in-progress deep search so results don't interleave
        // with the new folder contents.
        _searchCts?.Cancel();
        HasSearchResults = false;
        IsSearching = false;

        // Cancel any previous in-flight navigation to prevent duplicate entries
        // when NavigateTop is called rapidly (e.g. tree selection events).
        _topNavCts?.Cancel(); _topNavCts?.Dispose();
        var navCts = new CancellationTokenSource();
        _topNavCts = navCts;

        _topSizeCts?.Cancel(); _topSizeCts?.Dispose();
        _topSizeCts = new CancellationTokenSource();
        TopCurrentPath = path;
        TopPaneItems.Clear();

        var children = await Task.Run(() => _fs.GetChildren(path).ToList());
        // If a newer navigation has started, discard these results.
        if (navCts.IsCancellationRequested) return;
        foreach (var item in children)
            TopPaneItems.Add(item);
        _filteredTopView?.Refresh();
        IsTopCalculating = true;
        if (IsTopVisualMode)
        {
            TopPieSlices = new();
            TopTotalSize = "";
        }
        var sizeCts = _topSizeCts;
        _ = CalculateFolderSizesWithProgressAsync(TopPaneItems, true, sizeCts.Token);

        if (!_isNavigatingHistory)
        {
            if (_topHistoryIndex < _topHistory.Count - 1)
                _topHistory.RemoveRange(_topHistoryIndex + 1, _topHistory.Count - _topHistoryIndex - 1);
            _topHistory.Add(path);
            _topHistoryIndex = _topHistory.Count - 1;
        }
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task NavigateBottom(string path)
    {
        // Cancel any previous in-flight navigation to prevent duplicate entries.
        _bottomNavCts?.Cancel(); _bottomNavCts?.Dispose();
        var navCts = new CancellationTokenSource();
        _bottomNavCts = navCts;

        _bottomSizeCts?.Cancel(); _bottomSizeCts?.Dispose();
        _bottomSizeCts = new CancellationTokenSource();
        BottomCurrentPath = path;
        BottomPaneItems.Clear();

        var children = await Task.Run(() => _fs.GetChildren(path).ToList());
        // If a newer navigation has started, discard these results.
        if (navCts.IsCancellationRequested) return;
        foreach (var item in children)
            BottomPaneItems.Add(item);
        _filteredBottomView?.Refresh();
        IsBottomCalculating = true;
        if (IsBottomVisualMode)
        {
            BottomPieSlices = new();
            BottomTotalSize = "";
        }
        var sizeCts = _bottomSizeCts;
        _ = CalculateFolderSizesWithProgressAsync(BottomPaneItems, false, sizeCts.Token);

        if (!_isNavigatingBottomHistory)
        {
            if (_bottomHistoryIndex < _bottomHistory.Count - 1)
                _bottomHistory.RemoveRange(_bottomHistoryIndex + 1, _bottomHistory.Count - _bottomHistoryIndex - 1);
            _bottomHistory.Add(path);
            _bottomHistoryIndex = _bottomHistory.Count - 1;
        }
        GoBackBottomCommand.NotifyCanExecuteChanged();
        GoForwardBottomCommand.NotifyCanExecuteChanged();
        GoUpBottomCommand.NotifyCanExecuteChanged();
    }

    private static async Task CalculateFolderSizesAsync(ObservableCollection<FileSystemItem> items, CancellationToken cancellationToken)
    {
        var directories = items.Where(i => i.IsDirectory).ToList();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };
        await Parallel.ForEachAsync(directories, parallelOptions, async (dir, ct) =>
        {
            await dir.CalculateDirectorySizeAsync(ct);
        });
    }

    private async Task CalculateFolderSizesWithProgressAsync(
        ObservableCollection<FileSystemItem> items,
        bool isTop,
        CancellationToken cancellationToken)
    {
        var directories = items.Where(i => i.IsDirectory).ToList();
        int completed = 0;
        int total = directories.Count;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        // Periodic pie chart refresh while calculating
        using var refreshTimer = new System.Threading.Timer(_ =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            if ((isTop && IsTopVisualMode) || (!isTop && IsBottomVisualMode))
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => BuildPieSlices(items, isTop));
        }, null, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800));

        try
        {
            await Parallel.ForEachAsync(directories, parallelOptions, async (dir, ct) =>
            {
                await dir.CalculateDirectorySizeAsync(ct);
                Interlocked.Increment(ref completed);
            });
        }
        catch (OperationCanceledException) { return; }

        refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        await dispatcher.InvokeAsync(() =>
        {
            if (isTop) IsTopCalculating = false;
            else IsBottomCalculating = false;
            RebuildPieSlicesIfNeeded();
        });
    }

    // --- Navigation history ---
    private bool CanGoBack() => _topHistoryIndex > 0;
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBack()
    {
        _topHistoryIndex--;
        _isNavigatingHistory = true;
        await NavigateTop(_topHistory[_topHistoryIndex]);
        _isNavigatingHistory = false;
    }

    private bool CanGoForward() => _topHistoryIndex < _topHistory.Count - 1;
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForward()
    {
        _topHistoryIndex++;
        _isNavigatingHistory = true;
        await NavigateTop(_topHistory[_topHistoryIndex]);
        _isNavigatingHistory = false;
    }

    private bool CanGoUp() => !string.IsNullOrEmpty(TopCurrentPath) && Directory.GetParent(TopCurrentPath) != null;
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task GoUp()
    {
        var parent = Directory.GetParent(TopCurrentPath);
        if (parent != null)
            await NavigateTop(parent.FullName);
    }

    // --- Bottom navigation history ---
    private bool CanGoBackBottom() => _bottomHistoryIndex > 0;
    [RelayCommand(CanExecute = nameof(CanGoBackBottom))]
    private async Task GoBackBottom()
    {
        _bottomHistoryIndex--;
        _isNavigatingBottomHistory = true;
        await NavigateBottom(_bottomHistory[_bottomHistoryIndex]);
        _isNavigatingBottomHistory = false;
    }

    private bool CanGoForwardBottom() => _bottomHistoryIndex < _bottomHistory.Count - 1;
    [RelayCommand(CanExecute = nameof(CanGoForwardBottom))]
    private async Task GoForwardBottom()
    {
        _bottomHistoryIndex++;
        _isNavigatingBottomHistory = true;
        await NavigateBottom(_bottomHistory[_bottomHistoryIndex]);
        _isNavigatingBottomHistory = false;
    }

    private bool CanGoUpBottom() => !string.IsNullOrEmpty(BottomCurrentPath) && Directory.GetParent(BottomCurrentPath) != null;
    [RelayCommand(CanExecute = nameof(CanGoUpBottom))]
    private async Task GoUpBottom()
    {
        var parent = Directory.GetParent(BottomCurrentPath);
        if (parent != null)
            await NavigateBottom(parent.FullName);
    }

    // --- Deep search ---
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExecuteDeepSearchAsync()
    {
        // Cancel and dispose any in-progress search
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (string.IsNullOrWhiteSpace(DeepSearchQuery))
        {
            HasSearchResults = false;
            IsSearching = false;
            // Re-navigate to current folder to restore normal view
            if (!string.IsNullOrEmpty(TopCurrentPath))
                await NavigateTop(TopCurrentPath);
            return;
        }

        if (string.IsNullOrEmpty(TopCurrentPath))
        {
            StatusMessage = "Select a drive or navigate to a folder first, then search.";
            return;
        }

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        IsSearching = true;
        HasSearchResults = true;
        ShowNoResults = false;
        StatusMessage = $"Searching \"{TopCurrentPath}\" for \"{DeepSearchQuery.Trim()}\"...";

        var query = DeepSearchQuery.Trim();
        bool isExtensionSearch = query.StartsWith('.');

        // Clear pane for results
        TopPaneItems.Clear();
        _filteredTopView?.Refresh();

        var searchRoot = TopCurrentPath;
        var dispatcher = System.Windows.Application.Current.Dispatcher;

        await Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;
            SearchDirectoryRecursive(searchRoot, query, isExtensionSearch, token, dispatcher);
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        IsSearching = false;
        if (token.IsCancellationRequested) return;
        ShowNoResults = TopPaneItems.Count == 0;
        StatusMessage = TopPaneItems.Count > 0
            ? $"Found {TopPaneItems.Count} result{(TopPaneItems.Count != 1 ? "s" : "")} for \"{query}\""
            : $"No results for \"{query}\"";
    }

    private void SearchDirectoryRecursive(string directory, string query, bool isExtensionSearch, CancellationToken token, System.Windows.Threading.Dispatcher dispatcher)
    {
        if (token.IsCancellationRequested) return;

        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.None,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false
            };

            var batch = new List<FileSystemItem>();

            foreach (var filePath in Directory.EnumerateFiles(directory, "*", options))
            {
                if (token.IsCancellationRequested) return;
                var fileName = Path.GetFileName(filePath);
                bool matches = isExtensionSearch
                    ? Path.GetExtension(fileName).Equals(query, StringComparison.OrdinalIgnoreCase)
                    : fileName.Contains(query, StringComparison.OrdinalIgnoreCase);

                if (matches)
                {
                    batch.Add(new FileSystemItem(new FileInfo(filePath)));
                    if (batch.Count >= 50)
                    {
                        var items = batch.ToList();
                        batch.Clear();
                        dispatcher.BeginInvoke(() =>
                        {
                            foreach (var item in items)
                                TopPaneItems.Add(item);
                        });
                    }
                }
            }

            foreach (var dirPath in Directory.EnumerateDirectories(directory, "*", options))
            {
                if (token.IsCancellationRequested) return;

                // Skip junction points / symbolic links to avoid infinite recursion
                // and to match Windows Explorer behavior.
                try
                {
                    var dirAttr = File.GetAttributes(dirPath);
                    if (dirAttr.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                }
                catch { continue; }

                if (!isExtensionSearch)
                {
                    var dirName = Path.GetFileName(dirPath);
                    if (dirName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        batch.Add(new FileSystemItem(new DirectoryInfo(dirPath)));
                        if (batch.Count >= 50)
                        {
                            var items = batch.ToList();
                            batch.Clear();
                            dispatcher.BeginInvoke(() =>
                            {
                                foreach (var item in items)
                                    TopPaneItems.Add(item);
                            });
                        }
                    }
                }

                SearchDirectoryRecursive(dirPath, query, isExtensionSearch, token, dispatcher);
            }

            // Flush remaining batch
            if (batch.Count > 0)
            {
                var remaining = batch.ToList();
                dispatcher.BeginInvoke(() =>
                {
                    foreach (var item in remaining)
                        TopPaneItems.Add(item);
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    [RelayCommand]
    private async Task ClearSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        DeepSearchQuery = string.Empty;
        HasSearchResults = false;
        IsSearching = false;
        ShowNoResults = false;
        if (!string.IsNullOrEmpty(TopCurrentPath))
            await NavigateTop(TopCurrentPath);
    }

    [RelayCommand]
    private async Task SelectTopDrive(DriveItem? drive)
    {
        if (drive == null) return;
        SelectedTopDrive = drive;
        await NavigateTop(drive.RootPath);
    }

    [RelayCommand]
    private async Task SelectBottomDrive(DriveItem? drive)
    {
        if (drive == null) return;
        SelectedBottomDrive = drive;
        await NavigateBottom(drive.RootPath);
    }

    [RelayCommand]
    private async Task SelectTopTreeFolder(FolderTreeItem? folder)
    {
        if (folder == null) return;
        await NavigateTop(folder.FullPath);
    }

    [RelayCommand]
    private async Task SelectBottomTreeFolder(FolderTreeItem? folder)
    {
        if (folder == null) return;
        await NavigateBottom(folder.FullPath);
    }

    private static TransferMode? AskTransferMode()
    {
        var dialog = new TransferModeDialog
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.SelectedMode : null;
    }

    [RelayCommand]
    private async Task CopyToBottomAsync(IList<FileSystemItem> items)
    {
        if (string.IsNullOrEmpty(BottomCurrentPath)) return;
        var mode = AskTransferMode();
        if (mode == null) return;
        BeginTransfer();
        StatusMessage = "Copying...";
        FileLogger.Info($"Copy started: {items.Count} item(s) → {BottomCurrentPath} (mode={mode.Value})");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.CopyAsync(items.Select(i => i.FullPath), BottomCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums, throttleBytesPerSec: ThrottleValue);
            await NavigateBottom(BottomCurrentPath);
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Copy completed: {summary}");
            EndTransfer(summary);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Copy cancelled by user");
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Copy failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task CopyToTopAsync(IList<FileSystemItem> items)
    {
        if (string.IsNullOrEmpty(TopCurrentPath)) return;
        var mode = AskTransferMode();
        if (mode == null) return;
        BeginTransfer();
        StatusMessage = "Copying...";
        FileLogger.Info($"Copy started: {items.Count} item(s) → {TopCurrentPath} (mode={mode.Value})");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.CopyAsync(items.Select(i => i.FullPath), TopCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums, throttleBytesPerSec: ThrottleValue);
            await NavigateTop(TopCurrentPath);
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Copy completed: {summary}");
            EndTransfer(summary);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Copy cancelled by user");
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Copy failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task MoveToBottomAsync(IList<FileSystemItem> items)
    {
        if (string.IsNullOrEmpty(BottomCurrentPath)) return;
        var mode = AskTransferMode();
        if (mode == null) return;
        BeginTransfer();
        StatusMessage = "Moving...";
        FileLogger.Info($"Move started: {items.Count} item(s) → {BottomCurrentPath} (mode={mode.Value})");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.MoveAsync(items.Select(i => i.FullPath), BottomCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums, ThrottleValue);
            await NavigateTop(TopCurrentPath);
            await NavigateBottom(BottomCurrentPath);
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Move completed: {summary}");
            EndTransfer(summary);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Move cancelled by user");
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Move failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task MoveToTopAsync(IList<FileSystemItem> items)
    {
        if (string.IsNullOrEmpty(TopCurrentPath)) return;
        var mode = AskTransferMode();
        if (mode == null) return;
        BeginTransfer();
        StatusMessage = "Moving...";
        FileLogger.Info($"Move started: {items.Count} item(s) → {TopCurrentPath} (mode={mode.Value})");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.MoveAsync(items.Select(i => i.FullPath), TopCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums, ThrottleValue);
            await NavigateTop(TopCurrentPath);
            await NavigateBottom(BottomCurrentPath);
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Move completed: {summary}");
            EndTransfer(summary);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Move cancelled by user");
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Move failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void DeleteItem(FileSystemItem item)
    {
        FileLogger.Info($"Delete: {item.FullPath}");
        _fs.DeleteItem(item.FullPath);
        TopPaneItems.Remove(item);
        BottomPaneItems.Remove(item);
    }

    [RelayCommand]
    private async Task RenameItem((FileSystemItem item, string newName) args)
    {
        var (item, newName) = args;
        FileLogger.Info($"Rename: {item.FullPath} → {newName}");
        _fs.RenameItem(item.FullPath, newName);
        if (!string.IsNullOrEmpty(TopCurrentPath))
            await NavigateTop(TopCurrentPath);
        if (!string.IsNullOrEmpty(BottomCurrentPath))
            await NavigateBottom(BottomCurrentPath);
    }

    [RelayCommand]
    private async Task RefreshDrives()
    {
        LoadDrives();
        if (!string.IsNullOrEmpty(TopCurrentPath))
        {
            if (Directory.Exists(TopCurrentPath))
                await NavigateTop(TopCurrentPath);
            else
            {
                TopPaneItems.Clear();
                TopCurrentPath = string.Empty;
                StatusMessage = "Top pane path no longer exists.";
            }
        }
        if (!string.IsNullOrEmpty(BottomCurrentPath))
        {
            if (Directory.Exists(BottomCurrentPath))
                await NavigateBottom(BottomCurrentPath);
            else
            {
                BottomPaneItems.Clear();
                BottomCurrentPath = string.Empty;
                StatusMessage = "Bottom pane path no longer exists.";
            }
        }
    }

    [RelayCommand]
    private void OpenScheduleDialog()
    {
        var dialog = new ScheduleDialog
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _scheduler.AddJob(dialog.Result);
            StatusMessage = $"Scheduled: {dialog.Result.Name} — next run {dialog.Result.NextRun:g}";
        }
    }

    [RelayCommand]
    private void OpenJobsDialog()
    {
        var dialog = new JobsDialog(_scheduler)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private void OpenLogDialog()
    {
        var dialog = new LogDialog(_log, _scheduler)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    internal static string FormatTransferResult(TransferResult result)
    {
        var parts = new List<string>();
        if (result.FilesCopied > 0) parts.Add($"{result.FilesCopied} copied");
        if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped} skipped");
        if (result.FilesFailed > 0) parts.Add($"{result.FilesFailed} failed");
        if (result.DirectoriesFailed > 0) parts.Add($"{result.DirectoriesFailed} folders failed");
        if (result.FilesLocked > 0) parts.Add($"{result.FilesLocked} locked");
        if (result.DiskFullErrors > 0) parts.Add($"{result.DiskFullErrors} disk full errors");
        if (result.ChecksumMismatches > 0) parts.Add($"{result.ChecksumMismatches} checksum mismatches!");
        if (result.FilesDeleted > 0) parts.Add($"{result.FilesDeleted} deleted");
        if (result.DirectoriesDeleted > 0) parts.Add($"{result.DirectoriesDeleted} folders deleted");
        return parts.Count > 0 ? $"Done — {string.Join(", ", parts)}" : "Done.";
    }

    // --- Pie chart (data distribution) ---

    private static readonly Color[] PieColors =
    [
        Color.FromRgb(0x4C, 0xC2, 0xFF), // blue
        Color.FromRgb(0xFF, 0x6B, 0x8A), // rose
        Color.FromRgb(0x4A, 0xDE, 0x80), // green
        Color.FromRgb(0xF5, 0x9E, 0x0B), // amber
        Color.FromRgb(0xA7, 0x8B, 0xFA), // violet
        Color.FromRgb(0xF8, 0x71, 0x71), // red
        Color.FromRgb(0x38, 0xBD, 0xF8), // sky
        Color.FromRgb(0xFB, 0x92, 0x3C), // orange
        Color.FromRgb(0x34, 0xD3, 0x99), // emerald
        Color.FromRgb(0xE8, 0x79, 0xF9), // fuchsia
    ];

    [RelayCommand]
    private void ToggleTopVisualMode()
    {
        IsTopVisualMode = !IsTopVisualMode;
        if (IsTopVisualMode)
            BuildPieSlices(TopPaneItems, true);
    }

    [RelayCommand]
    private void ToggleBottomVisualMode()
    {
        IsBottomVisualMode = !IsBottomVisualMode;
        if (IsBottomVisualMode)
            BuildPieSlices(BottomPaneItems, false);
    }

    internal void BuildPieSlices(ObservableCollection<FileSystemItem> items, bool isTop)
    {
        var sorted = items
            .Where(i => i.Size > 0)
            .GroupBy(i => i.FullPath).Select(g => g.First()) // deduplicate
            .OrderByDescending(i => i.Size)
            .Take(10)
            .ToList();

        if (sorted.Count == 0)
        {
            if (isTop) { TopTotalSize = ""; TopPieSlices = new(); }
            else { BottomTotalSize = ""; BottomPieSlices = new(); }
            return;
        }

        long totalSize = items.Where(i => i.Size > 0).Sum(i => i.Size);
        if (totalSize <= 0) return;

        if (isTop) TopTotalSize = FileSystemItem.FormatBytes(totalSize);
        else BottomTotalSize = FileSystemItem.FormatBytes(totalSize);

        // Build into a local list, then assign as a new collection atomically
        // to avoid per-item CollectionChanged event storms in the PieChartControl.
        var newSlices = new ObservableCollection<PieSlice>();

        double angle = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            double pct = (double)sorted[i].Size / totalSize * 100.0;
            double sweep = pct / 100.0 * 360.0;
            newSlices.Add(new PieSlice
            {
                Name = sorted[i].Name,
                Icon = sorted[i].Icon,
                SizeBytes = sorted[i].Size,
                SizeDisplay = sorted[i].SizeDisplay,
                Percentage = pct,
                StartAngle = angle,
                SweepAngle = sweep,
                FillColor = PieColors[i % PieColors.Length],
                Index = i,
                FullPath = sorted[i].FullPath,
                IsDirectory = sorted[i].IsDirectory,
            });
            angle += sweep;
        }

        // Add an "Other" slice for remaining items not in top 10
        long top10Size = sorted.Sum(i => i.Size);
        long otherSize = totalSize - top10Size;
        if (otherSize > 0)
        {
            double otherPct = (double)otherSize / totalSize * 100.0;
            double otherSweep = otherPct / 100.0 * 360.0;
            newSlices.Add(new PieSlice
            {
                Name = "Other",
                Icon = "\u2026",
                SizeBytes = otherSize,
                SizeDisplay = FileSystemItem.FormatBytes(otherSize),
                Percentage = otherPct,
                StartAngle = angle,
                SweepAngle = otherSweep,
                FillColor = Color.FromRgb(0x60, 0x60, 0x70),
                Index = sorted.Count,
            });
        }

        // Assign the fully-built collection in one shot — triggers exactly
        // one PropertyChanged + one OnSlicesChanged in the PieChartControl
        if (isTop) TopPieSlices = newSlices;
        else BottomPieSlices = newSlices;
    }

    private void RebuildPieSlicesIfNeeded()
    {
        if (IsTopVisualMode)
            BuildPieSlices(TopPaneItems, true);
        if (IsBottomVisualMode)
            BuildPieSlices(BottomPaneItems, false);
    }
}
