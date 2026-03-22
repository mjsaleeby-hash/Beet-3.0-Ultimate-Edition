using System.IO;
using System.Text.Json;

namespace BeetsBackup.Services;

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "settings.json");

    public bool IsDarkMode { get; set; } = true; // default to dark

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<SettingsService>(json);
                if (loaded != null)
                    IsDarkMode = loaded.IsDarkMode;
            }
        }
        catch { /* use defaults if file is corrupt */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
