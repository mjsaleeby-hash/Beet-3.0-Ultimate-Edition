using BeetsBackup.Services;
using System.Runtime.CompilerServices;

namespace BeetsBackup.Tests.Infrastructure;

/// <summary>
/// Redirects <see cref="FileLogger"/> output into a per-run temp directory so the test suite never
/// writes into the user's real log directory (<c>%LocalAppData%\Beet's Backup</c>).
/// </summary>
/// <remarks>
/// WHY: the suite exercises the real static <see cref="FileLogger"/>. Before this, a single
/// <c>dotnet test</c> run appended ~22,000 lines to the user's production <c>operational.log</c>,
/// including <c>[ERROR] VSS failed for C:\: Access is denied</c> raised by the unelevated test
/// host — which reads exactly like a genuine field failure to anyone (or any cohort report)
/// consuming that log. The log also rotates at 10 MB, so test noise was actively evicting real
/// field history.
///
/// WHY A MODULE INITIALIZER: <see cref="FileLogger.LogDirectory"/> is a <c>static readonly</c>
/// resolved once by the type initializer, so the variable must be set before ANY test code touches
/// the logger. A module initializer runs when this assembly is loaded — strictly before every test,
/// fixture, and collection in it — which an xUnit fixture cannot guarantee.
/// </remarks>
internal static class TestLogRedirect
{
    /// <summary>Temp directory receiving this run's log output. Left on disk (under the OS temp
    /// folder) so a failing run's log can still be inspected; the OS reclaims it.</summary>
    internal static string Directory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Redirect()
    {
        Directory = Path.Combine(
            Path.GetTempPath(),
            "BeetsBackupTests",
            "logs",
            $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        System.IO.Directory.CreateDirectory(Directory);
        Environment.SetEnvironmentVariable(FileLogger.LogDirectoryOverrideVariable, Directory);
    }
}
