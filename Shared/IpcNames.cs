namespace BeetsBackup.Shared;

/// <summary>
/// Inter-process coordination names shared by the elevated main app and the
/// non-elevated launcher stub. These MUST be identical in both processes or
/// single-instance detection and taskbar grouping silently stop working (with
/// no compile error to warn you). Defined ONCE here and linked into both
/// executables so the two can never drift apart.
/// </summary>
public static class IpcNames
{
    /// <summary>Named mutex that enforces a single running instance of the app.</summary>
    public const string SingleInstanceMutex = "BeetsBackup_SingleInstance_Mutex";

    /// <summary>Named event the launcher signals to bring the running window forward.</summary>
    public const string ShowWindowSignal = "BeetsBackup_ShowWindow_Signal";

    /// <summary>AppUserModelID used for taskbar grouping across both executables.</summary>
    public const string AppUserModelId = "BeetSoftware.BeetsBackup";
}
