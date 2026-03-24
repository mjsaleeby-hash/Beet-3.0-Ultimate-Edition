using System.IO;

namespace BeetsBackup.Services;

public static class FileLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "operational.log");

    private static readonly object _lock = new();
    private const long MaxSizeBytes = 10 * 1024 * 1024; // 10 MB

    public static void Log(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                RotateIfNeeded();
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n");
            }
        }
        catch { }
    }

    public static void LogException(string context, Exception ex)
    {
        Log("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath)) return;
        var info = new FileInfo(LogPath);
        if (info.Length < MaxSizeBytes) return;

        var rotatedPath = LogPath + ".1";
        if (File.Exists(rotatedPath))
            File.Delete(rotatedPath);
        File.Move(LogPath, rotatedPath);
    }
}
