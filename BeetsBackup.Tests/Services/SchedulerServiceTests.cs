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
    [Fact]
    public async Task RunJobByIdAsync_UnknownId_ReturnsFalseAndDoesNotThrow()
    {
        var fs = new FileSystemService();
        var transfer = new TransferService(fs);
        var log = new BackupLogService();
        using var scheduler = new SchedulerService(transfer, log);

        var ran = await scheduler.RunJobByIdAsync(Guid.NewGuid());

        ran.Should().BeFalse();
    }
}
