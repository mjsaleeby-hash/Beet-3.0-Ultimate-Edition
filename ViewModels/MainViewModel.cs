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

public partial class MainViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly FileSystemService _fs;
    private readonly TransferService _transfer;
    private readonly SchedulerService _scheduler;
    private readonly BackupLogService _log;

    // --- Theme ---
    [ObservableProperty] private bool _isDarkMode;

    // --- Toolbar ---
    [ObservableProperty] private bool _removePermissions;
    [ObservableProperty] private bool _verifyChecksums;
    [ObservableProperty] private bool _isSplitPane;

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
    public ObservableCollection<PieSlice> TopPieSlices { get; } = new();
    public ObservableCollection<PieSlice> BottomPieSlices { get; } = new();

    // --- Status ---
    [ObservableProperty] private string _statusMessage = "Ready";

    // --- Transfer controls ---
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _transferProgressPercent;
    [ObservableProperty] private string _transferEta = string.Empty;

    private CancellationTokenSource? _transferCts;
    private ManualResetEventSlim _pauseGate = new(true);
    private DateTime _transferStartTime;

    private CancellationTokenSource? _topSizeCts;
    private CancellationTokenSource? _bottomSizeCts;
    private CancellationTokenSource? _topNavCts;
    private CancellationTokenSource? _bottomNavCts;

    public MainViewModel(ThemeService theme, FileSystemService fs, TransferService transfer, SchedulerService scheduler, BackupLogService log)
    {
        _theme = theme;
        _fs = fs;
        _transfer = transfer;
        _scheduler = scheduler;
        _log = log;
        IsDarkMode = theme.IsDark;
        LoadDrives();
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
            Drives.Add(drive);
            TopTreeItems.Add(new FolderTreeItem(drive));
            BottomTreeItems.Add(new FolderTreeItem(drive));
        }
    }

    [RelayCommand]
    private void PauseResumeTransfer()
    {
        if (!IsTransferring) return;
        if (IsPaused)
        {
            _pauseGate.Set();
            IsPaused = false;
            StatusMessage = "Resumed...";
        }
        else
        {
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
        _pauseGate.Set();
        IsTransferring = true;
        IsPaused = false;
        TransferProgressPercent = 0;
        TransferEta = string.Empty;
        _transferStartTime = DateTime.Now;
    }

    private void EndTransfer(string message)
    {
        IsTransferring = false;
        IsPaused = false;
        TransferProgressPercent = 0;
        TransferEta = string.Empty;
        StatusMessage = message;
    }

    private void OnTransferProgress(string msg) => StatusMessage = msg;

    private void OnTransferPercent(int percent)
    {
        TransferProgressPercent = percent;
        if (percent > 0)
        {
            var elapsed = DateTime.Now - _transferStartTime;
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
        _topNavCts?.Cancel();
        var navCts = new CancellationTokenSource();
        _topNavCts = navCts;

        _topSizeCts?.Cancel();
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
            TopPieSlices.Clear();
            TopTotalSize = "";
        }
        var sizeCts = _topSizeCts;
        _ = CalculateFolderSizesWithProgressAsync(TopPaneItems, TopPieSlices, true, sizeCts.Token);

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
        _bottomNavCts?.Cancel();
        var navCts = new CancellationTokenSource();
        _bottomNavCts = navCts;

        _bottomSizeCts?.Cancel();
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
            BottomPieSlices.Clear();
            BottomTotalSize = "";
        }
        var sizeCts = _bottomSizeCts;
        _ = CalculateFolderSizesWithProgressAsync(BottomPaneItems, BottomPieSlices, false, sizeCts.Token);

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
        ObservableCollection<PieSlice> slices,
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
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => BuildPieSlices(items, slices, isTop));
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

        await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
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
    [RelayCommand]
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

        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        IsSearching = true;
        HasSearchResults = true;
        ShowNoResults = false;

        var query = DeepSearchQuery.Trim();
        bool isExtensionSearch = query.StartsWith('.');

        // Clear pane for results
        TopPaneItems.Clear();
        _filteredTopView?.Refresh();

        var dispatcher = System.Windows.Application.Current.Dispatcher;

        await Task.Run(() =>
        {
            // Determine root paths to search
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(TopCurrentPath) && Directory.Exists(TopCurrentPath))
                roots.Add(TopCurrentPath);
            else
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                    roots.Add(drive.RootDirectory.FullName);
            }

            foreach (var root in roots)
            {
                if (token.IsCancellationRequested) return;
                SearchDirectoryRecursive(root, query, isExtensionSearch, token, dispatcher);
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        IsSearching = false;
        ShowNoResults = !token.IsCancellationRequested && TopPaneItems.Count == 0;
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
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.CopyAsync(items.Select(i => i.FullPath), BottomCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums);
            await NavigateBottom(BottomCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
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
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.CopyAsync(items.Select(i => i.FullPath), TopCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums);
            await NavigateTop(TopCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
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
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.MoveAsync(items.Select(i => i.FullPath), BottomCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums);
            await NavigateTop(TopCurrentPath);
            await NavigateBottom(BottomCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
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
        var progress = new Progress<string>(OnTransferProgress);
        var progressPercent = new Progress<int>(OnTransferPercent);
        try
        {
            var result = await _transfer.MoveAsync(items.Select(i => i.FullPath), TopCurrentPath, RemovePermissions, mode.Value, progress, progressPercent, _transferCts!.Token, _pauseGate, VerifyChecksums);
            await NavigateTop(TopCurrentPath);
            await NavigateBottom(BottomCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough disk space on the destination drive. Please free up space or choose a different destination.", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void DeleteItem(FileSystemItem item)
    {
        _fs.DeleteItem(item.FullPath);
        TopPaneItems.Remove(item);
        BottomPaneItems.Remove(item);
    }

    [RelayCommand]
    private async Task RenameItem((FileSystemItem item, string newName) args)
    {
        var (item, newName) = args;
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
        var dialog = new LogDialog(_log)
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
            BuildPieSlices(TopPaneItems, TopPieSlices, true);
    }

    [RelayCommand]
    private void ToggleBottomVisualMode()
    {
        IsBottomVisualMode = !IsBottomVisualMode;
        if (IsBottomVisualMode)
            BuildPieSlices(BottomPaneItems, BottomPieSlices, false);
    }

    internal void BuildPieSlices(ObservableCollection<FileSystemItem> items, ObservableCollection<PieSlice> slices, bool isTop)
    {
        slices.Clear();

        var sorted = items
            .Where(i => i.Size > 0)
            .OrderByDescending(i => i.Size)
            .Take(10)
            .ToList();

        if (sorted.Count == 0)
        {
            if (isTop) TopTotalSize = "";
            else BottomTotalSize = "";
            return;
        }

        long totalSize = items.Where(i => i.Size > 0).Sum(i => i.Size);
        if (totalSize <= 0) return;

        if (isTop) TopTotalSize = FileSystemItem.FormatBytes(totalSize);
        else BottomTotalSize = FileSystemItem.FormatBytes(totalSize);

        double angle = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            double pct = (double)sorted[i].Size / totalSize * 100.0;
            double sweep = pct / 100.0 * 360.0;
            slices.Add(new PieSlice
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
            slices.Add(new PieSlice
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
    }

    private void RebuildPieSlicesIfNeeded()
    {
        if (IsTopVisualMode)
            BuildPieSlices(TopPaneItems, TopPieSlices, true);
        if (IsBottomVisualMode)
            BuildPieSlices(BottomPaneItems, BottomPieSlices, false);
    }
}
