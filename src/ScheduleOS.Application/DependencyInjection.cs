using Microsoft.Extensions.DependencyInjection;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Catalog;
using ScheduleOS.Application.Customers;
using ScheduleOS.Application.Notifications;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Reporting;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;

namespace ScheduleOS.Application;

public static class DependencyInjection
{
    /// <summary>Registers application services. A desktop app has one user session, so services are singletons that open a DbContext per operation.</summary>
    public static IServiceCollection AddScheduleOSApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();
        services.AddSingleton<UserSession>();
        services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<UserSession>());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<SchedulingService>();
        services.AddSingleton<AppointmentService>();
        services.AddSingleton<CustomerService>();
        services.AddSingleton<StaffService>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<ResourceService>();
        services.AddSingleton<LocationService>();
        services.AddSingleton<PaymentService>();
        services.AddSingleton<InvoiceService>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<AnalyticsService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<AuditService>();
        return services;
    }
}
