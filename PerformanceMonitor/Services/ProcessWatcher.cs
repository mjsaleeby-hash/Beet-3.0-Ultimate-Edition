using System.Diagnostics;

namespace BeetsBackup.PerfMon.Services;

public sealed class ProcessWatcher
{
    private readonly string _processName;
    private readonly TimeSpan _pollInterval;

    public ProcessWatcher(string processName = "BeetsBackup", TimeSpan? pollInterval = null)
    {
        _processName = processName;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task<Process> WaitForProcessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var proc = FindProcess();
            if (proc is not null)
                return proc;

            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        throw new OperationCanceledException(ct);
    }

    public Process? FindProcess()
    {
        var matches = Process.GetProcessesByName(_processName);
        if (matches.Length == 0)
            return null;

        // Prefer the oldest instance (typically the main window).
        Process? oldest = null;
        foreach (var p in matches)
        {
            try
            {
                if (oldest is null || p.StartTime < oldest.StartTime)
                {
                    oldest?.Dispose();
                    oldest = p;
                }
                else
                {
                    p.Dispose();
                }
            }
            catch
            {
                p.Dispose();
            }
        }

        return oldest;
    }
}
