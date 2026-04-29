using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BeetsBackupLauncher;

/// <summary>
/// Tiny non-elevated stub pinned to the taskbar in place of BeetsBackup.exe.
///
/// The main app's manifest is <c>requireAdministrator</c> (VSS needs an elevated token),
/// which means Windows fires UAC every time the user clicks the pinned exe — even when
/// Beet is already running in the system tray. The single-instance check inside the app
/// happens AFTER UAC, so it can't suppress the prompt.
///
/// This launcher runs as <c>asInvoker</c> (no UAC) and does one of two things:
///   1. If BeetsBackup is already running, open the existing show-window event and Set()
///      it. The running app's wait-handle listener brings its window to the foreground.
///   2. If not running, start BeetsBackup.exe normally — UAC fires once for the cold
///      start, then never again while the app stays in the tray.
///
/// For step 1 to work across the integrity boundary (Medium IL launcher → High IL app),
/// the app creates the event with an explicit DACL granting Authenticated Users
/// EventModifyState+Synchronize, and lowers its mandatory label to Low IL.
/// </summary>
internal static class Program
{
    /// <summary>Must match the name used in App.xaml.cs.</summary>
    private const string ShowSignalName = "BeetsBackup_ShowWindow_Signal";

    /// <summary>Must match the AUMID set by App.xaml.cs so the taskbar groups both processes.</summary>
    private const string AppUserModelId = "BeetSoftware.BeetsBackup";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beet's Backup",
        "launcher.log");

    [STAThread]
    private static int Main()
    {
        Log("──── launcher start ────");
        try
        {
            return RunCore();
        }
        catch (Exception ex)
        {
            Log($"FATAL unhandled in Main: {ex.GetType().Name}: {ex.Message}");
            Log(ex.StackTrace ?? "(no stack)");
            return 99;
        }
    }

    private static int RunCore()
    {
        // Tag this process with the same AUMID the main app uses. Without this, Windows treats
        // the launcher and the main app as two unrelated taskbar entries — clicking the pinned
        // launcher would not animate the running app's window restore.
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch (Exception ex) { Log($"AUMID set failed (non-fatal): {ex.Message}"); }

        // Path 1 — Beet is already running. Open the named event and signal it.
        try
        {
            Log($"Path 1: OpenExisting('{ShowSignalName}')");
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            Log("Path 1: OpenExisting succeeded; calling Set()");
            signal.Set();
            Log("Path 1: Set() succeeded; exiting 0");
            // Environment.Exit short-circuits CLR finalizer teardown. Without this the
            // single-file host's post-Main cleanup occasionally hands back a spurious
            // ERROR_INVALID_NAME (123) as the process exit code even though Main returned 0.
            Environment.Exit(0);
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log("Path 1: signal does not exist — Beet is not running, falling through");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log($"Path 1: access denied across IL boundary: {ex.Message}");
            Log("Path 1: falling through to cold-start (will trigger UAC)");
        }
        catch (Exception ex)
        {
            // Catch IOException (Win32 error wrapped, exit code 123 territory),
            // ArgumentException, and any other surprise so the process never just dies.
            Log($"Path 1: unexpected {ex.GetType().Name}: {ex.Message}");
            Log("Path 1: falling through to cold-start");
        }

        // Path 2 — Beet isn't running (or we couldn't reach it). Start BeetsBackup.exe
        // from the same folder we live in.
        var launcherDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(launcherDir))
        {
            Log("Path 2: could not resolve launcher directory");
            return 2;
        }

        var exePath = Path.Combine(launcherDir, "BeetsBackup.exe");
        if (!File.Exists(exePath))
        {
            Log($"Path 2: BeetsBackup.exe not found at {exePath}");
            return 3;
        }

        try
        {
            Log($"Path 2: Process.Start('{exePath}') with UseShellExecute=true");
            // UseShellExecute=true so the UAC consent dialog appears (Process.Start with
            // UseShellExecute=false from a non-elevated process can't request elevation).
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = launcherDir
            };
            using var _ = Process.Start(psi);
            Log("Path 2: Process.Start succeeded; exiting 0");
            // See Path 1 note — bypass CLR teardown to keep the exit code clean.
            Environment.Exit(0);
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Path 2: Process.Start failed: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID {Environment.ProcessId}] {message}\n");
        }
        catch { /* logging must never throw */ }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
