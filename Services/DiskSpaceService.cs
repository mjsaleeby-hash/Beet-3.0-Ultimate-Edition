using System.IO;

namespace BeetsBackup.Services;

/// <summary>
/// Categorization of a pre-flight disk-space preview result.
/// </summary>
public enum DiskSpaceStatus
{
    /// <summary>Free space check could not be performed (network share, unknown drive, etc.).</summary>
    Unknown,
    /// <summary>Plenty of room — required size is well under free space.</summary>
    Sufficient,
    /// <summary>Fits, but within a safety margin of running out (under 10% headroom after transfer).</summary>
    Tight,
    /// <summary>Estimated required bytes exceed available free space.</summary>
    Insufficient
}

/// <summary>
/// Snapshot of a pre-flight disk-space check used by the wizard and schedule dialog
/// to warn users before they start a transfer that may run out of room.
/// </summary>
public sealed record DiskSpacePreview(
    long RequiredBytes,
    long AvailableBytes,
    long TotalBytes,
    string? DriveRoot,
    DiskSpaceStatus Status,
    string? Note)
{
    /// <summary>Human-readable required size (e.g. "4.7 GB").</summary>
    public string RequiredDisplay => FormatBytes(RequiredBytes);

    /// <summary>Human-readable available free space (e.g. "12.1 GB").</summary>
    public string AvailableDisplay => AvailableBytes > 0 ? FormatBytes(AvailableBytes) : "unknown";

    /// <summary>Short one-line summary suitable for showing next to the destination in the summary step.</summary>
    public string Summary => Status switch
    {
        DiskSpaceStatus.Unknown => Note ?? "Free space could not be determined for this destination.",
        DiskSpaceStatus.Insufficient => $"Not enough room — {RequiredDisplay} needed, only {AvailableDisplay} free on {DriveRoot}.",
        DiskSpaceStatus.Tight => $"Tight fit — {RequiredDisplay} needed, {AvailableDisplay} free on {DriveRoot}.",
        DiskSpaceStatus.Sufficient => $"{RequiredDisplay} needed, {AvailableDisplay} free on {DriveRoot}.",
        _ => string.Empty
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {suffixes[i]}";
    }
}

/// <summary>
/// Pre-flight disk-space preview: estimates the bytes required for a copy or compress
/// operation and compares them against the free space available on the destination drive.
/// Used by the backup wizard and schedule dialog to surface a warning *before* a job kicks off,
/// rather than after files have started moving.
/// </summary>
public static class DiskSpaceService
{
    /// <summary>
    /// Compression rarely hits the theoretical estimate; we apply this ratio to the raw
    /// source size to guess how much space a .zip archive will occupy at the destination.
    /// It's a conservative generalization — real ratios vary wildly by content type.
    /// </summary>
    private const double CompressionRatioEstimate = 0.7;

    /// <summary>
    /// Any time the projected leftover free space (available − required) falls below this
    /// fraction of the total drive capacity we surface a "Tight" warning instead of a clean pass.
    /// </summary>
    private const double TightHeadroomFraction = 0.10;

    /// <summary>
    /// Computes a pre-flight disk-space preview for the given sources and destination.
    /// </summary>
    /// <param name="sourcePaths">Files and directories that will be read from.</param>
    /// <param name="destinationPath">Destination directory (or parent of an archive).</param>
    /// <param name="exclusions">Optional exclusion patterns applied while summing source size.</param>
    /// <param name="willCompress">Whether the output is a single compressed .zip archive;
    /// when true the required bytes are scaled down by an estimated compression ratio.</param>
    /// <returns>A snapshot the caller can display directly — never <c>null</c>.</returns>
    public static DiskSpacePreview Preview(
        IEnumerable<string> sourcePaths,
        string destinationPath,
        IReadOnlyList<string>? exclusions = null,
        bool willCompress = false)
    {
        long requiredRaw = 0;
        try
        {
            requiredRaw = TransferService.EstimateTotalSize(sourcePaths, exclusions);
        }
        catch
        {
            // Enumeration can fail for permission-denied trees — still produce a preview.
        }

        long required = willCompress
            ? (long)(requiredRaw * CompressionRatioEstimate)
            : requiredRaw;

        var root = SafePathRoot(destinationPath);
        if (string.IsNullOrEmpty(root))
        {
            return new DiskSpacePreview(required, 0, 0, null, DiskSpaceStatus.Unknown,
                "Destination path is not rooted; free-space check skipped.");
        }

        // UNC/network paths are not queryable via DriveInfo on most configurations. Explorer
        // still reports space for mapped drives, but we play it safe and flag Unknown when
        // DriveInfo throws or returns 0 total bytes.
        try
        {
            var di = new DriveInfo(root);
            long available = di.AvailableFreeSpace;
            long total = di.TotalSize;

            var status = ClassifyStatus(required, available, total);
            return new DiskSpacePreview(required, available, total, root, status, null);
        }
        catch (ArgumentException)
        {
            return new DiskSpacePreview(required, 0, 0, root, DiskSpaceStatus.Unknown,
                "Free space cannot be measured for this destination (likely a network share).");
        }
        catch (IOException)
        {
            return new DiskSpacePreview(required, 0, 0, root, DiskSpaceStatus.Unknown,
                "Destination drive is not ready.");
        }
        catch (UnauthorizedAccessException)
        {
            return new DiskSpacePreview(required, 0, 0, root, DiskSpaceStatus.Unknown,
                "Access was denied while reading drive information.");
        }
    }

    private static DiskSpaceStatus ClassifyStatus(long required, long available, long total)
    {
        if (available <= 0 || total <= 0) return DiskSpaceStatus.Unknown;
        if (required > available) return DiskSpaceStatus.Insufficient;

        long remainingAfter = available - required;
        if (remainingAfter < (long)(total * TightHeadroomFraction))
            return DiskSpaceStatus.Tight;

        return DiskSpaceStatus.Sufficient;
    }

    private static string? SafePathRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetPathRoot(path); }
        catch { return null; }
    }
}
