using System.Text.Json;
using BeetsBackup.PerfMon.Models;

namespace BeetsBackup.PerfMon.Services;

public sealed class SessionLogWriter : IDisposable
{
    private const long WarnThresholdBytes = 5L * 1024 * 1024 * 1024; // 5 GB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _logDirectory;
    private readonly string _logFilePath;
    private readonly StreamWriter _writer;
    private bool _warnedOversize;
    private int _sampleCount;

    public string FilePath => _logFilePath;
    public int SampleCount => _sampleCount;

    public SessionLogWriter(string logDirectory, SessionMetadata metadata)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);

        var ts = metadata.StartedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
        _logFilePath = Path.Combine(_logDirectory, $"session_{ts}_{metadata.ProcessId}.jsonl");

        var fs = new FileStream(_logFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs) { AutoFlush = false };

        WriteLine(new { type = "session_start", metadata });
    }

    public void WriteSample(PerformanceSample sample)
    {
        WriteLine(new { type = "sample", data = sample });
        _sampleCount++;

        if (_sampleCount % 60 == 0) // roughly once per minute at 1s cadence
            _writer.Flush();

        if (_sampleCount % 300 == 0)
            CheckLogDirectorySize();
    }

    public void WriteEnd(string reason)
    {
        WriteLine(new
        {
            type = "session_end",
            endedAt = DateTimeOffset.UtcNow,
            sampleCount = _sampleCount,
            reason,
        });
        _writer.Flush();
    }

    public void WriteNote(string message)
    {
        WriteLine(new
        {
            type = "note",
            timestamp = DateTimeOffset.UtcNow,
            message,
        });
    }

    private void WriteLine(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _writer.WriteLine(json);
    }

    private void CheckLogDirectorySize()
    {
        if (_warnedOversize) return;

        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
                total += new FileInfo(file).Length;

            if (total > WarnThresholdBytes)
            {
                _warnedOversize = true;
                var gb = total / (1024.0 * 1024 * 1024);
                var warning = $"Log directory size {gb:F2} GB exceeds 5 GB threshold. Logging continues.";
                Console.WriteLine($"[WARN] {warning}");
                WriteNote(warning);
            }
        }
        catch { /* size check is best-effort */ }
    }

    public void Dispose()
    {
        try { _writer.Flush(); } catch { /* swallow on shutdown */ }
        _writer.Dispose();
    }
}
