using System.Diagnostics;
using System.Runtime.Versioning;
using BeetsBackup.PerfMon.Analysis;
using BeetsBackup.PerfMon.Models;
using BeetsBackup.PerfMon.Services;

[assembly: SupportedOSPlatform("windows")]

namespace BeetsBackup.PerfMon;

internal static class Program
{
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    private static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "monitor";

        return command switch
        {
            "monitor" => await RunMonitorAsync(),
            "analyze" or "analyse" => RunAnalyze(args),
            "help" or "--help" or "-h" => PrintHelp(),
            _ => PrintHelp(),
        };
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            Beet's Backup Performance Monitor

            Usage:
              BeetsBackup.PerfMon [monitor]     Watch for BeetsBackup.exe and log samples (default).
              BeetsBackup.PerfMon analyze       Produce a report from all session logs.
              BeetsBackup.PerfMon help          Show this message.

            Logs are written to:
              <tool-folder>/logs/session_<timestamp>_<pid>.jsonl

            Sampling cadence: 1 second. Auto-detects BeetsBackup.exe; polls every 2 seconds
            while the process is not running. If the log directory exceeds 5 GB, a warning is
            printed and recorded — logging continues.
            """);
        return 0;
    }

    private static async Task<int> RunMonitorAsync()
    {
        Console.WriteLine($"[PerfMon] Log directory: {LogDirectory}");
        Console.WriteLine("[PerfMon] Collecting system info...");
        var systemInfo = SystemInfoCollector.Collect();
        PrintSystemSummary(systemInfo);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var watcher = new ProcessWatcher();
        Console.WriteLine("[PerfMon] Waiting for BeetsBackup.exe... (Ctrl+C to exit)");

        while (!cts.IsCancellationRequested)
        {
            Process? process;
            try
            {
                process = await watcher.WaitForProcessAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await MonitorProcessAsync(process, systemInfo, cts.Token);
            process.Dispose();

            if (!cts.IsCancellationRequested)
                Console.WriteLine("[PerfMon] Process exited. Waiting for next launch...");
        }

        Console.WriteLine("[PerfMon] Shutting down.");
        return 0;
    }

    private static async Task MonitorProcessAsync(Process process, SystemInfo systemInfo, CancellationToken ct)
    {
        var metadata = BuildSessionMetadata(process, systemInfo);
        Console.WriteLine($"[PerfMon] Attached to PID {metadata.ProcessId} ({metadata.ProcessPath})");
        Console.WriteLine($"[PerfMon] Session: {metadata.SessionId}");

        using var writer = new SessionLogWriter(LogDirectory, metadata);
        var sampler = new PerformanceSampler(process);

        // First sample establishes baselines; discard its CPU% since it's always 0.
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { }, TaskScheduler.Default);

        var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await ticker.WaitForNextTickAsync(ct))
            {
                var sample = sampler.Sample();
                if (sample is null)
                {
                    writer.WriteEnd("process_exited");
                    break;
                }

                writer.WriteSample(sample);
            }
        }
        catch (OperationCanceledException)
        {
            writer.WriteEnd("monitor_cancelled");
        }
        finally
        {
            ticker.Dispose();
        }

        Console.WriteLine($"[PerfMon] Wrote {writer.SampleCount} samples to {writer.FilePath}");
    }

    private static SessionMetadata BuildSessionMetadata(Process process, SystemInfo systemInfo)
    {
        string processPath = "unknown";
        string processVersion = "unknown";
        try { processPath = process.MainModule?.FileName ?? "unknown"; } catch { /* access denied */ }
        try { processVersion = process.MainModule?.FileVersionInfo.FileVersion ?? "unknown"; } catch { /* access denied */ }

        return new SessionMetadata
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            StartedAt = DateTimeOffset.UtcNow,
            ProcessId = process.Id,
            ProcessName = process.ProcessName,
            ProcessPath = processPath,
            ProcessVersion = processVersion,
            System = systemInfo,
        };
    }

    private static void PrintSystemSummary(SystemInfo s)
    {
        Console.WriteLine($"  Machine: {s.MachineName}  OS: {s.OsVersion}");
        Console.WriteLine($"  CPU:     {s.CpuName}");
        Console.WriteLine($"  Cores:   {s.PhysicalCores} physical / {s.LogicalCores} logical @ {s.CpuMaxClockMhz:F0} MHz");
        Console.WriteLine($"  Memory:  {s.TotalMemoryBytes / (1024.0 * 1024 * 1024):F1} GB");
        Console.WriteLine($"  .NET:    {s.DotNetVersion}");
        foreach (var d in s.Disks)
            Console.WriteLine($"  Disk:    {d.Model} ({d.MediaType}, {d.SizeBytes / (1024.0 * 1024 * 1024):F0} GB)");
    }

    private static int RunAnalyze(string[] args)
    {
        var analyzer = new LogAnalyzer(LogDirectory);
        var report = analyzer.BuildReport();
        Console.Write(report);

        // Also drop a timestamped report into the audit folder for later reference.
        var reportDir = Path.Combine(AppContext.BaseDirectory, "audit");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir,
            $"trend_report_{DateTime.Now:yyyy-MM-dd_HHmmss}.md");
        File.WriteAllText(reportPath, report);
        Console.WriteLine();
        Console.WriteLine($"[PerfMon] Saved report to {reportPath}");
        return 0;
    }
}
