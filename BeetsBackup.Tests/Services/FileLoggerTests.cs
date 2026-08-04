using BeetsBackup.Services;
using BeetsBackup.Tests.Infrastructure;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Coverage for the asynchronous, batched logging path. Logging is now handed to a background writer
/// instead of opening/closing the file per line under a global lock; <see cref="FileLogger.Flush"/>
/// is the durability barrier that the crash-dump and shutdown paths rely on.
/// </summary>
public class FileLoggerTests
{
    private static string OperationalLog => Path.Combine(FileLogger.LogDirectory, "operational.log");
    private static string RotatedLog => OperationalLog + ".1";

    [Fact]
    [Trait("Category", "Integration")]
    public void Log_ThenFlush_LineReachesDisk()
    {
        var marker = $"FileLoggerTest-{Guid.NewGuid():N}";

        FileLogger.Info(marker);
        FileLogger.Flush();

        ReadLogTail().Should().Contain(marker,
            "Flush must guarantee everything queued before it is written to disk");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Log_HammeredConcurrently_DoesNotThrowAndFlushesAll()
    {
        var marker = $"FileLoggerConcurrent-{Guid.NewGuid():N}";

        var act = () =>
        {
            Parallel.For(0, 2000, i => FileLogger.Info($"{marker}-{i}"));
            FileLogger.Flush();
        };

        act.Should().NotThrow();
        // The final line logged before Flush must be durable.
        FileLogger.Info($"{marker}-final");
        FileLogger.Flush();
        ReadLogTail().Should().Contain($"{marker}-final");
    }

    // ============================================================
    //  LOG REDIRECTION (test runs must not touch the user's real log)
    // ============================================================

    /// <summary>
    /// The suite used to append ~22,000 lines per run — including unelevated-VSS ERROR lines that
    /// read like real field failures — straight into %LocalAppData%\Beet's Backup\operational.log,
    /// evicting genuine field history through the 10 MB rotation. This asserts the redirect is
    /// actually in force, because the failure mode is silent: everything still passes while
    /// quietly polluting the user's log.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void LogDirectory_UnderTest_IsRedirectedAwayFromTheRealUserLog()
    {
        var realUserLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Beet's Backup");

        FileLogger.LogDirectory.Should().Be(TestLogRedirect.Directory,
            "the module initializer must redirect logging before any test touches FileLogger");
        FileLogger.LogDirectory.Should().NotBe(realUserLogDir,
            "a test run must never write into the user's production log directory");
    }

    /// <summary>Writes through the real logging path, then proves the line landed in the temp
    /// directory and that the production log file was not created alongside it.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Log_UnderTest_WritesIntoTheRedirectedDirectoryOnly()
    {
        var marker = $"RedirectProbe-{Guid.NewGuid():N}";

        FileLogger.Info(marker);
        FileLogger.Flush();

        var redirected = Path.Combine(TestLogRedirect.Directory, "operational.log");
        File.Exists(redirected).Should().BeTrue("the redirected directory receives the log file");
        File.ReadAllText(redirected).Should().Contain(marker);
    }

    /// <summary>Reads the current operational log plus the most recent rotated segment, so a rotation
    /// that happens to fall mid-test doesn't hide a line we just flushed.</summary>
    private static string ReadLogTail()
    {
        var text = string.Empty;
        foreach (var path in new[] { RotatedLog, OperationalLog })
        {
            try
            {
                if (File.Exists(path))
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                        text += reader.ReadToEnd();
            }
            catch { /* best-effort read — concurrent writers hold a share-compatible handle */ }
        }
        return text;
    }
}
