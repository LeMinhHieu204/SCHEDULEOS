using Microsoft.EntityFrameworkCore;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Abstractions;

/// <summary>Persistence boundary used by the application services.</summary>
public interface IAppDbContext : IDisposable, IAsyncDisposable
{
    DbSet<Business> Businesses { get; }
    DbSet<Location> Locations { get; }
    DbSet<BusinessHours> BusinessHours { get; }
    DbSet<BusinessHoliday> BusinessHolidays { get; }
    DbSet<BusinessSetting> Settings { get; }
    DbSet<AppUser> Users { get; }
    DbSet<Customer> Customers { get; }
    DbSet<CustomerTag> CustomerTags { get; }
    DbSet<StaffMember> Staff { get; }
    DbSet<StaffSkill> StaffSkills { get; }
    DbSet<StaffShift> StaffShifts { get; }
    DbSet<StaffTimeOff> StaffTimeOff { get; }
    DbSet<ServiceCategory> ServiceCategories { get; }
    DbSet<Service> Services { get; }
    DbSet<ServiceResourceRequirement> ServiceResourceRequirements { get; }
    DbSet<Resource> Resources { get; }
    DbSet<ResourceMaintenance> ResourceMaintenance { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<AppointmentSegment> AppointmentSegments { get; }
    DbSet<AppointmentEvent> AppointmentEvents { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<Reminder> Reminders { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<NumberSequence> NumberSequences { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IAppDbContextFactory
{
    /// <summary>Creates a context for read-only queries (no tracking by default).</summary>
    IAppDbContext CreateReadContext();
}

/// <summary>
/// Runs a unit of work inside a serialised SQLite write transaction (BEGIN IMMEDIATE) with bounded
/// busy retries. Translates provider specific failures (busy, concurrency, trigger aborts) into domain exceptions.
/// </summary>
public interface IWriteExecutor
{
    Task<T> ExecuteAsync<T>(Func<IAppDbContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}

public static class WriteExecutorExtensions
{
    public static Task ExecuteAsync(this IWriteExecutor executor, Func<IAppDbContext, CancellationToken, Task> work, CancellationToken ct = default) =>
        executor.ExecuteAsync<bool>(async (db, c) =>
        {
            await work(db, c);
            return true;
        }, ct);
}

public interface IClock
{
    DateTime Now { get; }
    DateOnly Today => DateOnly.FromDateTime(Now);
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

/// <summary>Raised after successful commits so open views can refresh.</summary>
public interface IDataChangeNotifier
{
    event EventHandler<DataChangedEventArgs>? DataChanged;
    void Publish(DataArea area);
}

[Flags]
public enum DataArea
{
    None = 0,
    Appointments = 1,
    Customers = 2,
    Staff = 4,
    Services = 8,
    Resources = 16,
    Payments = 32,
    Notifications = 64,
    Settings = 128,
    Users = 256,
    All = 0x7FFFFFFF,
}

public sealed class DataChangedEventArgs(DataArea area) : EventArgs
{
    public DataArea Area { get; } = area;
}

public sealed class DataChangeNotifier : IDataChangeNotifier
{
    public event EventHandler<DataChangedEventArgs>? DataChanged;
    public void Publish(DataArea area) => DataChanged?.Invoke(this, new DataChangedEventArgs(area));
}
