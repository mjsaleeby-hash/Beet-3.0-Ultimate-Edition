using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BeetsBackup.Models;

/// <summary>
/// Represents a file or directory entry in the file manager pane.
/// Supports asynchronous directory size calculation with UI-thread marshalling.
/// </summary>
public sealed class FileSystemItem : INotifyPropertyChanged
{
    /// <summary>File or directory name (without path).</summary>
    public string Name { get; }

    /// <summary>Fully qualified path.</summary>
    public string FullPath { get; }

    /// <summary>Whether this item is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Whether the file system hidden attribute is set.</summary>
    public bool IsHidden { get; }

    /// <summary>Last write time of the file or directory.</summary>
    public DateTime Modified { get; }

    private long _size;

    /// <summary>
    /// Size in bytes. For directories, starts at -1 (calculating) and is updated asynchronously.
    /// </summary>
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

    /// <summary>Whether an async directory size calculation is still in progress.</summary>
    public bool IsCalculating => IsDirectory && _size < 0;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Creates a <see cref="FileSystemItem"/> from a <see cref="FileSystemInfo"/> object.
    /// Files have their size set immediately; directories start with size -1 (pending calculation).
    /// </summary>
    /// <param name="info">The file or directory info to wrap.</param>
    public FileSystemItem(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Name = info.Name;
        FullPath = info.FullName;
        IsDirectory = info is DirectoryInfo;
        IsHidden = info.Attributes.HasFlag(FileAttributes.Hidden);
        Modified = info.LastWriteTime;
        _size = info is FileInfo fi ? fi.Length : -1;
    }

    /// <summary>Human-readable size display (e.g. "12.5 MB" or "Calculating...").</summary>
    public string SizeDisplay => IsDirectory
        ? (_size < 0 ? "Calculating..." : FormatBytes(_size))
        : FormatBytes(_size);

    /// <summary>
    /// Recursively calculates the total size of this directory and updates <see cref="Size"/> on the UI thread.
    /// No-op for files.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the background enumeration.</param>
    public async Task CalculateDirectorySizeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDirectory) return;
        var path = FullPath;
        var size = await Task.Run(() => CalculateDirectorySize(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return;
        // Marshal to UI thread to avoid cross-thread binding exceptions
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
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

    /// <summary>Parent directory path.</summary>
    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? string.Empty;

    /// <summary>Display-friendly file type (e.g. "Folder", "TXT File").</summary>
    public string TypeDisplay => IsDirectory ? "Folder" : Path.GetExtension(Name).TrimStart('.').ToUpper() + " File";

    /// <summary>Emoji icon for the item type.</summary>
    public string Icon => IsDirectory ? "\U0001f4c1" : "\U0001f4c4";

    /// <summary>
    /// Formats a byte count into a human-readable string with appropriate unit suffix.
    /// Shared across multiple display properties and other classes.
    /// </summary>
    internal static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }
}
