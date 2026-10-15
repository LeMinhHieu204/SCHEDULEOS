using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Staff;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Reporting;

public sealed record KpiValue(long Value, long? Total, double? ChangePercent, string Caption);

public sealed record DailyAmount(DateOnly Date, long AmountMinor);

public sealed record StatusCount(AppointmentStatus Status, int Count);

public sealed record ScheduleItem(int AppointmentId, string Reference, DateTime Start, DateTime End, string CustomerName, string CustomerInitials,
    string Services, string Staff, AppointmentStatus Status, int Version);

public sealed record ActivityItem(DateTime Timestamp, string Action, string Title, string Summary, int? CustomerId, string? CustomerName, string? CustomerInitials, int? EntityId, string EntityType);

public sealed record DashboardData(
    DateOnly Date,
    KpiValue TodaysAppointments,
    KpiValue Confirmed,
    KpiValue Pending,
    KpiValue AvailableStaff,
    KpiValue RevenueToday,
    IReadOnlyList<DailyAmount> WeeklyRevenue,
    long WeekTotalMinor,
    long PreviousWeekTotalMinor,
    double? WeekChangePercent,
    IReadOnlyList<StatusCount> StatusCounts,
    IReadOnlyList<ScheduleItem> TodaysSchedule,
    IReadOnlyList<ScheduleItem> Upcoming,
    IReadOnlyList<StaffPerformanceDto> TopStaff,
    IReadOnlyList<ActivityItem> Activity,
    int ConflictsDetected);

