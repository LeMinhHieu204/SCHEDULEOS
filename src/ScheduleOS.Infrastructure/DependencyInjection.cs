using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Payments;
using ScheduleOS.Infrastructure.Backup;
using ScheduleOS.Infrastructure.Documents;
using ScheduleOS.Infrastructure.External;
using ScheduleOS.Infrastructure.Persistence;
using ScheduleOS.Infrastructure.Seeding;

namespace ScheduleOS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddScheduleOSInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(_ => new SqliteDbContextFactory(paths.ConnectionString()));
        services.AddSingleton<IAppDbContextFactory>(sp => sp.GetRequiredService<SqliteDbContextFactory>());
        services.AddSingleton<SqliteWriteExecutor>();
        services.AddSingleton<IWriteExecutor>(sp => sp.GetRequiredService<SqliteWriteExecutor>());
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IDatabaseInfo, DatabaseInfo>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<IBackupService>(sp => sp.GetRequiredService<BackupService>());
        services.AddSingleton<IDemoDataService, DemoDataService>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<ISmsSender, UnconfiguredSmsSender>();
        services.AddSingleton<IDocumentPdfWriter, PdfDocumentWriter>();
        services.AddSingleton<IReportExporter, ReportExporter>();
        services.AddSingleton<AppReadySignal>();
        services.AddHostedService<BackgroundJobs>();
        return services;
    }
}

/// <summary>Released by the UI once the database is initialised and the workspace exists.</summary>
public sealed class AppReadySignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WaitAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);
    public void Signal() => _tcs.TrySetResult();
}

/// <summary>Processes the reminder queue every 30 seconds and runs automatic backups when due.</summary>
public sealed class BackgroundJobs(AppReadySignal ready, ReminderService reminders, IBackupService backups, ILogger<BackgroundJobs> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ready.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var lastBackupCheck = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await reminders.ProcessDueAsync(stoppingToken);
                if (DateTime.Now - lastBackupCheck > TimeSpan.FromMinutes(30))
                {
                    lastBackupCheck = DateTime.Now;
                    await backups.RunAutomaticBackupIfDueAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
