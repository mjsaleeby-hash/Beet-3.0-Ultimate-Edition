using BeetsBackup.Models;

namespace BeetsBackup.Services;

/// <summary>
/// Single source of truth for human-readable summaries and toast notifications
/// generated from a completed <see cref="TransferResult"/>. Both the manual
/// transfer flow (MainViewModel) and the scheduled job flow (SchedulerService)
/// render through here so the user sees consistent counter breakdowns regardless
/// of how the backup was launched.
/// </summary>
public static class TransferReporter
{
    /// <summary>
    /// Builds a status string from a completed transfer. When nothing actually changed
    /// — every file was already up-to-date at the destination — the leading phrase becomes
    /// "Up to date — N files verified" instead of "0 copied, N skipped", which previously
    /// read as if the backup had done nothing. Otherwise lists each non-zero counter.
    /// </summary>
    public static string FormatSummary(TransferResult result)
    {
        if (IsNothingChanged(result))
        {
            return result.FilesSkipped > 0
                ? $"Up to date — {result.FilesSkipped} files verified"
                : "Done — nothing to do";
        }

        var parts = new List<string>();
        if (result.FilesCopied > 0) parts.Add($"{result.FilesCopied} copied");
        if (result.FilesCopiedViaVss > 0) parts.Add($"{result.FilesCopiedViaVss} via shadow copy");
        if (result.FilesSkipped > 0) parts.Add($"{result.FilesSkipped} skipped");
        if (result.FilesFailed > 0) parts.Add($"{result.FilesFailed} failed");
        if (result.DirectoriesFailed > 0) parts.Add($"{result.DirectoriesFailed} folders failed");
        if (result.FilesLocked > 0) parts.Add($"{result.FilesLocked} locked");
        if (result.DiskFullErrors > 0) parts.Add($"{result.DiskFullErrors} disk full errors");
        if (result.ChecksumMismatches > 0) parts.Add($"{result.ChecksumMismatches} checksum mismatches!");
        if (result.FilesDeleted > 0) parts.Add($"{result.FilesDeleted} deleted");
        if (result.DirectoriesDeleted > 0) parts.Add($"{result.DirectoriesDeleted} folders deleted");
        return parts.Count > 0 ? $"Done — {string.Join(", ", parts)}" : "Done.";
    }

    /// <summary>True when the backup completed without copying, deleting, or failing on
    /// anything — i.e. every file the user asked us to back up was already in place. Used
    /// to swap "0 copied, N skipped" for the more reassuring "Up to date".</summary>
    public static bool IsNothingChanged(TransferResult result) =>
        result.FilesCopied == 0 &&
        result.FilesCopiedViaVss == 0 &&
        result.FilesDeleted == 0 &&
        result.DirectoriesDeleted == 0 &&
        TotalFailed(result) == 0;

    /// <summary>
    /// Aggregate count of every "this didn't get copied" outcome — files, directories,
    /// disk-full hits, locked files, and checksum mismatches. Drives the toast's
    /// success-vs-warning distinction and the body's "X failed" wording.
    /// </summary>
    public static int TotalFailed(TransferResult result) =>
        result.FilesFailed + result.DirectoriesFailed + result.DiskFullErrors +
        result.FilesLocked + result.ChecksumMismatches;

    /// <summary>
    /// Surfaces a Windows toast describing the outcome. Warning kind when anything failed,
    /// success otherwise. Body is intentionally short — the full counter breakdown lives in
    /// the in-app log. When nothing changed, says so explicitly so the user doesn't read
    /// "0 copied" as failure.
    /// </summary>
    public static void Notify(string title, TransferResult result)
    {
        var totalFailed = TotalFailed(result);
        string body;
        if (totalFailed > 0)
            body = $"{result.FilesCopied} copied, {totalFailed} failed.";
        else if (IsNothingChanged(result))
            body = result.FilesSkipped > 0
                ? $"Up to date — {result.FilesSkipped} files verified."
                : "Up to date.";
        else
            body = $"{result.FilesCopied} copied, {result.FilesSkipped} skipped.";
        var kind = totalFailed > 0 ? ToastKind.Warning : ToastKind.Success;
        ToastNotifier.Notify(title, body, kind);
    }
}
