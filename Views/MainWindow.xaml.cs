using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.ViewModels;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ListView = System.Windows.Controls.ListView;
using Point = System.Windows.Point;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject = System.Windows.DataObject;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

namespace BeetsBackup.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;
    private List<FileSystemItem>? _clipboard;
    private bool _clipboardIsCut;

    private Point _dragStartPoint;
    private bool _isDragging;
    private System.Windows.Controls.ListViewItem? _deferredSelectItem;

    // Column sorting state per ListView
    private readonly Dictionary<ListView, (GridViewColumnHeader header, ListSortDirection direction)> _sortState = new();

    // System tray
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isReallyClosing;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        // Load icon: try extracting from the exe (works with single-file publish)
        System.Drawing.Icon? icon = null;
        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            try { icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath); }
            catch { /* fall through to default */ }
        }

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Show Beet's Backup", null, (_, _) => RestoreFromTray());
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add("Quit", null, (_, _) => QuitApplication());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "Beet's Backup",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitApplication()
    {
        _isReallyClosing = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Minimize to tray instead of taskbar
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isReallyClosing)
        {
            // Hide to tray instead of closing
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasSearchResults))
        {
            var width = Vm.HasSearchResults ? 240.0 : 0.0;
            TopPathColumn.Width = width;
            SplitTopPathColumn.Width = width;
            SplitBottomPathColumn.Width = width;
        }
    }

    // -- Tree selection --
    private void TopTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeItem folder)
            Vm.SelectTopTreeFolderCommand.Execute(folder);
    }

    private void BottomTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderTreeItem folder)
            Vm.SelectBottomTreeFolderCommand.Execute(folder);
    }

    // -- Navigation (double-click to open folder) --
    private void PaneItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HitTestItemNotScrollBar(e.OriginalSource as DependencyObject) == null) return;
        if (TopPaneList.SelectedItem is FileSystemItem item)
            HandleDoubleClick(item, isTop: true);
    }

    private void SplitTopItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HitTestItemNotScrollBar(e.OriginalSource as DependencyObject) == null) return;
        if (SplitTopList.SelectedItem is FileSystemItem item)
            HandleDoubleClick(item, isTop: true);
    }

    private void SplitBottomItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HitTestItemNotScrollBar(e.OriginalSource as DependencyObject) == null) return;
        if (SplitBottomList.SelectedItem is FileSystemItem item)
            HandleDoubleClick(item, isTop: false);
    }

    private void HandleDoubleClick(FileSystemItem item, bool isTop)
    {
        if (item.IsDirectory)
        {
            if (isTop)
                Vm.NavigateTopCommand.Execute(item.FullPath);
            else
                Vm.NavigateBottomCommand.Execute(item.FullPath);
        }
        else
        {
            MessageBox.Show("Files cannot be opened from within Beet's Backup. Please use Windows Explorer to open files.", "Beet's Backup",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // -- Preserve multi-selection on right-click --
    private void Pane_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list) return;

        // Hit-test to find the item under the cursor
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.ListViewItem)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);

        if (hit is System.Windows.Controls.ListViewItem item && item.IsSelected)
        {
            // Item is already part of multi-selection — don't let WPF deselect the others
            e.Handled = true;

            // Manually open the context menu since we suppressed the event
            if (list.ContextMenu != null)
            {
                list.ContextMenu.PlacementTarget = list;
                list.ContextMenu.IsOpen = true;
            }
        }
    }

    // -- Drag & Drop --

    /// Walk up the visual tree from the source element. Returns the first
    /// ListViewItem ancestor, or null if the click didn't land on a row
    /// (e.g. scrollbar, empty space, header).
    private static System.Windows.Controls.ListViewItem? HitTestListViewItem(DependencyObject? source)
    {
        var d = source;
        while (d != null)
        {
            if (d is System.Windows.Controls.ListViewItem lvi)
                return lvi;
            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : System.Windows.LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// Walk up the visual tree looking for a ScrollBar or ScrollViewer chrome.
    /// Returns true when the click landed on the scrollbar, its track, thumb,
    /// or repeat-buttons — even if a ListViewItem row extends underneath.
    private static bool IsOverScrollBar(DependencyObject? source)
    {
        var d = source;
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : System.Windows.LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// Combined check: returns the ListViewItem ONLY when the click is on a
    /// real data row AND not on the scrollbar overlay.
    private static System.Windows.Controls.ListViewItem? HitTestItemNotScrollBar(DependencyObject? source)
    {
        if (IsOverScrollBar(source)) return null;
        return HitTestListViewItem(source);
    }

    private void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only act when the click lands on an actual list row.
        // This ignores scrollbars, headers, empty space, etc.
        var hitItem = HitTestItemNotScrollBar(e.OriginalSource as DependencyObject);
        if (hitItem == null)
            return;

        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
        _deferredSelectItem = null;

        // If clicking on an already-selected item, suppress selection change so
        // multi-select is preserved for drag-and-drop.  Selection will be applied
        // on mouse-up if the user didn't drag.
        if (hitItem.IsSelected &&
            sender is ListView list && list.SelectedItems.Count > 1 &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _deferredSelectItem = hitItem;
            e.Handled = true;
        }
    }

    private void Pane_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Only act when the release lands on an actual list row
        if (HitTestItemNotScrollBar(e.OriginalSource as DependencyObject) == null)
        {
            _deferredSelectItem = null;
            return;
        }

        // Complete deferred single-select if the user clicked without dragging
        if (_deferredSelectItem != null && !_isDragging)
        {
            if (sender is ListView list)
            {
                list.SelectedItems.Clear();
                _deferredSelectItem.IsSelected = true;
            }
        }
        _deferredSelectItem = null;
    }

    private void Pane_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;
        if (HitTestItemNotScrollBar(e.OriginalSource as DependencyObject) == null) return;

        var pos = e.GetPosition(null);
        var diff = _dragStartPoint - pos;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (sender is not ListView sourceList) return;
        var items = GetSelectedItems(sourceList);
        if (items.Count == 0) return;

        _isDragging = true;
        var data = new DataObject("FileSystemItems", items);
        DragDrop.DoDragDrop(sourceList, data, DragDropEffects.Copy);
        _isDragging = false;
    }

    private async void SplitTop_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileSystemItems")) return;
        if (e.Data.GetData("FileSystemItems") is List<FileSystemItem> items)
            await Vm.CopyToTopCommand.ExecuteAsync(items);
    }

    private async void SplitBottom_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileSystemItems")) return;
        if (e.Data.GetData("FileSystemItems") is List<FileSystemItem> items)
            await Vm.CopyToBottomCommand.ExecuteAsync(items);
    }

    private async void SinglePane_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("FileSystemItems")) return;
        if (e.Data.GetData("FileSystemItems") is List<FileSystemItem> items)
            await Vm.CopyToTopCommand.ExecuteAsync(items);
    }

    private void Pane_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("FileSystemItems")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    // -- Backup Wizard (placeholder) --
    private void BackupWizard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MessageBox.Show("Backup Wizard is coming soon!", "Beet's Backup",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // -- Options dropdown --
    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    // -- Context menu: Open in Explorer --
    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var item = TopPaneList.SelectedItem as FileSystemItem
                ?? SplitTopList.SelectedItem as FileSystemItem
                ?? SplitBottomList.SelectedItem as FileSystemItem;
        if (item == null) return;

        if (item.IsDirectory)
        {
            Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
        }
        else
        {
            // Select the file in Explorer
            Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        }
    }

    // -- Context menu: single pane --
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        _clipboard = GetSelectedItems(TopPaneList);
        _clipboardIsCut = false;
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        _clipboard = GetSelectedItems(TopPaneList);
        _clipboardIsCut = true;
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_clipboard == null || string.IsNullOrEmpty(Vm.TopCurrentPath)) return;
        if (_clipboardIsCut)
            await Vm.MoveToTopCommand.ExecuteAsync(_clipboard);
        else
            await Vm.CopyToTopCommand.ExecuteAsync(_clipboard);
        _clipboard = null;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = GetAllSelectedItems();
        if (items.Count == 0) return;

        var names = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        var result = MessageBox.Show(
            $"Are you sure you want to delete {names}?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        foreach (var item in items)
            Vm.DeleteItemCommand.Execute(item);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = TopPaneList.SelectedItem as FileSystemItem
                ?? SplitTopList.SelectedItem as FileSystemItem
                ?? SplitBottomList.SelectedItem as FileSystemItem;
        if (item == null) return;

        var dialog = new RenameDialog(item.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            Vm.RenameItemCommand.Execute((item, dialog.NewName));
        }
    }

    // -- Context menu: split pane --
    private async void CopyToBottom_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedItems(SplitTopList);
        await Vm.CopyToBottomCommand.ExecuteAsync(items);
    }

    private async void CutToBottom_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedItems(SplitTopList);
        await Vm.MoveToBottomCommand.ExecuteAsync(items);
    }

    private async void CopyToTop_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedItems(SplitBottomList);
        await Vm.CopyToTopCommand.ExecuteAsync(items);
    }

    private async void CutToTop_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedItems(SplitBottomList);
        await Vm.MoveToTopCommand.ExecuteAsync(items);
    }

    // -- Search --
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Vm.ExecuteDeepSearchCommand.Execute(null);
    }

    // -- Column sorting --
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;
        if (sender is not ListView listView) return;

        var propertyName = GetSortProperty(header);
        if (propertyName == null) return;

        // Determine the ICollectionView for this ListView
        var view = listView == SplitBottomList
            ? Vm.FilteredBottomPaneItems
            : Vm.FilteredTopPaneItems;

        // Toggle direction or set ascending for new column
        var direction = ListSortDirection.Ascending;
        if (_sortState.TryGetValue(listView, out var prev))
        {
            // Remove indicator from previous header
            StripSortIndicator(prev.header);
            if (prev.header == header)
                direction = prev.direction == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
        }

        _sortState[listView] = (header, direction);

        // Apply sort: folders first, then by chosen column
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(FileSystemItem.IsDirectory), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));
        }

        // Update header text with indicator
        var arrow = direction == ListSortDirection.Ascending ? " \u25B2" : " \u25BC";
        var baseText = GetBaseHeaderText(header);
        header.Content = baseText + arrow;
    }

    private static string? GetSortProperty(GridViewColumnHeader header)
    {
        var text = GetBaseHeaderText(header);
        return text switch
        {
            "Name" or "Name (Source)" or "Name (Destination)" => nameof(FileSystemItem.Name),
            "Type" => nameof(FileSystemItem.TypeDisplay),
            "Size" => nameof(FileSystemItem.Size),
            "Modified" => nameof(FileSystemItem.Modified),
            _ => null
        };
    }

    private static string GetBaseHeaderText(GridViewColumnHeader header)
    {
        var text = header.Content?.ToString() ?? "";
        // Strip any existing sort indicator
        return text.TrimEnd(' ', '\u25B2', '\u25BC');
    }

    private static void StripSortIndicator(GridViewColumnHeader header)
    {
        header.Content = GetBaseHeaderText(header);
    }

    // -- Pie chart slice navigation --
    private void TopPieChart_SliceClicked(object? sender, PieSlice slice)
    {
        if (slice.IsDirectory && slice.FullPath != null)
            Vm.NavigateTopCommand.Execute(slice.FullPath);
    }

    private void BottomPieChart_SliceClicked(object? sender, PieSlice slice)
    {
        if (slice.IsDirectory && slice.FullPath != null)
            Vm.NavigateBottomCommand.Execute(slice.FullPath);
    }

    // -- Helpers --
    private static List<FileSystemItem> GetSelectedItems(ListView list) =>
        list.SelectedItems.Cast<FileSystemItem>().ToList();

    private List<FileSystemItem> GetAllSelectedItems()
    {
        var items = GetSelectedItems(TopPaneList);
        if (items.Count == 0) items = GetSelectedItems(SplitTopList);
        if (items.Count == 0) items = GetSelectedItems(SplitBottomList);
        return items;
    }
}
