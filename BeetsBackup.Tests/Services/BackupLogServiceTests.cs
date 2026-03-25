using BeetsBackup.Models;
using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;
using System.Text.Json;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// BackupLogService uses a hardcoded path and Application.Current dispatcher,
/// so we test the serialization round-trip and data model behavior directly.
/// Full integration tests require a WPF Application host (deferred to UI test phase).
/// </summary>
public class BackupLogServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void BackupLogEntry_Serialization_RoundTrip_PreservesId()
    {
        var entry = new BackupLogEntry
        {
            SourcePath = @"C:\Source",
            DestinationPath = @"D:\Backup",
            Status = BackupStatus.Complete,
            Message = "Done",
            FilesCopied = 10,
            FilesSkipped = 2,
            BytesTransferred = 1024 * 1024,
        };
        var originalId = entry.Id;

        var json = JsonSerializer.Serialize(new List<BackupLogEntry> { entry });
        var loaded = JsonSerializer.Deserialize<List<BackupLogEntry>>(json);

        loaded.Should().HaveCount(1);
        loaded![0].Id.Should().Be(originalId);
        loaded[0].SourcePath.Should().Be(@"C:\Source");
        loaded[0].FilesCopied.Should().Be(10);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BackupLogEntry_Serialization_MultipleEntries_AllIdsPreserved()
    {
        var entries = Enumerable.Range(0, 5).Select(_ => new BackupLogEntry
        {
            SourcePath = @"C:\Test",
            DestinationPath = @"D:\Test",
        }).ToList();
        var originalIds = entries.Select(e => e.Id).ToList();

        var json = JsonSerializer.Serialize(entries);
        var loaded = JsonSerializer.Deserialize<List<BackupLogEntry>>(json);

        loaded.Should().HaveCount(5);
        loaded!.Select(e => e.Id).Should().BeEquivalentTo(originalIds);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BackupLogEntry_StaleRunning_CanBeMarkedFailed()
    {
        var entry = new BackupLogEntry
        {
            SourcePath = @"C:\Test",
            DestinationPath = @"D:\Test",
            Status = BackupStatus.Running,
        };

        // Simulate what Load() does for stale entries
        entry.Status = BackupStatus.Failed;
        entry.Message = "Interrupted";

        entry.Status.Should().Be(BackupStatus.Failed);
        entry.Message.Should().Be("Interrupted");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BackupLogEntry_CorruptJson_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<List<BackupLogEntry>>("not valid json");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BackupLogEntry_EmptyList_SerializesAndDeserializes()
    {
        var json = JsonSerializer.Serialize(new List<BackupLogEntry>());
        var loaded = JsonSerializer.Deserialize<List<BackupLogEntry>>(json);

        loaded.Should().NotBeNull();
        loaded.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BackupLogEntry_AllStatuses_SurviveSerialization()
    {
        foreach (var status in Enum.GetValues<BackupStatus>())
        {
            var entry = new BackupLogEntry
            {
                SourcePath = "src",
                DestinationPath = "dst",
                Status = status,
            };

            var json = JsonSerializer.Serialize(entry);
            var loaded = JsonSerializer.Deserialize<BackupLogEntry>(json);

            loaded!.Status.Should().Be(status);
        }
    }
}
