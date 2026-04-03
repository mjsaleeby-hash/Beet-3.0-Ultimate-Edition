using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace BeetsBackup.Services;

public class SettingsData
{
    public bool IsDarkMode { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public string? SkippedVersion { get; set; }
}

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "settings.json");

    public SettingsData Data { get; private set; } = new();

    public bool IsDarkMode
    {
        get => Data.IsDarkMode;
        set => Data.IsDarkMode = value;
    }

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

    public bool StartupShortcutExists => File.Exists(ShortcutPath);

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

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        // Use PowerShell to create a .lnk shortcut (avoids COM interop dependency)
        var ps = $"""
            $ws = New-Object -ComObject WScript.Shell;
            $sc = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}');
            $sc.TargetPath = '{targetPath.Replace("'", "''")}';
            $sc.Arguments = '--startup';
            $sc.WorkingDirectory = '{Path.GetDirectoryName(targetPath)!.Replace("'", "''")}';
            $sc.Description = 'Beet''s Backup';
            $sc.Save()
            """;
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{ps.Replace("\"", "\\\"")}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }

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
