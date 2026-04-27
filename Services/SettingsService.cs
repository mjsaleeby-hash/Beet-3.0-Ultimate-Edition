using System.IO;
using System.Text.Json;

namespace BeetsBackup.Services;

/// <summary>
/// Persisted application settings (theme, startup preference, skipped update version).
/// </summary>
public sealed class SettingsData
{
    /// <summary>Whether dark mode is active.</summary>
    public bool IsDarkMode { get; set; } = true;

    /// <summary>Whether the application should launch at Windows startup.</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// When <c>true</c>, the app starts silently in the system tray when launched with the
    /// <c>--startup</c> flag (i.e. via the Windows Startup shortcut). When <c>false</c> (default),
    /// the main window is always shown on launch, even when auto-started.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>Version string the user chose to skip in the update dialog.</summary>
    public string? SkippedVersion { get; set; }
}

/// <summary>
/// Loads, saves, and manages application-wide user preferences.
/// Persisted as JSON in the app's local data directory.
/// Also manages the Windows Startup shortcut for auto-launch.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "settings.json");

    /// <summary>The current in-memory settings data.</summary>
    public SettingsData Data { get; private set; } = new();

    /// <summary>Whether dark mode is active (convenience wrapper around <see cref="Data"/>).</summary>
    public bool IsDarkMode
    {
        get => Data.IsDarkMode;
        set => Data.IsDarkMode = value;
    }

    /// <summary>Whether to start silently in tray when auto-launched (convenience wrapper).</summary>
    public bool StartMinimized
    {
        get => Data.StartMinimized;
        set { Data.StartMinimized = value; Save(); }
    }

    /// <summary>
    /// Whether the app should launch at Windows startup.
    /// Setting this property registers or removes the ONLOGON scheduled task — not a Startup
    /// folder shortcut, which would trigger a UAC prompt on every boot with requireAdministrator.
    /// </summary>
    public bool LaunchAtStartup
    {
        get => Data.LaunchAtStartup;
        set
        {
            Data.LaunchAtStartup = value;
            if (value)
                WindowsTaskSchedulerService.RegisterStartupTask();
            else
                WindowsTaskSchedulerService.UnregisterStartupTask();
            Save();
        }
    }

    // Legacy Startup-folder shortcut path — used only for one-time migration.
    private static readonly string _legacyShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Beet's Backup.lnk");

    /// <summary>
    /// Loads settings from disk. Falls back to defaults if the file is missing or corrupt.
    /// Also migrates any legacy Startup-folder shortcut to the ONLOGON scheduled task so the
    /// UAC consent prompt no longer appears on every boot.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<SettingsData>(json);
                if (loaded != null)
                    Data = loaded;
            }
        }
        catch { /* use defaults if file is corrupt */ }

        // One-time migration: delete the old .lnk and create the task in its place.
        if (File.Exists(_legacyShortcutPath))
        {
            try { File.Delete(_legacyShortcutPath); } catch { }
            if (Data.LaunchAtStartup)
                WindowsTaskSchedulerService.RegisterStartupTask();
        }
    }

    /// <summary>
    /// Persists current settings to disk using atomic write (write-to-temp then replace).
    /// </summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Data);
            var tmpPath = SettingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(SettingsPath))
                File.Replace(tmpPath, SettingsPath, SettingsPath + ".bak");
            else
                File.Move(tmpPath, SettingsPath);
        }
        catch { }
    }
}
