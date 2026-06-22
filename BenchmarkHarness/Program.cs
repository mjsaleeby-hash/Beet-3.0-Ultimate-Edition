using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using BeetsBackup.Benchmark.Models;
using BeetsBackup.Benchmark.Services;
using BeetsBackup.PerfMon.Analysis;

[assembly: SupportedOSPlatform("windows")]

namespace BeetsBackup.Benchmark;

internal static class Program
{
    private static readonly string ResultsDir = Path.Combine(AppContext.BaseDirectory, "results");

    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        return command switch
        {
            "list" => ListJobs(),
            "run" => RunScenario(args, doubleRun: false, scenario: "throughput"),
            "fat" => RunScenario(args, doubleRun: true, scenario: "fat-recopy"),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            Beet's Backup Benchmark Harness — repeatable backup scenarios for 3.0 vs 4.0.

            Run from an ELEVATED terminal (Beet's headless run elevates for VSS; otherwise
            each iteration shows a UAC prompt).

            Setup (once): in Beet, create a scheduled job pointing a FIXED source corpus at a
            FIXED destination. Name it e.g. "BENCH-SSD" or "BENCH-FAT" (FAT32/exFAT USB).

            Usage:
              BeetsBackup.Benchmark list
                  List scheduled jobs the harness can drive.

              BeetsBackup.Benchmark run <jobName|id> [--repeat N] [--exe PATH] [--timeout SEC]
                  Fresh backup N times; reports throughput, duration, outcome.

              BeetsBackup.Benchmark fat <jobName|id> [--repeat N] [--exe PATH] [--timeout SEC]
                  FAT/exFAT re-copy proof: each iteration runs the job TWICE and reports the
                  SECOND (no-change) run's files-copied. Bug => ~all re-copied; fix => ~0.

            Defaults: --repeat 10, --exe %USERPROFILE%\\Documents\\BeetsBackup.exe, --timeout 1800.
            Results: results/benchmark_<scenario>_<buildTag>_<timestamp>.jsonl  (feed the cohort
            report by tagging the build; the telemetry these runs emit is already build-tagged).
            """);
        return 0;
    }

    private static int ListJobs()
    {
        var jobs = BenchmarkRunner.ListJobs();
        if (jobs.Count == 0) { Console.WriteLine("No scheduled jobs found."); return 0; }
        Console.WriteLine($"{"Name",-28}  {"Destination",-40}  Id");
        foreach (var j in jobs)
            Console.WriteLine($"{Trunc(j.Name, 28),-28}  {Trunc(j.Destination, 40),-40}  {j.Id}");
        return 0;
    }

    private static int RunScenario(string[] args, bool doubleRun, string scenario)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Specify a job name or id. See `help`."); return 1; }
        var jobName = args[1];
        int repeat = IntArg(args, "--repeat", 10);
        int timeout = IntArg(args, "--timeout", 1800);
        string exe = StrArg(args, "--exe", DefaultExe());

        if (!File.Exists(exe)) { Console.Error.WriteLine($"Beet exe not found: {exe}  (pass --exe PATH)"); return 1; }
        var job = BenchmarkRunner.ResolveJob(jobName);
        if (job is null) { Console.Error.WriteLine($"No job matching '{jobName}'. Run `list`."); return 1; }

        Console.WriteLine($"[Bench] Scenario '{scenario}' on job '{job.Name}' -> {job.Destination}");
        Console.WriteLine($"[Bench] exe={exe}  repeat={repeat}  doubleRun={doubleRun}  timeout={timeout}s");

        var runner = new BenchmarkRunner(exe, timeout);
        var results = new List<BenchmarkRun>();
        for (int i = 1; i <= repeat; i++)
        {
            try
            {
                if (doubleRun)
                {
                    runner.RunOnce(job, i, scenario + "-seed");   // seed/refresh the destination
                    var second = runner.RunOnce(job, i, scenario); // the measured no-change re-run
                    results.Add(second);
                    Console.WriteLine($"  iter {i}: re-run filesCopied={second.FilesCopied} skipped={second.FilesSkipped} fs={second.DestFilesystem}");
                }
                else
                {
                    var r = runner.RunOnce(job, i, scenario);
                    results.Add(r);
                    Console.WriteLine($"  iter {i}: {r.ThroughputMbps:F1} MB/s, {r.BytesTransferred / 1048576.0:F0} MB, {r.FilesCopied} files, {r.Status}");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"  iter {i}: FAILED — {ex.Message}"); }
        }

        if (results.Count == 0) { Console.Error.WriteLine("No successful iterations."); return 1; }
        WriteResults(results, scenario);
        PrintSummary(results, scenario, doubleRun);
        return 0;
    }

    private static void PrintSummary(List<BenchmarkRun> results, string scenario, bool doubleRun)
    {
        var buildTag = results.Select(r => r.BuildTag).FirstOrDefault(t => t != "unknown") ?? "unknown";
        Console.WriteLine();
        Console.WriteLine($"== Summary: {scenario}  (build {buildTag}, n={results.Count}) ==");
        if (doubleRun)
        {
            var copied = results.Select(r => (double)r.FilesCopied).ToArray();
            Console.WriteLine($"  No-change re-run files copied: median {Stats.Percentile(copied, 0.5):F0}, "
                + $"max {copied.Max():F0}  (0 == fix working)");
            Console.WriteLine($"  Dest filesystem: {results.Select(r => r.DestFilesystem).FirstOrDefault()}");
        }
        else
        {
            var mbps = results.Select(r => r.ThroughputMbps).Where(x => x > 0).ToArray();
            var dur = results.Select(r => r.TelemetryDurationMs > 0 ? r.TelemetryDurationMs : r.ProcessWallMs).ToArray();
            if (mbps.Length > 0)
                Console.WriteLine($"  Throughput MB/s: median {Stats.Percentile(mbps, 0.5):F1}, p95 {Stats.Percentile(mbps, 0.95):F1}");
            Console.WriteLine($"  Duration ms:     median {Stats.Percentile(dur, 0.5):F0}, p95 {Stats.Percentile(dur, 0.95):F0}");
        }
        Console.WriteLine("  (Run the same scenario on the other build, then `BeetsBackup.PerfMon cohort` for the A/B verdict.)");
    }

    private static void WriteResults(List<BenchmarkRun> results, string scenario)
    {
        Directory.CreateDirectory(ResultsDir);
        var buildTag = results.Select(r => r.BuildTag).FirstOrDefault(t => t != "unknown") ?? "unknown";
        var path = Path.Combine(ResultsDir, $"benchmark_{scenario}_{buildTag}_{DateTime.Now:yyyy-MM-dd_HHmmss}.jsonl");
        var sb = new StringBuilder();
        foreach (var r in results)
            sb.AppendLine(JsonSerializer.Serialize(r));
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"[Bench] Wrote {results.Count} results to {path}");
    }

    // ---- arg helpers --------------------------------------------------------------
    private static string DefaultExe()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "BeetsBackup.exe");

    private static int IntArg(string[] args, string name, int fallback)
    {
        var v = StrArg(args, name, null);
        return v is not null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private static string StrArg(string[] args, string name, string? fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback!;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
