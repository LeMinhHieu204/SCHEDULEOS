using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Infrastructure.Persistence;

public sealed class ScheduleDbContext(DbContextOptions<ScheduleDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<BusinessHours> BusinessHours => Set<BusinessHours>();
    public DbSet<BusinessHoliday> BusinessHolidays => Set<BusinessHoliday>();
    public DbSet<BusinessSetting> Settings => Set<BusinessSetting>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerTag> CustomerTags => Set<CustomerTag>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<StaffSkill> StaffSkills => Set<StaffSkill>();
    public DbSet<StaffShift> StaffShifts => Set<StaffShift>();
    public DbSet<StaffTimeOff> StaffTimeOff => Set<StaffTimeOff>();
    public DbSet<ServiceCategory> ServiceCategories => Set<ServiceCategory>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<ServiceResourceRequirement> ServiceResourceRequirements => Set<ServiceResourceRequirement>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceMaintenance> ResourceMaintenance => Set<ResourceMaintenance>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentSegment> AppointmentSegments => Set<AppointmentSegment>();
    public DbSet<AppointmentEvent> AppointmentEvents => Set<AppointmentEvent>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Business>(e =>
        {
            e.ToTable("Businesses");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("CK_Business_Decimals", "CurrencyDecimals BETWEEN 0 AND 3"));
        });

        b.Entity<Location>(e => e.Property(x => x.Name).HasMaxLength(120).IsRequired());

        b.Entity<BusinessHours>(e =>
        {
            e.HasIndex(x => x.DayOfWeek).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_BusinessHours_Range", "IsOpen = 0 OR CloseTime > OpenTime"));
        });

        b.Entity<BusinessHoliday>(e => e.HasIndex(x => x.Date).IsUnique());

        b.Entity<BusinessSetting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        b.Entity<NumberSequence>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(40);
        });

        b.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.Username).HasMaxLength(80).IsRequired();
            e.Property(x => x.NormalizedUsername).HasMaxLength(80).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t => t.HasCheckConstraint("CK_User_Role", "Role BETWEEN 1 AND 4"));
        });

        b.Entity<Customer>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.FullName);
            e.HasIndex(x => x.NormalizedPhone);
            e.HasIndex(x => x.NormalizedEmail);
            e.HasOne(x => x.PreferredStaff).WithMany().HasForeignKey(x => x.PreferredStaffId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.Initials);
        });

        b.Entity<CustomerTag>(e =>
        {
            e.Property(x => x.Tag).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.CustomerId, x.Tag }).IsUnique();
            e.HasOne(x => x.Customer).WithMany(x => x.Tags).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StaffMember>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Position).HasMaxLength(80);
            e.Property(x => x.ColorHex).HasMaxLength(9);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.Initials);
            e.Ignore(x => x.IsBookable);
        });

        b.Entity<StaffSkill>(e =>
        {
            e.HasIndex(x => new { x.StaffMemberId, x.ServiceId }).IsUnique();
            e.HasOne(x => x.StaffMember).WithMany(x => x.Skills).HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Service).WithMany(x => x.QualifiedStaff).HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StaffShift>(e =>
        {
            e.HasOne(x => x.StaffMember).WithMany(x => x.Shifts).HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_Shift_Range", "EndTime > StartTime"));
        });

        b.Entity<StaffTimeOff>(e =>
        {
            e.HasOne(x => x.StaffMember).WithMany(x => x.TimeOff).HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.StaffMemberId, x.Start, x.End });
            e.ToTable(t => t.HasCheckConstraint("CK_TimeOff_Range", "\"End\" > \"Start\""));
        });

        b.Entity<ServiceCategory>(e => e.Property(x => x.Name).HasMaxLength(80).IsRequired());

        b.Entity<Service>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.IsBookable);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Service_Duration", "DurationMinutes > 0");
                t.HasCheckConstraint("CK_Service_Buffers", "PrepMinutes >= 0 AND CleanupMinutes >= 0");
                t.HasCheckConstraint("CK_Service_Price", "PriceMinor >= 0 AND DepositMinor >= 0 AND DepositMinor <= PriceMinor");
            });
        });

        b.Entity<ServiceResourceRequirement>(e =>
        {
            e.HasIndex(x => new { x.ServiceId, x.ResourceType }).IsUnique();
            e.HasOne(x => x.Service).WithMany(x => x.ResourceRequirements).HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Resource>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.IsBookable);
            e.ToTable(t => t.HasCheckConstraint("CK_Resource_Capacity", "Capacity >= 1"));
        });

        b.Entity<ResourceMaintenance>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(200);
            e.HasOne(x => x.Resource).WithMany(x => x.Maintenance).HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ResourceId, x.Start, x.End });
            e.ToTable(t => t.HasCheckConstraint("CK_Maintenance_Range", "\"End\" > \"Start\""));
        });

        b.Entity<Appointment>(e =>
        {
            e.Property(x => x.Reference).HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => x.SubmissionKey).IsUnique();
            e.HasIndex(x => x.Start);
            e.HasIndex(x => new { x.CustomerId, x.Start });
            e.HasIndex(x => x.Status);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Customer).WithMany(x => x.Appointments).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.Duration);
            e.Ignore(x => x.PaidMinor);
            e.Ignore(x => x.BalanceMinor);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Appointment_Range", "\"End\" >= \"Start\"");
                t.HasCheckConstraint("CK_Appointment_Money", "SubtotalMinor >= 0 AND DiscountMinor >= 0 AND TaxMinor >= 0 AND TotalMinor >= 0 AND DepositRequiredMinor >= 0");
                t.HasCheckConstraint("CK_Appointment_Status", "Status BETWEEN 0 AND 7");
            });
        });

        b.Entity<AppointmentSegment>(e =>
        {
            e.Property(x => x.ServiceName).HasMaxLength(120).IsRequired();
            e.HasOne(x => x.Appointment).WithMany(x => x.Segments).HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StaffMember).WithMany().HasForeignKey(x => x.StaffMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Resource).WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StaffMemberId, x.BlockStart, x.BlockEnd });
            e.HasIndex(x => new { x.ResourceId, x.BlockStart, x.BlockEnd });
            e.HasIndex(x => x.Start);
            e.Ignore(x => x.LineTotalMinor);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Segment_Range", "\"End\" > \"Start\" AND BlockStart <= \"Start\" AND BlockEnd >= \"End\"");
                t.HasCheckConstraint("CK_Segment_Money", "UnitPriceMinor >= 0 AND DiscountMinor >= 0 AND DiscountMinor <= UnitPriceMinor AND DepositMinor >= 0");
                t.HasCheckConstraint("CK_Segment_Duration", "DurationMinutes > 0 AND PrepMinutes >= 0 AND CleanupMinutes >= 0");
            });
        });

        b.Entity<AppointmentEvent>(e =>
        {
            e.HasOne(x => x.Appointment).WithMany(x => x.Events).HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AppointmentId, x.Timestamp });
        });

        b.Entity<Payment>(e =>
        {
            e.Property(x => x.ReceiptNumber).HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.ReceiptNumber).IsUnique();
            e.HasIndex(x => x.SubmissionKey).IsUnique();
            e.HasIndex(x => x.RecordedAt);
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.ReversesPaymentId);
            e.HasOne(x => x.Appointment).WithMany(x => x.Payments).HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.ReversesPaymentId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.IsInflow);
            e.Ignore(x => x.SignedAmountMinor);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Payment_Amount", "AmountMinor > 0");
                t.HasCheckConstraint("CK_Payment_Kind", "Kind BETWEEN 0 AND 3");
                t.HasCheckConstraint("CK_Payment_Reversal", "(Kind IN (2, 3)) = (ReversesPaymentId IS NOT NULL)");
            });
        });

        b.Entity<Invoice>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.AppointmentId);
            e.HasOne(x => x.Appointment).WithMany(x => x.Invoices).HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Invoice_Money", "TotalMinor >= 0 AND SubtotalMinor >= 0"));
        });

        b.Entity<InvoiceLine>(e =>
            e.HasOne(x => x.Invoice).WithMany(x => x.Lines).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade));

        b.Entity<Reminder>(e =>
        {
            e.Property(x => x.DedupKey).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.DueAt });
            e.HasIndex(x => x.AppointmentId);
            e.HasOne(x => x.Appointment).WithMany(x => x.Reminders).HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.CustomerId);
        });
    }
}

/// <summary>Applies per-connection pragmas: busy timeout so concurrent writers wait instead of failing immediately.</summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
