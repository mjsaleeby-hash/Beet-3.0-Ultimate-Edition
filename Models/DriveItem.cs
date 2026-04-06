using System.IO;

namespace BeetsBackup.Models;

/// <summary>
/// Represents a mounted drive with capacity and usage information for the drive selector UI.
/// </summary>
public sealed class DriveItem
{
    /// <summary>Display name combining the volume label and drive letter (e.g. "Local Disk (C:)").</summary>
    public string Name { get; }

    /// <summary>Root directory path (e.g. "C:\").</summary>
    public string RootPath { get; }

    /// <summary>Volume label, defaulting to "Local Disk" when the drive has no label.</summary>
    public string Label { get; }

    /// <summary>Total capacity in bytes.</summary>
    public long TotalSize { get; }

    /// <summary>Used space in bytes (total minus free).</summary>
    public long UsedSpace { get; }

    /// <summary>Available free space in bytes.</summary>
    public long FreeSpace { get; }

    /// <summary>
    /// Initializes a new <see cref="DriveItem"/> from a <see cref="DriveInfo"/> instance.
    /// </summary>
    /// <param name="drive">The system drive information to wrap.</param>
    public DriveItem(DriveInfo drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        RootPath = drive.RootDirectory.FullName;
        Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
        Name = $"{Label} ({drive.Name.TrimEnd('\\')})";
        TotalSize = drive.TotalSize;
        FreeSpace = drive.AvailableFreeSpace;
        UsedSpace = TotalSize - FreeSpace;
    }

    /// <summary>Percentage of total capacity that is in use (0-100).</summary>
    public double UsagePercent => TotalSize > 0 ? (double)UsedSpace / TotalSize * 100.0 : 0;

    /// <summary>Human-readable total size (e.g. "465.76 GB").</summary>
    public string TotalSizeDisplay => FormatBytes(TotalSize);

    /// <summary>Human-readable used space.</summary>
    public string UsedSpaceDisplay => FormatBytes(UsedSpace);

    /// <summary>Human-readable free space.</summary>
    public string FreeSpaceDisplay => FormatBytes(FreeSpace);

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }
}
