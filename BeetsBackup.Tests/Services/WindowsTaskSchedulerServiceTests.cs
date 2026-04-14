using BeetsBackup.Models;
using BeetsBackup.Services;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Tests for the <c>schtasks.exe</c> argument builder used when registering scheduled jobs
/// with Windows Task Scheduler. We deliberately don't exercise the actual <c>schtasks.exe</c>
/// subprocess — these tests cover the argument-building logic that is the most bug-prone part.
/// </summary>
public class WindowsTaskSchedulerServiceTests
{
    private static readonly DateTime Now = new(2026, 4, 14, 10, 0, 0);

    // ============================================================
    //  RECURRENCE MAPPING
    // ============================================================

    [Fact]
    public void BuildScheduleArgs_OneTimeJob_EmitsOnce()
    {
        var job = MakeJob(isRecurring: false, nextRun: Now.AddHours(1));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/SC", "ONCE");
        args.Should().NotContain("/MO");
    }

    [Fact]
    public void BuildScheduleArgs_Daily_EmitsDailyWithoutModifier()
    {
        var job = MakeJob(isRecurring: true, interval: TimeSpan.FromDays(1), nextRun: Now.AddHours(2));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/SC", "DAILY");
        args.Should().NotContain("/MO");
    }

    [Fact]
    public void BuildScheduleArgs_Weekly_EmitsWeekly()
    {
        var job = MakeJob(isRecurring: true, interval: TimeSpan.FromDays(7), nextRun: Now.AddHours(2));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/SC", "WEEKLY");
    }

    [Fact]
    public void BuildScheduleArgs_EverySixHours_EmitsHourlyWithModifierSix()
    {
        var job = MakeJob(isRecurring: true, interval: TimeSpan.FromHours(6), nextRun: Now.AddHours(2));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/SC", "HOURLY");
        AssertFlagValue(args, "/MO", "6");
    }

    [Fact]
    public void BuildScheduleArgs_ThirtyMinuteInterval_EmitsMinuteWithModifier()
    {
        var job = MakeJob(isRecurring: true, interval: TimeSpan.FromMinutes(30), nextRun: Now.AddHours(2));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/SC", "MINUTE");
        AssertFlagValue(args, "/MO", "30");
    }

    // ============================================================
    //  START-TIME FORMATTING
    // ============================================================

    [Fact]
    public void BuildScheduleArgs_FutureNextRun_UsesJobNextRunAsStartTime()
    {
        var job = MakeJob(isRecurring: false, nextRun: new DateTime(2026, 7, 1, 14, 30, 0));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/ST", "14:30");
        AssertFlagValue(args, "/SD", "07/01/2026");
    }

    [Fact]
    public void BuildScheduleArgs_PastNextRun_BumpsForwardBasedOnNowParameter()
    {
        // NextRun is in the past relative to the Now reference — the builder should
        // bump start time to "Now + 1 minute" so schtasks doesn't reject the registration.
        var job = MakeJob(isRecurring: false, nextRun: Now.AddDays(-1));
        var args = WindowsTaskSchedulerService.BuildScheduleArgs(job, Now);

        AssertFlagValue(args, "/ST", Now.AddMinutes(1).ToString("HH:mm"));
        AssertFlagValue(args, "/SD", Now.AddMinutes(1).ToString("MM/dd/yyyy"));
    }

    // ============================================================
    //  TASK NAME FORMATTING
    // ============================================================

    [Fact]
    public void TaskNameFor_UsesPrefixAndCompactGuidFormat()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789012");
        var name = WindowsTaskSchedulerService.TaskNameFor(id);

        name.Should().Be("BeetsBackup_12345678123412341234123456789012");
    }

    // ============================================================
    //  HELPERS
    // ============================================================

    private static ScheduledJob MakeJob(bool isRecurring, DateTime nextRun, TimeSpan? interval = null) => new()
    {
        Name = "Test Job",
        SourcePaths = new List<string> { "C:\\src" },
        DestinationPath = "D:\\dest",
        IsRecurring = isRecurring,
        RecurInterval = interval,
        NextRun = nextRun,
        IsEnabled = true
    };

    /// <summary>
    /// Asserts that the argument list contains <paramref name="flag"/> immediately
    /// followed by <paramref name="value"/>. schtasks positional args are flag/value pairs.
    /// </summary>
    private static void AssertFlagValue(List<string> args, string flag, string value)
    {
        var idx = args.IndexOf(flag);
        idx.Should().BeGreaterOrEqualTo(0, $"expected {flag} to be in the argument list");
        (idx + 1).Should().BeLessThan(args.Count, $"{flag} should be followed by a value");
        args[idx + 1].Should().Be(value);
    }
}
