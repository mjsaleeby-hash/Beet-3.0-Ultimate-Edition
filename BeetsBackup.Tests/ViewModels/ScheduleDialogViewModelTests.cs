using BeetsBackup.ViewModels;
using FluentAssertions;

namespace BeetsBackup.Tests.ViewModels;

public class ScheduleDialogViewModelTests
{
    [Theory]
    [InlineData(12, "AM", 0)]   // 12 AM = midnight = 0
    [InlineData(1, "AM", 1)]
    [InlineData(11, "AM", 11)]
    [InlineData(12, "PM", 12)]  // 12 PM = noon = 12
    [InlineData(1, "PM", 13)]
    [InlineData(11, "PM", 23)]
    [Trait("Category", "Unit")]
    public void BuildJob_AmPmConversion_ProducesCorrectHour(int hour12, string amPm, int expectedHour24)
    {
        var vm = new ScheduleDialogViewModel();
        vm.SourcePaths.Add(@"C:\Test");
        vm.DestinationPath = @"D:\Backup";
        vm.ScheduledDate = DateTime.Now.AddDays(1);
        vm.ScheduledHour = hour12;
        vm.ScheduledMinute = 0;
        vm.SelectedAmPm = amPm;

        var job = vm.BuildJob();

        job.NextRun.Hour.Should().Be(expectedHour24);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildJob_PastTime_RollsForwardOneDay()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SourcePaths.Add(@"C:\Test");
        vm.DestinationPath = @"D:\Backup";
        // Schedule for yesterday at current time
        vm.ScheduledDate = DateTime.Now.AddDays(-1);
        vm.ScheduledHour = 1;
        vm.ScheduledMinute = 0;
        vm.SelectedAmPm = "AM";

        var job = vm.BuildJob();

        // Should have rolled forward by a day
        job.NextRun.Should().BeOnOrAfter(DateTime.Now.AddDays(-1).Date);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildJob_RecurringDaily_SetsCorrectInterval()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SourcePaths.Add(@"C:\Test");
        vm.DestinationPath = @"D:\Backup";
        vm.ScheduledDate = DateTime.Now.AddDays(1);
        vm.IsRecurring = true;
        vm.SelectedInterval = "Daily";

        var job = vm.BuildJob();

        job.IsRecurring.Should().BeTrue();
        job.RecurInterval.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BuildJob_NotRecurring_IntervalIsNull()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SourcePaths.Add(@"C:\Test");
        vm.DestinationPath = @"D:\Backup";
        vm.ScheduledDate = DateTime.Now.AddDays(1);
        vm.IsRecurring = false;

        var job = vm.BuildJob();

        job.RecurInterval.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsValid_NoSources_ReturnsFalse()
    {
        var vm = new ScheduleDialogViewModel();
        vm.DestinationPath = @"D:\Backup";

        vm.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsValid_NoDestination_ReturnsFalse()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SourcePaths.Add(@"C:\Test");

        vm.IsValid.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsValid_AllFieldsSet_ReturnsTrue()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SourcePaths.Add(@"C:\Test");
        vm.DestinationPath = @"D:\Backup";
        vm.JobName = "My Job";

        vm.IsValid.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShowKeepBothWarning_KeepBothAndRecurring_ReturnsTrue()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SelectedTransferMode = BeetsBackup.Models.TransferMode.KeepBoth;
        vm.IsRecurring = true;

        vm.ShowKeepBothWarning.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ShowKeepBothWarning_SkipExistingAndRecurring_ReturnsFalse()
    {
        var vm = new ScheduleDialogViewModel();
        vm.SelectedTransferMode = BeetsBackup.Models.TransferMode.SkipExisting;
        vm.IsRecurring = true;

        vm.ShowKeepBothWarning.Should().BeFalse();
    }
}
