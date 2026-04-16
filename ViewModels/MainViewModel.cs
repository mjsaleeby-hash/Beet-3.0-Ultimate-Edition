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
using Color = System.Windows.Media.Color;

namespace BeetsBackup.ViewModels;

/// <summary>
/// Primary ViewModel for the dual-pane file manager. Manages drive/folder navigation,
/// file transfers (copy/move), deep search, pie-chart visualization, scheduled backups,
/// theme toggling, and update checking.
/// </summary>
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
    [ObservableProperty] private bool _isSimpleMode;

    // --- Options ---
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _startMinimized;

    // --- Update notification ---
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateMessage = string.Empty;

    // --- Crash recovery banner (shown once if the previous session didn't exit cleanly) ---
    [ObservableProperty] private bool _isCrashBannerVisible;
    [ObservableProperty] private string _crashBannerMessage = string.Empty;

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

    /// <summary>Default manual throttle: 10 MB/s.</summary>
    private const long ManualThrottleBytesPerSec = 10L * 1024 * 1024;

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
        StartMinimized = settings.StartMinimized;
        IsSimpleMode = settings.Data.IsSimpleMode;
        _scheduler.SchedulerError += OnSchedulerError;
        LoadDrives();

        if (App.PreviousUncleanShutdownAt is { } whenStarted)
        {
            IsCrashBannerVisible = true;
            CrashBannerMessage = $"Previous session ended unexpectedly (started {whenStarted:MMM d, h:mm tt}). Export diagnostics to send to support.";
        }
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        _settings.LaunchAtStartup = value;
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        _settings.StartMinimized = value;
    }

    partial void OnIsSimpleModeChanged(bool value)
    {
        _settings.Data.IsSimpleMode = value;
        _settings.Save();
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

    /// <summary>
    /// Bundles operational logs, crash dumps, settings, backup history, and system info into a
    /// timestamped zip on the user's Desktop, then opens Explorer with the file selected so the
    /// user can attach it to a support email.
    /// </summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        StatusMessage = "Exporting diagnostics…";
        try
        {
            var path = await Task.Run(DiagnosticsService.ExportToDesktop);
            DiagnosticsService.RevealInExplorer(path);
            StatusMessage = $"Diagnostics saved to Desktop: {Path.GetFileName(path)}";
            // Recovery banner is one-shot — once the user has the bundle, it served its purpose.
            IsCrashBannerVisible = false;
        }
        catch (Exception ex)
        {
            FileLogger.LogException("Diagnostics export failed", ex);
            StatusMessage = $"Diagnostics export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DismissCrashBanner() => IsCrashBannerVisible = false;

    /// <summary>Creates a filtered <see cref="ICollectionView"/> that live-filters by <see cref="SearchFilter"/>.</summary>
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

    /// <summary>Refreshes the drive list and rebuilds folder trees for both panes.</summary>
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

    /// <summary>Toggles the pause/resume state of an in-progress transfer.</summary>
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

    /// <summary>Cancels the current transfer by signalling the cancellation token.</summary>
    [RelayCommand]
    private void StopTransfer()
    {
        if (!IsTransferring) return;
        _pauseGate.Set();
        _transferCts?.Cancel();
        IsPaused = false;
        StatusMessage = "Stopping...";
    }

    /// <summary>Resets transfer state (CTS, pause gate, progress) and marks IsTransferring = true.</summary>
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

    /// <summary>Clears transfer state and displays the final status message.</summary>
    private void EndTransfer(string message)
    {
        IsTransferring = false;
        IsPaused = false;
        TransferProgressPercent = 0;
        TransferEta = string.Empty;
        StatusMessage = message;
    }

    /// <inheritdoc/>
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

    /// <summary>Progress callback — updates the status bar text.</summary>
    private void OnTransferProgress(string msg) => StatusMessage = msg;

    /// <summary>Percent callback — updates the progress bar and computes ETA.</summary>
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

    /// <summary>Navigates the top pane to the given path, loading children and computing sizes.</summary>
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

    /// <summary>Navigates the bottom pane to the given path, loading children and computing sizes.</summary>
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

    /// <summary>Calculates folder sizes in parallel for all directory items in the collection.</summary>
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

    /// <summary>
    /// Calculates folder sizes in parallel and periodically rebuilds pie chart slices
    /// while computation is in progress.
    /// </summary>
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
    /// <summary>
    /// Recursively searches the current top pane directory for files/folders matching
    /// the query. Supports extension search (e.g. ".pdf") and name substring search.
    /// Results stream into the top pane in batches of 50.
    /// </summary>
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

    /// <summary>Recursively walks <paramref name="directory"/> and dispatches matching items to the UI.</summary>
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

    /// <summary>Cancels any active deep search and restores the normal folder view.</summary>
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

    /// <summary>Shows the transfer mode dialog and returns the selected mode, or <c>null</c> if cancelled.</summary>
    private static TransferMode? AskTransferMode()
    {
        var dialog = new TransferModeDialog
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.SelectedMode : null;
    }

    /// <summary>
    /// Archive Now — compresses the passed-in items (or the current top-pane folder if none provided)
    /// into a single timestamped <c>.zip</c> at a user-chosen destination folder.
    /// Shows completion via toast + status bar.
    /// </summary>
    /// <param name="selection">Items bound from the caller's ListView selection; may be <c>null</c> or empty.
    /// Typed as the non-generic <see cref="System.Collections.IList"/> so WPF's <c>SelectedItemCollection</c>
    /// binds cleanly — RelayCommand&lt;IList&lt;FileSystemItem&gt;&gt; throws at bind time on that type.</param>
    [RelayCommand]
    private async Task ArchiveNowAsync(System.Collections.IList? selection)
    {
        // Prefer the caller's selection; fall back to the current top-pane folder.
        var selectedPaths = selection?.OfType<FileSystemItem>().Select(i => i.FullPath).ToList();
        var items = (selectedPaths != null && selectedPaths.Count > 0)
            ? selectedPaths
            : !string.IsNullOrEmpty(TopCurrentPath)
                ? new List<string> { TopCurrentPath }
                : new List<string>();

        if (items.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Select one or more files or folders to archive, or navigate into a folder first.",
                "Archive Now", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var folderDialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select destination folder for the archive" };
        if (folderDialog.ShowDialog() != true) return;
        var destDir = folderDialog.FolderName;

        var archiveName = $"archive_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";

        BeginTransfer();
        StatusMessage = $"Compressing {items.Count} item(s)...";
        FileLogger.Info($"Archive Now started: {items.Count} item(s) → {destDir}\\{archiveName}");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.CompressAsync(items, destDir, archiveName,
                exclusions: null,
                cancellationToken: _transferCts!.Token,
                pauseToken: _pauseGate,
                progress: progress,
                progressPercent: progressPercent);
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Archive Now completed: {summary}");
            EndTransfer($"Archive saved: {archiveName} — {summary}");
            NotifyTransferCompletion("Archive complete", result);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Archive Now cancelled by user");
            EndTransfer("Archive stopped.");
            ToastNotifier.Notify("Archive stopped", "Compression was cancelled.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Archive Now failed — insufficient space: {ex.Message}");
            EndTransfer("Archive aborted — not enough space.");
            ToastNotifier.Notify("Archive failed", "Not enough disk space on the destination drive.", ToastKind.Error);
        }
    }

    /// <summary>
    /// Extract Here — extracts a <c>.zip</c> into a sibling folder next to the archive.
    /// Folder name is derived from the archive's base name; disambiguated with a counter if it already exists.
    /// </summary>
    [RelayCommand]
    private async Task ExtractHereAsync(FileSystemItem? archive)
    {
        if (!TryResolveArchive(archive, out var archivePath)) return;

        var parentDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrEmpty(parentDir))
        {
            System.Windows.MessageBox.Show(
                "Cannot determine a folder next to this archive.",
                "Extract", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Build a unique target folder: "name", "name (2)", "name (3)", …
        // TryResolveArchive guarantees archivePath is non-null on the true branch.
        var baseName = Path.GetFileNameWithoutExtension(archivePath!);
        var destDir = Path.Combine(parentDir, baseName);
        int counter = 2;
        while (Directory.Exists(destDir) || File.Exists(destDir))
        {
            destDir = Path.Combine(parentDir, $"{baseName} ({counter})");
            counter++;
        }

        await RunExtractAsync(archivePath!, destDir);
    }

    /// <summary>
    /// Extract To… — prompts for a destination folder and extracts into it.
    /// Existing files are skipped (safer default); users who want to overwrite can re-extract to an empty folder.
    /// </summary>
    [RelayCommand]
    private async Task ExtractToAsync(FileSystemItem? archive)
    {
        if (!TryResolveArchive(archive, out var archivePath)) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder to extract into" };
        if (dlg.ShowDialog() != true) return;

        await RunExtractAsync(archivePath!, dlg.FolderName);
    }

    /// <summary>
    /// Validates that <paramref name="item"/> is a <c>.zip</c> file and resolves its full path.
    /// Shows a user-facing message if validation fails. Kept as a single point of enforcement
    /// so both Extract commands treat bad input identically.
    /// </summary>
    private bool TryResolveArchive(FileSystemItem? item, out string? archivePath)
    {
        archivePath = null;
        if (item == null || item.IsDirectory || string.IsNullOrEmpty(item.FullPath))
        {
            System.Windows.MessageBox.Show(
                "Select a .zip archive first.",
                "Extract", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return false;
        }
        if (!item.FullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(
                "Extract only works on .zip archives. This item doesn't appear to be one.",
                "Extract", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return false;
        }

        archivePath = item.FullPath;
        return true;
    }

    /// <summary>Shared transfer-lifecycle wrapper around <see cref="TransferService.ExtractAsync"/>.</summary>
    private async Task RunExtractAsync(string archivePath, string destDir)
    {
        BeginTransfer();
        StatusMessage = $"Extracting {Path.GetFileName(archivePath)}...";
        FileLogger.Info($"Extract started: {archivePath} → {destDir}");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.ExtractAsync(archivePath, destDir,
                overwriteExisting: false,
                cancellationToken: _transferCts!.Token,
                pauseToken: _pauseGate,
                progress: progress,
                progressPercent: progressPercent);
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Extract completed: {summary}");
            EndTransfer($"Extracted to {destDir} — {summary}");
            NotifyTransferCompletion("Extract complete", result);
            // If the destination's parent is the current top-pane folder, re-navigate to refresh
            // the listing so the extracted folder appears without the user manually hitting F5.
            var parentOfDest = Path.GetDirectoryName(destDir);
            if (!string.IsNullOrEmpty(parentOfDest) &&
                string.Equals(TopCurrentPath, parentOfDest, StringComparison.OrdinalIgnoreCase))
            {
                await NavigateTop(parentOfDest);
            }
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Extract cancelled by user");
            EndTransfer("Extract stopped.");
            ToastNotifier.Notify("Extract stopped", "Extraction was cancelled.", ToastKind.Warning);
        }
    }

    /// <summary>Copies selected items from the top pane to the bottom pane's current directory.</summary>
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
            NotifyTransferCompletion("Copy complete", result);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Copy cancelled by user");
            EndTransfer("Transfer stopped.");
            ToastNotifier.Notify("Copy stopped", "Transfer was cancelled.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Copy failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            ToastNotifier.Notify("Copy failed", "Not enough disk space on the destination drive.", ToastKind.Error);
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Copies selected items from the bottom pane to the top pane's current directory.</summary>
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
            NotifyTransferCompletion("Copy complete", result);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Copy cancelled by user");
            EndTransfer("Transfer stopped.");
            ToastNotifier.Notify("Copy stopped", "Transfer was cancelled.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Copy failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            ToastNotifier.Notify("Copy failed", "Not enough disk space on the destination drive.", ToastKind.Error);
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Moves selected items from the top pane to the bottom pane's current directory.</summary>
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
            NotifyTransferCompletion("Move complete", result);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Move cancelled by user");
            EndTransfer("Transfer stopped.");
            ToastNotifier.Notify("Move stopped", "Transfer was cancelled.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Move failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            ToastNotifier.Notify("Move failed", "Not enough disk space on the destination drive.", ToastKind.Error);
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Moves selected items from the bottom pane to the top pane's current directory.</summary>
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
            NotifyTransferCompletion("Move complete", result);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Move cancelled by user");
            EndTransfer("Transfer stopped.");
            ToastNotifier.Notify("Move stopped", "Transfer was cancelled.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Move failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            ToastNotifier.Notify("Move failed", "Not enough disk space on the destination drive.", ToastKind.Error);
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Executes a backup job created by the wizard, with progress tracking and throttle support.</summary>
    public async Task RunWizardBackupAsync(ScheduledJob job)
    {
        BeginTransfer();
        StatusMessage = $"Wizard backup: {job.Name}...";
        FileLogger.Info($"Wizard backup started: {job.Name} — {job.SourcePaths.Count} source(s) → {job.DestinationPath} (mode={job.TransferMode})");
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        long throttle = job.ThrottleMBps * 1024L * 1024L;
        try
        {
            var versioning = job.EnableVersioning
                ? new VersioningOptions { Enabled = true, MaxVersions = job.MaxVersions }
                : null;

            TransferResult result;
            if (job.EnableCompression)
            {
                result = await _transfer.CompressAsync(
                    job.SourcePaths, job.DestinationPath,
                    archiveName: $"{SanitizeName(job.Name)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip",
                    exclusions: job.ExclusionFilters.Count > 0 ? job.ExclusionFilters : null,
                    cancellationToken: _transferCts!.Token,
                    pauseToken: _pauseGate,
                    progress: progress,
                    progressPercent: progressPercent);
            }
            else
            {
                result = await _transfer.CopyAsync(
                    job.SourcePaths, job.DestinationPath, job.StripPermissions,
                    job.TransferMode, progress, progressPercent,
                    _transferCts!.Token, _pauseGate, job.VerifyChecksums,
                    exclusions: job.ExclusionFilters.Count > 0 ? job.ExclusionFilters : null,
                    throttleBytesPerSec: throttle,
                    versioning: versioning);
            }
            var summary = FormatTransferResult(result);
            FileLogger.Info($"Wizard backup completed: {summary}");
            EndTransfer(summary);
            NotifyTransferCompletion($"Backup complete: {job.Name}", result);
        }
        catch (OperationCanceledException)
        {
            FileLogger.Warn("Wizard backup cancelled by user");
            EndTransfer("Transfer stopped.");
            ToastNotifier.Notify($"Backup stopped: {job.Name}", "Transfer was cancelled.", ToastKind.Warning);
        }
        catch (InsufficientSpaceException ex)
        {
            FileLogger.Error($"Wizard backup failed — insufficient space: {ex.Message}");
            EndTransfer("Transfer aborted — not enough space.");
            ToastNotifier.Notify($"Backup failed: {job.Name}", "Not enough disk space on the destination drive.", ToastKind.Error);
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Permanently deletes the specified item from disk and removes it from both panes.</summary>
    [RelayCommand]
    private void DeleteItem(FileSystemItem item)
    {
        FileLogger.Info($"Delete: {item.FullPath}");
        _fs.DeleteItem(item.FullPath);
        TopPaneItems.Remove(item);
        BottomPaneItems.Remove(item);
    }

    /// <summary>Renames a file or folder on disk and refreshes both panes.</summary>
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

    /// <summary>Reloads drives and re-navigates both panes, clearing any stale paths.</summary>
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

    /// <summary>Strips invalid filename characters from a job name so it can be embedded in an archive filename.</summary>
    private static string SanitizeName(string name)
    {
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        // Windows rejects filenames ending in '.' or whitespace — strip those too.
        safe = safe.TrimEnd(' ', '.', '\t');
        return string.IsNullOrWhiteSpace(safe) ? "backup" : safe;
    }

    /// <summary>
    /// Fires a Windows toast notification summarizing the outcome of a completed transfer.
    /// Warning kind if there were any per-file errors; otherwise success.
    /// </summary>
    private static void NotifyTransferCompletion(string title, TransferResult result)
    {
        var totalFailed = result.FilesFailed + result.DirectoriesFailed + result.DiskFullErrors + result.FilesLocked + result.ChecksumMismatches;
        var body = totalFailed > 0
            ? $"{result.FilesCopied} copied, {totalFailed} failed."
            : $"{result.FilesCopied} copied, {result.FilesSkipped} skipped.";
        var kind = totalFailed > 0 ? ToastKind.Warning : ToastKind.Success;
        ToastNotifier.Notify(title, body, kind);
    }

    /// <summary>Builds a human-readable summary string from a <see cref="TransferResult"/>.</summary>
    internal static string FormatTransferResult(TransferResult result)
    {
        var parts = new List<string>();
        if (result.FilesCopied > 0) parts.Add($"{result.FilesCopied} copied");
        if (result.FilesCopiedViaVss > 0) parts.Add($"{result.FilesCopiedViaVss} via shadow copy");
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
        Color.FromRgb(0x38, 0xA8, 0xEB), // blue (darkened for light mode contrast)
        Color.FromRgb(0xF0, 0x5C, 0x7A), // rose
        Color.FromRgb(0x2E, 0xBD, 0x60), // green (darkened for light mode contrast)
        Color.FromRgb(0xE8, 0x90, 0x08), // amber
        Color.FromRgb(0x93, 0x78, 0xE8), // violet
        Color.FromRgb(0xE8, 0x5C, 0x5C), // red
        Color.FromRgb(0x28, 0x9E, 0xDD), // sky (darkened for light mode contrast)
        Color.FromRgb(0xEB, 0x82, 0x2C), // orange
        Color.FromRgb(0x22, 0xB5, 0x80), // emerald (darkened for light mode contrast)
        Color.FromRgb(0xD4, 0x66, 0xE8), // fuchsia
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

    /// <summary>
    /// Builds pie chart slices from the top-10 largest items plus an "Other" slice
    /// and assigns them atomically to avoid UI collection-change storms.
    /// </summary>
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
                FillColor = Color.FromRgb(0x88, 0x88, 0x98),
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
