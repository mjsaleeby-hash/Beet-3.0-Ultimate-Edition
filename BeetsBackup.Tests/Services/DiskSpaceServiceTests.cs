using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Tests for the pre-flight disk-space preview used by the wizard and schedule dialog.
/// Most of these are integration tests because we need a real filesystem and a real DriveInfo —
/// the service itself contains no mocks, so pure unit tests wouldn't exercise the meaningful paths.
/// </summary>
public class DiskSpaceServiceTests
{
    // ============================================================
    //  REQUIRED-BYTES AGGREGATION
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public void Preview_SumsSourceSizes_AsRequiredBytes()
    {
        using var tmp = new TempDirectory();
        FileBuilder.In(tmp.Path)
            .File("a.bin", sizeBytes: 1000)
            .File("b.bin", sizeBytes: 2500)
            .Build();

        var preview = DiskSpaceService.Preview(
            new[] { Path.Combine(tmp.Path, "a.bin"), Path.Combine(tmp.Path, "b.bin") },
            tmp.Path);

        preview.RequiredBytes.Should().Be(3500);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Preview_WithExclusions_SkipsMatchingFiles()
    {
        using var tmp = new TempDirectory();
        FileBuilder.In(tmp.Path)
            .File("keep.dat", sizeBytes: 1000)
            .File("drop.tmp", sizeBytes: 5000)
            .Build();

        var preview = DiskSpaceService.Preview(
            new[] { tmp.Path },
            tmp.Path,
            exclusions: new[] { "*.tmp" });

        preview.RequiredBytes.Should().Be(1000);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Preview_WillCompress_AppliesReductionRatio()
    {
        using var tmp = new TempDirectory();
        FileBuilder.In(tmp.Path).File("big.bin", sizeBytes: 10_000).Build();

        var raw = DiskSpaceService.Preview(new[] { tmp.Path }, tmp.Path);
        var compressed = DiskSpaceService.Preview(new[] { tmp.Path }, tmp.Path, willCompress: true);

        // We don't pin to an exact constant because the ratio is deliberately a private tuning
        // knob — just assert the direction and that it's at least meaningfully smaller.
        compressed.RequiredBytes.Should().BeLessThan(raw.RequiredBytes);
        compressed.RequiredBytes.Should().BeLessThan((long)(raw.RequiredBytes * 0.95));
    }

    [Fact]
    public void Preview_EmptySources_ReturnsZeroRequired()
    {
        using var tmp = new TempDirectory();
        var preview = DiskSpaceService.Preview(Array.Empty<string>(), tmp.Path);

        preview.RequiredBytes.Should().Be(0);
    }

    // ============================================================
    //  STATUS CLASSIFICATION
    // ============================================================

    [Fact]
    [Trait("Category", "Integration")]
    public void Preview_SmallSourceOnRealDrive_ReportsSufficient()
    {
        using var tmp = new TempDirectory();
        FileBuilder.In(tmp.Path).File("tiny.bin", sizeBytes: 100).Build();

        var preview = DiskSpaceService.Preview(new[] { tmp.Path }, tmp.Path);

        // A few hundred bytes on any real dev machine should always classify as Sufficient.
        preview.Status.Should().Be(DiskSpaceStatus.Sufficient);
        preview.AvailableBytes.Should().BeGreaterThan(0);
        preview.TotalBytes.Should().BeGreaterThan(0);
        preview.DriveRoot.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Preview_UnrootedDestination_ReturnsUnknown()
    {
        var preview = DiskSpaceService.Preview(
            Array.Empty<string>(),
            destinationPath: "relative/path/no-root");

        preview.Status.Should().Be(DiskSpaceStatus.Unknown);
        preview.DriveRoot.Should().BeNull();
    }

    [Fact]
    public void Preview_EmptyDestinationPath_ReturnsUnknown()
    {
        var preview = DiskSpaceService.Preview(Array.Empty<string>(), string.Empty);
        preview.Status.Should().Be(DiskSpaceStatus.Unknown);
    }

    // ============================================================
    //  DISPLAY / SUMMARY STRINGS
    // ============================================================

    [Fact]
    public void RequiredDisplay_FormatsBytesHumanReadable()
    {
        var preview = new DiskSpacePreview(1536, 0, 0, null, DiskSpaceStatus.Unknown, null);
        preview.RequiredDisplay.Should().Contain("KB");
    }

    [Fact]
    public void Summary_InsufficientStatus_ExplainsShortfall()
    {
        var preview = new DiskSpacePreview(
            RequiredBytes: 20L * 1024 * 1024 * 1024, // 20 GB
            AvailableBytes: 5L * 1024 * 1024 * 1024, // 5 GB
            TotalBytes: 100L * 1024 * 1024 * 1024,
            DriveRoot: "D:\\",
            Status: DiskSpaceStatus.Insufficient,
            Note: null);

        preview.Summary.Should().Contain("Not enough");
        preview.Summary.Should().Contain("20");
        preview.Summary.Should().Contain("5");
    }
}
