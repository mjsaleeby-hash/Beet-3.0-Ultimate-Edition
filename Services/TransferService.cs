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
        bool verifyChecksums = false)
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
                    CopyItem(source, destinationDir, stripPermissions, mode, progress, progressPercent, result, cancellationToken, pauseToken, verifyChecksums);
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

    public async Task<TransferResult> MoveAsync(IEnumerable<string> sourcePaths, string destinationDir,
        bool stripPermissions, TransferMode mode = TransferMode.SkipExisting,
        IProgress<string>? progress = null, IProgress<int>? progressPercent = null,
        CancellationToken cancellationToken = default, ManualResetEventSlim? pauseToken = null,
        bool verifyChecksums = false)
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
                    CopyItem(source, destinationDir, stripPermissions, mode, progress, progressPercent, result, cancellationToken, pauseToken, verifyChecksums);
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
        bool verifyChecksums)
    {
        pauseToken?.Wait(ct);
        ct.ThrowIfCancellationRequested();

        var attr = File.GetAttributes(sourcePath);
        bool isDirectory = attr.HasFlag(FileAttributes.Directory);
        var name = Path.GetFileName(sourcePath);

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
                    CopyItem(file, destSubDir, stripPermissions, mode, progress, progressPercent, result, ct, pauseToken, verifyChecksums);
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
                    CopyItem(dir, destSubDir, stripPermissions, mode, progress, progressPercent, result, ct, pauseToken, verifyChecksums);
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
                        var sourceInfo = new FileInfo(sourcePath);
                        var destInfo = new FileInfo(destFile);
                        if (sourceInfo.Length != destInfo.Length || sourceInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc)
                        {
                            File.Delete(destFile);
                            CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress);
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
                        CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress);
                        progress?.Report($"Copied (renamed): {Path.GetFileName(destFile)}");
                        break;

                    case TransferMode.Replace:
                        File.Delete(destFile);
                        CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress);
                        progress?.Report($"Replaced: {name}");
                        break;
                }
            }
            else
            {
                CopyAndVerify(sourcePath, destFile, stripPermissions, verifyChecksums, result, progress);
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
        bool verifyChecksums, TransferResult result, IProgress<string>? progress)
    {
        var fileSize = new FileInfo(source).Length;

        if (verifyChecksums)
        {
            // Single-pass copy: hash source bytes as they're written to dest,
            // then only re-read the destination for verification.
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
        long totalSize = sourcePaths.Sum(s => CalculateTotalSize(s));
        var driveInfo = new DriveInfo(Path.GetPathRoot(destinationDir)!);
        long available = driveInfo.AvailableFreeSpace;

        if (totalSize > available)
            throw new InsufficientSpaceException(totalSize, available);
    }

    private static long CalculateTotalSize(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", EnumOptions)
                    .Sum(f => { try { return f.Length; } catch { return 0L; } });
            }
            return new FileInfo(path).Length;
        }
        catch { return 0; }
    }

    private static int CountFiles(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.Directory))
                return Directory.EnumerateFiles(path, "*", EnumOptions).Count();
            return 1;
        }
        catch { return 0; }
    }
}
