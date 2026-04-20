using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace BeetsBackup.Services;

/// <summary>
/// Checks GitHub Releases for newer versions and exposes the result
/// so the UI can prompt the user to update or skip.
/// </summary>
public sealed class UpdateService
{
    private const string GitHubOwner = "mjsaleeby-hash";
    private const string GitHubRepo = "Beet-3.0-Ultimate-Edition";
    private const string ApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private readonly SettingsService _settings;

    /// <summary>Gets the latest remote version string after a successful check, or <c>null</c>.</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>Gets the GitHub release page URL, or <c>null</c> if no update was found.</summary>
    public string? ReleaseUrl { get; private set; }

    /// <summary>Gets the release notes body, or <c>null</c>.</summary>
    public string? ReleaseNotes { get; private set; }

    /// <summary>Gets the running application version (major.minor.patch).</summary>
    public string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BeetsBackup", "3.0"));
        return client;
    }

    /// <summary>
    /// Checks GitHub for a newer release. Returns true if an update is available.
    /// Silently returns false on any error (no internet, rate limit, etc.).
    /// </summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            // Skip if user dismissed this version
            var skipped = _settings.Data.SkippedVersion;

            var json = await Http.GetStringAsync(ApiUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tagName)) return false;

            // Strip leading "v" if present (e.g. "v3.1.0" → "3.1.0")
            var versionStr = tagName.StartsWith('v') ? tagName[1..] : tagName;

            if (!Version.TryParse(versionStr, out var remoteVersion)) return false;
            if (!Version.TryParse(CurrentVersion, out var localVersion)) return false;

            if (remoteVersion <= localVersion) return false;

            // Check if user already skipped this specific version
            if (versionStr == skipped) return false;

            LatestVersion = versionStr;
            ReleaseUrl = root.GetProperty("html_url").GetString();
            ReleaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() : null;

            FileLogger.Info($"Update available: v{versionStr} (current: v{CurrentVersion})");
            return true;
        }
        catch (Exception ex)
        {
            // Silently fail — no internet, rate limited, API changed, etc.
            FileLogger.Info($"Update check skipped: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Persists the latest version as "skipped" so the user isn't prompted again for it.</summary>
    public void SkipVersion()
    {
        if (LatestVersion != null)
        {
            _settings.Data.SkippedVersion = LatestVersion;
            _settings.Save();
        }
    }
}
