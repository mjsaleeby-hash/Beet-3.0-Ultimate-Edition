using BeetsBackup.Helpers;
using FluentAssertions;

namespace BeetsBackup.Tests.Helpers;

/// <summary>
/// Tests for the CLI-argument parser used by the Windows Task Scheduler headless entry-point.
/// A wrong parse here would cause scheduled jobs launched by Windows to silently do nothing —
/// worth the tiny investment to pin down the expected forms.
/// </summary>
public class CliArgsTests
{
    private static readonly Guid SampleGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void TryParseRunJob_SpaceSeparatedForm_ReturnsGuid()
    {
        var result = CliArgs.TryParseRunJob(new[] { "--run-job", SampleGuid.ToString() });
        result.Should().Be(SampleGuid);
    }

    [Fact]
    public void TryParseRunJob_EqualsForm_ReturnsGuid()
    {
        var result = CliArgs.TryParseRunJob(new[] { $"--run-job={SampleGuid}" });
        result.Should().Be(SampleGuid);
    }

    [Fact]
    public void TryParseRunJob_CaseInsensitiveFlag_StillParses()
    {
        var result = CliArgs.TryParseRunJob(new[] { "--RUN-JOB", SampleGuid.ToString() });
        result.Should().Be(SampleGuid);
    }

    [Fact]
    public void TryParseRunJob_NoArgs_ReturnsNull()
    {
        CliArgs.TryParseRunJob(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void TryParseRunJob_UnrelatedArgs_ReturnsNull()
    {
        CliArgs.TryParseRunJob(new[] { "--startup", "--verbose" }).Should().BeNull();
    }

    [Fact]
    public void TryParseRunJob_FlagWithoutValue_ReturnsNull()
    {
        CliArgs.TryParseRunJob(new[] { "--run-job" }).Should().BeNull();
    }

    [Fact]
    public void TryParseRunJob_InvalidGuidValue_ReturnsNull()
    {
        CliArgs.TryParseRunJob(new[] { "--run-job", "not-a-guid" }).Should().BeNull();
    }

    [Fact]
    public void TryParseRunJob_MixedWithOtherArgs_FindsTheRightOne()
    {
        var result = CliArgs.TryParseRunJob(
            new[] { "--startup", "--run-job", SampleGuid.ToString(), "--extra" });
        result.Should().Be(SampleGuid);
    }

    [Fact]
    public void TryParseRunJob_NullArgs_ReturnsNull()
    {
        CliArgs.TryParseRunJob(null!).Should().BeNull();
    }
}
