using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

public class TransferServiceTests
{
    private readonly FileSystemService _fs = new();
    private readonly TransferService _transfer;

    public TransferServiceTests()
    {
        _transfer = new TransferService(_fs);
    }

    // ============================================================
    //  COPY — SKIP EXISTING
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_SkipExisting_IdenticalSizeAndTimestamp_FileSkipped()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllBytes(Path.Combine(src, "file.bin"), new byte[100]);
        File.Copy(Path.Combine(src, "file.bin"), Path.Combine(dest, "file.bin"));
        // Ensure identical timestamp
        File.SetLastWriteTimeUtc(Path.Combine(dest, "file.bin"),
            File.GetLastWriteTimeUtc(Path.Combine(src, "file.bin")));

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesSkipped.Should().Be(1);
        result.FilesCopied.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_SkipExisting_SourceNewer_FileCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllBytes(Path.Combine(src, "file.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dest, "file.bin"), new byte[100]);
        // Make source newer
        File.SetLastWriteTimeUtc(Path.Combine(src, "file.bin"), DateTime.UtcNow);
        File.SetLastWriteTimeUtc(Path.Combine(dest, "file.bin"), DateTime.UtcNow.AddHours(-1));

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_SkipExisting_DifferentSize_FileCopied()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllBytes(Path.Combine(src, "file.bin"), new byte[200]);
        File.WriteAllBytes(Path.Combine(dest, "file.bin"), new byte[100]);

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesCopied.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_SkipExisting_SourceOlderSameSize_Skipped()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllBytes(Path.Combine(src, "file.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dest, "file.bin"), new byte[100]);
        // Make source OLDER
        File.SetLastWriteTimeUtc(Path.Combine(src, "file.bin"), DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(dest, "file.bin"), DateTime.UtcNow);

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        result.FilesSkipped.Should().Be(1);
    }

    // ============================================================
    //  COPY — KEEP BOTH
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_KeepBoth_ExistingFile_CreatesRenamedCopy()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllText(Path.Combine(src, "file.txt"), "new");
        File.WriteAllText(Path.Combine(dest, "file.txt"), "old");

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.txt") }, dest,
            stripPermissions: false, TransferMode.KeepBoth);

        File.Exists(Path.Combine(dest, "file.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "file-1.txt")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_KeepBoth_MultipleExisting_IncrementsCorrectly()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllText(Path.Combine(src, "file.txt"), "new");
        File.WriteAllText(Path.Combine(dest, "file.txt"), "v0");
        File.WriteAllText(Path.Combine(dest, "file-1.txt"), "v1");

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.txt") }, dest,
            stripPermissions: false, TransferMode.KeepBoth);

        File.Exists(Path.Combine(dest, "file-2.txt")).Should().BeTrue();
    }

    // ============================================================
    //  COPY — REPLACE
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_Replace_ExistingFile_IsOverwritten()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllText(Path.Combine(src, "file.txt"), "new content");
        File.WriteAllText(Path.Combine(dest, "file.txt"), "old content");

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.txt") }, dest,
            stripPermissions: false, TransferMode.Replace);

        File.ReadAllText(Path.Combine(dest, "file.txt")).Should().Be("new content");
    }

