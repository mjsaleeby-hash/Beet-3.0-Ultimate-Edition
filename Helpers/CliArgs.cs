namespace BeetsBackup.Helpers;

/// <summary>
/// Parsing helpers for command-line arguments. Kept narrow on purpose — each supported
/// switch lives as its own static method so they can be unit-tested in isolation without
/// pulling in the <see cref="System.Windows.Application"/> startup machinery.
/// </summary>
internal static class CliArgs
{
    private const string RunJobSwitch = "--run-job";
    private const string RunJobEqPrefix = RunJobSwitch + "=";

    /// <summary>
    /// Parses the <c>--run-job &lt;guid&gt;</c> or <c>--run-job=&lt;guid&gt;</c> CLI switch used
    /// by the Windows Task Scheduler integration. Returns <c>null</c> when the switch is
    /// absent, malformed, or the value is not a valid GUID.
    /// </summary>
    public static Guid? TryParseRunJob(string[] args)
    {
        if (args == null) return null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith(RunJobEqPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(a.Substring(RunJobEqPrefix.Length), out var g)) return g;
            }
            else if (string.Equals(a, RunJobSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Guid.TryParse(args[i + 1], out var g)) return g;
            }
        }
        return null;
    }
}
