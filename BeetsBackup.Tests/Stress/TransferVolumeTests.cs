using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Stress;

/// <summary>
/// Stress tests for transfer operations at scale.
/// These tests create thousands of files and/or large files.
/// Tagged "Stress" so they can be run separately from the fast unit tests.
/// </summary>
public class TransferVolumeTests
{
    private readonly FileSystemService _fs = new();
    private readonly TransferService _transfer;

    public TransferVolumeTests()
    {
        _transfer = new TransferService(_fs);
    }

    // ============================================================
    //  HIGH FILE COUNT
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_5000SmallFiles_AllCopiedCorrectly()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("batch", 5000, sizeBytes: 128).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "batch") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(5000);
        result.FilesFailed.Should().Be(0);
        result.FilesSkipped.Should().Be(0);
        Directory.EnumerateFiles(Path.Combine(dest, "batch")).Count().Should().Be(5000);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_1000FilesInFlatDirectory_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("wide", 1000, sizeBytes: 512).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "wide") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1000);
        result.FilesFailed.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_5000Files_ThenSkipExisting_AllSkipped()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("batch", 5000, sizeBytes: 128).Build();

        // First copy
        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "batch") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Second copy — all should be skipped
        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "batch") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesSkipped.Should().Be(5000);
        result.FilesCopied.Should().Be(0);
    }

    // ============================================================
    //  LARGE FILES
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_100MB_File_CopiedWithCorrectSize()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        long size = 100 * 1024 * 1024; // 100 MB
        FileBuilder.In(src).LargeFile("bigfile.bin", size).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "bigfile.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1);
        result.BytesTransferred.Should().Be(size);
        new FileInfo(Path.Combine(dest, "bigfile.bin")).Length.Should().Be(size);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_100MB_File_WithChecksum_ZeroMismatches()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        long size = 100 * 1024 * 1024;
        FileBuilder.In(src).LargeFile("verified.bin", size).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "verified.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            verifyChecksums: true);

        result.ChecksumMismatches.Should().Be(0);
        result.FilesCopied.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_MultipleLargeFiles_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        long size = 25 * 1024 * 1024; // 25 MB each
        for (int i = 0; i < 4; i++)
            FileBuilder.In(src).LargeFile($"large_{i}.bin", size).Build();

        var files = Directory.GetFiles(src);
        var result = await _transfer.CopyAsync(
            files, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(4);
        result.BytesTransferred.Should().Be(size * 4);
    }

    // ============================================================
    //  MIXED WORKLOAD
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_MixedSizes_FromTinyToLarge_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "mixed");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        var builder = FileBuilder.In(src);
        // Tiny files
        builder.BulkFiles("tiny", 100, sizeBytes: 1);
        // Small files
        builder.BulkFiles("small", 50, sizeBytes: 4096);
        // Medium files
        builder.BulkFiles("medium", 10, sizeBytes: 1024 * 1024);
        // One large file
        builder.LargeFile("big.bin", 10 * 1024 * 1024);
        // Zero-byte files
        builder.BulkFiles("empty", 20, sizeBytes: 0);
        builder.Build();

        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(100 + 50 + 10 + 1 + 20);
        result.FilesFailed.Should().Be(0);
    }

    // ============================================================
    //  DEEP NESTING
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_DeepNesting30Levels_SuccessfullyCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).DeepNesting(30, fileSize: 64).Build();

        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1);
        result.FilesFailed.Should().Be(0);

        // Verify the file exists at the bottom
        var deepPath = Path.Combine(dest, "src");
        for (int i = 0; i < 30; i++)
            deepPath = Path.Combine(deepPath, $"level{i}");
        File.Exists(Path.Combine(deepPath, "deep.dat")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_DeepNestingWithFilesAtEveryLevel_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "tree");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        // Create 20 levels with a file at each level
        var builder = FileBuilder.In(src);
        var path = "";
        for (int i = 0; i < 20; i++)
        {
            path = Path.Combine(path, $"d{i}");
            builder.File(Path.Combine(path, $"file_{i}.txt"), 64);
        }
        builder.Build();

        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(20);
        result.FilesFailed.Should().Be(0);
    }

    // ============================================================
    //  ALL TRANSFER MODES UNDER LOAD
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Replace_1000ExistingFiles_AllReplaced()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("data", 1000, sizeBytes: 256).Build();

        // First copy
        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "data") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Now replace all
        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "data") }, dest,
            stripPermissions: false, TransferMode.Replace);

        result.FilesCopied.Should().Be(1000);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task KeepBoth_500Collisions_AllRenamedCorrectly()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("", 500, sizeBytes: 64).Build();
        var srcFiles = Directory.GetFiles(src);

        // First copy — individual files into dest
        await _transfer.CopyAsync(
            srcFiles, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Second copy with KeepBoth — all 500 should create renamed copies
        var result = await _transfer.CopyAsync(
            srcFiles, dest,
            stripPermissions: false, TransferMode.KeepBoth);

        result.FilesCopied.Should().Be(500);
        // Should now have 1000 files total
        Directory.EnumerateFiles(dest).Count().Should().Be(1000);
    }

    // ============================================================
    //  CHECKSUM AT SCALE
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_1000Files_WithChecksum_ZeroMismatches()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("verified", 1000, sizeBytes: 1024).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "verified") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            verifyChecksums: true);

        result.ChecksumMismatches.Should().Be(0);
        result.FilesCopied.Should().Be(1000);
    }
}
