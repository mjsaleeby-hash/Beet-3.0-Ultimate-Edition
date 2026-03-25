using BeetsBackup.Models;
using FluentAssertions;
using System.Text.Json;

namespace BeetsBackup.Tests.Models;

public class ScheduledJobTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateNextRun_NonRecurring_DoesNotChangeNextRun()
    {
        var job = new ScheduledJob
        {
            IsRecurring = false,
            NextRun = new DateTime(2026, 1, 1, 12, 0, 0)
        };
        var original = job.NextRun;

        job.UpdateNextRun();

        job.NextRun.Should().Be(original);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateNextRun_RecurringDaily_AdvancesPastNow()
    {
        var job = new ScheduledJob
        {
            IsRecurring = true,
            RecurInterval = TimeSpan.FromDays(1),
            NextRun = DateTime.Now.AddDays(-2)
        };

        job.UpdateNextRun();

        job.NextRun.Should().BeAfter(DateTime.Now);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateNextRun_RecurringSixHour_AdvancesMultipleTicks()
    {
        var pastTime = DateTime.Now.AddHours(-13);
        var job = new ScheduledJob
        {
            IsRecurring = true,
            RecurInterval = TimeSpan.FromHours(6),
            NextRun = pastTime
        };

        job.UpdateNextRun();

        // Should have advanced at least 3 times (18h > 13h)
        job.NextRun.Should().BeAfter(DateTime.Now);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateNextRun_NullInterval_DoesNotThrow()
    {
        var job = new ScheduledJob
        {
            IsRecurring = true,
            RecurInterval = null,
            NextRun = DateTime.Now.AddDays(-1)
        };

        var act = () => job.UpdateNextRun();
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SourcePath_BackwardCompat_GetReturnsFirstElement()
    {
        var job = new ScheduledJob();
        job.SourcePaths.Add(@"C:\Folder1");
        job.SourcePaths.Add(@"C:\Folder2");

        job.SourcePath.Should().Be(@"C:\Folder1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SourcePath_BackwardCompat_SetAddsToEmptyList()
    {
        var job = new ScheduledJob();
        job.SourcePath = @"C:\NewFolder";

        job.SourcePaths.Should().HaveCount(1);
        job.SourcePaths[0].Should().Be(@"C:\NewFolder");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SourcePathsDisplay_MultiplePaths_JoinedBySemicolon()
    {
        var job = new ScheduledJob();
        job.SourcePaths.Add(@"C:\A");
        job.SourcePaths.Add(@"D:\B");
        job.SourcePaths.Add(@"E:\C");

        job.SourcePathsDisplay.Should().Be(@"C:\A; D:\B; E:\C");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NullableTimeSpanConverter_RoundTrip_PreservesValue()
    {
        var job = new ScheduledJob
        {
            RecurInterval = TimeSpan.FromHours(6)
        };

        var json = JsonSerializer.Serialize(job);
        var deserialized = JsonSerializer.Deserialize<ScheduledJob>(json);

        deserialized!.RecurInterval.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NullableTimeSpanConverter_NullValue_DeserializesAsNull()
    {
        var job = new ScheduledJob { RecurInterval = null };

        var json = JsonSerializer.Serialize(job);
        var deserialized = JsonSerializer.Deserialize<ScheduledJob>(json);

        deserialized!.RecurInterval.Should().BeNull();
    }
}
