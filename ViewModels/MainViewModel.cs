using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;

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

    // --- Search ---
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

    // --- Navigation history ---
    private readonly List<string> _topHistory = new();
    private int _topHistoryIndex = -1;
    private bool _isNavigatingHistory;

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
    private void NavigateTop(string path)
    {
        _topSizeCts?.Cancel();
        _topSizeCts = new CancellationTokenSource();
        TopCurrentPath = path;
        TopPaneItems.Clear();
        foreach (var item in _fs.GetChildren(path))
            TopPaneItems.Add(item);
        _filteredTopView?.Refresh();
        _ = CalculateFolderSizesAsync(TopPaneItems, _topSizeCts.Token);

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
    private void NavigateBottom(string path)
    {
        _bottomSizeCts?.Cancel();
        _bottomSizeCts = new CancellationTokenSource();
        BottomCurrentPath = path;
        BottomPaneItems.Clear();
        foreach (var item in _fs.GetChildren(path))
            BottomPaneItems.Add(item);
        _filteredBottomView?.Refresh();
        _ = CalculateFolderSizesAsync(BottomPaneItems, _bottomSizeCts.Token);
    }

    private static async Task CalculateFolderSizesAsync(ObservableCollection<FileSystemItem> items, CancellationToken cancellationToken)
    {
        var directories = items.Where(i => i.IsDirectory).ToList();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };
        try
        {
            await Parallel.ForEachAsync(directories, parallelOptions, async (dir, ct) =>
            {
                await dir.CalculateDirectorySizeAsync(ct);
            });
        }
        catch (OperationCanceledException) { }
    }

    // --- Navigation history ---
    private bool CanGoBack() => _topHistoryIndex > 0;
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        _topHistoryIndex--;
        _isNavigatingHistory = true;
        NavigateTop(_topHistory[_topHistoryIndex]);
        _isNavigatingHistory = false;
    }

    private bool CanGoForward() => _topHistoryIndex < _topHistory.Count - 1;
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        _topHistoryIndex++;
        _isNavigatingHistory = true;
        NavigateTop(_topHistory[_topHistoryIndex]);
        _isNavigatingHistory = false;
    }

    private bool CanGoUp() => !string.IsNullOrEmpty(TopCurrentPath) && Directory.GetParent(TopCurrentPath) != null;
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        var parent = Directory.GetParent(TopCurrentPath);
        if (parent != null)
            NavigateTop(parent.FullName);
    }

    [RelayCommand]
    private void SelectTopDrive(DriveItem? drive)
    {
        if (drive == null) return;
        SelectedTopDrive = drive;
        NavigateTop(drive.RootPath);
    }

    [RelayCommand]
    private void SelectBottomDrive(DriveItem? drive)
    {
        if (drive == null) return;
        SelectedBottomDrive = drive;
        NavigateBottom(drive.RootPath);
    }

    [RelayCommand]
    private void SelectTopTreeFolder(FolderTreeItem? folder)
    {
        if (folder == null) return;
        NavigateTop(folder.FullPath);
    }

    [RelayCommand]
    private void SelectBottomTreeFolder(FolderTreeItem? folder)
    {
        if (folder == null) return;
        NavigateBottom(folder.FullPath);
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
            NavigateBottom(BottomCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough space on destination dummy!", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            NavigateTop(TopCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough space on destination dummy!", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            NavigateTop(TopCurrentPath);
            NavigateBottom(BottomCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough space on destination dummy!", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            NavigateTop(TopCurrentPath);
            NavigateBottom(BottomCurrentPath);
            EndTransfer(FormatTransferResult(result));
        }
        catch (OperationCanceledException)
        {
            EndTransfer("Transfer stopped.");
        }
        catch (InsufficientSpaceException)
        {
            EndTransfer("Transfer aborted — not enough space.");
            System.Windows.MessageBox.Show("Not enough space on destination dummy!", "Beets Backup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
    private void RenameItem((FileSystemItem item, string newName) args)
    {
        var (item, newName) = args;
        _fs.RenameItem(item.FullPath, newName);
        if (!string.IsNullOrEmpty(TopCurrentPath))
            NavigateTop(TopCurrentPath);
        if (!string.IsNullOrEmpty(BottomCurrentPath))
            NavigateBottom(BottomCurrentPath);
    }

    [RelayCommand]
    private void RefreshDrives() => LoadDrives();

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

    private static string FormatTransferResult(TransferResult result)
    {
        var parts = new List<string>();
        if (result.FilesCopied > 0) parts.Add($"{result.FilesCopied} copied");
        if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped} skipped");
        if (result.FilesFailed > 0) parts.Add($"{result.FilesFailed} failed");
        if (result.ChecksumMismatches > 0) parts.Add($"{result.ChecksumMismatches} checksum mismatches!");
        return parts.Count > 0 ? $"Done — {string.Join(", ", parts)}" : "Done.";
    }
}