public sealed class DashboardService(IAppDbContextFactory dbFactory, ICurrentUser user, IClock clock, SchedulingService scheduling)
{
    public async Task<DashboardData> GetAsync(DateOnly date, int? locationId, CancellationToken ct = default)
    {
        user.Require(Permission.ViewDashboard, "view the dashboard");
        await using var db = dbFactory.CreateReadContext();
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var lastWeekStart = dayStart.AddDays(-7);
        var ownOnly = !user.Has(Permission.ViewAllAppointments);
        var ownStaff = user.StaffMemberId ?? -1;

        IQueryable<Appointment> Appts()
        {
            var q = db.Appointments.AsNoTracking();
            if (locationId is not null) q = q.Where(a => a.LocationId == locationId || a.LocationId == null);
            if (ownOnly) q = q.Where(a => a.Segments.Any(s => s.StaffMemberId == ownStaff));
            return q;
        }

        // KPIs
        var today = await Appts().Where(a => a.Start >= dayStart && a.Start < dayEnd).Select(a => a.Status).ToListAsync(ct);
        var lastWeek = await Appts().Where(a => a.Start >= lastWeekStart && a.Start < lastWeekStart.AddDays(1)).Select(a => a.Status).ToListAsync(ct);
        static bool Counts(AppointmentStatus s) => s is not (AppointmentStatus.Cancelled or AppointmentStatus.Draft);
        var todayCount = today.Count(Counts);
        var lastWeekCount = lastWeek.Count(Counts);
        var confirmed = today.Count(s => s is AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn or AppointmentStatus.InProgress or AppointmentStatus.Completed);
        var pending = today.Count(s => s == AppointmentStatus.Pending);

        var staffQ = db.Staff.AsNoTracking().Include(s => s.Shifts).Where(s => !s.IsArchived && s.EmploymentStatus != EmploymentStatus.Inactive);
        if (locationId is not null) staffQ = staffQ.Where(s => s.LocationId == locationId || s.LocationId == null);
        var staff = await staffQ.ToListAsync(ct);
        var offToday = await db.StaffTimeOff.AsNoTracking().Where(t => t.Start <= dayStart.AddHours(12) && t.End >= dayStart.AddHours(12)).Select(t => t.StaffMemberId).ToListAsync(ct);
        var onShift = staff.Count(s => s.Shifts.Any(x => x.DayOfWeek == dayStart.DayOfWeek) && !offToday.Contains(s.Id) && s.EmploymentStatus == EmploymentStatus.Active);

        var canSeeMoney = user.Has(Permission.ViewPayments);
        long revenueToday = 0, revenueLastWeekDay = 0;
        var weekStart = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7));
        var weekly = new List<DailyAmount>();
        long weekTotal = 0, prevWeekTotal = 0;
        if (canSeeMoney)
        {
            var payments = await PaymentsQuery(db, locationId).Where(p => p.RecordedAt >= weekStart.AddDays(-7) && p.RecordedAt < weekStart.AddDays(7) ||
                                                                        p.RecordedAt >= lastWeekStart && p.RecordedAt < dayEnd)
                .Select(p => new { p.RecordedAt, Signed = p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor })
                .ToListAsync(ct);
            revenueToday = payments.Where(p => p.RecordedAt >= dayStart && p.RecordedAt < dayEnd).Sum(p => p.Signed);
            revenueLastWeekDay = payments.Where(p => p.RecordedAt >= lastWeekStart && p.RecordedAt < lastWeekStart.AddDays(1)).Sum(p => p.Signed);
            for (var i = 0; i < 7; i++)
            {
                var d = weekStart.AddDays(i);
                weekly.Add(new DailyAmount(DateOnly.FromDateTime(d), payments.Where(p => p.RecordedAt >= d && p.RecordedAt < d.AddDays(1)).Sum(p => p.Signed)));
            }

            weekTotal = weekly.Sum(w => w.AmountMinor);
            prevWeekTotal = payments.Where(p => p.RecordedAt >= weekStart.AddDays(-7) && p.RecordedAt < weekStart).Sum(p => p.Signed);
        }

        // Status distribution and schedules
        var statusCounts = today.GroupBy(s => s).Select(g => new StatusCount(g.Key, g.Count())).OrderBy(s => s.Status).ToList();
        var schedule = await LoadScheduleAsync(Appts().Where(a => a.Start >= dayStart && a.Start < dayEnd && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.Start).Take(60), ct);
        var now = clock.Now;
        var upcoming = await LoadScheduleAsync(Appts().Where(a => a.Start >= now && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.Start).Take(8), ct);

        var topStaff = await StaffService.PerformanceAsync(db, weekStart, weekStart.AddDays(7), ownOnly ? [ownStaff] : null, ct);

        // Activity feed: genuine recorded events only.
        var activity = new List<ActivityItem>();
        if (user.Has(Permission.ViewCustomers))
        {
            string[] actions = ["Appointment.Created", "Appointment.Rescheduled", "Appointment.Completed", "Appointment.Cancelled", "Appointment.NoShow",
                "Appointment.CheckedIn", "Payment.Recorded", "Payment.Refunded", "Customer.Created", "Invoice.Issued"];
            var logs = await db.AuditLogs.AsNoTracking().Where(l => l.CustomerId != null && actions.Contains(l.Action))
                .OrderByDescending(l => l.Timestamp).ThenByDescending(l => l.Id).Take(8).ToListAsync(ct);
            var custIds = logs.Select(l => l.CustomerId!.Value).Distinct().ToList();
            var names = await db.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
            activity = logs.Select(l =>
            {
                var name = names.GetValueOrDefault(l.CustomerId!.Value);
                return new ActivityItem(l.Timestamp, l.Action, ActionTitle(l.Action), l.Summary, l.CustomerId, name, NameHelper.Initials(name), l.EntityId, l.EntityType);
            }).ToList();
        }

        var conflicts = (await scheduling.ScanConflictsAsync(now, dayStart.AddDays(14), locationId, ct)).Count;

        return new DashboardData(date,
            new KpiValue(todayCount, null, Change(todayCount, lastWeekCount), $"vs. last {dayStart:dddd}"),
            new KpiValue(confirmed, todayCount, null, "of today's appointments"),
            new KpiValue(pending, todayCount, null, "need confirmation"),
            new KpiValue(onShift, staff.Count, null, "on shift today"),
            new KpiValue(revenueToday, null, Change(revenueToday, revenueLastWeekDay), $"vs. last {dayStart:dddd}"),
            weekly, weekTotal, prevWeekTotal, Change(weekTotal, prevWeekTotal), statusCounts, schedule, upcoming,
            topStaff.Where(s => s.Appointments > 0).Take(5).ToList(), activity, conflicts);
    }

    private static IQueryable<Payment> PaymentsQuery(IAppDbContext db, int? locationId)
    {
        var q = db.Payments.AsNoTracking();
        if (locationId is not null) q = q.Where(p => p.Appointment == null || p.Appointment.LocationId == locationId || p.Appointment.LocationId == null);
        return q;
    }

    internal static async Task<List<ScheduleItem>> LoadScheduleAsync(IQueryable<Appointment> q, CancellationToken ct)
    {
        var rows = await q.Select(a => new
        {
            a.Id, a.Reference, a.Start, a.End, Customer = a.Customer!.FullName, a.Status, a.Version,
            Segs = a.Segments.OrderBy(s => s.SortOrder).Select(s => new { s.ServiceName, Staff = s.StaffMember!.FullName }).ToList(),
        }).ToListAsync(ct);
        return rows.Select(r => new ScheduleItem(r.Id, r.Reference, r.Start, r.End, r.Customer, NameHelper.Initials(r.Customer),
            string.Join(", ", r.Segs.Select(s => s.ServiceName)), string.Join(", ", r.Segs.Select(s => s.Staff).Distinct()), r.Status, r.Version)).ToList();
    }

    internal static double? Change(long current, long previous) =>
        previous == 0 ? (current == 0 ? 0 : null) : Math.Round((current - previous) * 100.0 / Math.Abs(previous), 1);

    public static string ActionTitle(string action) => action switch
    {
        "Appointment.Created" => "Booked appointment",
        "Appointment.Rescheduled" => "Rescheduled appointment",
        "Appointment.Completed" => "Completed appointment",
        "Appointment.Cancelled" => "Cancelled appointment",
        "Appointment.NoShow" => "Missed appointment",
        "Appointment.CheckedIn" => "Checked in",
        "Payment.Recorded" => "Payment recorded",
        "Payment.Refunded" => "Refund issued",
        "Customer.Created" => "New customer",
        "Invoice.Issued" => "Invoice issued",
        _ => action,
    };
}
