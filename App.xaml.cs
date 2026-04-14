using BeetsBackup.Helpers;
using BeetsBackup.Services;
using BeetsBackup.ViewModels;
using BeetsBackup.Views;
using Microsoft.Extensions.DependencyInjection;
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

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSignal;
    private CancellationTokenSource? _showSignalCts;

    /// <summary>
    /// Initializes the application: enforces single-instance, registers services,
    /// loads settings, handles missed backups, and starts the scheduler.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Create the signal that a second instance can use to wake us
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "BeetsBackup_ShowWindow_Signal");
        _showSignalCts = new CancellationTokenSource();
        StartShowSignalListener();

        // Global exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        FileLogger.Info("═══ Application starting ═══");

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
        var missedJobs = scheduler.GetMissedJobs();
        bool hasMissedBackups = missedJobs.Count > 0;

        // Launch to tray only when auto-started by Windows (--startup flag from shortcut)
        bool isAutoStartup = Environment.GetCommandLineArgs().Contains("--startup", StringComparer.OrdinalIgnoreCase);
        if (isAutoStartup && !hasMissedBackups)
        {
            // Don't show window — it's already in the tray via InitializeTrayIcon
            FileLogger.Info("Launched to system tray (auto-startup, no missed backups)");
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
            Services.GetRequiredService<BackupLogService>().Load();

            var scheduler = Services.GetRequiredService<SchedulerService>();
            var ran = scheduler.RunJobByIdAsync(jobId).GetAwaiter().GetResult();

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
            try { (Services as IDisposable)?.Dispose(); } catch { /* best effort */ }
            Current.Shutdown(Environment.ExitCode);
        }
    }

    /// <summary>
    /// Disposes services with a 5-second timeout, releases the single-instance mutex,
    /// and cleans up the inter-process show signal.
    /// </summary>
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        FileLogger.Info("═══ Application shutting down ═══");
        // Dispose services on a background thread with a timeout to avoid
        // deadlocking the UI thread if a scheduler job is in progress
        try
        {
            var disposeTask = Task.Run(() => (Services as IDisposable)?.Dispose());
            if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                FileLogger.Warn("Service disposal timed out after 5 seconds");
        }
        catch (Exception ex) { FileLogger.LogException("Error disposing services", ex); }
        _showSignalCts?.Cancel();
        _showSignal?.Dispose();
        _showSignalCts?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* Mutex not owned — second instance path */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
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
    /// Polls a named <see cref="EventWaitHandle"/> so a second app instance
    /// can signal us to bring our window to the foreground.
    /// </summary>
    private void StartShowSignalListener()
    {
        var cts = _showSignalCts!;
        var signal = _showSignal!;
        Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    bool signaled = signal.WaitOne(1000);
                    if (cts.IsCancellationRequested) break;
                    if (!signaled) continue; // timeout, not a signal
                    Dispatcher.BeginInvoke(() =>
                    {
                        var mainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
                        if (mainWindow != null)
                        {
                            mainWindow.Show();
                            mainWindow.WindowState = System.Windows.WindowState.Normal;
                            mainWindow.Activate();
                        }
                    });
                }
                catch (ObjectDisposedException) { break; }
            }
        });
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

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views
        services.AddTransient<MainWindow>();
    }
}
