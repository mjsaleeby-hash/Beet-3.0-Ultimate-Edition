using BeetsBackup.Models;
using System.IO;
using System.Security.AccessControl;
using System.Runtime.Versioning;

namespace BeetsBackup.Services;

public class FileSystemService
{
    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    public IEnumerable<DriveItem> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DriveItem(d));
    }

    public IEnumerable<FileSystemItem> GetChildren(string path)
    {
        var items = new List<FileSystemItem>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", EnumOptions))
                items.Add(new FileSystemItem(new DirectoryInfo(dir)));

            foreach (var file in Directory.EnumerateFiles(path, "*", EnumOptions))
                items.Add(new FileSystemItem(new FileInfo(file)));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return items;
    }

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

    public void CopyFile(string source, string dest, bool stripPermissions)
    {
        bool isHidden = File.GetAttributes(source).HasFlag(FileAttributes.Hidden);

        File.Copy(source, dest, overwrite: true);

        if (stripPermissions)
            ResetFilePermissions(dest);

        if (isHidden)
            File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
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

    public void DeleteItem(string path)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.Directory))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }

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

    public bool ItemExists(string path) =>
        File.Exists(path) || Directory.Exists(path);
}
