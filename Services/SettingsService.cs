using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BeetsBackup.Services;

/// <summary>
/// Persisted application settings (theme, startup preference, simple mode, skipped update version).
/// </summary>
public sealed class SettingsData
{
    /// <summary>Whether dark mode is active.</summary>
    public bool IsDarkMode { get; set; } = true;

    /// <summary>Whether the application should launch at Windows startup.</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>Whether the simplified toolbar mode is enabled.</summary>
    public bool IsSimpleMode { get; set; }

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

    /// <summary>
    /// Whether the app should launch at Windows startup.
    /// Setting this property creates or removes the startup shortcut.
    /// </summary>
    public bool LaunchAtStartup
    {
        get => Data.LaunchAtStartup;
        set
        {
            Data.LaunchAtStartup = value;
            ApplyStartupShortcut(value);
            Save();
        }
    }

    private static readonly string StartupFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    private static readonly string ShortcutPath =
        Path.Combine(StartupFolder, "Beet's Backup.lnk");

    /// <summary>Whether a startup shortcut currently exists on disk.</summary>
    public bool StartupShortcutExists => File.Exists(ShortcutPath);

    /// <summary>
    /// Creates or removes the Windows Startup folder shortcut.
    /// </summary>
    private static void ApplyStartupShortcut(bool enable)
    {
        try
        {
            if (enable)
            {
                if (File.Exists(ShortcutPath)) return; // already exists
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;
                CreateShortcut(ShortcutPath, exePath);
            }
            else
            {
                if (File.Exists(ShortcutPath))
                    File.Delete(ShortcutPath);
            }
        }
        catch (Exception ex)
        {
            FileLogger.LogException("Failed to manage startup shortcut", ex);
        }
    }

    /// <summary>
    /// Creates a .lnk shortcut file using WScript.Shell COM interop.
    /// Avoids PowerShell injection risks that would come from shelling out.
    /// </summary>
    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        // Use COM interop to create .lnk — avoids PowerShell injection risks
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            FileLogger.Warn("WScript.Shell COM object not available — cannot create startup shortcut");
            return;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            if (shortcut == null) return;

            var scType = shortcut.GetType();
            scType.InvokeMember("TargetPath",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            scType.InvokeMember("Arguments",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "--startup" });
            scType.InvokeMember("WorkingDirectory",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetPath)! });
            scType.InvokeMember("Description",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Beet's Backup" });
            scType.InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Loads settings from disk. Falls back to defaults if the file is missing or corrupt.
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
