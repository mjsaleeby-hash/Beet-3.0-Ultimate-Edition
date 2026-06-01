namespace BeetsBackup.Services;

/// <summary>
/// Cross-process per-job lock used by every code path that runs a <see cref="Models.ScheduledJob"/>
/// — the scheduler's headless and in-process flows, plus the foreground "Back up now" command.
/// Without this, the same job can race two ways: Windows Task Scheduler launches
/// <c>BeetsBackup.exe --run-job</c> while the foreground app's in-process minute-tick also fires
/// it, or two foreground threads dispatch the same job back-to-back. A named semaphore
/// (<c>Global\</c> scope so it spans sessions) lets the first runner win and the rest skip cleanly.
/// </summary>
/// <remarks>
/// A <see cref="System.Threading.Semaphore"/> — NOT a <see cref="System.Threading.Mutex"/> — is used
/// deliberately. The lock is acquired on one thread-pool thread but released on whichever thread the
/// job's <c>await</c> chain happens to resume on. A mutex has thread affinity: releasing it from a
/// different thread throws <see cref="System.ApplicationException"/>, which (when swallowed) left the
/// kernel object owned/abandoned and could silently wedge a recurring job. A semaphore has no owning
/// thread, so the release succeeds regardless of which thread completes the run.
/// </remarks>
public static class JobMutex
{
    /// <summary>
    /// Attempts to acquire the cross-process lock for the given job. The returned lease is
    /// always safe to wrap in <c>using</c>; consult <see cref="JobMutexLease.WasBusy"/> to
    /// decide whether to skip the run.
    /// </summary>
    /// <param name="jobId">Identifier of the job — drives the semaphore's named-object key.</param>
    /// <param name="jobName">Display name, used purely for log messages on creation failure.</param>
    public static JobMutexLease TryAcquire(System.Guid jobId, string jobName)
    {
        var semaphoreName = $@"Global\BeetsBackup_Job_{jobId:N}";
        System.Threading.Semaphore? jobSemaphore;
        try
        {
            // initialCount: 1, maximumCount: 1 → a binary semaphore (one runner at a time). When the
            // named object already exists (another process created it), the initial/max counts are
            // ignored and we open the existing one, so WaitOne reflects the real current state.
            jobSemaphore = new System.Threading.Semaphore(initialCount: 1, maximumCount: 1, name: semaphoreName);
        }
        catch (System.Exception ex)
        {
            // Creation failure (out of handles, ACL anomaly) is extremely rare. Fall through
            // and run without a cross-process guard — better a possible duplicate run than
            // a silently missed scheduled backup.
            FileLogger.LogException($"Could not create job lock for '{jobName}'; running without cross-process lock", ex);
            return new JobMutexLease(null, heldByUs: false, wasBusy: false);
        }

        // WaitOne(0) on a semaphore just returns true/false in normal use, but if the named
        // object's DACL has been tampered with we can hit AccessControlException — or
        // theoretically WaitHandleCannotBeOpenedException, ObjectDisposedException after
        // a TOCTOU race, etc. Dispose the just-created semaphore in any of those paths so we
        // don't leak the OS handle just because we couldn't poll it.
        bool gotLock;
        try
        {
            gotLock = jobSemaphore.WaitOne(System.TimeSpan.Zero);
        }
        catch (System.Exception ex)
        {
            FileLogger.LogException($"Could not poll job lock for '{jobName}'; running without cross-process lock", ex);
            jobSemaphore.Dispose();
            return new JobMutexLease(null, heldByUs: false, wasBusy: false);
        }

        if (!gotLock)
        {
            // Another process/thread holds the lock — we'll skip this run.
            jobSemaphore.Dispose();
            return new JobMutexLease(null, heldByUs: false, wasBusy: true);
        }

        return new JobMutexLease(jobSemaphore, heldByUs: true, wasBusy: false);
    }
}

/// <summary>
/// Disposable handle returned from <see cref="JobMutex.TryAcquire"/>. Releases the held
/// kernel semaphore (if any) on dispose. Consult <see cref="WasBusy"/> to discover the
/// "already-running" outcome; <see cref="ShouldRun"/> bundles the two run-anyway cases
/// (we hold the lock, or semaphore creation failed and we're proceeding without one).
/// </summary>
public sealed class JobMutexLease : System.IDisposable
{
    private readonly System.Threading.Semaphore? _semaphore;
    private readonly bool _heldByUs;

    /// <summary>True if the caller should proceed with the job. Composed: we hold the lock,
    /// or the lock was unreachable and we're falling through.</summary>
    public bool ShouldRun { get; }

    /// <summary>True if the lock exists and is held by another process/thread — the caller
    /// should log + skip rather than running.</summary>
    public bool WasBusy { get; }

    internal JobMutexLease(System.Threading.Semaphore? semaphore, bool heldByUs, bool wasBusy)
    {
        _semaphore = semaphore;
        _heldByUs = heldByUs;
        WasBusy = wasBusy;
        ShouldRun = !wasBusy;
    }

    /// <summary>Releases the held semaphore (if any) and disposes the kernel handle. Unlike a
    /// mutex, a semaphore has no thread affinity, so this is safe to call from any thread —
    /// including a continuation thread different from the one that acquired the lock.</summary>
    public void Dispose()
    {
        if (_semaphore == null) return;
        if (_heldByUs)
        {
            try { _semaphore.Release(); }
            catch { /* already released, or count anomaly — Dispose still tears down the handle */ }
        }
        _semaphore.Dispose();
    }
}
