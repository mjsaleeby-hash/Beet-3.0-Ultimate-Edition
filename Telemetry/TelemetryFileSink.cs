using System.Diagnostics.Tracing;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BeetsBackup.Telemetry;

/// <summary>
/// In-process listener that persists <see cref="BeetTelemetry"/> events to a
/// JSON-Lines file the external PerformanceMonitor and BenchmarkHarness ingest.
///
/// One line per event, e.g.:
///   {"ts":"2026-06-22T18:03:11.482Z","event":"BackupCompleted","buildTag":"3.0-baseline",
///    "data":{"jobName":"Photos","bytesTransferred":7400000000,"durationMs":54210, ...}}
///
/// Files live next to Beet's other data so a single folder captures everything:
///   %LocalAppData%\Beet's Backup\telemetry\telemetry_&lt;yyyyMMdd&gt;.jsonl
///
/// Robustness notes:
///   - Append + shared-read so the monitor can tail the file while we write.
///   - Each write is best-effort under a lock; a telemetry failure must NEVER affect
///     the app (the whole point is to observe without perturbing).
///   - When no analysis is running this still writes the file, which is what we want:
///     the field window needs the data persisted, not just live-streamed.
/// </summary>
public sealed class TelemetryFileSink : EventListener
{
    private static readonly string TelemetryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup", "telemetry");

    private readonly object _gate = new();
    private string _currentFilePath = string.Empty;
    private string _currentDateStamp = string.Empty;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    // EventListener's base constructor calls OnEventSourceCreated for sources that
    // already exist, so subscription is wired up before this constructor body runs.
    // We additionally force-create and enable our own source here in case it has not
    // been touched yet (referencing BeetTelemetry.Log instantiates it).
    public TelemetryFileSink()
    {
        try { EnableEvents(BeetTelemetry.Log, EventLevel.Verbose); }
        catch { /* never fail startup over telemetry */ }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);
        // Subscribe only to our own provider — we don't want the firehose of
        // framework EventSources in this file.
        if (eventSource.Name == "BeetsBackup-Telemetry")
        {
            try { EnableEvents(eventSource, EventLevel.Verbose); }
            catch { /* ignore — telemetry is best-effort */ }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            // Map the positional payload back to its declared parameter names so the
            // JSON is self-describing rather than a bare array.
            var data = new Dictionary<string, object?>();
            var names = eventData.PayloadNames;
            var values = eventData.Payload;
            if (names is not null && values is not null)
            {
                int count = Math.Min(names.Count, values.Count);
                for (int i = 0; i < count; i++)
                    data[names[i]] = values[i];
            }

            var record = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["event"] = eventData.EventName,
                ["buildTag"] = BuildInfo.BuildTag,
                ["data"] = data,
            };

            var line = JsonSerializer.Serialize(record, _jsonOptions);
            WriteLine(line);
        }
        catch
        {
            // Swallow — a telemetry hiccup must not disturb the app or the operation
            // that just emitted the event.
        }
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            RollFileIfNeeded();
            // FileShare.ReadWrite|Delete: the monitor may read (and old files be deleted)
            // while we append. Open/append/close per line keeps the on-disk file durable
            // even if the process is killed mid-session (matches Beet's JSONL philosophy).
            using var fs = new FileStream(_currentFilePath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sw.WriteLine(line);
        }
    }

    private void RollFileIfNeeded()
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        if (today == _currentDateStamp && _currentFilePath.Length > 0) return;

        Directory.CreateDirectory(TelemetryDir);
        _currentDateStamp = today;
        _currentFilePath = Path.Combine(TelemetryDir, $"telemetry_{today}.jsonl");
    }
}
