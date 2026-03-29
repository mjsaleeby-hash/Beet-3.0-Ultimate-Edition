using BeetsBackup.Models;
using System.IO;
using System.Security.Cryptography;

namespace BeetsBackup.Services;

public class InsufficientSpaceException : Exception
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }

    public InsufficientSpaceException(long required, long available)
        : base($"Not enough disk space. Required: {FormatBytes(required)}, Available: {FormatBytes(available)}")
    {
        RequiredBytes = required;
        AvailableBytes = available;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < suffixes.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {suffixes[i]}";
    }
}

public class TransferService
{
    private readonly FileSystemService _fs;

    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    private static readonly EnumerationOptions ShallowEnumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
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
        long throttleBytesPerSec = 0)
    {
        var result = new TransferResult();
        var sourceList = sourcePaths.ToList();

        await Task.Run(() =>
        {
            CheckFreeSpace(sourceList, destinationDir);
            var excludeSet = exclusions != null && exclusions.Count > 0 ? exclusions : null;
            result.TotalFiles = sourceList.Sum(s => CountFiles(s, excludeSet));

            foreach (var source in sourceList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    CopyItem(source, destinationDir, stripPermissions, mode, progress, progressPercent, result, cancellationToken, pauseToken, verifyChecksums, excludeSet, throttleBytesPerSec);
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
                        MirrorCleanup(source, destSubDir, progress, result, cancellationToken);
                }
            }
        }, cancellationToken);

        return result;
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
            CheckFreeSpace(sourceList, destinationDir);
            result.TotalFiles = sourceList.Sum(s => CountFiles(s));

            foreach (var source in sourceList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var failedBefore = result.FilesFailed + result.DiskFullErrors + result.FilesLocked;
                    CopyItem(source, destinationDir, stripPermissions, mode, progress, progressPercent, result, cancellationToken, pauseToken, verifyChecksums, throttleBytesPerSec: throttleBytesPerSec);
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
        }, cancellationToken);

        return result;
    }

    private void CopyItem(string sourcePath, string destinationDir, bool stripPermissions,
        TransferMode mode, IProgress<string>? progress, IProgress<int>? progressPercent,
        TransferResult result, CancellationToken ct, ManualResetEventSlim? pauseToken,
        bool verifyChecksums, IReadOnlyList<string>? exclusions = null,
        long throttleBytesPerSec = 0)
    {
        pauseToken?.Wait(ct);
        ct.ThrowIfCancellationRequested();

        var attr = File.GetAttributes(sourcePath);
        bool isDirectory = attr.HasFlag(FileAttributes.Directory);
        var name = Path.GetFileName(sourcePath);

        if (exclusions != null && IsExcluded(name, exclusions))
        {
            if (!isDirectory)
                result.FilesSkipped++;
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
                    CopyItem(file, destSubDir, stripPermissions, mode, progress, progressPercent, result, ct, pauseToken, verifyChecksums, exclusions, throttleBytesPerSec);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0070)
                {
                    result.DiskFullErrors++;
                    FileLogger.Error($"Disk full: {file}");
                    progress?.Report($"DISK FULL: {Path.GetFileName(file)} — not enough space");
                }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020)
                {
                    result.FilesLocked++;
                    FileLogger.Warn($"File locked: {file}");
                    progress?.Report($"LOCKED: {Path.GetFileName(file)} — file is in use");
                }
                catch (Exception ex)
                {
                    result.FilesFailed++;
                    FileLogger.LogException($"File copy failed: {file}", ex);
                    progress?.Report($"ERROR: {Path.GetFileName(file)} — {ex.Message}");
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(sourcePath, "*", ShallowEnumOptions))
            {
                try
                {
                    CopyItem(dir, destSubDir, stripPermissions, mode, progress, progressPercent, result, ct, pauseToken, verifyChecksums, exclusions, throttleBytesPerSec);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0070)
                {
                    result.DiskFullErrors++;
                    FileLogger.Error($"Disk full: {dir}");
                    progress?.Report($"DISK FULL: {Path.GetFileName(dir)} — not enough space");
                }
                catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 0x0020)
                {
                    result.FilesLocked++;
                    FileLogger.Warn($"Directory locked: {dir}");
                    progress?.Report($"LOCKED: {Path.GetFileName(dir)} — file is in use");
                }
                catch (Exception ex)
                {
                    result.DirectoriesFailed++;
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
                            File.Delete(destFile);
                            CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct);
                            progress?.Report($"Updated (changed): {name}");
                        }
                        else
                        {
                            result.FilesSkipped++;
                            progress?.Report($"Skipped (identical): {name}");
                        }
                        break;

                    case TransferMode.KeepBoth:
                        destFile = GetUniqueFilePath(destFile);
                        CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct);
                        progress?.Report($"Copied (renamed): {Path.GetFileName(destFile)}");
                        break;

                    case TransferMode.Replace:
                        File.Delete(destFile);
                        CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct);
                        progress?.Report($"Replaced: {name}");
                        break;
                }
            }
            else
            {
                CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress, throttleBytesPerSec, pauseToken, ct);
                progress?.Report($"Copied: {name}");
            }

            if (result.TotalFiles > 0)
            {
                var percent = (int)Math.Round((result.FilesCopied + result.FilesSkipped + result.FilesFailed + result.DiskFullErrors + result.FilesLocked) * 100.0 / result.TotalFiles);
                progressPercent?.Report(Math.Min(percent, 100));
            }
        }
    }

    private void CopyAndVerify(string source, string dest, bool stripPermissions,
        bool verifyChecksums, TransferResult result, IProgress<string>? progress,
        long throttleBytesPerSec = 0, ManualResetEventSlim? pauseToken = null,
        CancellationToken ct = default)
    {
        var fileSize = new FileInfo(source).Length;

        if (throttleBytesPerSec > 0)
        {
            var sourceHash = ThrottledCopy(source, dest, stripPermissions, throttleBytesPerSec, verifyChecksums, pauseToken, ct);
            result.FilesCopied++;
            result.BytesTransferred += fileSize;

            if (verifyChecksums && sourceHash != null)
            {
                var destHash = ComputeSha256(dest);
                if (!sourceHash.SequenceEqual(destHash))
                {
                    result.ChecksumMismatches++;
                    progress?.Report($"CHECKSUM MISMATCH: {Path.GetFileName(dest)}");
                }
            }
        }
        else if (verifyChecksums)
        {
            var sourceHash = _fs.CopyFileWithHash(source, dest, stripPermissions);
            result.FilesCopied++;
            result.BytesTransferred += fileSize;

            var destHash = ComputeSha256(dest);
            if (!sourceHash.SequenceEqual(destHash))
            {
                result.ChecksumMismatches++;
                progress?.Report($"CHECKSUM MISMATCH: {Path.GetFileName(dest)}");
            }
        }
        else
        {
            _fs.CopyFile(source, dest, stripPermissions);
            result.FilesCopied++;
            result.BytesTransferred += fileSize;
        }
    }

    /// <summary>
    /// Copies a file with bandwidth throttling using chunked reads with timed delays.
    /// Optionally computes SHA-256 of source data during the copy (single pass).
    /// Supports pause/cancel between chunks for responsive UI control.
    /// </summary>
    private static byte[]? ThrottledCopy(string source, string dest, bool stripPermissions,
        long bytesPerSec, bool computeHash, ManualResetEventSlim? pauseToken = null,
        CancellationToken ct = default)
    {
        bool isHidden = File.GetAttributes(source).HasFlag(FileAttributes.Hidden);

        const int bufferSize = 65536; // 64 KB chunks
        byte[]? hash = null;

        using (var srcStream = File.OpenRead(source))
        using (var destStream = File.Create(dest))
        {
            System.Security.Cryptography.SHA256? sha = computeHash
                ? System.Security.Cryptography.SHA256.Create() : null;
            try
            {
                var buffer = new byte[bufferSize];
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

                    // Throttle: if we've written more bytes than the rate allows, sleep
                    var elapsedMs = stopwatch.ElapsedMilliseconds;
                    var expectedMs = (long)(totalBytesWritten * 1000.0 / bytesPerSec);
                    if (expectedMs > elapsedMs)
                        Thread.Sleep((int)(expectedMs - elapsedMs));
                }

                if (sha != null)
                {
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hash = sha.Hash;
                }
            }
            finally
            {
                sha?.Dispose();
            }
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
                    File.Delete(destFile);
                    result.FilesDeleted++;
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
            var sourceSub = Path.Combine(sourceDir, name);
            if (Directory.Exists(sourceSub))
            {
                MirrorCleanup(sourceSub, destSub, progress, result, ct);
            }
            else
            {
                try
                {
                    Directory.Delete(destSub, true);
                    result.DirectoriesDeleted++;
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

    private static byte[] ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
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
        var driveInfo = new DriveInfo(Path.GetPathRoot(destinationDir)!);
        long available = driveInfo.AvailableFreeSpace;

        if (totalSize > available)
            throw new InsufficientSpaceException(totalSize, available);
    }

    private static int CountFiles(string path, IReadOnlyList<string>? exclusions = null)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                var files = Directory.EnumerateFiles(path, "*", EnumOptions);
                if (exclusions != null)
                    files = files.Where(f => !IsExcluded(Path.GetFileName(f), exclusions));
                return files.Count();
            }
            if (exclusions != null && IsExcluded(Path.GetFileName(path), exclusions))
                return 0;
            return 1;
        }
        catch { return 0; }
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
                var files = new DirectoryInfo(path).EnumerateFiles("*", EnumOptions);
                if (exclusions != null)
                    files = files.Where(f => !IsExcluded(f.Name, exclusions));
                return files.Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            if (exclusions != null && IsExcluded(Path.GetFileName(path), exclusions))
                return 0;
            return new FileInfo(path).Length;
        }
        catch { return 0; }
    }
}
