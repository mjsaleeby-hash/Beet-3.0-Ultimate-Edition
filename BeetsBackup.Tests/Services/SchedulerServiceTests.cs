using BeetsBackup.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
        scheduler.RunningJobChanged += _ => eventFireCount++;

        scheduler.PauseRunning();
        scheduler.ResumeRunning();
        scheduler.CancelRunning();

        eventFireCount.Should().Be(0, "no running jobs means no state changed");
    }

    // ============================================================
    //  DISPOSE AFTER START  (regression — see notes/bugs.md 2026-08-04)
    // ============================================================

    /// <summary>Stand-in for the services that sit before SchedulerService in the real container
    /// (BackupLogService, TransferService, FileSystemService, ThemeService, SettingsService).</summary>
    private sealed class DisposeProbe : IDisposable
    {
        public bool WasDisposed { get; private set; }
        public void Dispose() => WasDisposed = true;
    }

    /// <summary>
    /// Dispose used to throw whenever the loop had been started: Dispose cancels <c>_cts</c> and
    /// then calls <c>_runTask.Wait(...)</c>, and waiting on a task that ended Canceled raises
    /// AggregateException(TaskCanceledException). Every shutdown logged
    /// "Error disposing services" (96 times in the field log before this fix).
    ///
    /// The existing tests all missed it because none of them called <see cref="SchedulerService.Start"/>,
    /// leaving <c>_runTask</c> null and skipping the throwing branch entirely — so the suite stayed
    /// green while every real shutdown hit the bug. This test starts the loop first.
    ///
    /// Start() is safe here: RunAsync immediately awaits a one-minute tick and <c>_jobs</c> is
    /// empty without Load(), so nothing is dispatched and no disk is touched.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Dispose_AfterStart_DoesNotThrow()
    {
        var scheduler = BuildScheduler();
        scheduler.Start();

        var dispose = () => scheduler.Dispose();

        dispose.Should().NotThrow("cancelling our own loop is the expected shutdown path, not a fault");
    }

    /// <summary>
    /// The consequence that made the stray exception more than noise: ServiceProvider disposes its
    /// singletons in ONE unguarded reverse-creation-order loop, so a throw from SchedulerService.Dispose
    /// aborted the loop and every service constructed BEFORE it was never disposed at all.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Dispose_AfterStart_DoesNotAbortTheContainersRemainingDisposals()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DisposeProbe>();
        services.AddSingleton<FileSystemService>();
        services.AddSingleton<TransferService>();
        services.AddSingleton<BackupLogService>();
        services.AddSingleton<SchedulerService>();
        var provider = services.BuildServiceProvider();

        // Resolve the probe FIRST so it is constructed before SchedulerService. Disposal is
        // reverse-creation-order, so the probe is disposed AFTER the scheduler — the same slot the
        // real app's earlier-constructed services occupy.
        var probe = provider.GetRequiredService<DisposeProbe>();
        provider.GetRequiredService<SchedulerService>().Start();

        var dispose = () => provider.Dispose();

        dispose.Should().NotThrow();
        probe.WasDisposed.Should().BeTrue(
            "a throw from SchedulerService.Dispose must not strand the services disposed after it");
    }
}
