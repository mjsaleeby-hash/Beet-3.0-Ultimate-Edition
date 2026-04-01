using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BeetsBackup.Services;

public static class FileLogger
{
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup");

    private static readonly string LogPath = Path.Combine(LogDirectory, "operational.log");
    private static readonly string CrashDumpPath = Path.Combine(LogDirectory, "crash_dump.log");

    private static readonly object _lock = new();
    private const long MaxSizeBytes = 10 * 1024 * 1024; // 10 MB

    public static void Log(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(LogPath);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n");
            }
        }
        catch { }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);

    public static void LogException(string context, Exception ex)
    {
        Log("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    /// <summary>
    /// Writes a full crash dump with system context, app state, and exception details.
    /// Called from global exception handlers in App.xaml.cs.
    /// </summary>
    public static void WriteCrashDump(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded(CrashDumpPath);

            var process = Process.GetCurrentProcess();
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

            var dump = $"""
                ════════════════════════════════════════════════════════════════
                CRASH DUMP — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                ════════════════════════════════════════════════════════════════

                Source: {source}
                App Version: {version}
                OS: {RuntimeInformation.OSDescription}
                Architecture: {RuntimeInformation.OSArchitecture}
                .NET: {RuntimeInformation.FrameworkDescription}
                Process Memory: {process.WorkingSet64 / (1024 * 1024)} MB
                Thread Count: {process.Threads.Count}
                Uptime: {DateTime.Now - process.StartTime:hh\:mm\:ss}
                Current Thread: {Environment.CurrentManagedThreadId}

                ── Exception ──────────────────────────────────────────────────
                Type: {ex.GetType().FullName}
                Message: {ex.Message}
                HResult: 0x{ex.HResult:X8}

                ── Stack Trace ────────────────────────────────────────────────
                {ex.StackTrace}

                """;

            // Append inner exceptions
            var inner = ex.InnerException;
            int depth = 1;
            while (inner != null)
            {
                dump += $"""

                    ── Inner Exception #{depth} ──────────────────────────────────
                    Type: {inner.GetType().FullName}
                    Message: {inner.Message}
                    HResult: 0x{inner.HResult:X8}
                    Stack Trace:
                    {inner.StackTrace}

                    """;
                inner = inner.InnerException;
                depth++;
            }

            // AggregateException: log all inner exceptions
            if (ex is AggregateException agg)
            {
                dump += $"\n── Aggregate Inner Exceptions ({agg.InnerExceptions.Count}) ──\n";
                for (int i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    var aggInner = agg.InnerExceptions[i];
                    dump += $"""

                        [{i}] {aggInner.GetType().FullName}: {aggInner.Message}
                        {aggInner.StackTrace}

                        """;
                }
            }

            dump += "\n\n";

            File.AppendAllText(CrashDumpPath, dump);

            // Also log to operational log for correlation
            Log("FATAL", $"{source}: {ex.GetType().Name}: {ex.Message}");
        }
        catch { /* Last resort — nothing we can do */ }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length < MaxSizeBytes) return;

        var rotatedPath = path + ".1";
        if (File.Exists(rotatedPath))
            File.Delete(rotatedPath);
        File.Move(path, rotatedPath);
    }
}
