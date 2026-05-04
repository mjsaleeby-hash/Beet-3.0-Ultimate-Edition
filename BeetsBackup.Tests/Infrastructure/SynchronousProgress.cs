namespace BeetsBackup.Tests.Infrastructure;

/// <summary>
/// Test-only <see cref="IProgress{T}"/> that invokes the supplied action inline on the
/// reporting thread, instead of posting to the thread pool the way <see cref="Progress{T}"/>
/// does. Eliminates the callback-ordering race and post-test latency that make assertions
/// against <see cref="Progress{T}"/> flaky in a parallel-worker pipeline.
/// </summary>
/// <remarks>
/// The action runs on whatever thread called <see cref="Report"/>. If the production code
/// reports from multiple threads, the action must be thread-safe.
/// </remarks>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;

    public SynchronousProgress(Action<T> action) => _action = action;

    public void Report(T value) => _action(value);
}
