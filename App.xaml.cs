using BeetsBackup.Helpers;
using BeetsBackup.Services;
using BeetsBackup.ViewModels;
using BeetsBackup.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace BeetsBackup;

/// <summary>
/// Application entry point. Configures DI, enforces single-instance via a named mutex,
/// applies saved theme/settings, and starts the backup scheduler.
/// </summary>
public partial class App : Application
{
    /// <summary>Gets the application-wide DI service provider.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// When the previous session ended without firing <c>OnExit</c> (crash, kill, power loss),
    /// this holds the timestamp the previous session started. Read once by <see cref="MainWindow"/>
    /// to surface a recovery banner; null on a clean prior shutdown.
    /// </summary>
    public static DateTime? PreviousUncleanShutdownAt { get; private set; }

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSignal;
    // RegisteredWaitHandle fires an OS callback when _showSignal is set, instead of a
    // polling Task.Run that wakes every second. Disposed in OnExit via Unregister.
    private RegisteredWaitHandle? _showSignalRegistration;

    /// <summary>
    /// Shared AppUserModelID. Both this elevated process and the non-elevated launcher stub
    /// (BeetsBackupLauncher.exe) tag themselves with this so Windows groups them in a single
    /// taskbar slot — the pinned launcher icon picks up the running-app indicator from the
    /// main window.
    /// </summary>
    private const string AppUserModelId = "BeetSoftware.BeetsBackup";

