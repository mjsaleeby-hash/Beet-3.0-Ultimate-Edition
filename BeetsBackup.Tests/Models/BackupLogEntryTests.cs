using BeetsBackup.Models;
using FluentAssertions;
using System.Text.Json;

namespace BeetsBackup.Tests.Models;

public class BackupLogEntryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void StatsDisplay_WhenComplete_ShowsCopiedSkippedAndBytes()
    {
        var entry = new BackupLogEntry
        {
            Status = BackupStatus.Complete,
            FilesCopied = 10,
            FilesSkipped = 2,
            BytesTransferred = 1048576
        };

        entry.StatsDisplay.Should().Contain("10 copied");
        entry.StatsDisplay.Should().Contain("2 skipped");
        entry.StatsDisplay.Should().Contain("1 MB");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StatsDisplay_WhenNotComplete_ReturnsEmpty()
    {
        var entry = new BackupLogEntry { Status = BackupStatus.Failed };
        entry.StatsDisplay.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TimestampDisplay_FormatsCorrectly()
    {
        var entry = new BackupLogEntry { Timestamp = new DateTime(2026, 3, 25, 14, 30, 45) };
        entry.TimestampDisplay.Should().Be("2026-03-25  14:30:45");
    }

    [Theory]
    [InlineData(BackupStatus.Scheduled, "Scheduled")]
    [InlineData(BackupStatus.Running, "Running")]
    [InlineData(BackupStatus.Complete, "Complete")]
    [InlineData(BackupStatus.Failed, "Failed")]
    [Trait("Category", "Unit")]
    public void StatusDisplay_AllValues_ReturnCorrectStrings(BackupStatus status, string expected)
    {
        var entry = new BackupLogEntry { Status = status };
        entry.StatusDisplay.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Id_Serialization_RoundTrip_PreservesId()
    {
        var original = new BackupLogEntry
        {
            JobName = "Test Job",
            Status = BackupStatus.Running
        };
        var originalId = original.Id;

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<BackupLogEntry>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(originalId, "deserialized entry must keep the same ID");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Id_Serialization_ListRoundTrip_AllIdsPreserved()
    {
        var entries = new List<BackupLogEntry>
        {
            new() { JobName = "Job 1", Status = BackupStatus.Complete },
            new() { JobName = "Job 2", Status = BackupStatus.Running },
            new() { JobName = "Job 3", Status = BackupStatus.Failed }
        };
        var originalIds = entries.Select(e => e.Id).ToList();

        var json = JsonSerializer.Serialize(entries);
        var deserialized = JsonSerializer.Deserialize<List<BackupLogEntry>>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Select(e => e.Id).Should().BeEquivalentTo(originalIds);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PropertyChanged_Status_FiresStatusDisplayAndStatsDisplay()
    {
        var entry = new BackupLogEntry();
        var changedProps = new List<string>();
        entry.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName!);

        entry.Status = BackupStatus.Complete;

        changedProps.Should().Contain("Status");
        changedProps.Should().Contain("StatusDisplay");
        changedProps.Should().Contain("StatsDisplay");
    }
}
