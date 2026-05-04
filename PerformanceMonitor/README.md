# Beet's Backup — Performance Monitor

External performance monitoring tool for **Beet's Backup**. Runs alongside the main app, samples its CPU/memory/handle/I/O metrics once per second, and writes structured JSON Lines logs for later analysis. No changes are made to Beet itself — this tool observes from the outside.

## Folder layout

```
PerformanceMonitor/
├── BeetsBackup.PerfMon.csproj     # standalone .NET 8 console project
├── Program.cs                     # entry point (monitor | analyze | help)
├── Services/                      # process watcher, sampler, log writer, WMI collector
├── Models/                        # PerformanceSample, SystemInfo, SessionMetadata
├── Analysis/                      # log analyzer (percentiles, trends, outliers)
├── logs/                          # session_<timestamp>_<pid>.jsonl files
└── audit/                         # windows-expert audit reports + trend reports
```

## Build

```bash
dotnet build PerformanceMonitor/BeetsBackup.PerfMon.csproj -c Release
```

Produces `PerformanceMonitor/bin/Release/net8.0-windows/win-x64/BeetsBackup.PerfMon.exe`.

## Usage

### Live monitoring

```bash
BeetsBackup.PerfMon.exe
```

1. Prints system info (CPU, cores, memory, disks).
2. Polls every 2 seconds for a running `BeetsBackup.exe`.
3. When detected, writes `logs/session_<timestamp>_<pid>.jsonl` and samples every 1 s.
4. When the process exits, the session file is finalized and the tool waits for the next launch.
5. If the `logs/` directory grows past 5 GB, a warning is printed once and also recorded in the current session log — **logging continues** regardless of size.
6. Ctrl+C exits cleanly and finalizes any open session.

### Analyze accumulated logs

```bash
BeetsBackup.PerfMon.exe analyze
```

Reads every `session_*.jsonl` and produces:

- **Aggregate stats** — min/avg/p50/p95/p99/max for CPU, working set, private bytes, handles, threads, I/O throughput.
- **Per-session table** — start time, duration, sample count, average & peak metrics.
- **Outliers** — top 10 CPU spikes above the 99th percentile, so one-off pathological workloads are easy to spot.
- **Cross-session trends** — first vs latest session comparison, with warnings if working set or handle count has grown significantly over time (leak detection).

The report is printed to stdout and saved to `audit/trend_report_<timestamp>.md`.

## Log format

Each line in a session file is a standalone JSON object. Line types:

- `session_start` — metadata header with system info, process PID, path, version.
- `sample` — one per second while Beet is running.
- `note` — free-form entries (e.g., the 5 GB size warning).
- `session_end` — final line with sample count and reason (`process_exited` or `monitor_cancelled`).

Sample record:

```json
{
  "type": "sample",
  "data": {
    "Timestamp": "2026-04-18T14:22:03+00:00",
    "UptimeMs": 45201,
    "ProcessCpuPercent": 3.4,
    "WorkingSetBytes": 182345728,
    "PrivateBytes": 156237824,
    "VirtualBytes": 2201763840,
    "HandleCount": 487,
    "ThreadCount": 29,
    "IoReadBytesTotal": 104857600,
    "IoWriteBytesTotal": 52428800,
    "IoReadBytesPerSec": 1048576,
    "IoWriteBytesPerSec": 524288,
    "SystemCpuPercent": 18.7,
    "SystemAvailableMemoryBytes": 8589934592
  }
}
```

## Windows-practices audit

The `.claude/agents/windows-expert.md` agent can be invoked to walk the Beet codebase and produce a markdown audit at `audit/windows-practices-audit-<date>.md`. The audit covers threading, I/O, memory, handle leaks, API choices, and — crucially — whether the app is underutilizing the machine's CPU cores, SSD queue depth, and memory.

## Design notes

- **Separate project** — not added to any solution file. Builds independently.
- **No code changes to Beet** — all metrics come from Windows APIs (`GetProcessIoCounters`, `GetSystemTimes`, `GlobalMemoryStatusEx`) and the managed `Process` class.
- **PInvoke over PerformanceCounter** — `PerformanceCounter` has instance-name quirks, ACL issues, and warmup behavior. Native calls are simpler and faster.
- **JSON Lines** — append-only, streamable, easy to parse line by line for very large logs.
- **Sampling cadence of 1 s** — produces roughly 2 KB of log per minute, ~3 MB per 24 h run.
