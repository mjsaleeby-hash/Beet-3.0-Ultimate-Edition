using BeetsBackup.Models;
using BeetsBackup.Services;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Wording tests for the completion-summary surface. Locks down the "Up to date — N verified"
/// reading users see when an incremental backup runs against an already-current destination,
/// so it doesn't regress to the old "0 copied, N skipped" that read as if nothing happened.
/// </summary>
public class TransferReporterTests
{
    [Fact]
    public void FormatSummary_AllSkipped_NoFailures_ReadsAsUpToDate()
    {
        var result = new TransferResult
        {
            FilesCopied = 0,
            FilesSkipped = 13780,
            BytesTransferred = 0,
            TotalFiles = 13780,
        };

        var summary = TransferReporter.FormatSummary(result);

        summary.Should().Be("Up to date — 13780 files verified");
    }

    [Fact]
    public void FormatSummary_NothingToDo_ReadsCleanly()
    {
        var result = new TransferResult();
        TransferReporter.FormatSummary(result).Should().Be("Done — nothing to do");
    }

    [Fact]
    public void FormatSummary_RealCopyWork_ListsCounters()
    {
        var result = new TransferResult { FilesCopied = 10, FilesSkipped = 5 };
        var summary = TransferReporter.FormatSummary(result);
        summary.Should().StartWith("Done — 10 copied, 5 skipped");
    }

    [Fact]
    public void FormatSummary_FailureMixed_KeepsExistingFailureWording()
    {
        var result = new TransferResult { FilesCopied = 100, FilesSkipped = 50 };
        result.IncrementFilesFailed();
        result.IncrementFilesLocked();
        var summary = TransferReporter.FormatSummary(result);

        summary.Should().Contain("100 copied");
        summary.Should().Contain("50 skipped");
        summary.Should().Contain("1 failed");
        summary.Should().Contain("1 locked");
        summary.Should().NotContain("Up to date");
    }

    [Fact]
    public void FormatSummary_ZeroCopiedButLocked_NotConsideredUpToDate()
    {
        var result = new TransferResult { FilesCopied = 0, FilesSkipped = 100 };
        result.IncrementFilesLocked();

        var summary = TransferReporter.FormatSummary(result);

        summary.Should().NotContain("Up to date",
            "a locked file is a real reason something didn't get backed up — don't tell the user everything is fine");
        summary.Should().Contain("1 locked");
    }

    [Fact]
    public void IsNothingChanged_ZeroCopiesNoFailures_True()
    {
        var result = new TransferResult { FilesCopied = 0, FilesSkipped = 100 };
        TransferReporter.IsNothingChanged(result).Should().BeTrue();
    }

    [Fact]
    public void IsNothingChanged_AnyCopy_False()
    {
        var result = new TransferResult { FilesCopied = 1 };
        TransferReporter.IsNothingChanged(result).Should().BeFalse();
    }

    [Fact]
    public void IsNothingChanged_AnyDeletion_False()
    {
        var result = new TransferResult();
        result.IncrementFilesDeleted();
        TransferReporter.IsNothingChanged(result).Should().BeFalse();
    }
}