    /// <summary>
    /// Initializes the application: enforces single-instance, registers services,
    /// loads settings, handles missed backups, and starts the scheduler.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Tag the process with the shared AUMID before any window is shown, so the taskbar
        // entry the WPF window registers itself under matches the launcher stub's icon.
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* AUMID grouping is cosmetic; never fail startup over it */ }

        // Headless path: Windows Task Scheduler launches `BeetsBackup.exe --run-job <guid>`
        // at the scheduled time. Skip the single-instance dance, skip the UI entirely,
        // run the job synchronously, then exit. This is what lets scheduled backups fire
        // when the main app isn't already running (Feature F).
        var runJobId = CliArgs.TryParseRunJob(e.Args);
        if (runJobId.HasValue)
        {
            RunHeadlessJob(runJobId.Value);
            return;
        }

        // Single-instance guard — if already running, signal the first instance to show its window
        _singleInstanceMutex = new Mutex(true, "BeetsBackup_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting("BeetsBackup_ShowWindow_Signal");
                signal.Set();
            }
            catch { /* first instance hasn't created the signal yet — rare race condition */ }
            Current.Shutdown();
            return;
        }

        // Create the signal that a second instance can use to wake us. Two cross-IL hurdles
        // need clearing so the non-elevated launcher (BeetsBackupLauncher.exe, Medium IL) can
        // Set() this event from outside our elevated (High IL) process:
        //   1. DACL — the default DACL on a kernel object created by an elevated process
        //      grants access only to the elevated owner (Administrators). The medium-IL
        //      caller is the same user but has Administrators as a deny-only SID, so it
        //      gets UnauthorizedAccessException without an explicit grant.
        //   2. Mandatory label — Windows' no-write-up policy blocks Medium IL from writing
        //      to a High IL object regardless of DACL. We lower the label to Low IL.
        _showSignal = CreateShowSignalCrossIL("BeetsBackup_ShowWindow_Signal");
        StartShowSignalListener();

        // Global exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        FileLogger.Info("═══ Application starting ═══");

        // Detect if the previous session ended uncleanly (crash, kill, power loss). Read this BEFORE
        // MarkRunning() so we don't immediately overwrite the prior session's sentinel.
        PreviousUncleanShutdownAt = DiagnosticsService.ConsumeUncleanShutdown();
        if (PreviousUncleanShutdownAt.HasValue)
            FileLogger.Warn($"Previous session did not exit cleanly (started {PreviousUncleanShutdownAt:yyyy-MM-dd HH:mm:ss})");
        DiagnosticsService.MarkRunning();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Load saved settings and apply theme before showing window
        var settings = Services.GetRequiredService<SettingsService>();
        settings.Load();
        Services.GetRequiredService<BackupLogService>().Load();
        Services.GetRequiredService<ThemeService>().ApplySaved();

        var mainWindow = Services.GetRequiredService<MainWindow>();

        // Check for missed backups before starting the scheduler
        var scheduler = Services.GetRequiredService<SchedulerService>();
        scheduler.Load();
        var missedJobs = scheduler.GetMissedJobs();
        bool hasMissedBackups = missedJobs.Count > 0;

        // Only suppress the window when ALL THREE conditions are met:
        //   1. Launched via the Windows Startup shortcut (--startup flag)
        //   2. User opted into "Start minimized" in Options
        //   3. No missed backups that need attention
        // Without the StartMinimized preference, hitting the exe always opens the window —
        // even from the Startup shortcut — so users aren't left wondering if the app launched.
        bool isAutoStartup = Environment.GetCommandLineArgs().Contains("--startup", StringComparer.OrdinalIgnoreCase);
        bool startMinimized = settings.StartMinimized;
        if (isAutoStartup && startMinimized && !hasMissedBackups)
        {
            FileLogger.Info("Launched to system tray (auto-startup, start-minimized enabled, no missed backups)");
        }
        else
        {
            mainWindow.Show();
        }

        if (hasMissedBackups)
        {
            FileLogger.Info($"Detected {missedJobs.Count} missed backup(s)");
            var dialog = new MissedBackupsDialog(scheduler, missedJobs);
            dialog.Owner = mainWindow;
            dialog.ShowDialog();

            if (dialog.RunNow)
                scheduler.RunMissedJobs(missedJobs);
            else
                FileLogger.Info("User skipped missed backups");
        }

        scheduler.Start();

        // Re-register saved jobs with Windows Task Scheduler (Feature F) so they fire even
        // when the app is closed. Runs on a background thread because each schtasks.exe
        // invocation waits on a child process and we don't want to stall the UI.
        _ = Task.Run(() =>
        {
            try { scheduler.ReconcileWindowsTasks(); }
            catch (Exception ex) { FileLogger.LogException("Windows Task Scheduler reconciliation failed", ex); }
        });

        FileLogger.Info("Application started successfully");

        // Check for updates in the background (non-blocking)
        _ = CheckForUpdatesAsync(mainWindow);
    }

    /// <summary>
    /// Runs a single scheduled job in a minimal headless context: DI services are constructed
    /// but no window is shown and no scheduler loop is started. Exits as soon as the job completes.
    /// </summary>
    private void RunHeadlessJob(Guid jobId)
    {
        try
        {
            FileLogger.Info($"=== Headless run: job {jobId} ===");

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            Services.GetRequiredService<SettingsService>().Load();
            // Switch the log service into headless mode BEFORE Load(): every mutation must
            // run synchronously, not be posted to a dispatcher that will never pump while we
            // sit in GetAwaiter().GetResult() below. Without this the run finishes cleanly
            // but no Running/Complete entry ever lands in backup_log.json.
            var log = Services.GetRequiredService<BackupLogService>();
            log.MarkHeadless();
            log.Load();

            var scheduler = Services.GetRequiredService<SchedulerService>();
            scheduler.Load();
            // Run the async work on a thread-pool thread rather than blocking the WPF dispatcher
            // directly. Without the outer Task.Run, any await inside RunJobByIdAsync that captured
            // the dispatcher SynchronizationContext would deadlock when it tried to resume — the
            // dispatcher thread is sitting in GetResult() and can't drain its queue.
            var ran = Task.Run(() => scheduler.RunJobByIdAsync(jobId)).GetAwaiter().GetResult();

            FileLogger.Info($"=== Headless run complete: {(ran ? "ran" : "job not found")} ===");
            Environment.ExitCode = ran ? 0 : 2;
        }
        catch (Exception ex)
        {
            FileLogger.LogException("Headless job run failed", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            // Bounded dispose: if a service hangs, don't hold the process open waiting forever.
            try
            {
                var disposeTask = Task.Run(() => (Services as IDisposable)?.Dispose());
                if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                    FileLogger.Warn("Headless service disposal timed out after 5 seconds");
            }
            catch (Exception ex) { FileLogger.LogException("Error disposing services (headless)", ex); }
            DiagnosticsService.MarkExitedCleanly();
            // Environment.Exit hard-kills the process. In the headless path there is no UI,
            // so WPF's Shutdown() (which posts to the dispatcher) can leave a zombie if the
            // dispatcher never drains. Environment.Exit guarantees the process terminates.
            Environment.Exit(Environment.ExitCode);
        }
    }

    /// <summary>
    /// Arms a background thread that force-kills the process after <paramref name="timeout"/>.
    /// This is a belt-and-suspenders failsafe: if <see cref="Environment.Exit"/> itself hangs
    /// (stuck finalizer, blocked COM RCW release, etc.), the watchdog guarantees termination so
    /// the user is never left with an invisible zombie Beet process.
    /// </summary>
    private static void ArmShutdownWatchdog(TimeSpan timeout, string caller)
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(timeout);
            try { FileLogger.Warn($"Shutdown watchdog firing ({caller}) — force-killing process after {timeout.TotalSeconds:0}s"); }
            catch { /* logging may itself be broken during shutdown */ }
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { /* last resort — nothing more to do */ }
        })
        {
            IsBackground = true,
            Name = "Beet-ShutdownWatchdog"
        };
        t.Start();
    }

    /// <summary>
    /// Disposes services with a 5-second timeout, releases the single-instance mutex,
    /// and cleans up the inter-process show signal.
    /// </summary>
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        FileLogger.Info("═══ Application shutting down ═══");
        // Arm the watchdog before we start disposing anything. If Environment.Exit hangs at the
        // bottom of this method (stuck finalizer, COM RCW release blocking), the watchdog kills
        // the process so the user never sees an invisible zombie Beet in Task Manager.
        ArmShutdownWatchdog(TimeSpan.FromSeconds(15), "OnExit");
        // Dispose services on a background thread with a timeout to avoid
        // deadlocking the UI thread if a scheduler job is in progress
        try
        {
            var disposeTask = Task.Run(() => (Services as IDisposable)?.Dispose());
            if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                FileLogger.Warn("Service disposal timed out after 5 seconds");
        }
        catch (Exception ex) { FileLogger.LogException("Error disposing services", ex); }
        // Unregister before disposing the event: a callback in flight after disposal
        // would hit an ObjectDisposedException. Passing the handle makes Unregister block
        // until any in-flight callback completes.
        _showSignalRegistration?.Unregister(_showSignal);
        _showSignal?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* Mutex not owned — second instance path */ }
        _singleInstanceMutex?.Dispose();
        DiagnosticsService.MarkExitedCleanly();
        base.OnExit(e);
        // Failsafe: if WPF's dispatcher loop doesn't exit cleanly (rare edge cases with
        // background threads or WinForms interop), force-terminate the process.
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>Logs UI-thread exceptions and marks them handled to prevent a crash.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.WriteCrashDump("DispatcherUnhandledException", e.Exception);
        e.Handled = true; // Prevent crash on recoverable UI errors
    }

    /// <summary>Logs fatal unhandled exceptions from non-UI threads.</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            FileLogger.WriteCrashDump("AppDomain.UnhandledException", ex);
    }

    /// <summary>Logs and observes unobserved Task exceptions to prevent process termination.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLogger.WriteCrashDump("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// Registers an OS-level wait on the named show-signal so a second app instance
    /// can bring our window to the foreground. The callback fires on a thread-pool
    /// thread only when the event is set — no polling loop or dedicated thread.
    /// </summary>
    private void StartShowSignalListener()
    {
        var signal = _showSignal!;
        _showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(
            signal,
            static (state, timedOut) =>
            {
                if (timedOut) return;
                var app = (App)state!;
                app.Dispatcher.BeginInvoke(() =>
                {
                    var mainWindow = app.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (mainWindow != null)
                    {
                        mainWindow.Show();
                        mainWindow.WindowState = System.Windows.WindowState.Normal;
                        mainWindow.Activate();
                    }
                });
            },
            state: this,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Waits briefly for the UI to load, then triggers a background update check.</summary>
    private static async Task CheckForUpdatesAsync(MainWindow mainWindow)
    {
        try
        {
            // Small delay so the UI has time to fully load
            await Task.Delay(3000);
            var vm = mainWindow.DataContext as MainViewModel;
            if (vm != null)
                await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            FileLogger.Info($"Background update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the named auto-reset event used by <c>BeetsBackupLauncher.exe</c> to surface
    /// the running tray window. Applies an explicit DACL (Authenticated Users → Modify+Synchronize)
    /// so a non-elevated caller can open it, and lowers the mandatory integrity label to Low so
    /// no-write-up doesn't block the cross-IL Set(). On any failure, falls back to a plain event;
    /// the launcher then takes its cold-start path (UAC fires once).
    /// </summary>
    private static EventWaitHandle CreateShowSignalCrossIL(string name)
    {
        try
        {
            var rule = new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow);
            var sec = new EventWaitHandleSecurity();
            sec.AddAccessRule(rule);

            var handle = EventWaitHandleAcl.Create(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: name,
                createdNew: out _,
                eventSecurity: sec);

            TryLowerMandatoryLabel(handle.SafeWaitHandle);
            return handle;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Cross-IL show-signal create failed, falling back to default DACL: {ex.Message}");
            var fallback = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            TryLowerMandatoryLabel(fallback.SafeWaitHandle);
            return fallback;
        }
    }

    /// <summary>
    /// Replaces the show-window event's mandatory integrity SACL with a Low label so the
    /// non-elevated <c>BeetsBackupLauncher.exe</c> stub (Medium IL) can <c>Set()</c> the event
    /// from across the integrity boundary. Failures are logged and swallowed — if this can't
    /// be applied, the launcher falls back to starting a new BeetsBackup.exe instance.
    /// </summary>
    private static void TryLowerMandatoryLabel(SafeHandle eventHandle)
    {
        // SDDL: low mandatory label, no-write-up flag set. Effect — any process at Low IL
        // or above can write (Set/Reset) the event; nothing below Low (which is essentially
        // nothing in normal user sessions) is locked out.
        const string sddl = "S:(ML;;NW;;;LW)";
        IntPtr sdPtr = IntPtr.Zero;

        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out sdPtr, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            if (!GetSecurityDescriptorSacl(sdPtr, out _, out var saclPtr, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            uint result = SetSecurityInfo(
                eventHandle.DangerousGetHandle(),
                SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, saclPtr);

            if (result != 0)
                throw new System.ComponentModel.Win32Exception((int)result);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Could not lower mandatory label on show-window event: {ex.Message}");
        }
        finally
        {
            if (sdPtr != IntPtr.Zero) LocalFree(sdPtr);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        int stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
        out IntPtr sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        SE_OBJECT_TYPE objectType,
        SECURITY_INFORMATION securityInfo,
        IntPtr sidOwner,
        IntPtr sidGroup,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private enum SE_OBJECT_TYPE
    {
        SE_KERNEL_OBJECT = 6,
    }

    [Flags]
    private enum SECURITY_INFORMATION : uint
    {
        LABEL_SECURITY_INFORMATION = 0x00000010,
    }

    /// <summary>Registers all services, view models, and views in the DI container.</summary>
    private static void ConfigureServices(ServiceCollection services)
    {
        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<FileSystemService>();
        services.AddSingleton<TransferService>();
        services.AddSingleton<BackupLogService>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<UpdateService>();
        // Lazy wrapper: defers UpdateService construction until something actually
        // touches `.Value` (the post-startup update check, ~3 s after launch).
        services.AddSingleton<Lazy<UpdateService>>(sp =>
            new Lazy<UpdateService>(() => sp.GetRequiredService<UpdateService>()));

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views
        services.AddTransient<MainWindow>();
    }
}
