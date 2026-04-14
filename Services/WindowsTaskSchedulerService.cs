using BeetsBackup.Models;
using System.Diagnostics;
using System.IO;

namespace BeetsBackup.Services;

/// <summary>
/// Thin wrapper around <c>schtasks.exe</c> that registers Beet's Backup jobs with the
/// Windows Task Scheduler so they fire even when the app is closed or after a reboot.
/// </summary>
/// <remarks>
/// <para>Task naming convention: <c>BeetsBackup_{jobId}</c> — prefixed so it's trivial to
/// distinguish our tasks from anything else in Task Scheduler, and keyed off the job's GUID
/// so it can be found again without scanning all tasks.</para>
/// <para>We deliberately use <c>schtasks.exe</c> rather than a managed Task Scheduler wrapper:
/// no extra NuGet dependency, no COM registration concerns, and it works identically in both
/// elevated and non-elevated processes.</para>
/// </remarks>
public static class WindowsTaskSchedulerService
{
    /// <summary>Prefix used for all scheduled-task names registered by this app.</summary>
    public const string TaskNamePrefix = "BeetsBackup_";

    /// <summary>Returns the Windows Task Scheduler name for a given job id.</summary>
    public static string TaskNameFor(Guid jobId) => $"{TaskNamePrefix}{jobId:N}";

    /// <summary>Path to the running app's <c>.exe</c>, used as the command the task invokes.</summary>
    private static string AppExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BeetsBackup.exe");

    /// <summary>
    /// Creates (or replaces, with <c>/F</c>) a Windows scheduled task that will launch this app
    /// with <c>--run-job {id}</c> at the job's next-run time, repeating if the job is recurring.
    /// </summary>
    /// <returns><c>true</c> if <c>schtasks.exe</c> exited with code 0; otherwise <c>false</c>.</returns>
    public static bool Register(ScheduledJob job)
    {
        try
        {
            var taskName = TaskNameFor(job.Id);
            // Quote the exe path so schtasks preserves paths containing spaces.
            var command = $"\"{AppExePath}\" --run-job {job.Id}";

            var args = new List<string>
            {
                "/Create",
                "/TN", taskName,
                "/TR", command,
                "/F" // force overwrite if a task with this name already exists
            };

            // Run as the interactive user — no admin elevation required, and the app runs
            // in the user's session where NotifyIcon and file access behave normally.
            args.Add("/RL"); args.Add("LIMITED");

            args.AddRange(BuildScheduleArgs(job, DateTime.Now));

            return RunSchtasks(args);
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Failed to register Windows task for job '{job.Name}'", ex);
            return false;
        }
    }

    /// <summary>Deletes the Windows task associated with the given job id, if it exists.</summary>
    public static bool Unregister(Guid jobId)
    {
        try
        {
            var args = new List<string> { "/Delete", "/TN", TaskNameFor(jobId), "/F" };
            return RunSchtasks(args);
        }
        catch (Exception ex)
        {
            FileLogger.LogException($"Failed to unregister Windows task {jobId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Re-registers all jobs passed in. Called on app startup so that tasks removed by the user
    /// in Task Scheduler (or lost through a Windows reinstall) are put back.
    /// </summary>
    public static void ReconcileAll(IEnumerable<ScheduledJob> jobs)
    {
        foreach (var job in jobs)
        {
            if (!job.IsEnabled) continue;
            // One-time jobs that have already run are pruned by the scheduler (IsEnabled = false),
            // so anything enabled is worth re-registering.
            Register(job);
        }
    }

    /// <summary>
    /// Builds the <c>/SC</c>/<c>/ST</c>/<c>/SD</c>/<c>/MO</c> portion of a schtasks invocation
    /// from a job's timing and recurrence. Exposed as <c>internal</c> for unit testing — the
    /// rest of the command-line construction is glue that doesn't benefit from isolated tests.
    /// </summary>
    /// <param name="job">Job whose NextRun and RecurInterval drive the schedule.</param>
    /// <param name="now">Reference "current time" used to bump past-due NextRun values forward.
    /// Parameterized so tests aren't wall-clock dependent.</param>
    internal static List<string> BuildScheduleArgs(ScheduledJob job, DateTime now)
    {
        var args = new List<string>();
        var startTime = job.NextRun;
        if (startTime < now) startTime = now.AddMinutes(1);

        // schtasks accepts /ST in HH:mm (24-hour) and /SD in the machine's short-date locale.
        // Using invariant-culture formatting with explicit patterns avoids locale surprises.
        args.Add("/ST"); args.Add(startTime.ToString("HH:mm"));
        args.Add("/SD"); args.Add(startTime.ToString("MM/dd/yyyy"));

        if (!job.IsRecurring || !job.RecurInterval.HasValue)
        {
            args.Add("/SC"); args.Add("ONCE");
            return args;
        }

        var interval = job.RecurInterval.Value;

        // Map our four supported recurrences onto schtasks /SC values. Hourly intervals use
        // /MO N for "every N hours"; Daily/Weekly don't need a multiplier.
        if (interval == TimeSpan.FromDays(1))
        {
            args.Add("/SC"); args.Add("DAILY");
        }
        else if (interval == TimeSpan.FromDays(7))
        {
            args.Add("/SC"); args.Add("WEEKLY");
        }
        else if (interval.TotalHours >= 1 && interval.TotalMinutes % 60 == 0)
        {
            args.Add("/SC"); args.Add("HOURLY");
            args.Add("/MO"); args.Add(((int)interval.TotalHours).ToString());
        }
        else
        {
            // Fallback for unusual intervals (e.g. 30-minute): /SC MINUTE /MO N.
            args.Add("/SC"); args.Add("MINUTE");
            args.Add("/MO"); args.Add(Math.Max(1, (int)interval.TotalMinutes).ToString());
        }

        return args;
    }

    /// <summary>
    /// Spawns <c>schtasks.exe</c> with the given argument list and waits for it to exit.
    /// Output is captured to the app log on failure so the user can diagnose registration issues.
    /// </summary>
    private static bool RunSchtasks(List<string> args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null) return false;

        proc.WaitForExit(15_000);

        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            var stdout = proc.StandardOutput.ReadToEnd();
            FileLogger.Warn($"schtasks exited with code {proc.ExitCode}: {stderr.Trim()} {stdout.Trim()}");
            return false;
        }
        return true;
    }
}
