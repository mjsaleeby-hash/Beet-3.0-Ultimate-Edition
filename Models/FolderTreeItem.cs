using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BeetsBackup.Models;

public class FolderTreeItem : INotifyPropertyChanged
{
    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDrive { get; }
    public DriveItem? DriveInfo { get; }

    public ObservableCollection<FolderTreeItem> Children { get; } = new();

    private bool _isExpanded;
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
                    LoadChildren();
            }
        }
    }

    private bool _isSelected;
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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Drive root constructor
    public FolderTreeItem(DriveItem drive)
    {
        Name = drive.Name;
        FullPath = drive.RootPath;
        IsDrive = true;
        DriveInfo = drive;
        AddPlaceholder();
    }

    // Subfolder constructor
    public FolderTreeItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
            Name = path;
        IsDrive = false;
        AddPlaceholder();
    }

    private void AddPlaceholder()
    {
        // Add a dummy child so the expander arrow shows
        Children.Add(new FolderTreeItem("__placeholder__", dummy: true));
        _hasPlaceholder = true;
    }

    // Dummy placeholder constructor
    private FolderTreeItem(string name, bool dummy)
    {
        Name = name;
        FullPath = string.Empty;
        IsDrive = false;
        _hasPlaceholder = false;
    }

    private void LoadChildren()
    {
        _hasPlaceholder = false;
        Children.Clear();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath, "*", EnumOptions))
            {
                Children.Add(new FolderTreeItem(dir));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
