using BeetsBackup.Models;
using Microsoft.VisualBasic.FileIO;
using System.Buffers;
using System.IO;
using System.Security.AccessControl;
using System.Runtime.Versioning;

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
    public void CopyFile(string source, string dest, bool stripPermissions)
    {
        bool isHidden = File.GetAttributes(source).HasFlag(FileAttributes.Hidden);
        var srcLastWrite = File.GetLastWriteTimeUtc(source);

        File.Copy(source, dest, overwrite: true);
        File.SetLastWriteTimeUtc(dest, srcLastWrite);

        if (stripPermissions)
            ResetFilePermissions(dest);

        if (isHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
    }

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

        foreach (var file in Directory.EnumerateFiles(source, "*", EnumOptions))
        {
            CopyFile(file, Path.Combine(dest, Path.GetFileName(file)), stripPermissions);
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", EnumOptions))
        {
            var dirAttr = File.GetAttributes(dir);
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), stripPermissions, dirAttr.HasFlag(FileAttributes.Hidden));
        }

        if (preserveHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
    }

    /// <summary>
    /// Removes explicit NTFS permissions from a file so it inherits from its parent folder.
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
        catch (UnauthorizedAccessException) { }
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
