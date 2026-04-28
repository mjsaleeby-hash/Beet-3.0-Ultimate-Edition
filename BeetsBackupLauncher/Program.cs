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
/// the app lowers the event's mandatory integrity label to Low at creation time.
/// </summary>
internal static class Program
{
    /// <summary>Must match the name used in App.xaml.cs.</summary>
    private const string ShowSignalName = "BeetsBackup_ShowWindow_Signal";

    /// <summary>Must match the AUMID set by App.xaml.cs so the taskbar groups both processes.</summary>
    private const string AppUserModelId = "BeetSoftware.BeetsBackup";

    [STAThread]
    private static int Main()
    {
        // Tag this process with the same AUMID the main app uses. Without this, Windows treats
        // the launcher and the main app as two unrelated taskbar entries — clicking the pinned
        // launcher would not animate the running app's window restore.
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* AUMID is cosmetic; never block on it */ }

        // Path 1 — Beet is already running. Open the named event and signal it.
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
            return 0;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Fall through to cold-start path.
        }
        catch (UnauthorizedAccessException)
        {
            // The running app didn't (or couldn't) lower the event's mandatory label.
            // Best we can do is start a new instance — UAC will fire, but the existing
            // one will absorb it via its own single-instance mutex.
        }

        // Path 2 — Beet isn't running. Start BeetsBackup.exe from the same folder we live in.
        var launcherDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(launcherDir))
            return 2;

        var exePath = Path.Combine(launcherDir, "BeetsBackup.exe");
        if (!File.Exists(exePath))
            return 3;

        try
        {
            // UseShellExecute=true so the UAC consent dialog appears (Process.Start with
            // UseShellExecute=false from a non-elevated process can't request elevation).
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = launcherDir
            };
            Process.Start(psi);
            return 0;
        }
        catch
        {
            return 4;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
