using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BeetsBackup.Models;

/// <summary>
/// Represents a folder node in the navigation tree view.
/// Children are loaded lazily on first expansion to keep initial load fast.
/// </summary>
public sealed class FolderTreeItem : INotifyPropertyChanged
{
    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    /// <summary>Display name of the folder or drive.</summary>
    public string Name { get; }

    /// <summary>Fully qualified path to this folder.</summary>
    public string FullPath { get; }

    /// <summary>Whether this node represents a drive root.</summary>
    public bool IsDrive { get; }

    /// <summary>Drive capacity information, populated only for drive root nodes.</summary>
    public DriveItem? DriveInfo { get; }

    /// <summary>Child folder nodes (populated lazily on first expansion).</summary>
    public ObservableCollection<FolderTreeItem> Children { get; } = new();

    private bool _isExpanded;

    /// <summary>
    /// Whether the tree node is expanded. Setting to <c>true</c> triggers lazy child loading
    /// if children have not been loaded yet.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
                if (value && _hasPlaceholder)
                    _ = LoadChildrenAsync();
            }
        }
    }

    private bool _isSelected;

    /// <summary>Whether this node is currently selected in the tree view.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _hasPlaceholder;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Creates a tree node for a drive root.
    /// </summary>
    /// <param name="drive">The drive to represent.</param>
    public FolderTreeItem(DriveItem drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Name = drive.Name;
        FullPath = drive.RootPath;
        IsDrive = true;
        DriveInfo = drive;
        AddPlaceholder();
    }

    /// <summary>
    /// Creates a tree node for a subdirectory.
    /// </summary>
    /// <param name="path">Full path to the directory.</param>
    public FolderTreeItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
            Name = path;
        IsDrive = false;
        AddPlaceholder();
    }

    /// <summary>
    /// Adds a placeholder child so the TreeView shows an expander arrow before the node is expanded.
    /// </summary>
    private void AddPlaceholder()
    {
        Children.Add(CreatePlaceholder());
        _hasPlaceholder = true;
    }

    /// <summary>
    /// Builds the lazy-load placeholder node — a throwaway child used only so the TreeView
    /// shows an expander arrow before the real children are enumerated.
    /// </summary>
    private static FolderTreeItem CreatePlaceholder() => new(placeholder: true);

    /// <summary>
    /// Private constructor for the placeholder node. The <paramref name="placeholder"/> parameter
    /// exists purely to select this overload — it's the factory method that carries the intent.
    /// </summary>
    private FolderTreeItem(bool placeholder)
    {
        _ = placeholder;
        Name = "__placeholder__";
        FullPath = string.Empty;
        IsDrive = false;
        _hasPlaceholder = false;
    }

    /// <summary>
    /// Asynchronously loads subdirectories as child nodes, replacing the placeholder.
    /// Skips junction points and symbolic links to avoid infinite recursion.
    /// </summary>
    private async Task LoadChildrenAsync()
    {
        _hasPlaceholder = false;
        Children.Clear();

        try
        {
            var dirs = await Task.Run(() =>
            {
                var result = new List<string>();
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(FullPath, "*", EnumOptions))
                    {
                        // Skip NTFS junction points and symbolic links to match
                        // Windows Explorer behavior and avoid enumeration loops.
                        var attr = File.GetAttributes(dir);
                        if (attr.HasFlag(FileAttributes.ReparsePoint))
                            continue;
                        result.Add(dir);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                return result;
            });

            foreach (var dir in dirs)
                Children.Add(new FolderTreeItem(dir));
        }
        catch { }
    }
}
