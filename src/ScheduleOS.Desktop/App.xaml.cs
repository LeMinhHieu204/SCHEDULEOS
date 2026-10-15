using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Desktop.ViewModels;
using ScheduleOS.Desktop.ViewModels.Pages;
using ScheduleOS.Infrastructure;
using Serilog;
using Serilog.Events;

namespace ScheduleOS.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _instanceMutex;
    private static string[] _args = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        var dataDir = ArgValue(e.Args, "--data-dir");
        var paths = AppPaths.Resolve(dataDir);
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogDirectory, "scheduleos-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // One running instance per data directory keeps SQLite access on a single workstation process.
        var mutexName = "Local\\ScheduleOS-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16];
        _instanceMutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ScheduleOS is already running. Switch to the open window.", "ScheduleOS", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(b => b.ClearProviders().AddSerilog(dispose: false));
                services.AddScheduleOSApplication();
                services.AddScheduleOSInfrastructure(paths);
                AddDesktop(services);
            })
            .Build();
        Ui.Services = _host.Services;
        await _host.StartAsync();

        Log.Information("ScheduleOS starting (data: {DataRoot})", paths.Root);
        var shell = _host.Services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        MainWindow = window;
        window.Show();
        await shell.StartAsync();
    }

    private static void AddDesktop(IServiceCollection services)
    {
        services.AddSingleton<ToastService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ErrorHandler>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<AppState>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IdleMonitor>();
        services.AddSingleton<FileDialogs>();
        services.AddSingleton<PrintService>();
        services.AddSingleton<Workflows>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<CalendarViewModel>();
        services.AddTransient<AppointmentsViewModel>();
        services.AddTransient<AppointmentDetailViewModel>();
        services.AddTransient<CustomersViewModel>();
        services.AddTransient<CustomerDetailViewModel>();
        services.AddTransient<StaffViewModel>();
        services.AddTransient<ServicesViewModel>();
        services.AddTransient<ResourcesViewModel>();
        services.AddTransient<PaymentsViewModel>();
        services.AddTransient<AnalyticsViewModel>();
        services.AddTransient<NotificationsViewModel>();
        services.AddTransient<SettingsViewModel>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        try
        {
            _ = Ui.Errors.HandleAsync(e.Exception);
        }
        catch (Exception inner)
        {
            Log.Fatal(inner, "Error handler failed");
            MessageBox.Show("An unexpected error occurred. Details were written to the log file.", "ScheduleOS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host shutdown failed");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Log.Information("ScheduleOS stopped");
            await Log.CloseAndFlushAsync();
            ReleaseMutex();
            base.OnExit(e);
        }
    }

    private void ReleaseMutex()
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    /// <summary>Restarts the application (used after a database restore).</summary>
    public static void Restart()
    {
        var app = (App)Current;
        app.ReleaseMutex();
        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe, string.Join(' ', _args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))) { UseShellExecute = false });
        Current.Shutdown();
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(name.Length + 1)..];
        }

        return null;
    }
}

/// <summary>Writes WPF data-binding errors to the diagnostic log so UI defects are visible outside the debugger.</summary>
internal sealed class BindingErrorListener : System.Diagnostics.TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) Log.Warning("WPF binding: {Message}", message);
    }
}
