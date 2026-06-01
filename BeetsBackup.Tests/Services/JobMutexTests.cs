using BeetsBackup.Services;
using FluentAssertions;

namespace BeetsBackup.Tests.Services;

/// <summary>
/// Coverage for the cross-process per-job lock. The headline scenario is the one that motivated
/// switching the underlying primitive from a <see cref="System.Threading.Mutex"/> to a
/// <see cref="System.Threading.Semaphore"/>: the scheduler acquires the lock on one thread-pool
/// thread but releases it on whichever thread its <c>await</c> chain happens to resume on. A mutex
/// has thread affinity and throws when released off-thread; a semaphore does not.
///
/// Each test uses a fresh <see cref="Guid"/> so the Global\ named object can never collide with a
/// sibling test or a real running instance of the app.
/// </summary>
public class JobMutexTests
{
    [Fact]
    public void TryAcquire_Uncontended_ShouldRun()
    {
        using var lease = JobMutex.TryAcquire(Guid.NewGuid(), "Test");

        lease.ShouldRun.Should().BeTrue();
        lease.WasBusy.Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_WhileHeld_SecondCallReportsBusy()
    {
        var id = Guid.NewGuid();
        using var first = JobMutex.TryAcquire(id, "Test");

        using var second = JobMutex.TryAcquire(id, "Test");

        second.WasBusy.Should().BeTrue();
        second.ShouldRun.Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_AfterRelease_CanReacquire()
    {
        var id = Guid.NewGuid();

        var first = JobMutex.TryAcquire(id, "Test");
        first.ShouldRun.Should().BeTrue();
        first.Dispose();

        using var second = JobMutex.TryAcquire(id, "Test");
        second.ShouldRun.Should().BeTrue("the lock is free once the prior lease is disposed");
        second.WasBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Lease_AcquiredOnOneThread_ReleasedOnAnother_LeavesLockReusable()
    {
        // This is the regression guard. Acquire on the calling thread, then dispose from a different
        // thread — exactly what happens when ExecuteJobAsync's await chain resumes off-thread before
        // the finally runs. With a thread-affine mutex this release path misbehaves; with a semaphore
        // it's clean, and the lock must be immediately re-acquirable afterwards.
        var id = Guid.NewGuid();
        var lease = JobMutex.TryAcquire(id, "Test");
        lease.ShouldRun.Should().BeTrue();

        var acquiringThread = Environment.CurrentManagedThreadId;
        await Task.Run(() =>
        {
            Environment.CurrentManagedThreadId.Should().NotBe(acquiringThread,
                "the test only proves something if the release really happens on another thread");
            lease.Dispose();
        });

        using var reacquired = JobMutex.TryAcquire(id, "Test");
        reacquired.ShouldRun.Should().BeTrue();
        reacquired.WasBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Lease_HeldAcrossAwaitContinuation_ReleasesCleanly()
    {
        // Mirrors the scheduler's actual usage shape: take the lease, await work that hops threads,
        // then dispose in a finally on the continuation thread.
        var id = Guid.NewGuid();
        var lease = JobMutex.TryAcquire(id, "Test");
        try
        {
            lease.ShouldRun.Should().BeTrue();
            await Task.Yield();
            await Task.Delay(10);
        }
        finally
        {
            lease.Dispose();
        }

        using var next = JobMutex.TryAcquire(id, "Test");
        next.ShouldRun.Should().BeTrue();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var lease = JobMutex.TryAcquire(Guid.NewGuid(), "Test");

        var act = () => { lease.Dispose(); lease.Dispose(); };

        act.Should().NotThrow();
    }
}
