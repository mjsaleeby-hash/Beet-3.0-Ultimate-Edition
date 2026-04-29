using BeetsBackup.Services;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Tests for the parallel-copy concurrency decision table. The IOCTL probe path needs a
/// real machine with real volumes and isn't covered here — only the pure
/// <see cref="DriveTypeService.ComputeWorkers"/> mapping from drive kinds to worker counts.
/// </summary>
public class DriveTypeServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeWorkers_SsdToSsd_ReturnsMultipleWorkers()
    {
        var n = DriveTypeService.ComputeWorkers(DriveKind.SSD, DriveKind.SSD);
        n.Should().BeGreaterThan(1, "SSD-to-SSD should fan out across workers");
        n.Should().BeLessThanOrEqualTo(8, "should cap at MaxSsdWorkers regardless of CPU count");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(DriveKind.HDD, DriveKind.SSD)]
    [InlineData(DriveKind.SSD, DriveKind.HDD)]
    [InlineData(DriveKind.HDD, DriveKind.HDD)]
    [InlineData(DriveKind.SSD, DriveKind.Removable)]
    [InlineData(DriveKind.Removable, DriveKind.SSD)]
    public void ComputeWorkers_AnyHddOrRemovable_ForcesSingleWorker(DriveKind src, DriveKind dst)
    {
        DriveTypeService.ComputeWorkers(src, dst).Should().Be(1,
            "spinning disks and cheap removable controllers serialize seeks — parallelism thrashes them");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(DriveKind.SSD, DriveKind.Network)]
    [InlineData(DriveKind.Network, DriveKind.SSD)]
    [InlineData(DriveKind.Network, DriveKind.Network)]
    public void ComputeWorkers_NetworkInPair_ReturnsModeratePool(DriveKind src, DriveKind dst)
    {
        DriveTypeService.ComputeWorkers(src, dst).Should().Be(4,
            "SMB benefits from a few streams but more saturates the link without payoff");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeWorkers_UnknownEndpoints_FallsBackToConservativeDefault()
    {
        DriveTypeService.ComputeWorkers(DriveKind.Unknown, DriveKind.Unknown).Should().Be(2,
            "Unknown should not assume SSD-style fan-out, but also shouldn't waste a real SSD with serial copy");
    }
}