    // ============================================================
    //  COPY — PROGRESS
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_ProgressPercent_ReachesOneHundred()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), new byte[10]);

        // Track the peak via Interlocked.Exchange-with-CAS rather than a plain assignment:
        // parallel copy workers may call Report from different threads, and a stale 50% landing
        // after a 100% would otherwise mask the test's intent.
        var maxPercent = 0;
        var progress = new SynchronousProgress<int>(p =>
        {
            int prev;
            do { prev = maxPercent; if (p <= prev) return; }
            while (Interlocked.CompareExchange(ref maxPercent, p, prev) != prev);
        });

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "a.bin"), Path.Combine(src, "b.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            progressPercent: progress);

        maxPercent.Should().Be(100);
    }

    // ============================================================
    //  COPY — CHECKSUMS
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_VerifyChecksums_CleanCopy_ZeroMismatches()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        File.WriteAllBytes(Path.Combine(src, "data.bin"), new byte[1024]);

        var result = await _transfer.CopyAsync(
            new[] { Path.Combine(src, "data.bin") }, dest,
            stripPermissions: false, TransferMode.SkipExisting,
            verifyChecksums: true);

        result.ChecksumMismatches.Should().Be(0);
        result.FilesCopied.Should().Be(1);
    }

    // ============================================================
    //  COPY — HIDDEN FILES
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_HiddenFile_PreservesHiddenAttribute()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        var srcFile = Path.Combine(src, "secret.txt");
        File.WriteAllText(srcFile, "hidden");
        File.SetAttributes(srcFile, File.GetAttributes(srcFile) | FileAttributes.Hidden);

        await _transfer.CopyAsync(
            new[] { srcFile }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        File.GetAttributes(Path.Combine(dest, "secret.txt"))
            .HasFlag(FileAttributes.Hidden).Should().BeTrue();
    }

    // ============================================================
    //  COPY — CANCELLATION
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_CancellationBeforeStart_ThrowsOperationCanceled()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "file.bin"), new byte[10]);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _transfer.CopyAsync(
            new[] { Path.Combine(src, "file.bin") }, dest,
            stripPermissions: false, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ============================================================
    //  COPY — DIRECTORY STRUCTURE
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_NestedDirectoryStructure_PreservesStructure()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "project");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);

        FileBuilder.In(src)
            .File("readme.txt", 50)
            .File(Path.Combine("sub1", "a.txt"), 30)
            .File(Path.Combine("sub1", "sub2", "b.txt"), 20)
            .Build();

        await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        File.Exists(Path.Combine(dest, "project", "readme.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "project", "sub1", "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "project", "sub1", "sub2", "b.txt")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_EmptyFolder_DestinationFolderCreated()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "empty");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        await _transfer.CopyAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        Directory.Exists(Path.Combine(dest, "empty")).Should().BeTrue();
    }

    // ============================================================
    //  COPY — SKIP EXISTING DIRECTORY MERGE (debugger addition)
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_SkipExisting_DirectoryAlwaysMerges_NeverSkipped()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        // Source has folder with file
        FileBuilder.In(src).File(Path.Combine("shared", "new.txt"), 50).Build();
        // Dest already has same-named folder with different file
        FileBuilder.In(dest).File(Path.Combine("shared", "existing.txt"), 30).Build();

        await _transfer.CopyAsync(
            new[] { Path.Combine(src, "shared") }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        // Both files should exist — directory contents merged
        File.Exists(Path.Combine(dest, "shared", "existing.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "shared", "new.txt")).Should().BeTrue();
    }

    // ============================================================
    //  MOVE — DATA SAFETY (highest priority)
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MoveAsync_AllFilesSucceed_SourceDeleted()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        var srcFile = Path.Combine(src, "moveme.txt");
        File.WriteAllText(srcFile, "data");

        await _transfer.MoveAsync(
            new[] { srcFile }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        File.Exists(Path.Combine(dest, "moveme.txt")).Should().BeTrue();
        // Source should be deleted (sent to recycle bin)
        File.Exists(srcFile).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MoveAsync_DirectoryAllSucceed_SourceDeleted()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src", "folder");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);
        FileBuilder.In(src).File("a.txt", 50).File("b.txt", 50).Build();

        await _transfer.MoveAsync(
            new[] { src }, dest,
            stripPermissions: false, TransferMode.SkipExisting);

        File.Exists(Path.Combine(dest, "folder", "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "folder", "b.txt")).Should().BeTrue();
        Directory.Exists(src).Should().BeFalse();
    }

    // ============================================================
    //  FREE-SPACE REQUIREMENT — sized off the plan, not the raw source total
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public void RequiredFreeBytes_AllNewFiles_CountsEveryByte()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        FileBuilder.In(src).File("a.bin", 1000).File("b.bin", 2000).Build();
        Directory.CreateDirectory(dest);

        var plan = _transfer.EnumerateWorkItems(
            new[] { Path.Combine(src, "a.bin"), Path.Combine(src, "b.bin") },
            dest, TransferMode.SkipExisting, exclusions: null, CancellationToken.None);

        TransferService.RequiredFreeBytes(plan).Should().Be(3000);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RequiredFreeBytes_SkipExisting_IdenticalFilesCostNothing()
    {
        // The headline regression: re-running a backup where most data already exists at the
        // destination. The old check summed the raw source total and could reject a backup that
        // needed almost no space; the plan-based requirement counts only what actually copies.
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        // A big file already present and identical at the destination, plus one small new file.
        File.WriteAllBytes(Path.Combine(src, "big.bin"), new byte[100_000]);
        File.WriteAllBytes(Path.Combine(dest, "big.bin"), new byte[100_000]);
        File.SetLastWriteTimeUtc(Path.Combine(dest, "big.bin"),
            File.GetLastWriteTimeUtc(Path.Combine(src, "big.bin")));
        File.WriteAllBytes(Path.Combine(src, "small.bin"), new byte[50]);

        var plan = _transfer.EnumerateWorkItems(
            new[] { Path.Combine(src, "big.bin"), Path.Combine(src, "small.bin") },
            dest, TransferMode.SkipExisting, exclusions: null, CancellationToken.None);

        // Only the 50-byte new file is required — the 100 KB identical file is skipped, not re-sized.
        TransferService.RequiredFreeBytes(plan).Should().Be(50);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RequiredFreeBytes_ExcludedFiles_DoNotCount()
    {
        using var tmp = new TempDirectory();
        var src = Path.Combine(tmp.Path, "src");
        var dest = Path.Combine(tmp.Path, "dest");
        FileBuilder.In(src).File("keep.dat", 1000).File("drop.tmp", 9000).Build();
        Directory.CreateDirectory(dest);

        var plan = _transfer.EnumerateWorkItems(
            new[] { src }, dest, TransferMode.SkipExisting,
            exclusions: new[] { "*.tmp" }, CancellationToken.None);

        TransferService.RequiredFreeBytes(plan).Should().Be(1000);
    }

    // ============================================================
    //  COPY — KEEP BOTH, DUPLICATE TOP-LEVEL FOLDER NAMES
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CopyAsync_KeepBoth_TwoSourcesSameFolderName_BothPreserved_NoOverwrite()
    {
        // Two distinct source folders share a leaf name ("Photos") and each holds a same-named file.
        // The planner does no disk writes, so without reservation both would target dest\Photos\pic.jpg
        // and one copy would silently clobber the other. The fix lands the second under "Photos-1".
        using var tmp = new TempDirectory();
        var srcA = Path.Combine(tmp.Path, "a", "Photos");
        var srcB = Path.Combine(tmp.Path, "b", "Photos");
        var dest = Path.Combine(tmp.Path, "dest");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(srcA);
        Directory.CreateDirectory(srcB);
        File.WriteAllText(Path.Combine(srcA, "pic.jpg"), "one");
        File.WriteAllText(Path.Combine(srcB, "pic.jpg"), "two");

        await _transfer.CopyAsync(
            new[] { srcA, srcB }, dest,
            stripPermissions: false, TransferMode.KeepBoth);

        File.Exists(Path.Combine(dest, "Photos", "pic.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(dest, "Photos-1", "pic.jpg")).Should().BeTrue();

        // Neither copy was lost — both distinct contents survive.
        var contents = new[]
        {
            File.ReadAllText(Path.Combine(dest, "Photos", "pic.jpg")),
            File.ReadAllText(Path.Combine(dest, "Photos-1", "pic.jpg")),
        };
        contents.Should().BeEquivalentTo(new[] { "one", "two" });
    }
}
