using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BeetsBackup.Services;

/// <summary>
/// Bundles user-facing log and configuration files into a single zip the user can email
/// to support, and tracks whether the previous session exited cleanly via a sentinel file.
/// </summary>
public static class DiagnosticsService
{
    private static readonly string SentinelPath = Path.Combine(FileLogger.LogDirectory, "running.lock");

    /// <summary>Writes the running sentinel. Call once at app startup.</summary>
    public static void MarkRunning()
    {
        try
        {
            Directory.CreateDirectory(FileLogger.LogDirectory);
            File.WriteAllText(SentinelPath, DateTime.Now.ToString("o"));
        }
        catch { }
    }

    /// <summary>Removes the running sentinel. Call from <c>OnExit</c> on the clean-shutdown path.</summary>
    public static void MarkExitedCleanly()
    {
        try { if (File.Exists(SentinelPath)) File.Delete(SentinelPath); }
        catch { }
    }

    /// <summary>
    /// Returns the timestamp of the last unclean shutdown, or null if the previous session exited cleanly.
    /// Consumes the sentinel — subsequent calls return null until the next unclean shutdown.
    /// </summary>
    public static DateTime? ConsumeUncleanShutdown()
    {
        try
        {
            if (!File.Exists(SentinelPath)) return null;
            var raw = File.ReadAllText(SentinelPath);
            File.Delete(SentinelPath);
            return DateTime.TryParse(raw, out var ts) ? ts : DateTime.Now;
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds a timestamped diagnostics zip on the user's Desktop and returns its full path.
    /// Includes any logs and config that exist; missing files are skipped without error.
    /// </summary>
    public static string ExportToDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(desktop, $"BeetsBackup-Diagnostics-{stamp}.zip");

        // Build into a temp file first so a partial failure never leaves a half-written zip on the Desktop.
        var tempZip = Path.Combine(Path.GetTempPath(), $"BeetsBackup-Diag-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                AddIfExists(archive, Path.Combine(FileLogger.LogDirectory, "operational.log"), "operational.log");
                AddIfExists(archive, Path.Combine(FileLogger.LogDirectory, "operational.log.1"), "operational.log.1");
                AddIfExists(archive, Path.Combine(FileLogger.LogDirectory, "crash_dump.log"), "crash_dump.log");
                AddIfExists(archive, Path.Combine(FileLogger.LogDirectory, "crash_dump.log.1"), "crash_dump.log.1");
                AddIfExists(archive, Path.Combine(FileLogger.LogDirectory, "settings.json"), "settings.json");
                AddIfExists(archive, Path.Combine(FileLogger.LogDirectory, "backup_log.json"), "backup_log.json");

                var entry = archive.CreateEntry("system_info.txt", CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(BuildSystemInfo());
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tempZip, zipPath);
            FileLogger.Info($"Diagnostics bundle exported: {zipPath}");
            return zipPath;
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
    }

    /// <summary>Opens an Explorer window with the given file selected.</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void AddIfExists(ZipArchive archive, string sourcePath, string entryName)
    {
        if (!File.Exists(sourcePath)) return;
        try
        {
            // Copy to a temp path so we don't fight an exclusive lock on the live log.
            var tempCopy = Path.Combine(Path.GetTempPath(), $"diag-{Guid.NewGuid():N}-{entryName}");
            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = File.Create(tempCopy))
            {
                src.CopyTo(dst);
            }
            archive.CreateEntryFromFile(tempCopy, entryName, CompressionLevel.Optimal);
            try { File.Delete(tempCopy); } catch { }
        }
        catch (Exception ex)
        {
            // Don't fail the whole export if one file misbehaves — record the skip in system_info.
            FileLogger.LogException($"Diagnostics: skipped {entryName}", ex);
        }
    }

    private static string BuildSystemInfo()
    {
        var sb = new StringBuilder();
        var process = Process.GetCurrentProcess();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        sb.AppendLine("Beet's Backup — Diagnostics Bundle");
        sb.AppendLine("════════════════════════════════════════════");
        sb.AppendLine($"Generated:        {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"App Version:      {version}");
        sb.AppendLine($"Process Uptime:   {DateTime.Now - process.StartTime:hh\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("── System ──────────────────────────────────");
        sb.AppendLine($"OS:               {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture:     {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET:             {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Machine:          {Environment.MachineName}");
        sb.AppendLine($"User:             {Environment.UserName}");
        sb.AppendLine($"Working Set:      {process.WorkingSet64 / (1024 * 1024)} MB");
        sb.AppendLine($"Logical Cores:    {Environment.ProcessorCount}");
        sb.AppendLine();
        sb.AppendLine("── Display ─────────────────────────────────");
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                sb.AppendLine($"  {screen.DeviceName}: {screen.Bounds.Width}x{screen.Bounds.Height} (primary: {screen.Primary})");
            }
        }
        catch (Exception ex) { sb.AppendLine($"  (display enumeration failed: {ex.Message})"); }
        sb.AppendLine();
        sb.AppendLine("── Drives ──────────────────────────────────");
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) { sb.AppendLine($"  {d.Name} (not ready)"); continue; }
                var freeGb = d.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                var totalGb = d.TotalSize / 1024.0 / 1024.0 / 1024.0;
                sb.AppendLine($"  {d.Name} [{d.DriveType}, {d.DriveFormat}]  {freeGb:F1} GB free / {totalGb:F1} GB total");
            }
        }
        catch (Exception ex) { sb.AppendLine($"  (drive enumeration failed: {ex.Message})"); }
        sb.AppendLine();
        sb.AppendLine("── Log Directory ───────────────────────────");
        sb.AppendLine($"  {FileLogger.LogDirectory}");
        return sb.ToString();
    }
}
