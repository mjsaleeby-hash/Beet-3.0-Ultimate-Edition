using BeetsBackup.Models;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace BeetsBackup.Services;

/// <summary>
/// Thrown when a transfer cannot proceed because the destination drive lacks sufficient free space.
/// </summary>
public sealed class InsufficientSpaceException : Exception
{
    /// <summary>Bytes required to complete the transfer.</summary>
    public long RequiredBytes { get; }

    /// <summary>Bytes currently available on the destination drive.</summary>
    public long AvailableBytes { get; }

    /// <summary>
    /// Creates a new insufficient space exception with human-readable byte sizes in the message.
    /// </summary>
    public InsufficientSpaceException(long required, long available)
        : base($"Not enough disk space. Required: {FormatBytes(required)}, Available: {FormatBytes(available)}")
    {
        RequiredBytes = required;
        AvailableBytes = available;
    }

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
/// Orchestrates file copy, move, and mirror operations with support for
/// checksum verification, bandwidth throttling, pause/resume, exclusion filters,
/// and VSS shadow copy fallback for locked files.
/// </summary>
public sealed class TransferService
{
    private readonly FileSystemService _fs;

    // Skip junction points and symlinks during enumeration. Without this, backing up C:\Users
    // follows the All Users junction and copies ~21 GB of C:\ProgramData into the backup, plus
    // the per-profile legacy junctions (Application Data, Local Settings, My Documents, ...) cause
    // AppData paths to be copied multiple times under different names. Skipping ReparsePoint here
    // applies to count, size-estimate, copy, archive, and mirror-cleanup paths in one place.
    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    private static readonly EnumerationOptions ShallowEnumOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    public TransferService(FileSystemService fs)
    {
        _fs = fs;
    }

