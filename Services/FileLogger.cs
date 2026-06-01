using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BeetsBackup.Services;

/// <summary>
/// Simple file-based logger that writes structured log entries to the app's local data directory.
/// Supports automatic log rotation at 10 MB and a separate crash dump file for unhandled exceptions.
/// </summary>
/// <remarks>
/// All methods are static and thread-safe (guarded by a shared lock).
/// Logging failures are silently swallowed to avoid cascading errors.
/// </remarks>
public static class FileLogger
{
    /// <summary>Directory where all log files are stored (%LocalAppData%\Beet's Backup).</summary>
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup");

    private static readonly string LogPath = Path.Combine(LogDirectory, "operational.log");
    private static readonly string CrashDumpPath = Path.Combine(LogDirectory, "crash_dump.log");

    private static readonly object _lock = new();
    private const long MaxSizeBytes = 10 * 1024 * 1024; // 10 MB

    // Log writes go through a single-consumer background queue so callers — including the parallel
    // copy workers — never block on disk I/O or contend on the file lock per line. The writer thread
    // coalesces everything currently queued into one append, collapsing a burst of N per-line
    // open/write/close cycles into a single one. A ProcessExit hook flushes the tail so a graceful
    // shutdown loses nothing; an abrupt kill can still drop in-flight INFO/WARN lines, which is an
    // acceptable trade for an operational log (crash dumps are flushed explicitly — see WriteCrashDump).
    private static readonly BlockingCollection<object> _queue = new();

    /// <summary>Marker enqueued by <see cref="Flush"/>; the writer signals <see cref="Done"/> once
    /// every line queued before it has reached disk.</summary>
    private sealed record FlushSignal(ManualResetEventSlim Done);

    static FileLogger()
    {
        var writer = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "FileLogger",
        };
        writer.Start();
        // Best-effort flush of the tail on a graceful exit (normal shutdown, headless --run-job done).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>
    /// Writes a timestamped log entry at the specified level. Returns immediately — the formatted
    /// line is handed to the background writer rather than written inline.
    /// </summary>
    /// <param name="level">Severity level (e.g. "INFO", "WARN", "ERROR").</param>
    /// <param name="message">The message to log.</param>
    public static void Log(string level, string message)
    {
        try
        {
            _queue.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n");
        }
        catch { /* queue completed during shutdown — drop the line */ }
    }

    /// <summary>
    /// Blocks until every log line queued so far has reached disk. Called from
    /// <see cref="WriteCrashDump"/> and on process exit so durability-critical lines aren't stranded
    /// in the background queue. Bounded by a short timeout so a wedged writer can't hang shutdown.
    /// </summary>
    public static void Flush()
    {
        var done = new ManualResetEventSlim(false);
        try { _queue.Add(new FlushSignal(done)); }
        catch { return; /* nothing draining the queue */ }
        try { done.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* timeout/disposed — give up rather than block forever */ }
    }

    /// <summary>Single-consumer loop: drains the queue, coalescing all immediately-available lines
    /// into one append so a burst of logging is one disk write, not one per line.</summary>
    private static void DrainLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item is FlushSignal flushOnly)
            {
                // All prior lines were written in earlier iterations — nothing buffered to flush.
                flushOnly.Done.Set();
                continue;
            }

            var batch = new StringBuilder((string)item);
            bool released = false;
            while (_queue.TryTake(out var next))
            {
                if (next is FlushSignal signal)
                {
                    // Write what we've accumulated, THEN release the waiter — it must observe its
                    // own preceding lines on disk before Flush returns.
                    WriteBatch(batch.ToString());
                    signal.Done.Set();
                    released = true;
                    break;
                }
                batch.Append((string)next!);
            }
            if (!released) WriteBatch(batch.ToString());
        }
    }

    /// <summary>Appends a pre-formatted block of one or more lines to the operational log,
    /// rotating first if needed. Failures are swallowed — logging must never throw.</summary>
    private static void WriteBatch(string text)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(LogPath);
                File.AppendAllText(LogPath, text);
            }
        }
        catch { }
    }

    /// <summary>Logs an informational message.</summary>
    public static void Info(string message) => Log("INFO", message);

    /// <summary>Logs a warning message.</summary>
    public static void Warn(string message) => Log("WARN", message);

    /// <summary>Logs an error message.</summary>
    public static void Error(string message) => Log("ERROR", message);

    /// <summary>
    /// Logs an exception with its type, message, and full stack trace.
    /// </summary>
    /// <param name="context">Description of what was happening when the exception occurred.</param>
    /// <param name="ex">The exception to log.</param>
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

            // Also log to operational log for correlation, then force the background queue to disk —
            // a crash dump usually precedes process death, so we can't leave that line in flight.
            Log("FATAL", $"{source}: {ex.GetType().Name}: {ex.Message}");
            Flush();
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
