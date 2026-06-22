using System.Reflection;

namespace BeetsBackup.Telemetry;

/// <summary>
/// Reads the build identity used to bucket telemetry as "3.0 baseline" vs
/// "4.0 candidate". The values come from assembly metadata declared in the .csproj
/// (&lt;AssemblyMetadata Include="BuildTag" .../&gt;), so flipping a build from baseline
/// to candidate is a one-line project change — no code edits, no manual log labelling.
/// </summary>
public static class BuildInfo
{
    /// <summary>The product version (e.g. "3.0.0").</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>"3.0-baseline" or "4.0-candidate". Defaults to a version-derived value
    /// when the csproj does not declare an explicit BuildTag.</summary>
    public static string BuildTag { get; } = ReadMetadata("BuildTag")
        ?? (ReadVersion().StartsWith("4.") ? "4.0-candidate" : "3.0-baseline");

    /// <summary>Short git commit, if the build stamped one in; otherwise empty.</summary>
    public static string GitCommit { get; } = ReadMetadata("GitCommit") ?? string.Empty;

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // InformationalVersion can carry a "+<commit>" suffix; keep just the numeric part.
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static string? ReadMetadata(string key)
    {
        foreach (var m in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
            if (string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(m.Value) ? null : m.Value;
        return null;
    }
}