    public async Task<TransferResult> CopyAsync(IEnumerable<string> sourcePaths, string destinationDir,
        bool stripPermissions, TransferMode mode = TransferMode.SkipExisting,
        IProgress<string>? progress = null, IProgress<int>? progressPercent = null,
        CancellationToken cancellationToken = default, ManualResetEventSlim? pauseToken = null,
        bool verifyChecksums = false, IReadOnlyList<string>? exclusions = null,
        long throttleBytesPerSec = 0, VersioningOptions? versioning = null)
    {
        var result = new TransferResult();
        var sourceList = sourcePaths.ToList();

        // CopyAsync owns the destination root — always overwrite whatever the caller supplied so
        // the archiver can never resolve a stale or wrong path. Previously this was conditional
        // on IsNullOrEmpty, which worked today only because every caller happens to leave it blank.
        if (versioning != null && versioning.Enabled)
            versioning.DestinationRoot = destinationDir;

        await Task.Run(() =>
        {
            // Keep Windows awake for the duration of the backup — without this hint, an idle sleep
            // timeout can suspend the machine mid-copy and the transfer fails on resume.
            using var _awake = PowerManagement.KeepSystemAwake();
            // Create a per-session VSS scope — snapshots are cached per volume and cleaned up at the end
            using var vssSession = new VssSnapshotService();

            CheckFreeSpace(sourceList, destinationDir);
            var excludeSet = exclusions != null && exclusions.Count > 0 ? exclusions : null;
            result.TotalFiles = sourceList.Sum(s => CountFiles(s, excludeSet));

            foreach (var source in sourceList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    CopyItem(source, destinationDir, stripPermissions, mode, progress, progressPercent, result, cancellationToken, pauseToken, verifyChecksums, excludeSet, throttleBytesPerSec, vssSession, versioning);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress?.Report($"Error: {Path.GetFileName(source)} — {ex.Message}");
                }
            }

            if (mode == TransferMode.Mirror)
            {
                progress?.Report("Mirror: removing extra files from destination...");
                foreach (var source in sourceList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceName = Path.GetFileName(source);
                    var destSubDir = string.IsNullOrEmpty(sourceName) ? destinationDir : Path.Combine(destinationDir, sourceName);
                    if (Directory.Exists(source) && Directory.Exists(destSubDir))
                    {
                        // Safeguard: refuse mirror cleanup if source appears empty (drive may be disconnected)
                        if (!Directory.EnumerateFileSystemEntries(source, "*", EnumOptions).Any())
                        {
                            progress?.Report($"Mirror cleanup SKIPPED: '{Path.GetFileName(source)}' is empty — refusing to delete destination");
                            FileLogger.Warn($"Mirror cleanup skipped for empty source: {source}");
                            continue;
                        }
                        MirrorCleanup(source, destSubDir, progress, result, cancellationToken);
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Compresses all source paths into a single <c>.zip</c> archive placed in <paramref name="destinationDir"/>.
    /// Honors exclusion filters; supports pause/cancel between file entries.
    /// </summary>
    /// <param name="sourcePaths">Files or directories to add to the archive.</param>
    /// <param name="destinationDir">Folder in which to create the archive file.</param>
    /// <param name="archiveName">File name for the archive, e.g. <c>"MyBackup_2026-04-13_14-30-22.zip"</c>.</param>
    /// <param name="exclusions">Optional name/extension patterns to skip (e.g. <c>"*.tmp"</c>, <c>"Thumbs.db"</c>).</param>
    /// <param name="cancellationToken">Cancellation token for stopping the archive operation.</param>
    /// <param name="pauseToken">Pause gate checked between entries.</param>
    /// <param name="progress">Optional progress callback for per-file status strings.</param>
    /// <param name="progressPercent">Optional percent callback (0-100) for the overall archive progress.</param>
    /// <returns>A <see cref="TransferResult"/> with file counts and bytes compressed.</returns>
    public async Task<TransferResult> CompressAsync(IEnumerable<string> sourcePaths, string destinationDir,
        string archiveName,
        IReadOnlyList<string>? exclusions = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseToken = null,
        IProgress<string>? progress = null,
        IProgress<int>? progressPercent = null)
    {
        var result = new TransferResult();
        var sourceList = sourcePaths.ToList();

        await Task.Run(() =>
        {
            using var _awake = PowerManagement.KeepSystemAwake();
            Directory.CreateDirectory(destinationDir);

            // Disambiguate if a prior archive with this exact name already exists — never silently
            // clobber user data. Counter suffix: "name.zip" → "name (2).zip" → "name (3).zip" …
            var archivePath = GetUniqueFilePath(Path.Combine(destinationDir, archiveName));

            // Rough free-space check based on uncompressed size (conservative — real archive will be smaller)
            CheckFreeSpace(sourceList, destinationDir);

            var excludeSet = exclusions != null && exclusions.Count > 0 ? exclusions : null;
            result.TotalFiles = sourceList.Sum(s => CountFiles(s, excludeSet));

            // Warn loudly if the archive will be empty — silently producing a 22-byte zip would
            // leave the user believing their backup succeeded when in reality nothing was captured
            // (bad paths, everything excluded, or permissions denied across the board).
            if (result.TotalFiles == 0)
            {
                FileLogger.Warn($"Compress: zero eligible files across {sourceList.Count} source(s) — archive will be empty.");
                progress?.Report("Warning: no eligible files to compress — the archive will be empty.");
            }

            // Pre-compute top-level entry prefixes, disambiguating duplicates. When two sources share a
            // base name (e.g. D:\A\Data and E:\B\Data) the zip would otherwise produce colliding entries.
            var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefixes = new string[sourceList.Count];
            for (int i = 0; i < sourceList.Count; i++)
                prefixes[i] = ReserveUniquePrefix(Path.GetFileName(sourceList[i].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), usedPrefixes);

            using var archiveStream = File.Create(archivePath);
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

            for (int i = 0; i < sourceList.Count; i++)
            {
                var source = sourceList[i];
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Pass the pre-reserved unique prefix; AddToArchive no longer derives it from the leaf name.
                    AddToArchive(zip, source, prefixes[i], excludeSet, result, progress, progressPercent, pauseToken, cancellationToken, isTopLevel: true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.IncrementFilesFailed();
                    result.AddFileError(source, ex.Message);
                    FileLogger.LogException($"Compress failed: {source}", ex);
                    progress?.Report($"ERROR: {Path.GetFileName(source)} — {ex.Message}");
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Recursively adds a file or directory to the open <see cref="ZipArchive"/>.
    /// </summary>
    /// <param name="isTopLevel">
    /// When <c>true</c>, <paramref name="entryPrefix"/> is treated as the already-disambiguated entry name
    /// for this source (no leaf name is appended). Subsequent recursive calls pass <c>false</c> so the
    /// normal "prefix/leaf" composition applies for nested entries.
    /// </param>
    private static void AddToArchive(ZipArchive zip, string sourcePath, string entryPrefix,
        IReadOnlyList<string>? exclusions, TransferResult result,
        IProgress<string>? progress, IProgress<int>? progressPercent,
        ManualResetEventSlim? pauseToken, CancellationToken ct,
        bool isTopLevel = false)
    {
        pauseToken?.Wait(ct);
        ct.ThrowIfCancellationRequested();

        var name = Path.GetFileName(sourcePath);
        if (!isTopLevel && exclusions != null && IsExcluded(name, exclusions))
        {
            result.IncrementFilesSkipped();
            // Still advance the percent bar so users with heavy exclusion lists (e.g. huge
            // node_modules trees filtered out) don't see progress appear to stall.
            if (result.TotalFiles > 0)
            {
                var done = result.FilesCopied + result.FilesSkipped + result.FilesFailed + result.FilesLocked;
                progressPercent?.Report(Math.Min((int)Math.Round(done * 100.0 / result.TotalFiles), 100));
            }
            return;
        }

        var attr = File.GetAttributes(sourcePath);
        if (attr.HasFlag(FileAttributes.Directory))
        {
            // For the top-level call the prefix is already the reserved unique name; otherwise compose.
            var dirEntry = isTopLevel
                ? entryPrefix
                : (string.IsNullOrEmpty(entryPrefix)
                    ? (string.IsNullOrEmpty(name) ? string.Empty : name)
                    : (string.IsNullOrEmpty(name) ? entryPrefix : $"{entryPrefix}/{name}"));

            foreach (var file in Directory.EnumerateFiles(sourcePath, "*", ShallowEnumOptions))
            {
                try { AddToArchive(zip, file, dirEntry, exclusions, result, progress, progressPercent, pauseToken, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.IncrementFilesFailed();
                    result.AddFileError(file, ex.Message);
                    FileLogger.LogException($"Compress file failed: {file}", ex);
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(sourcePath, "*", ShallowEnumOptions))
            {
                try { AddToArchive(zip, dir, dirEntry, exclusions, result, progress, progressPercent, pauseToken, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.IncrementDirectoriesFailed();
                    result.AddFileError(dir, ex.Message);
                    FileLogger.LogException($"Compress directory failed: {dir}", ex);
                }
            }
        }
        else
        {
            // Top-level file source: the reserved prefix IS the entry name (already unique).
            // Otherwise compose "prefix/leaf".
            var entryName = isTopLevel
                ? entryPrefix
                : (string.IsNullOrEmpty(entryPrefix) ? name : $"{entryPrefix}/{name}");
            try
            {
                var entry = zip.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
                result.IncrementFilesCopied();
                result.AddBytesTransferred(new FileInfo(sourcePath).Length);
                progress?.Report($"Compressed: {name}");
            }
            catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020)
            {
                result.IncrementFilesLocked();
                result.AddFileError(sourcePath, "File is in use");
                FileLogger.Warn($"File locked during compression: {sourcePath}");
            }

            if (result.TotalFiles > 0)
            {
                var done = result.FilesCopied + result.FilesSkipped + result.FilesFailed + result.FilesLocked;
                progressPercent?.Report(Math.Min((int)Math.Round(done * 100.0 / result.TotalFiles), 100));
            }
        }
    }

    /// <summary>
    /// Extracts a single <c>.zip</c> archive to a destination folder. Creates the destination if
    /// it doesn't exist. Protects against "zip slip" attacks by rejecting any entry whose resolved
    /// full path falls outside <paramref name="destinationDir"/>.
    /// </summary>
    /// <param name="archivePath">Full path to the <c>.zip</c> file to extract.</param>
    /// <param name="destinationDir">Target folder. Will be created if missing.</param>
    /// <param name="overwriteExisting">
    /// When <c>true</c>, existing files at the destination are overwritten; when <c>false</c> they
    /// are skipped and counted under <see cref="TransferResult.FilesSkipped"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for stopping extraction mid-archive.</param>
    /// <param name="pauseToken">Pause gate checked between entries.</param>
    /// <param name="progress">Optional progress callback for per-file status strings.</param>
    /// <param name="progressPercent">Optional percent callback (0-100) for the overall extract progress.</param>
    /// <returns>A <see cref="TransferResult"/> with counts of extracted / skipped / failed entries.</returns>
    public async Task<TransferResult> ExtractAsync(string archivePath, string destinationDir,
        bool overwriteExisting = false,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseToken = null,
        IProgress<string>? progress = null,
        IProgress<int>? progressPercent = null)
    {
        var result = new TransferResult();

        await Task.Run(() =>
        {
            using var _awake = PowerManagement.KeepSystemAwake();
            Directory.CreateDirectory(destinationDir);
            // Canonicalize once so every zip-slip check compares apples to apples.
            var destRootFull = Path.GetFullPath(destinationDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            using var archiveStream = File.OpenRead(archivePath);
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            // Count only file entries (ones with a non-empty name) so the percent denominator
            // ignores pure-directory markers.
            var fileEntries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            result.TotalFiles = fileEntries.Count;

            foreach (var entry in fileEntries)
            {
                pauseToken?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // Resolve the entry's target path defensively:
                //  • reject absolute-rooted entries
                //  • reject any path that escapes destinationDir via "..\" segments
                //  • normalize via GetFullPath before the containment check
                var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalized))
                {
                    result.IncrementFilesFailed();
                    result.AddFileError(entry.FullName, "Rejected: absolute path in archive entry.");
                    FileLogger.Warn($"Extract: refused rooted entry: {entry.FullName}");
                    ReportPercent(result, progressPercent);
                    continue;
                }

                string targetPath;
                try
                {
                    targetPath = Path.GetFullPath(Path.Combine(destinationDir, normalized));
                }
                catch (Exception ex)
                {
                    result.IncrementFilesFailed();
                    result.AddFileError(entry.FullName, $"Unresolvable path: {ex.Message}");
                    ReportPercent(result, progressPercent);
                    continue;
                }

                if (!targetPath.StartsWith(destRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    // Zip-slip: entry tried to escape the destination tree (e.g. "..\..\windows\evil").
                    result.IncrementFilesFailed();
                    result.AddFileError(entry.FullName, "Rejected: entry escapes destination folder.");
                    FileLogger.Warn($"Extract: zip-slip blocked — {entry.FullName} → {targetPath}");
                    ReportPercent(result, progressPercent);
                    continue;
                }

                try
                {
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                    if (File.Exists(targetPath) && !overwriteExisting)
                    {
                        result.IncrementFilesSkipped();
                        progress?.Report($"Skipped (exists): {entry.Name}");
                        ReportPercent(result, progressPercent);
                        continue;
                    }

                    entry.ExtractToFile(targetPath, overwrite: overwriteExisting);
                    result.IncrementFilesCopied();
                    result.AddBytesTransferred(entry.Length);
                    progress?.Report($"Extracted: {entry.Name}");
                }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020)
                {
                    result.IncrementFilesLocked();
                    result.AddFileError(targetPath, "File is in use");
                    FileLogger.Warn($"Extract locked: {targetPath}");
                }
                catch (Exception ex)
                {
                    result.IncrementFilesFailed();
                    result.AddFileError(entry.FullName, ex.Message);
                    FileLogger.LogException($"Extract failed: {entry.FullName}", ex);
                }

                ReportPercent(result, progressPercent);
            }
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>Shared progress-percent emission for <see cref="ExtractAsync"/>.</summary>
    private static void ReportPercent(TransferResult result, IProgress<int>? progressPercent)
    {
        if (result.TotalFiles <= 0) return;
        var done = result.FilesCopied + result.FilesSkipped + result.FilesFailed + result.FilesLocked;
        progressPercent?.Report(Math.Min((int)Math.Round(done * 100.0 / result.TotalFiles), 100));
    }

    public async Task<TransferResult> MoveAsync(IEnumerable<string> sourcePaths, string destinationDir,
        bool stripPermissions, TransferMode mode = TransferMode.SkipExisting,
        IProgress<string>? progress = null, IProgress<int>? progressPercent = null,
        CancellationToken cancellationToken = default, ManualResetEventSlim? pauseToken = null,
        bool verifyChecksums = false, long throttleBytesPerSec = 0)
    {
        var result = new TransferResult();
        var sourceList = sourcePaths.ToList();

        await Task.Run(() =>
        {
            using var _awake = PowerManagement.KeepSystemAwake();
            using var vssSession = new VssSnapshotService();
            CheckFreeSpace(sourceList, destinationDir);
            result.TotalFiles = sourceList.Sum(s => CountFiles(s));

            foreach (var source in sourceList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var failedBefore = result.FilesFailed + result.DiskFullErrors + result.FilesLocked;
                    CopyItem(source, destinationDir, stripPermissions, mode, progress, progressPercent, result, cancellationToken, pauseToken, verifyChecksums, throttleBytesPerSec: throttleBytesPerSec, vss: vssSession);
                    var failedDuring = (result.FilesFailed + result.DiskFullErrors + result.FilesLocked) - failedBefore;
                    if (failedDuring == 0)
                        _fs.DeleteItem(source);
                    else
                        progress?.Report($"Skipped delete of {Path.GetFileName(source)} — {failedDuring} file(s) failed to copy");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress?.Report($"Error: {Path.GetFileName(source)} — {ex.Message}");
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private void CopyItem(string sourcePath, string destinationDir, bool stripPermissions,
        TransferMode mode, IProgress<string>? progress, IProgress<int>? progressPercent,
        TransferResult result, CancellationToken ct, ManualResetEventSlim? pauseToken,
        bool verifyChecksums, IReadOnlyList<string>? exclusions = null,
        long throttleBytesPerSec = 0, VssSnapshotService? vss = null,
        VersioningOptions? versioning = null)
    {
        pauseToken?.Wait(ct);
        ct.ThrowIfCancellationRequested();

        var attr = File.GetAttributes(sourcePath);
        bool isDirectory = attr.HasFlag(FileAttributes.Directory);
        var name = Path.GetFileName(sourcePath);

        if (exclusions != null && IsExcluded(name, exclusions))
        {
            if (!isDirectory)
                result.IncrementFilesSkipped();
            return;
        }

        if (isDirectory)
        {
            // Guard against empty folder name (e.g. drive root "D:\")
            var destSubDir = string.IsNullOrEmpty(name)
                ? destinationDir
                : Path.Combine(destinationDir, name);

            if (mode == TransferMode.KeepBoth && Directory.Exists(destSubDir))
                destSubDir = GetUniqueFolderPath(destSubDir);

            // Always ensure the destination directory exists — never skip a folder
            if (!Directory.Exists(destSubDir))
                Directory.CreateDirectory(destSubDir);

            if (attr.HasFlag(FileAttributes.Hidden))
                File.SetAttributes(destSubDir, File.GetAttributes(destSubDir) | FileAttributes.Hidden);

            progress?.Report($"Scanning: {name}\\");

            // Always recurse into contents regardless of transfer mode.
            // Wrap each child in its own try/catch so one failure doesn't abort the whole tree.
            foreach (var file in Directory.EnumerateFiles(sourcePath, "*", ShallowEnumOptions))
            {
                try
                {
                    CopyItem(file, destSubDir, stripPermissions, mode, progress, progressPercent, result, ct, pauseToken, verifyChecksums, exclusions, throttleBytesPerSec, vss, versioning);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0070)
                {
                    result.IncrementDiskFullErrors();
                    result.AddFileError(file, "Disk full");
                    FileLogger.Error($"Disk full: {file}");
                    progress?.Report($"DISK FULL: {Path.GetFileName(file)} — not enough space");
                }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020)
                {
                    result.IncrementFilesLocked();
                    result.AddFileError(file, "File is in use");
                    FileLogger.Warn($"File locked (retries + VSS exhausted): {file}");
                    progress?.Report($"LOCKED: {Path.GetFileName(file)} — file is in use");
                }
                catch (Exception ex)
                {
                    result.IncrementFilesFailed();
                    result.AddFileError(file, ex.Message);
                    FileLogger.LogException($"File copy failed: {file}", ex);
                    progress?.Report($"ERROR: {Path.GetFileName(file)} — {ex.Message}");
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(sourcePath, "*", ShallowEnumOptions))
            {
                try
                {
                    CopyItem(dir, destSubDir, stripPermissions, mode, progress, progressPercent, result, ct, pauseToken, verifyChecksums, exclusions, throttleBytesPerSec, vss, versioning);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0070)
                {
                    result.IncrementDiskFullErrors();
                    result.AddFileError(dir, "Disk full");
                    FileLogger.Error($"Disk full: {dir}");
                    progress?.Report($"DISK FULL: {Path.GetFileName(dir)} — not enough space");
                }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020)
                {
                    result.IncrementFilesLocked();
                    result.AddFileError(dir, "Directory is in use");
                    FileLogger.Warn($"Directory locked: {dir}");
                    progress?.Report($"LOCKED: {Path.GetFileName(dir)} — file is in use");
                }
                catch (Exception ex)
                {
                    result.IncrementDirectoriesFailed();
                    result.AddFileError(dir, ex.Message);
                    FileLogger.LogException($"Directory copy failed: {dir}", ex);
                    progress?.Report($"ERROR: {Path.GetFileName(dir)} — {ex.Message}");
                }
            }
        }
        else
        {
            var destFile = Path.Combine(destinationDir, name);

            if (File.Exists(destFile))
            {
                switch (mode)
                {
                    case TransferMode.SkipExisting:
                    case TransferMode.Mirror:
                        var sourceInfo = new FileInfo(sourcePath);
                        var destInfo = new FileInfo(destFile);
                        if (sourceInfo.Length != destInfo.Length || sourceInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc)
                        {
                            // If versioning was requested and archiving fails, DO NOT destroy the
                            // destination — the user asked us to protect it. Record a failure and skip.
                            if (!VersioningService.ArchiveBeforeOverwrite(destFile, versioning))
                            {
                                result.IncrementFilesFailed();
                                result.AddFileError(destFile, "Could not archive existing version; destination left untouched.");
                                progress?.Report($"ERROR: {name} — version archive failed, keeping existing");
                                break;
                            }
                            if (File.Exists(destFile)) File.Delete(destFile);
                            CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct, vss);
                            progress?.Report($"Updated (changed): {name}");
                        }
                        else
                        {
                            result.IncrementFilesSkipped();
                            progress?.Report($"Skipped (identical): {name}");
                        }
                        break;

                    case TransferMode.KeepBoth:
                        destFile = GetUniqueFilePath(destFile);
                        CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct, vss);
                        progress?.Report($"Copied (renamed): {Path.GetFileName(destFile)}");
                        break;

                    case TransferMode.Replace:
                        if (!VersioningService.ArchiveBeforeOverwrite(destFile, versioning))
                        {
                            result.IncrementFilesFailed();
                            result.AddFileError(destFile, "Could not archive existing version; destination left untouched.");
                            progress?.Report($"ERROR: {name} — version archive failed, keeping existing");
                            break;
                        }
                        if (File.Exists(destFile)) File.Delete(destFile);
                        CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct, vss);
                        progress?.Report($"Replaced: {name}");
                        break;
                }
            }
            else
            {
                CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct, vss);
                progress?.Report($"Copied: {name}");
            }

            if (result.TotalFiles > 0)
            {
                var percent = (int)Math.Round((result.FilesCopied + result.FilesSkipped + result.FilesFailed + result.DiskFullErrors + result.FilesLocked) * 100.0 / result.TotalFiles);
                progressPercent?.Report(Math.Min(percent, 100));
            }
        }
    }

    private const int MaxRetries = 3;
    private const int RetryDelayMs = 500;

    private void CopyAndVerify(string source, string dest, bool stripPermissions,
        bool verifyChecksums, TransferResult result, IProgress<string>? progress,
        long throttleBytesPerSec = 0, ManualResetEventSlim? pauseToken = null,
        CancellationToken ct = default, VssSnapshotService? vss = null)
    {
        string activeSrc = source;
        bool usedVss = false;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                ExecuteCopy(activeSrc, dest, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct);

                if (usedVss)
                    result.IncrementFilesCopiedViaVss();

                return; // success
            }
            catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020 && !usedVss)
            {
                // Sharing violation — file is locked
                if (attempt < MaxRetries)
                {
                    FileLogger.Info($"File locked, retry {attempt}/{MaxRetries}: {source}");
                    progress?.Report($"Retrying ({attempt}/{MaxRetries}): {Path.GetFileName(source)}");
                    Thread.Sleep(RetryDelayMs);
                    continue;
                }

                // Retries exhausted — attempt VSS fallback
                if (vss != null)
                {
                    try
                    {
                        FileLogger.Info($"Retries exhausted, attempting VSS fallback: {source}");
                        progress?.Report($"Shadow copy: {Path.GetFileName(source)}");
                        var snapshotRoot = vss.GetOrCreateSnapshotRoot(source);
                        activeSrc = vss.GetShadowPath(source, snapshotRoot);
                        usedVss = true;
                        // One more attempt from the shadow copy
                        continue;
                    }
                    catch (Exception vssEx)
                    {
                        FileLogger.Warn($"VSS fallback failed for {source}: {vssEx.Message}");
                        // Re-throw the original sharing violation so CopyItem's catch handles it.
                        // Use ExceptionDispatchInfo to preserve the original stack trace rather than
                        // `throw ioEx;` which resets it and makes the root cause unloggable.
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ioEx).Throw();
                        throw; // Unreachable, but the compiler needs it for definite-assignment analysis.
                    }
                }

                // No VSS available — let the sharing violation propagate
                throw;
            }
        }

        // If we used VSS, we get one final attempt (the loop ended after setting usedVss)
        if (usedVss)
        {
            ExecuteCopy(activeSrc, dest, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct);
            result.IncrementFilesCopiedViaVss();
        }
    }

    /// <summary>
    /// Performs the actual file copy for a single file, choosing the appropriate strategy
    /// based on throttling and checksum verification settings.
    /// </summary>
    private void ExecuteCopy(string source, string dest, bool stripPermissions,
        bool verifyChecksums, TransferResult result, IProgress<string>? progress,
        long throttleBytesPerSec, ManualResetEventSlim? pauseToken, CancellationToken ct)
    {
        var fileSize = new FileInfo(source).Length;

        if (throttleBytesPerSec > 0)
        {
            var sourceHash = ThrottledCopy(source, dest, stripPermissions, throttleBytesPerSec, computeHash: verifyChecksums, pauseToken, ct);
            result.IncrementFilesCopied();
            result.AddBytesTransferred(fileSize);

            if (verifyChecksums && sourceHash != null)
            {
                var destHash = ComputeSha256(dest);
                if (!sourceHash.SequenceEqual(destHash))
                {
                    result.IncrementChecksumMismatches();
                    result.AddFileError(dest, "Checksum mismatch after copy");
                    progress?.Report($"CHECKSUM MISMATCH: {Path.GetFileName(dest)}");
                }
            }
        }
        else if (verifyChecksums)
        {
            var sourceHash = _fs.CopyFileWithHash(source, dest, stripPermissions);
            result.IncrementFilesCopied();
            result.AddBytesTransferred(fileSize);

            var destHash = ComputeSha256(dest);
            if (!sourceHash.SequenceEqual(destHash))
            {
                result.IncrementChecksumMismatches();
                result.AddFileError(dest, "Checksum mismatch after copy");
                progress?.Report($"CHECKSUM MISMATCH: {Path.GetFileName(dest)}");
            }
        }
        else
        {
            _fs.CopyFile(source, dest, stripPermissions);
            result.IncrementFilesCopied();
            result.AddBytesTransferred(fileSize);
        }
    }

    /// <summary>
    /// Copies a file with bandwidth throttling using chunked reads with timed delays.
    /// Optionally computes SHA-256 of source data during the copy (single pass).
    /// Supports pause/cancel between chunks for responsive UI control.
    /// </summary>
    /// <param name="source">Full path of the source file.</param>
    /// <param name="dest">Full path of the destination file (created or overwritten).</param>
    /// <param name="stripPermissions">When <c>true</c>, resets NTFS ACLs to inherit from parent.</param>
    /// <param name="bytesPerSec">Maximum throughput in bytes per second.</param>
    /// <param name="computeHash">When <c>true</c>, computes SHA-256 of the source bytes during copy.</param>
    /// <param name="pauseToken">Optional pause gate checked between chunks.</param>
    /// <param name="ct">Cancellation token; also used to break throttle sleeps immediately.</param>
    /// <returns>The SHA-256 hash if <paramref name="computeHash"/> is <c>true</c>; otherwise <c>null</c>.</returns>
    private static byte[]? ThrottledCopy(string source, string dest, bool stripPermissions,
        long bytesPerSec, bool computeHash, ManualResetEventSlim? pauseToken = null,
        CancellationToken ct = default)
    {
        bool isHidden = File.GetAttributes(source).HasFlag(FileAttributes.Hidden);

        byte[]? hash = null;

        // 1 MB buffer rented from the shared pool. Larger chunks mean fewer syscalls and
        // fewer throttle-sleep cycles; pooling avoids per-file heap churn under heavy loads.
        const int BufferSize = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
            SHA256? sha = computeHash ? SHA256.Create() : null;
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                long totalBytesWritten = 0;
                int bytesRead;

                while ((bytesRead = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    pauseToken?.Wait(ct);
                    ct.ThrowIfCancellationRequested();

                    sha?.TransformBlock(buffer, 0, bytesRead, null, 0);
                    destStream.Write(buffer, 0, bytesRead);
                    totalBytesWritten += bytesRead;

                    // Throttle: if we've written more bytes than the rate allows, wait on the
                    // cancel token's wait handle so a Cancel() request breaks the delay
                    // immediately instead of blocking up to `expectedMs - elapsedMs` ms.
                    var elapsedMs = stopwatch.ElapsedMilliseconds;
                    var expectedMs = (long)(totalBytesWritten * 1000.0 / bytesPerSec);
                    if (expectedMs > elapsedMs)
                    {
                        if (ct.WaitHandle.WaitOne((int)(expectedMs - elapsedMs)))
                            ct.ThrowIfCancellationRequested();
                    }
                }

                if (sha is not null)
                {
                    sha.TransformFinalBlock([], 0, 0);
                    hash = sha.Hash;
                }
                // Flush to the physical disk so the data is durable before we report success.
                destStream.Flush(flushToDisk: true);
            }
            finally
            {
                sha?.Dispose();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Preserve timestamps
        var srcInfo = new FileInfo(source);
        File.SetLastWriteTimeUtc(dest, srcInfo.LastWriteTimeUtc);
        File.SetCreationTimeUtc(dest, srcInfo.CreationTimeUtc);

        // Match FileSystemService.ResetFilePermissions: remove protection, inherit from parent
        if (stripPermissions)
        {
            try
            {
                var fi = new FileInfo(dest);
                var ac = fi.GetAccessControl();
                ac.SetAccessRuleProtection(false, false);
                fi.SetAccessControl(ac);
            }
            catch (UnauthorizedAccessException) { }
        }

        if (isHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);

        return hash;
    }

    private static void MirrorCleanup(string sourceDir, string destDir,
        IProgress<string>? progress, TransferResult result, CancellationToken ct)
    {
        // Delete destination files that don't exist in source
        foreach (var destFile in Directory.EnumerateFiles(destDir, "*", ShallowEnumOptions))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(destFile);
            var sourceFile = Path.Combine(sourceDir, name);
            if (!File.Exists(sourceFile))
            {
                try
                {
                    FileSystem.DeleteFile(destFile, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    result.IncrementFilesDeleted();
                    progress?.Report($"Mirror deleted: {name}");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"Mirror cleanup failed: {destFile} — {ex.Message}");
                    progress?.Report($"Mirror delete failed: {name} — {ex.Message}");
                }
            }
        }

        // Recurse into subdirectories, then delete empty ones not in source
        foreach (var destSub in Directory.EnumerateDirectories(destDir, "*", ShallowEnumOptions))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(destSub);

            // Never touch the versioning archive during mirror cleanup
            if (name.Equals(VersioningOptions.VersionsFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceSub = Path.Combine(sourceDir, name);
            if (Directory.Exists(sourceSub))
            {
                MirrorCleanup(sourceSub, destSub, progress, result, ct);
            }
            else
            {
                try
                {
                    FileSystem.DeleteDirectory(destSub, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    result.IncrementDirectoriesDeleted();
                    progress?.Report($"Mirror deleted folder: {name}\\");
                }
                catch (Exception ex)
                {
                    FileLogger.Warn($"Mirror cleanup failed: {destSub} — {ex.Message}");
                    progress?.Report($"Mirror delete failed: {name}\\ — {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file using a 1 MB internal buffer and sequential-scan hint
    /// to maximize read-ahead throughput.
    /// </summary>
    private static byte[] ComputeSha256(string filePath)
    {
        const int BufferSize = 1024 * 1024;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static string GetUniqueFilePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameNoExt}-{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private static string GetUniqueFolderPath(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(parent, $"{name}-{counter}");
            counter++;
        } while (Directory.Exists(candidate));
        return candidate;
    }

    private static void CheckFreeSpace(List<string> sourcePaths, string destinationDir)
    {
        long totalSize = EstimateTotalSize(sourcePaths);

        var root = Path.GetPathRoot(destinationDir);
        if (string.IsNullOrEmpty(root)) return; // relative path — skip check

        try
        {
            var driveInfo = new DriveInfo(root);
            long available = driveInfo.AvailableFreeSpace;

            if (totalSize > available)
                throw new InsufficientSpaceException(totalSize, available);
        }
        catch (ArgumentException)
        {
            // UNC/network paths are not supported by DriveInfo — skip check
        }
    }

    private static int CountFiles(string path, IReadOnlyList<string>? exclusions = null)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                // Manual walk so we can honor DIRECTORY-name exclusions (e.g. "node_modules").
                // Directory.EnumerateFiles with RecurseSubdirectories has no way to prune by name,
                // so it would inflate TotalFiles when excluded directories contain many files, and
                // the transfer progress percent would stall below 100%.
                if (exclusions != null && exclusions.Count > 0)
                    return CountFilesPruned(path, exclusions);

                return Directory.EnumerateFiles(path, "*", EnumOptions).Count();
            }
            if (exclusions != null && IsExcluded(Path.GetFileName(path), exclusions))
                return 0;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Recursive file count that prunes whole directory subtrees whose name matches an exclusion.</summary>
    private static int CountFilesPruned(string dir, IReadOnlyList<string> exclusions)
    {
        int total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", ShallowEnumOptions))
            {
                if (!IsExcluded(Path.GetFileName(file), exclusions))
                    total++;
            }
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", ShallowEnumOptions))
            {
                if (IsExcluded(Path.GetFileName(sub), exclusions)) continue;
                total += CountFilesPruned(sub, exclusions);
            }
        }
        catch { /* best-effort count */ }
        return total;
    }

    private static bool IsExcluded(string name, IReadOnlyList<string> exclusions)
    {
        foreach (var pattern in exclusions)
        {
            if (pattern.StartsWith("*."))
            {
                // Extension match: *.tmp, *.log
                var ext = pattern.Substring(1); // ".tmp"
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                // Exact name match: Thumbs.db, node_modules
                return true;
            }
        }
        return false;
    }

    public static long EstimateTotalSize(IEnumerable<string> sourcePaths, IReadOnlyList<string>? exclusions = null)
    {
        return sourcePaths.Sum(s => CalculateTotalSize(s, exclusions));
    }

    private static long CalculateTotalSize(string path, IReadOnlyList<string>? exclusions = null)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                // Honor directory-name exclusions (same rationale as CountFiles).
                if (exclusions != null && exclusions.Count > 0)
                    return CalculateTotalSizePruned(path, exclusions);

                return new DirectoryInfo(path).EnumerateFiles("*", EnumOptions)
                    .Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            if (exclusions != null && IsExcluded(Path.GetFileName(path), exclusions))
                return 0;
            return new FileInfo(path).Length;
        }
        catch { return 0; }
    }

    /// <summary>Recursive size total that prunes whole directory subtrees whose name matches an exclusion.</summary>
    private static long CalculateTotalSizePruned(string dir, IReadOnlyList<string> exclusions)
    {
        long total = 0;
        try
        {
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", ShallowEnumOptions))
            {
                if (IsExcluded(fi.Name, exclusions)) continue;
                try { total += fi.Length; } catch { /* skip unreadable */ }
            }
            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", ShallowEnumOptions))
            {
                if (IsExcluded(sub.Name, exclusions)) continue;
                total += CalculateTotalSizePruned(sub.FullName, exclusions);
            }
        }
        catch { /* best-effort */ }
        return total;
    }

    /// <summary>
    /// Returns a unique archive-entry prefix derived from <paramref name="desired"/>, disambiguating
    /// against names already reserved in <paramref name="used"/>. Falls back to a numbered suffix
    /// ("Data (2)", "Data (3)", …) and reserves the chosen name before returning.
    /// Used to prevent duplicate top-level entries when multiple sources share the same leaf name.
    /// </summary>
    private static string ReserveUniquePrefix(string desired, HashSet<string> used)
    {
        if (string.IsNullOrEmpty(desired)) desired = "root";

        if (used.Add(desired)) return desired;

        for (int i = 2; ; i++)
        {
            var candidate = $"{desired} ({i})";
            if (used.Add(candidate)) return candidate;
        }
    }
}
