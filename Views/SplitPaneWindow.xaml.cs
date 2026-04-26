using BeetsBackup.Models;
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

namespace BeetsBackup.Views;

public partial class SplitPaneWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    private Point _dragStartPoint;
    private bool _isDragging;
    private System.Windows.Controls.ListViewItem? _deferredSelectItem;

    private readonly Dictionary<ListView, (GridViewColumnHeader header, ListSortDirection direction)> _sortState = new();

    public SplitPaneWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasSearchResults))
        {
            var width = Vm.HasSearchResults ? 240.0 : 0.0;
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
            MessageBox.Show("Files cannot be opened from within Beet's Backup. Please use Windows Explorer to open files.",
                "Beet's Backup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // -- Preserve multi-selection on right-click --
    private void Pane_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list) return;

        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.ListViewItem)
        {
            hit = hit is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(hit)
                : System.Windows.LogicalTreeHelper.GetParent(hit);
        }

        if (hit is System.Windows.Controls.ListViewItem item && item.IsSelected)
        {
            e.Handled = true;
            if (list.ContextMenu != null)
            {
                list.ContextMenu.PlacementTarget = list;
                list.ContextMenu.IsOpen = true;
            }
        }
    }

    // -- Drag & Drop --
    private static System.Windows.Controls.ListViewItem? HitTestListViewItem(DependencyObject? source)
    {
        var d = source;
        while (d != null)
        {
            if (d is System.Windows.Controls.ListViewItem lvi) return lvi;
            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : System.Windows.LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

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

    private static System.Windows.Controls.ListViewItem? HitTestItemNotScrollBar(DependencyObject? source)
    {
        if (IsOverScrollBar(source)) return null;
        return HitTestListViewItem(source);
    }

    private void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hitItem = HitTestItemNotScrollBar(e.OriginalSource as DependencyObject);
        if (hitItem == null) return;

        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
        _deferredSelectItem = null;

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
        if (HitTestItemNotScrollBar(e.OriginalSource as DependencyObject) == null)
        {
            _deferredSelectItem = null;
            return;
        }

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

    private void Pane_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("FileSystemItems")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    // -- Context menu: Open in Explorer --
    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var item = SplitTopList.SelectedItem as FileSystemItem
                ?? SplitBottomList.SelectedItem as FileSystemItem;
        if (item == null) return;

        if (item.IsDirectory)
            Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
        else
            Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
    }

    // -- Context menu: Copy/Cut/Move between panes --
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

    // -- Context menu: Delete --
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

    // -- Context menu: Extract archive --
    private async void ExtractHere_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedArchive();
        if (item != null) await Vm.ExtractHereCommand.ExecuteAsync(item);
    }

    private async void ExtractTo_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedArchive();
        if (item != null) await Vm.ExtractToCommand.ExecuteAsync(item);
    }

    private FileSystemItem? GetSelectedArchive() =>
        SplitTopList.SelectedItem as FileSystemItem
        ?? SplitBottomList.SelectedItem as FileSystemItem;

    // -- Context menu: Previous versions --
    private void PreviousVersions_Click(object sender, RoutedEventArgs e)
    {
        var item = SplitTopList.SelectedItem as FileSystemItem
                ?? SplitBottomList.SelectedItem as FileSystemItem;
        if (item == null) return;

        if (item.IsDirectory)
        {
            MessageBox.Show(this, "Previous versions are only tracked for files, not folders.",
                "Previous Versions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (BeetsBackup.Services.VersioningService.FindVersionsRoot(item.FullPath) == null)
        {
            MessageBox.Show(this,
                "No version history exists for this file.\n\n" +
                "Previous Versions is only populated when a file has been overwritten by a backup " +
                "job that had versioning enabled.",
                "Previous Versions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PreviousVersionsDialog(item.FullPath) { Owner = this };
        dialog.ShowDialog();
    }

    // -- Context menu: Rename --
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = SplitTopList.SelectedItem as FileSystemItem
                ?? SplitBottomList.SelectedItem as FileSystemItem;
        if (item == null) return;

        var dialog = new RenameDialog(item.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
            Vm.RenameItemCommand.Execute((item, dialog.NewName));
    }

    // -- Search --
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Vm.ExecuteDeepSearchCommand.Execute(null);
    }

    private void BottomSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Vm.ExecuteBottomDeepSearchCommand.Execute(null);
    }

    // -- Column sorting --
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;
        if (sender is not ListView listView) return;

        var propertyName = GetSortProperty(header);
        if (propertyName == null) return;

        var view = listView == SplitBottomList
            ? Vm.FilteredBottomPaneItems
            : Vm.FilteredTopPaneItems;

        var direction = ListSortDirection.Ascending;
        if (_sortState.TryGetValue(listView, out var prev))
        {
            StripSortIndicator(prev.header);
            if (prev.header == header)
                direction = prev.direction == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
        }

        _sortState[listView] = (header, direction);

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(FileSystemItem.IsDirectory), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));
        }

        var arrow = direction == ListSortDirection.Ascending ? " ▲" : " ▼";
        header.Content = GetBaseHeaderText(header) + arrow;
    }

    private static string? GetSortProperty(GridViewColumnHeader header)
    {
        return GetBaseHeaderText(header) switch
        {
            "Name" or "Name (Source)" or "Name (Destination)" => nameof(FileSystemItem.Name),
            "Type" => nameof(FileSystemItem.TypeDisplay),
            "Size" => nameof(FileSystemItem.Size),
            "Modified" => nameof(FileSystemItem.Modified),
            _ => null
        };
    }

    private static string GetBaseHeaderText(GridViewColumnHeader header) =>
        (header.Content?.ToString() ?? "").TrimEnd(' ', '▲', '▼');

    private static void StripSortIndicator(GridViewColumnHeader header) =>
        header.Content = GetBaseHeaderText(header);

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
        var items = GetSelectedItems(SplitTopList);
        if (items.Count == 0) items = GetSelectedItems(SplitBottomList);
        return items;
    }
}
