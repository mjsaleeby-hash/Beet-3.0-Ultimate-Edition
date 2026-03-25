using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

public class FileSystemServiceTests
{
    private readonly FileSystemService _fs = new();

    [Fact]
    [Trait("Category", "Integration")]
    public void GetChildren_EmptyDirectory_ReturnsEmpty()
    {
        using var tmp = new TempDirectory();
        _fs.GetChildren(tmp.Path).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetChildren_MixedContent_ReturnsBothFilesAndFolders()
    {
        using var tmp = new TempDirectory();
        FileBuilder.In(tmp.Path)
            .File("readme.txt", 100)
            .Dir("subfolder")
            .Build();

        var items = _fs.GetChildren(tmp.Path).ToList();
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.IsDirectory && i.Name == "subfolder");
        items.Should().Contain(i => !i.IsDirectory && i.Name == "readme.txt");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetChildren_HiddenFile_IsIncludedAndFlagged()
    {
        using var tmp = new TempDirectory();
        FileBuilder.In(tmp.Path)
            .File("hidden.txt", 50, hidden: true)
            .Build();

        var items = _fs.GetChildren(tmp.Path).ToList();
        items.Should().HaveCount(1);
        items[0].IsHidden.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CopyFile_BasicFile_CopiedSuccessfully()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "source.txt");
        var dest = Path.Combine(tmp.Path, "dest.txt");
        File.WriteAllText(src, "hello");

        _fs.CopyFile(src, dest, stripPermissions: false);

        File.Exists(dest).Should().BeTrue();
        File.ReadAllText(dest).Should().Be("hello");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void CopyFile_HiddenFile_HiddenAttributePreserved()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "hidden.txt");
        var dest = Path.Combine(tmp.Path, "copy.txt");
        File.WriteAllText(src, "secret");
        File.SetAttributes(src, File.GetAttributes(src) | FileAttributes.Hidden);

        _fs.CopyFile(src, dest, stripPermissions: false);

        File.GetAttributes(dest).HasFlag(FileAttributes.Hidden).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RenameItem_File_RenamedSuccessfully()
    {
        using var tmp = new TempDirectory();
        var original = Path.Combine(tmp.Path, "old.txt");
        File.WriteAllText(original, "data");

        _fs.RenameItem(original, "new.txt");

        File.Exists(Path.Combine(tmp.Path, "new.txt")).Should().BeTrue();
        File.Exists(original).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RenameItem_Directory_RenamedSuccessfully()
    {
        using var tmp = new TempDirectory();
        var original = Path.Combine(tmp.Path, "olddir");
        Directory.CreateDirectory(original);

        _fs.RenameItem(original, "newdir");

        Directory.Exists(Path.Combine(tmp.Path, "newdir")).Should().BeTrue();
        Directory.Exists(original).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ItemExists_ExistingFile_ReturnsTrue()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Path, "exists.txt");
        File.WriteAllText(path, "yes");

        _fs.ItemExists(path).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ItemExists_Nonexistent_ReturnsFalse()
    {
        _fs.ItemExists(@"C:\ThisPathDoesNotExist12345\file.txt").Should().BeFalse();
    }
}
