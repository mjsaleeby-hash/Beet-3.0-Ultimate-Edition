using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BeetsBackup.Models;

public class FileSystemItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsHidden { get; }
    public DateTime Modified { get; }

    private long _size;
    public long Size
    {
        get => _size;
        private set
        {
            if (_size != value)
            {
                _size = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(IsCalculating));
            }
        }
    }

    public bool IsCalculating => IsDirectory && _size < 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public FileSystemItem(FileSystemInfo info)
    {
        Name = info.Name;
        FullPath = info.FullName;
        IsDirectory = info is DirectoryInfo;
        IsHidden = info.Attributes.HasFlag(FileAttributes.Hidden);
        Modified = info.LastWriteTime;
        _size = info is FileInfo fi ? fi.Length : -1;
    }

    public string SizeDisplay => IsDirectory
        ? (_size < 0 ? "Calculating..." : FormatBytes(_size))
        : FormatBytes(_size);

    public async Task CalculateDirectorySizeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDirectory) return;
        var path = FullPath;
        var size = await Task.Run(() => CalculateDirectorySize(path, cancellationToken), cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        // Marshal to UI thread to avoid cross-thread binding exceptions
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(() => Size = size);
        else
            Size = size;
    }

    private static long CalculateDirectorySize(string path, CancellationToken cancellationToken)
    {
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.None,
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };
            long total = 0;
            var dirInfo = new DirectoryInfo(path);
            foreach (var fi in dirInfo.EnumerateFiles("*", options))
            {
                if (cancellationToken.IsCancellationRequested) return 0;
                try { total += fi.Length; }
                catch { /* skip inaccessible files */ }
            }
            return total;
        }
        catch { return 0; }
    }

    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? string.Empty;
    public string TypeDisplay => IsDirectory ? "Folder" : Path.GetExtension(Name).TrimStart('.').ToUpper() + " File";
    public string Icon => IsDirectory ? "📁" : "📄";

    internal static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }
}
