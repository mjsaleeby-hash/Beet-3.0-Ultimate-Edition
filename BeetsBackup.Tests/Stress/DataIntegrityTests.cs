using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;
using System.Security.Cryptography;

namespace BeetsBackup.Tests.Stress;

/// <summary>
/// Data integrity tests — byte-for-byte verification, progress accuracy,
/// counter correctness, and content preservation across all transfer modes.
/// </summary>
public class DataIntegrityTests
{
    private readonly FileSystemService _fs = new();
    private readonly TransferService _transfer;

    public DataIntegrityTests()
    {
        _transfer = new TransferService(_fs);
    }

    // ============================================================
    //  BYTE-FOR-BYTE CONTENT VERIFICATION
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_VariedSizes_ByteForByte_Identical()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        int[] sizes = { 0, 1, 511, 512, 1023, 1024, 4095, 4096, 65535, 65536, 1024 * 1024 };
        var rng = new Random(12345);

        foreach (var size in sizes)
        {
            var data = new byte[size];
            rng.NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, $"test_{size}.bin"), data);
        }

        var srcFiles = Directory.GetFiles(src);
        await _transfer.CopyAsync(srcFiles, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Verify byte-for-byte equality
        foreach (var srcFile in srcFiles)
        {
            var destFile = Path.Combine(dest, Path.GetFileName(srcFile));
            var srcBytes = File.ReadAllBytes(srcFile);
            var destBytes = File.ReadAllBytes(destFile);
            destBytes.Should().Equal(srcBytes, $"{Path.GetFileName(srcFile)} should be byte-identical");
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_Replace_ContentActuallyReplaced()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        // Create source with specific content
        var srcData = new byte[1024];
        new Random(111).NextBytes(srcData);
        File.WriteAllBytes(Path.Combine(src, "data.bin"), srcData);

        // Create dest with different content
        var oldData = new byte[1024];
        new Random(999).NextBytes(oldData);
        File.WriteAllBytes(Path.Combine(dest, "data.bin"), oldData);

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "data.bin") }, dest,
            stripPermissions: false, TransferMode.Replace);

        var destData = File.ReadAllBytes(Path.Combine(dest, "data.bin"));
        destData.Should().Equal(srcData);
        destData.Should().NotEqual(oldData);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_KeepBoth_OriginalUntouched()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        var originalData = new byte[512];
        new Random(42).NextBytes(originalData);
        File.WriteAllBytes(Path.Combine(dest, "file.bin"), originalData);

        var newData = new byte[512];
        new Random(99).NextBytes(newData);
        File.WriteAllBytes(Path.Combine(src, "file.bin"), newData);

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.bin") }, dest,
            stripPermissions: false, TransferMode.KeepBoth);

        // Original should be untouched
        File.ReadAllBytes(Path.Combine(dest, "file.bin")).Should().Equal(originalData);
        // New copy should have the new data
        File.ReadAllBytes(Path.Combine(dest, "file-1.bin")).Should().Equal(newData);
    }

    // ============================================================
    //  SHA256 CHECKSUM VERIFICATION AT SCALE
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_500Files_ManualChecksumVerification()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("hashed", 500, sizeBytes: 2048).Build();

        // Compute checksums before copy
        var srcDir = Path.Combine(src, "hashed");
        var preHashes = new Dictionary<string, byte[]>();
        foreach (var file in Directory.GetFiles(srcDir))
        {
            using var stream = File.OpenRead(file);
            preHashes[Path.GetFileName(file)] = SHA256.HashData(stream);
        }

        await _transfer.CopyAsync(
            new[] { srcDir }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Verify checksums after copy
        var destDir = Path.Combine(dest, "hashed");
        foreach (var (name, srcHash) in preHashes)
        {
            using var stream = File.OpenRead(Path.Combine(destDir, name));
            var destHash = SHA256.HashData(stream);
            destHash.Should().Equal(srcHash, $"{name} should have matching SHA256");
        }
    }

    // ============================================================
    //  PROGRESS ACCURACY
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_1000Files_ProgressMonotonicallyIncreasing()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("prog", 1000, sizeBytes: 128).Build();

        var percents = new List<int>();
        var progress = new Progress<int>(pct => percents.Add(pct));

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "prog") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            progressPercent: progress);

        // Allow progress callbacks to fire
        await Task.Delay(100);

        // Should be monotonically increasing
        for (int i = 1; i < percents.Count; i++)
            percents[i].Should().BeGreaterThanOrEqualTo(percents[i - 1],
                $"progress should never decrease (index {i})");

        // Should reach 100
        percents.Should().Contain(100);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_1000Files_ProgressMessages_ReportEachFile()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("msg", 200, sizeBytes: 64).Build();

        var messages = new List<string>();
        var progress = new Progress<string>(msg => messages.Add(msg));

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "msg") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            progress: progress);

        await Task.Delay(100);

        // Should have a message for each copied file plus the scanning message
        messages.Where(m => m.StartsWith("Copied:")).Count().Should().Be(200);
    }

    // ============================================================
    //  TRANSFER RESULT COUNTER ACCURACY
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task TransferResult_Counters_SumToTotal()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "counter");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        // Create initial 500 files
        FileBuilder.In(src).BulkFiles("", 500, sizeBytes: 256).Build();

        // Copy once — all 500 go to dest
        await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Add 100 NEW files with a different prefix so no name collisions
        FileBuilder.In(src).BulkFiles("", 100, sizeBytes: 256, prefix: "extra").Build();

        // Copy again — 500 should be skipped, 100 should be copied
        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        var processed = result.FilesCopied + result.FilesSkipped +
                        result.FilesFailed + result.DiskFullErrors + result.FilesLocked;
        processed.Should().Be(result.TotalFiles,
            "all files should be accounted for in exactly one counter");
        result.FilesCopied.Should().Be(100);
        result.FilesSkipped.Should().Be(500);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task TransferResult_BytesTransferred_MatchesActualData()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        int[] sizes = { 100, 200, 500, 1024, 2048, 4096 };
        long expectedTotal = 0;
        foreach (var size in sizes)
        {
            File.WriteAllBytes(Path.Combine(src, $"f{size}.bin"), new byte[size]);
            expectedTotal += size;
        }

        var result = await _transfer.CopyAsync(
            Directory.GetFiles(src), dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.BytesTransferred.Should().Be(expectedTotal);
    }

    // ============================================================
    //  MOVE — DATA NOT LOST
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Move_500Files_AllDataPreserved()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "movebatch");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("", 500, sizeBytes: 512).Build();

        // Record pre-move checksums
        var preHashes = new Dictionary<string, byte[]>();
        foreach (var file in Directory.GetFiles(src))
        {
            using var stream = File.OpenRead(file);
            preHashes[Path.GetFileName(file)] = SHA256.HashData(stream);
        }

        var result = await _transfer.MoveAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(500);

        // Source should be gone
        Directory.Exists(src).Should().BeFalse();

        // All files should exist in dest with same checksums
        var destDir = Path.Combine(dest, "movebatch");
        foreach (var (name, srcHash) in preHashes)
        {
            var destFile = Path.Combine(destDir, name);
            File.Exists(destFile).Should().BeTrue($"{name} should exist in dest");
            using var stream = File.OpenRead(destFile);
            SHA256.HashData(stream).Should().Equal(srcHash, $"{name} checksum should match");
        }
    }

    // ============================================================
    //  TIMESTAMP PRESERVATION
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_TimestampsPreserved()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        var srcFile = Path.Combine(src, "dated.txt");
        File.WriteAllText(srcFile, "timestamp test");
        var specificTime = new DateTime(2020, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(srcFile, specificTime);

        await _transfer.CopyAsync(
            new[] { srcFile }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        var destTime = File.GetLastWriteTimeUtc(Path.Combine(dest, "dated.txt"));
        destTime.Should().Be(specificTime);
    }

    // ============================================================
    //  STRIP PERMISSIONS
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_StripPermissions_DoesNotFailOnLargeBatch()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("perms", 200, sizeBytes: 128).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "perms") }, dest,
            stripPermissions: true, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(200);
        result.FilesFailed.Should().Be(0);
    }
}
