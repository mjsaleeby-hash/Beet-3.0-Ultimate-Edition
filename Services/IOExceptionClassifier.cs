using System.IO;

namespace BeetsBackup.Services;

/// <summary>
/// Classifies the two <see cref="IOException"/> conditions the transfer engine handles
/// specially. These checks were previously written out by hand at nine sites in
/// TransferService with raw hex literals and no explanation of the mask.
///
/// FILTER SAFETY — read before adding anything here. Every caller uses these inside a
/// <c>catch (...) when (...)</c> exception filter. A filter that THROWS is silently treated as
/// <c>false</c> by the CLR, which would turn a handled disk-full into an unhandled crash. Both
/// predicates are integer arithmetic on a property that cannot throw, and must stay that way:
/// no I/O, no allocation, no logging in this class.
/// </summary>
internal static class IOExceptionClassifier
{
    // A Win32 error surfaces as an HRESULT of 0x8007xxxx (FACILITY_WIN32), so masking the low
    // 16 bits recovers the raw Win32 code. Callers may also see a bare code, which the same
    // mask passes through unchanged.
    private const int Win32CodeMask = 0xFFFF;

    /// <summary>ERROR_SHARING_VIOLATION — another process holds the file open.</summary>
    private const int ErrorSharingViolation = 0x0020;

    /// <summary>ERROR_HANDLE_DISK_FULL — the destination volume is out of space.</summary>
    private const int ErrorHandleDiskFull = 0x0070;

    /// <summary>
    /// Whether this exception means the file is locked by another process. Retryable: the
    /// engine's response is to fall back to a VSS snapshot.
    /// </summary>
    internal static bool IsSharingViolation(IOException ex)
        => (ex.HResult & Win32CodeMask) == ErrorSharingViolation;

    /// <summary>
    /// Whether this exception means the destination volume is full. NOT retryable — the engine
    /// records it and moves on rather than re-attempting a copy that cannot succeed.
    /// </summary>
    internal static bool IsDiskFull(IOException ex)
        => (ex.HResult & Win32CodeMask) == ErrorHandleDiskFull;
}
