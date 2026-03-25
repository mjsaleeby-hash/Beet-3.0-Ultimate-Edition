using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Stress;

/// <summary>
/// Edge case and adversarial tests — unusual filenames, attributes,
/// path lengths, locked files, rapid operations, and cancellation scenarios.
/// </summary>
public class EdgeCaseTests
{
    private readonly FileSystemService _fs = new();
    private readonly TransferService _transfer;

    public EdgeCaseTests()
    {
        _transfer = new TransferService(_fs);
    }

    // ============================================================
    //  UNICODE & SPECIAL CHARACTER FILENAMES
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_UnicodeFilenames_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).UnicodeFiles(sizeBytes: 256).Build();

        var srcFiles = Directory.GetFiles(src);
        var result = await _transfer.CopyAsync(
            srcFiles, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(srcFiles.Length);
        result.FilesFailed.Should().Be(0);

        // Verify each file exists in destination
        foreach (var file in srcFiles)
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            File.Exists(destFile).Should().BeTrue($"because {Path.GetFileName(file)} should be copied");
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_UnicodeFilenames_KeepBoth_RenamedCorrectly()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).UnicodeFiles(sizeBytes: 128).Build();
        var srcFiles = Directory.GetFiles(src);

        // First copy
        await _transfer.CopyAsync(srcFiles, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Second copy with KeepBoth
        var result = await _transfer.CopyAsync(srcFiles, dest,
            stripPermissions: false, TransferMode.KeepBoth);

        result.FilesCopied.Should().Be(srcFiles.Length);
        Directory.GetFiles(dest).Length.Should().Be(srcFiles.Length * 2);
    }

    // ============================================================
    //  SPECIAL CHARACTERS IN FOLDER NAMES
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_FolderWithSpacesAndParens_Copied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src)
            .File(Path.Combine("My Documents (Backup)", "report.txt"), 200)
            .File(Path.Combine("My Documents (Backup)", "sub folder [2024]", "data.csv"), 300)
            .Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "My Documents (Backup)") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(2);
        result.FilesFailed.Should().Be(0);
    }

    // ============================================================
    //  ZERO-BYTE FILES
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_100ZeroByteFiles_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("zeros", 100, sizeBytes: 0).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "zeros") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(100);
        result.BytesTransferred.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_ZeroByteFiles_SkipExisting_AllSkipped()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("zeros", 50, sizeBytes: 0).Build();

        // First copy
        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "zeros") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Second copy — zero-byte files with same timestamp should be skipped
        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "zeros") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesSkipped.Should().Be(50);
    }

    // ============================================================
    //  LONG FILE NAMES
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_LongFileName_CopiedSuccessfully()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).LongNameFile(nameLength: 150, sizeBytes: 256).Build();

        var srcFiles = Directory.GetFiles(src);
        var result = await _transfer.CopyAsync(
            srcFiles, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1);
        result.FilesFailed.Should().Be(0);
    }

    // ============================================================
    //  READ-ONLY FILES
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_ReadOnlyFile_CopiedSuccessfully()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).File("readonly.txt", 100, readOnly: true).Build();

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "readonly.txt") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1);
        File.Exists(Path.Combine(dest, "readonly.txt")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_MixedAttributes_AllCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src)
            .File("normal.txt", 100)
            .File("hidden.txt", 100, hidden: true)
            .File("readonly.txt", 100, readOnly: true)
            .File("both.txt", 100, hidden: true, readOnly: true)
            .Build();

        var srcFiles = Directory.GetFiles(src);
        var result = await _transfer.CopyAsync(
            srcFiles, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(4);
        result.FilesFailed.Should().Be(0);
    }

    // ============================================================
    //  LOCKED FILES (simulated)
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_LockedFileAmongMany_OthersCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "mixed");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src)
            .File("good1.txt", 100)
            .File("locked.txt", 100)
            .File("good2.txt", 100)
            .Build();

        // Lock one file with exclusive access
        using var lockStream = new FileStream(
            Path.Combine(src, "locked.txt"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // The two unlocked files should copy; the locked one should fail
        result.FilesCopied.Should().Be(2);
        (result.FilesFailed + result.FilesLocked).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_LockedFileDoesNotCorruptTransfer()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "batch");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("", 50, sizeBytes: 256).Build();

        // Lock one file in the middle
        using var lockStream = new FileStream(
            Path.Combine(src, "file_0025.dat"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // 49 should succeed, 1 should fail
        result.FilesCopied.Should().Be(49);
        (result.FilesFailed + result.FilesLocked).Should().Be(1);

        // Verify the 49 copied files are intact
        var copiedFiles = Directory.GetFiles(Path.Combine(dest, "batch"));
        copiedFiles.Length.Should().Be(49);
    }

    // ============================================================
    //  KEEPBOTH COLLISION STRESS
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task KeepBoth_50Collisions_IncrementingNames()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllText(Path.Combine(src, "report.txt"), "new data");

        // Copy the same file 50 times with KeepBoth
        for (int i = 0; i < 50; i++)
        {
            await _transfer.CopyAsync(
                new[] { Path.Combine(src, "report.txt") }, dest,
                stripPermissions: false, TransferMode.KeepBoth);
        }

        var destFiles = Directory.GetFiles(dest);
        destFiles.Length.Should().Be(50);

        // Should have report.txt, report-1.txt, report-2.txt, ..., report-49.txt
        File.Exists(Path.Combine(dest, "report.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "report-49.txt")).Should().BeTrue();
    }

    // ============================================================
    //  RAPID BACK-TO-BACK TRANSFERS
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_RapidBackToBack_10Transfers_NoCorruption()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        Directory.CreateDirectory(src);
        FileBuilder.In(src).BulkFiles("data", 100, sizeBytes: 512).Build();

        for (int i = 0; i < 10; i++)
        {
            var dest = Path.Combine(tmp.Path, $"dest_{i}");
            Directory.CreateDirectory(dest);

            var result = await _transfer.CopyAsync(
                new[] { Path.Combine(src, "data") }, dest,
                stripPermissions: false, TransferMode.SkipExisting);

            result.FilesCopied.Should().Be(100, $"iteration {i} should copy all files");
            result.FilesFailed.Should().Be(0, $"iteration {i} should have no failures");
        }
    }

    // ============================================================
    //  CANCELLATION MID-TRANSFER
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_CancelledMidway_NoPartialFiles()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        // Create enough files that cancellation can happen mid-way
        FileBuilder.In(src).BulkFiles("cancel", 2000, sizeBytes: 1024).Build();

        var cts = new CancellationTokenSource();
        var progress = new Progress<int>(pct =>
        {
            if (pct >= 10)
                cts.Cancel(); // Cancel after ~10%
        });

        var act = () => _transfer.CopyAsync(
            new[] { Path.Combine(src, "cancel") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            progressPercent: progress,
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Every file that exists in dest should be complete (not truncated)
        foreach (var destFile in Directory.EnumerateFiles(Path.Combine(dest, "cancel")))
        {
            new FileInfo(destFile).Length.Should().Be(1024,
                $"because {Path.GetFileName(destFile)} should not be truncated");
        }
    }

    // ============================================================
    //  PAUSE / RESUME
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_PauseAndResume_AllFilesEventuallyCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src).BulkFiles("paused", 500, sizeBytes: 128).Build();

        var pauseToken = new ManualResetEventSlim(true); // Start unpaused
        var pausedOnce = false;

        var progress = new Progress<int>(pct =>
        {
            if (pct >= 20 && !pausedOnce)
            {
                pauseToken.Reset(); // Pause
                pausedOnce = true;
                // Resume after a short delay (on threadpool)
                Task.Delay(100).ContinueWith(_ => pauseToken.Set());
            }
        });

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "paused") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            progressPercent: progress,
            pauseToken: pauseToken);

        result.FilesCopied.Should().Be(500);
        result.FilesFailed.Should().Be(0);
    }

    // ============================================================
    //  EMPTY DIRECTORIES NESTED DEEP
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Copy_EmptyNestedDirectories_AllCreated()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "root");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        // Create 20 empty nested directories
        var current = src;
        for (int i = 0; i < 20; i++)
        {
            current = Path.Combine(current, $"empty_{i}");
            Directory.CreateDirectory(current);
        }

        var result = await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Verify all directories exist in dest
        var destPath = Path.Combine(dest, "root");
        for (int i = 0; i < 20; i++)
        {
            destPath = Path.Combine(destPath, $"empty_{i}");
            Directory.Exists(destPath).Should().BeTrue($"level {i} should exist");
        }
    }

    // ============================================================
    //  MOVE WITH LOCKED FILE — SOURCE PRESERVED
    // ============================================================

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Move_LockedFileInFolder_SourceNotDeleted()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "moveme");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src)
            .File("good.txt", 100)
            .File("locked.txt", 100)
            .Build();

        // Lock one file
        using var lockStream = new FileStream(
            Path.Combine(src, "locked.txt"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await _transfer.MoveAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Source folder should NOT be deleted because one file failed
        Directory.Exists(src).Should().BeTrue("source should be preserved when a file fails");
    }
}
