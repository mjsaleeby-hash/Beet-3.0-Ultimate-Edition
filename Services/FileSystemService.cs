using BeetsBackup.Models;
using Microsoft.VisualBasic.FileIO;
using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace BeetsBackup.Services;

/// <summary>
/// Provides file system operations: drive enumeration, directory listing,
/// copy/move/delete/rename, and permission management.
/// </summary>
public sealed class FileSystemService
{
    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    /// <summary>Enumeration options used by <see cref="CopyDirectory"/>. Skips reparse points
    /// (NTFS junctions and symbolic links) so a pane drag-drop copy of e.g. C:\Users\Owner
    /// doesn't follow the All-Users junction into C:\Users\Public, blow up to many GB, or
    /// infinite-loop on a user-created symlink. <see cref="EnumOptions"/> keeps reparse
    /// points visible for <see cref="GetChildren"/> because the file pane needs to show them.</summary>
    private static readonly EnumerationOptions CopyEnumOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    /// <summary>Returns all ready drives on the system as <see cref="DriveItem"/> instances.</summary>
    public IEnumerable<DriveItem> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveItem(d));
    }

    /// <summary>
    /// Lists immediate children (directories then files) of the given path.
    /// Skips NTFS junction points and symbolic links to prevent loops.
    /// </summary>
    /// <param name="path">Directory path to enumerate.</param>
    /// <returns>File and folder items in the directory.</returns>
    public IEnumerable<FileSystemItem> GetChildren(string path)
    {
        var items = new List<FileSystemItem>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", EnumOptions))
            {
                var dirInfo = new DirectoryInfo(dir);
                // Skip NTFS junction points and symbolic links (e.g. "Documents and Settings").
                // These are hidden by Windows Explorer and following them can cause loops.
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                items.Add(new FileSystemItem(dirInfo));
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", EnumOptions))
                items.Add(new FileSystemItem(new FileInfo(file)));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return items;
    }

    /// <summary>
    /// Copies a file or directory to the destination, optionally stripping NTFS permissions.
    /// </summary>
    public void CopyItem(string sourcePath, string destinationDir, bool stripPermissions)
    {
        var attr = File.GetAttributes(sourcePath);
        bool isDirectory = attr.HasFlag(FileAttributes.Directory);
        bool isHidden = attr.HasFlag(FileAttributes.Hidden);

        if (isDirectory)
            CopyDirectory(sourcePath, Path.Combine(destinationDir, Path.GetFileName(sourcePath)), stripPermissions, isHidden);
        else
            CopyFile(sourcePath, Path.Combine(destinationDir, Path.GetFileName(sourcePath)), stripPermissions);
    }

    /// <summary>
    /// Copies a single file, preserving timestamps and optionally resetting permissions.
    /// Hidden files remain hidden at the destination.
    /// </summary>
    /// <remarks>
    /// Uses Win32 <c>CopyFileEx</c> rather than <see cref="File.Copy(string,string,bool)"/> so the
    /// kernel handles sparse files, alternate data streams, and large-file optimisations directly.
    /// </remarks>
    public void CopyFile(string source, string dest, bool stripPermissions, Action<long, long>? onBytesProgress = null)
    {
        bool isHidden = File.GetAttributes(source).HasFlag(FileAttributes.Hidden);
        var srcLastWrite = File.GetLastWriteTimeUtc(source);

        CopyFileExNative(source, dest, onBytesProgress);
        // CopyFileEx already mirrors timestamps, but the destination filesystem can normalise them
        // (e.g. FAT32 truncates to 2-second resolution). Re-stamp explicitly so a later
        // SkipExisting pass sees identical mtimes between source and dest.
        File.SetLastWriteTimeUtc(dest, srcLastWrite);

        if (stripPermissions)
            ResetFilePermissions(dest);

        if (isHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
    }

    /// <summary>
    /// Wraps <c>CopyFileExW</c> with overwrite semantics matching <c>File.Copy(..., overwrite: true)</c>.
    /// When <paramref name="onBytesProgress"/> is supplied, attaches a CopyProgressRoutine so callers
    /// can surface within-file progress for multi-GB copies — without this hook a 50 GB ISO copy
    /// reports nothing to the UI until it finishes.
    /// Throws <see cref="IOException"/> with the underlying Win32 error message on failure.
    /// </summary>
    private static void CopyFileExNative(string source, string dest, Action<long, long>? onBytesProgress = null)
    {
        int cancel = 0;
        CopyProgressRoutine? routine = null;
        IntPtr routinePtr = IntPtr.Zero;
        if (onBytesProgress != null)
        {
            // The delegate is stored in a local so it stays strongly rooted on this stack
            // frame for the duration of the CopyFileEx call; GC.KeepAlive(routine) below
            // makes that explicit so the JIT can't optimise the local away. We deliberately
            // do NOT use GCHandle.Alloc here — a Normal-kind handle just adds rooting,
            // which we already have, and a Pinned handle isn't allowed on a delegate (it's
            // not blittable). The previous version's GCHandle was redundant and the
            // accompanying "pins the managed object" comment was misleading.
            routine = (total, transferred, _, _, _, _, _, _, _) =>
            {
                // The kernel calls back into this delegate during the copy. Letting an
                // exception escape across the unmanaged→managed return edge is undefined
                // behavior in .NET 8 (ExecutionEngineException / SEH chain corruption,
                // depending on Windows build). Progress reporting is advisory; if the
                // caller's callback throws (e.g. IProgress<string>.Report touching a
                // disposing dispatcher), swallow it and keep the copy running.
                try { onBytesProgress(transferred, total); }
                catch { /* never let an exception cross the P/Invoke boundary */ }
                return 0; // PROGRESS_CONTINUE
            };
            routinePtr = Marshal.GetFunctionPointerForDelegate(routine);
        }
        try
        {
            // dwCopyFlags = 0 → allow overwrite, no restartable mode, buffered I/O.
            // \\?\ prefix bypasses the MAX_PATH limit unconditionally — required because the
            // raw Win32 API doesn't honour the longPathAware manifest in every host process
            // (e.g. unit-test runners), and paths > 260 chars are common in deeply nested trees.
            if (!CopyFileEx(ToExtendedPath(source), ToExtendedPath(dest),
                    lpProgressRoutine: routinePtr, lpData: IntPtr.Zero, ref cancel, dwCopyFlags: 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"CopyFileEx failed copying '{source}' to '{dest}': {new Win32Exception(err).Message}", err);
            }
        }
        finally
        {
            // Anchor the delegate past the CopyFileEx return so the kernel can't see
            // a freed function pointer if a callback is still draining when CopyFileEx
            // returns. KeepAlive emits the necessary read on `routine` to prevent the
            // JIT from eliding the local.
            GC.KeepAlive(routine);
        }
    }

    /// <summary>Win32 CopyProgressRoutine — fired by CopyFileEx as bytes stream to the destination.</summary>
    private delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred,
        long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason,
        IntPtr sourceFile, IntPtr destinationFile,
        IntPtr data);

    /// <summary>
    /// Returns the path in Win32 extended-length form (<c>\\?\</c> prefix) so <c>CopyFileEx</c>
    /// accepts paths longer than <c>MAX_PATH</c>. Already-prefixed and UNC paths pass through unchanged.
    /// </summary>
    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string lpExistingFileName,
        string lpNewFileName,
        IntPtr lpProgressRoutine,
        IntPtr lpData,
        ref int pbCancel,
        uint dwCopyFlags);

    /// <summary>
    /// Copies a file while computing the SHA-256 hash of the source data in a single read pass.
    /// The destination stream is flushed to the physical disk before returning, so the caller
    /// can trust that the written bytes are durable without re-reading the destination.
    /// </summary>
    /// <param name="source">Full path of the source file.</param>
    /// <param name="dest">Full path of the destination file (created or overwritten).</param>
    /// <param name="stripPermissions">When <c>true</c>, resets NTFS ACLs to inherit from parent.</param>
    /// <returns>The SHA-256 hash of the source data that was written.</returns>
    public byte[] CopyFileWithHash(string source, string dest, bool stripPermissions)
    {
        bool isHidden = File.GetAttributes(source).HasFlag(FileAttributes.Hidden);

        byte[] hash;
        // 1 MB buffer rented from the shared pool: better SSD command-granularity match than the
        // old 80 KB default, and avoids a fresh heap allocation per file (important when a backup
        // copies tens of thousands of files).
        const int BufferSize = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
            int bytesRead;
            while ((bytesRead = srcStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, bytesRead, null, 0);
                destStream.Write(buffer, 0, bytesRead);
            }
            sha.TransformFinalBlock([], 0, 0);
            hash = sha.Hash!;
            // Flush to the physical disk so the data is durable before we report success.
            // This eliminates the need to re-read the destination for verification.
            destStream.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Preserve timestamps
        var srcInfo = new FileInfo(source);
        File.SetLastWriteTimeUtc(dest, srcInfo.LastWriteTimeUtc);
        File.SetCreationTimeUtc(dest, srcInfo.CreationTimeUtc);

        if (stripPermissions)
            ResetFilePermissions(dest);

        if (isHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);

        return hash;
    }

    private void CopyDirectory(string source, string dest, bool stripPermissions, bool preserveHidden)
    {
        Directory.CreateDirectory(dest);

        // CopyEnumOptions skips reparse points — see field-level comment.
        foreach (var file in Directory.EnumerateFiles(source, "*", CopyEnumOptions))
        {
            CopyFile(file, Path.Combine(dest, Path.GetFileName(file)), stripPermissions);
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", CopyEnumOptions))
        {
            var dirAttr = File.GetAttributes(dir);
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), stripPermissions, dirAttr.HasFlag(FileAttributes.Hidden));
        }

        if (preserveHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
    }

    /// <summary>
    /// Removes explicit NTFS permissions from a file so it inherits from its parent folder.
    /// Permission stripping is best-effort: a failure here must not fail an otherwise-successful
    /// copy. Beyond UnauthorizedAccessException we also see IOException (file deleted between
    /// copy and reset) and InvalidOperationException (NTFS-only API hitting a FAT32 destination);
    /// any of these used to propagate up and inflate FilesFailed.
    /// </summary>
    private static void ResetFilePermissions(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(false, false);
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Could not reset permissions on {path}: {ex.Message}");
        }
    }

    /// <summary>Sends the file or directory at the given path to the Recycle Bin.</summary>
    public void DeleteItem(string path)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.Directory))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>Renames a file or directory within its parent folder.</summary>
    public void RenameItem(string path, string newName)
    {
        var parentDir = Path.GetDirectoryName(path)!;
        var newPath = Path.Combine(parentDir, newName);
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.Directory))
            Directory.Move(path, newPath);
        else
            File.Move(path, newPath);
    }

    /// <summary>Checks whether a file or directory exists at the given path.</summary>
    public bool ItemExists(string path) =>
        File.Exists(path) || Directory.Exists(path);
}
