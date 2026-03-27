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

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard
        _singleInstanceMutex = new Mutex(true, "BeetsBackup_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Beet's Backup is already running.", "Beet's Backup",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Current.Shutdown();
            return;
        }

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
        mainWindow.Show();

        // Check for missed backups before starting the scheduler
        var scheduler = Services.GetRequiredService<SchedulerService>();
        var missedJobs = scheduler.GetMissedJobs();
        if (missedJobs.Count > 0)
        {
            FileLogger.Info($"Detected {missedJobs.Count} missed backup(s)");
            var dialog = new MissedBackupsDialog(scheduler, missedJobs);
            dialog.Owner = mainWindow;
            dialog.ShowDialog();

            if (!dialog.RunNow)
                FileLogger.Info("User skipped missed backups");
        }

        scheduler.Start();
        FileLogger.Info("Application started successfully");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        FileLogger.Info("═══ Application shutting down ═══");
        try { _singleInstanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* Mutex not owned — second instance path */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.WriteCrashDump("DispatcherUnhandledException", e.Exception);
        e.Handled = true; // Prevent crash on recoverable UI errors
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            FileLogger.WriteCrashDump("AppDomain.UnhandledException", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLogger.WriteCrashDump("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<FileSystemService>();
        services.AddSingleton<TransferService>();
        services.AddSingleton<BackupLogService>();
        services.AddSingleton<SchedulerService>();

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views
        services.AddTransient<MainWindow>();
    }
}
