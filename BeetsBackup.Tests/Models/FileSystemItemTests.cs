using BeetsBackup.Models;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Models;

public class FileSystemItemTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    [Trait("Category", "Unit")]
    public void FormatBytes_Boundaries_FormatsCorrectly(long bytes, string expected)
    {
        // FormatBytes uses "0.##" format, so exact powers show no decimals
        var result = FileSystemItem.FormatBytes(bytes);
        result.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FileSystemItem_File_Properties_AreCorrect()
    {
        using var tmp = new TempDirectory();
        var filePath = Path.Combine(tmp.Path, "test.txt");
        File.WriteAllText(filePath, "Hello");
        var fi = new FileInfo(filePath);
        var item = new FileSystemItem(fi);

        item.IsDirectory.Should().BeFalse();
        item.Name.Should().Be("test.txt");
        item.Size.Should().Be(fi.Length);
        item.TypeDisplay.Should().Be("TXT File");
        item.Icon.Should().Be("\U0001F4C4");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FileSystemItem_Directory_Properties_AreCorrect()
    {
        using var tmp = new TempDirectory();
        var dirPath = Path.Combine(tmp.Path, "subdir");
        Directory.CreateDirectory(dirPath);
        var di = new DirectoryInfo(dirPath);
        var item = new FileSystemItem(di);

        item.IsDirectory.Should().BeTrue();
        item.Name.Should().Be("subdir");
        item.IsCalculating.Should().BeTrue();
        item.SizeDisplay.Should().Be("Calculating...");
        item.TypeDisplay.Should().Be("Folder");
        item.Icon.Should().Be("\U0001F4C1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FileSystemItem_HiddenFile_IsHidden_ReturnsTrue()
    {
        using var tmp = new TempDirectory();
        var filePath = Path.Combine(tmp.Path, "hidden.txt");
        File.WriteAllText(filePath, "secret");
        File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.Hidden);

        var item = new FileSystemItem(new FileInfo(filePath));
        item.IsHidden.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CalculateDirectorySizeAsync_EmptyDirectory_SetsZero()
    {
        using var tmp = new TempDirectory();
        var dirPath = Path.Combine(tmp.Path, "empty");
        Directory.CreateDirectory(dirPath);
        var item = new FileSystemItem(new DirectoryInfo(dirPath));

        await item.CalculateDirectorySizeAsync();

        item.Size.Should().Be(0);
        item.IsCalculating.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CalculateDirectorySizeAsync_WithFiles_ReturnsSumOfFileSizes()
    {
        using var tmp = new TempDirectory();
        var dirPath = Path.Combine(tmp.Path, "sized");
        Directory.CreateDirectory(dirPath);
        File.WriteAllBytes(Path.Combine(dirPath, "a.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(dirPath, "b.bin"), new byte[2048]);

        var item = new FileSystemItem(new DirectoryInfo(dirPath));
        await item.CalculateDirectorySizeAsync();

        item.Size.Should().Be(3072);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CalculateDirectorySizeAsync_Cancelled_ThrowsOrLeavesUnchanged()
    {
        using var tmp = new TempDirectory();
        var dirPath = Path.Combine(tmp.Path, "cancel");
        Directory.CreateDirectory(dirPath);
        File.WriteAllBytes(Path.Combine(dirPath, "a.bin"), new byte[1024]);

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var item = new FileSystemItem(new DirectoryInfo(dirPath));

        var act = () => item.CalculateDirectorySizeAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        item.Size.Should().Be(-1); // unchanged
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PropertyChanged_FiredOnSizeUpdate()
    {
        using var tmp = new TempDirectory();
        var dirPath = Path.Combine(tmp.Path, "notify");
        Directory.CreateDirectory(dirPath);
        File.WriteAllBytes(Path.Combine(dirPath, "a.bin"), new byte[100]);

        var item = new FileSystemItem(new DirectoryInfo(dirPath));
        var changedProps = new List<string>();
        item.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        await item.CalculateDirectorySizeAsync();

        changedProps.Should().Contain("Size");
        changedProps.Should().Contain("SizeDisplay");
        changedProps.Should().Contain("IsCalculating");
    }
}
