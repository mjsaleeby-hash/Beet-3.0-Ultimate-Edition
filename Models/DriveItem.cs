using System.IO;

namespace BeetsBackup.Models;

public class DriveItem
{
    public string Name { get; }
    public string RootPath { get; }
    public string Label { get; }
    public long TotalSize { get; }
    public long UsedSpace { get; }
    public long FreeSpace { get; }

    public DriveItem(DriveInfo drive)
    {
        RootPath = drive.RootDirectory.FullName;
        Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
        Name = $"{Label} ({drive.Name.TrimEnd('\\')})";
        TotalSize = drive.TotalSize;
        FreeSpace = drive.AvailableFreeSpace;
        UsedSpace = TotalSize - FreeSpace;
    }

    public double UsagePercent => TotalSize > 0 ? (double)UsedSpace / TotalSize * 100.0 : 0;

    public string TotalSizeDisplay => FormatBytes(TotalSize);
    public string UsedSpaceDisplay => FormatBytes(UsedSpace);
    public string FreeSpaceDisplay => FormatBytes(FreeSpace);

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {suffixes[i]}";
    }
}
