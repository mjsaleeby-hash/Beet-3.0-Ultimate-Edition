using System.Runtime.InteropServices;

namespace BeetsBackup.Services;

/// <summary>
/// Prevents Windows from sleeping or turning off the display while a long-running
/// operation (backup, compression, extraction) is in progress. Wrap any transfer
/// entry point with <c>using var _ = PowerManagement.KeepSystemAwake();</c>.
///
/// Without this, unattended overnight backups will silently fail when Windows hits
/// its idle sleep timeout — the destination drive spins down or a network share
/// disconnects, and the in-flight copy fails with "file in use" or "path not found".
/// </summary>
internal static class PowerManagement
{
    [Flags]
    private enum ExecutionState : uint
    {
        /// <summary>Keep the flag active until explicitly cleared.</summary>
        Continuous = 0x80000000,
        /// <summary>Keep the system in the working state (prevents system idle sleep).</summary>
        SystemRequired = 0x00000001,
        /// <summary>Keep the display on (prevents display sleep).</summary>
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>
    /// Enables the system-required power hint on the calling thread. The hint is released
    /// when the returned <see cref="IDisposable"/> is disposed (typically at the end of a
    /// <c>using</c> block around the whole backup operation).
    /// </summary>
    public static IDisposable KeepSystemAwake()
    {
        SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
        return new ReleaseScope();
    }

    private sealed class ReleaseScope : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            SetThreadExecutionState(ExecutionState.Continuous);
        }
    }
}
