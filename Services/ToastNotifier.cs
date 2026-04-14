using System.Windows.Forms;

namespace BeetsBackup.Services;

/// <summary>
/// Severity categories for transfer-completion toast notifications.
/// </summary>
public enum ToastKind
{
    /// <summary>Backup finished successfully.</summary>
    Success,
    /// <summary>Backup finished but encountered per-file errors.</summary>
    Warning,
    /// <summary>Backup failed outright (cancelled, out of space, unhandled exception).</summary>
    Error
}

/// <summary>
/// Thin global entry point for firing Windows balloon/toast notifications via the existing tray icon.
/// MainWindow registers the tray <see cref="NotifyIcon"/> at startup; viewmodels and services call
/// <see cref="Notify"/> to show a completion toast without needing to know how the tray icon is wired.
/// </summary>
/// <remarks>
/// Uses <see cref="NotifyIcon.ShowBalloonTip(int, string, string, ToolTipIcon)"/> rather than the WinRT
/// ToastNotificationManager to avoid the AppUserModelId / manifest requirements of modern toasts.
/// Balloon tips still render as native Windows 10/11 toasts when the app has a tray icon.
/// </remarks>
public static class ToastNotifier
{
    /// <summary>The tray <see cref="NotifyIcon"/> used to render the toast. Set once by <c>MainWindow</c>.</summary>
    public static NotifyIcon? TrayIcon { get; set; }

    /// <summary>Shows a balloon/toast notification with the given title, message, and severity.</summary>
    /// <param name="title">Short heading shown on the first line of the toast.</param>
    /// <param name="message">Body text shown on the second line.</param>
    /// <param name="kind">Severity — controls the icon shown next to the message.</param>
    /// <param name="timeoutMs">How long the toast should stay up (Windows may clamp this). Defaults to 5000 ms.</param>
    public static void Notify(string title, string message, ToastKind kind = ToastKind.Success, int timeoutMs = 5000)
    {
        var icon = TrayIcon;
        if (icon == null) return;

        var toolTipIcon = kind switch
        {
            ToastKind.Error => ToolTipIcon.Error,
            ToastKind.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info
        };

        try { icon.ShowBalloonTip(timeoutMs, title, message, toolTipIcon); }
        catch { /* Toast is advisory — never let a notification failure surface */ }
    }
}
