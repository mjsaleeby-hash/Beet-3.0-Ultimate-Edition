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

    // ============================================================
    //  SINGLE-DRIVE READ FAN-OUT  (Wave 2.4 — folder sizing)
    // ============================================================

    /// <summary>
    /// Folder sizing walks ONE drive, so its fan-out must come from the same table as the copy
    /// path with that drive on both sides. Asserted against a real path without assuming which
    /// kind this machine reports: whatever the probe says, the read count must equal the
    /// same-drive pair decision for exactly that kind.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void GetReadWorkerCount_EqualsTheSameDrivePairDecisionForThatDrive()
    {
        var path = Path.GetTempPath();
        var kind = DriveTypeService.GetDriveKind(path);

        DriveTypeService.GetReadWorkerCount(path).Should().Be(
            DriveTypeService.ComputeWorkers(kind, kind),
            "a one-drive read and a same-drive copy must never disagree about fan-out");
    }

    /// <summary>
    /// The specific regression Wave 2.4 exists to prevent: folder sizing used
    /// Environment.ProcessorCount, so a spinning disk got 8-16 concurrent walkers thrashing the
    /// head. A spinning disk must size with exactly one worker.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ReadFanOut_OnSpinningDisk_IsSerial()
    {
        DriveTypeService.ComputeWorkers(DriveKind.HDD, DriveKind.HDD).Should().Be(1,
            "sizing a folder on an HDD must serialize — this is the entire point of Wave 2.4");
        DriveTypeService.ComputeWorkers(DriveKind.Removable, DriveKind.Removable).Should().Be(1,
            "USB sticks and SD cards serialize at the controller regardless of what we ask for");
    }

    /// <summary>
    /// GetReadWorkerCount runs the real IOCTL probe, so it cannot assert a specific kind on an
    /// arbitrary machine. It CAN assert the invariant every caller relies on: a usable, bounded
    /// degree of parallelism, never zero or negative (which would throw at ParallelOptions).
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void GetReadWorkerCount_OnARealPath_ReturnsAUsableDegreeOfParallelism()
    {
        var n = DriveTypeService.GetReadWorkerCount(Path.GetTempPath());

        n.Should().BeGreaterThan(0, "ParallelOptions rejects a MaxDegreeOfParallelism below 1");
        n.Should().BeLessThanOrEqualTo(8, "no drive classification should exceed the SSD cap");
    }

    /// <summary>A path that classifies as nothing at all must still yield a safe fan-out rather
    /// than throwing into the sizing pass.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void GetReadWorkerCount_OnAnUnusablePath_StillReturnsSafeDefault()
    {
        DriveTypeService.GetReadWorkerCount(string.Empty).Should().BeGreaterThan(0);
    }
}
