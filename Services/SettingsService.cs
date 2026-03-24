using System.IO;
using System.Text.Json;

namespace BeetsBackup.Services;

public class SettingsData
{
    public bool IsDarkMode { get; set; } = true;
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
