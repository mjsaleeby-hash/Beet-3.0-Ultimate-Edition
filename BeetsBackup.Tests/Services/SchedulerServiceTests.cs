using BeetsBackup.Services;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Slim coverage of <see cref="SchedulerService"/> behaviour that isn't already exercised
/// indirectly through the wizard and schedule-dialog tests. Full end-to-end job execution
/// is covered by the <c>TransferServiceTests</c> suite.
/// </summary>
public class SchedulerServiceTests
{
    private static SchedulerService BuildScheduler()
    {
        var fs = new FileSystemService();
        var transfer = new TransferService(fs);
        var log = new BackupLogService();
        return new SchedulerService(transfer, log);
    }

    [Fact]
    public async Task RunJobByIdAsync_UnknownId_ReturnsFalseAndDoesNotThrow()
    {
        using var scheduler = BuildScheduler();

        var ran = await scheduler.RunJobByIdAsync(Guid.NewGuid());

        ran.Should().BeFalse();
    }

    [Fact]
    public void IsRunningAnyJob_NoJobs_ReturnsFalse()
    {
        using var scheduler = BuildScheduler();

        scheduler.IsRunningAnyJob.Should().BeFalse();
        scheduler.IsRunningJobPaused.Should().BeFalse();
        scheduler.RunningJobLogIds.Should().BeEmpty();
    }

    [Fact]
    public void PauseRunning_NoJobs_DoesNotFireEvent()
    {
        using var scheduler = BuildScheduler();
        int eventFireCount = 0;
        scheduler.RunningJobChanged += () => eventFireCount++;

        scheduler.PauseRunning();
        scheduler.ResumeRunning();
        scheduler.CancelRunning();

        eventFireCount.Should().Be(0, "no running jobs means no state changed");
    }
}
