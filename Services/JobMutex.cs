namespace BeetsBackup.Services;

/// <summary>
/// Cross-process per-job lock used by every code path that runs a <see cref="Models.ScheduledJob"/>
/// — the scheduler's headless and in-process flows, plus the foreground "Back up now" command.
/// Without this, the same job can race two ways: Windows Task Scheduler launches
/// <c>BeetsBackup.exe --run-job</c> while the foreground app's in-process minute-tick also fires
/// it, or two foreground threads dispatch the same job back-to-back. A named mutex
/// (<c>Global\</c> scope so it spans sessions) lets the first runner win and the rest skip cleanly.
/// </summary>
public static class JobMutex
{
    /// <summary>
    /// Attempts to acquire the cross-process lock for the given job. The returned lease is
    /// always safe to wrap in <c>using</c>; consult <see cref="JobMutexLease.WasBusy"/> to
    /// decide whether to skip the run.
    /// </summary>
    /// <param name="jobId">Identifier of the job — drives the mutex's named-object key.</param>
    /// <param name="jobName">Display name, used purely for log messages on creation failure.</param>
    public static JobMutexLease TryAcquire(System.Guid jobId, string jobName)
    {
        var mutexName = $@"Global\BeetsBackup_Job_{jobId:N}";
        System.Threading.Mutex? jobMutex;
        try
        {
            jobMutex = new System.Threading.Mutex(initiallyOwned: false, name: mutexName);
        }
        catch (System.Exception ex)
        {
            // Creation failure (out of handles, ACL anomaly) is extremely rare. Fall through
            // and run without a cross-process guard — better a possible duplicate run than
            // a silently missed scheduled backup.
            FileLogger.LogException($"Could not create job mutex for '{jobName}'; running without cross-process lock", ex);
            return new JobMutexLease(null, heldByUs: false, wasBusy: false);
        }

        bool gotLock;
        try { gotLock = jobMutex.WaitOne(System.TimeSpan.Zero); }
        catch (System.Threading.AbandonedMutexException) { gotLock = true; }

        if (!gotLock)
        {
            // Another process/thread holds the lock — we'll skip this run.
            jobMutex.Dispose();
            return new JobMutexLease(null, heldByUs: false, wasBusy: true);
        }

        return new JobMutexLease(jobMutex, heldByUs: true, wasBusy: false);
    }
}

/// <summary>
/// Disposable handle returned from <see cref="JobMutex.TryAcquire"/>. Releases the held
/// kernel mutex (if any) on dispose. Consult <see cref="WasBusy"/> to discover the
/// "already-running" outcome; <see cref="ShouldRun"/> bundles the two run-anyway cases
/// (we hold the lock, or mutex creation failed and we're proceeding without one).
/// </summary>
public sealed class JobMutexLease : System.IDisposable
{
    private readonly System.Threading.Mutex? _mutex;
    private readonly bool _heldByUs;

    /// <summary>True if the caller should proceed with the job. Composed: we hold the lock,
    /// or the mutex was unreachable and we're falling through.</summary>
    public bool ShouldRun { get; }

    /// <summary>True if the mutex exists and is held by another process/thread — the caller
    /// should log + skip rather than running.</summary>
    public bool WasBusy { get; }

    internal JobMutexLease(System.Threading.Mutex? mutex, bool heldByUs, bool wasBusy)
    {
        _mutex = mutex;
        _heldByUs = heldByUs;
        WasBusy = wasBusy;
        ShouldRun = !wasBusy;
    }

    /// <summary>Releases the held mutex (if any) and disposes the kernel handle.</summary>
    public void Dispose()
    {
        if (_mutex == null) return;
        if (_heldByUs)
        {
            try { _mutex.ReleaseMutex(); }
            catch { /* never owned, or already released — Dispose still tears down the handle */ }
        }
        _mutex.Dispose();
    }
}
