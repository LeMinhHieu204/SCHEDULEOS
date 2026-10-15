using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Application.Staff;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Application.Reporting;

public sealed record ServicePerformance(string ServiceName, int Bookings, int Completed, long RevenueMinor, double AverageMinutes);

public sealed record ResourceUtilization(int ResourceId, string Name, string Type, double BookedHours, double AvailableHours, double Utilization);

public sealed record DailyVolume(DateOnly Date, int Appointments, int Completed, int Cancelled, int NoShows);

public sealed record AnalyticsReport(
    DateTime From,
    DateTime To,
    long RevenueMinor,
    long InflowMinor,
    long RefundsMinor,
    double? RevenueChangePercent,
    IReadOnlyList<DailyAmount> RevenueByDay,
    int TotalAppointments,
    IReadOnlyList<DailyVolume> VolumeByDay,
    IReadOnlyList<StatusCount> StatusBreakdown,
    int UniqueCustomers,
    int NewCustomers,
    int ReturningCustomers,
    double RetentionRate,
    double NoShowRate,
    double CancellationRate,
    long AverageTicketMinor,
    IReadOnlyList<ServicePerformance> Services,
    IReadOnlyList<StaffPerformanceDto> Staff,
    IReadOnlyList<ResourceUtilization> Resources,
    IReadOnlyList<OutstandingItem> Outstanding,
    long OutstandingTotalMinor);

public sealed class AnalyticsService(IAppDbContextFactory dbFactory, ICurrentUser user, IClock clock, SettingsService settings)
{
    public async Task<AnalyticsReport> GetReportAsync(DateTime from, DateTime to, int? locationId, CancellationToken ct = default)
    {
        user.Require(Permission.ViewReports, "view reports");
        if (to <= from) throw new ValidationException("The end of the range must be after its start.");
        if ((to - from).TotalDays > 3700) throw new ValidationException("The range is too large.");
        await using var db = dbFactory.CreateReadContext();

        // Revenue = recorded payments (cash basis) net of refunds and reversals.
        var payQ = db.Payments.AsNoTracking();
        if (locationId is not null) payQ = payQ.Where(p => p.Appointment == null || p.Appointment.LocationId == locationId || p.Appointment.LocationId == null);
        var span = to - from;
        var payments = await payQ.Where(p => p.RecordedAt >= from - span && p.RecordedAt < to)
            .Select(p => new { p.RecordedAt, p.Kind, p.AmountMinor }).ToListAsync(ct);
        long Signed(PaymentKind k, long a) => k is PaymentKind.Deposit or PaymentKind.Payment ? a : -a;
        var current = payments.Where(p => p.RecordedAt >= from).ToList();
        var inflow = current.Where(p => p.Kind is PaymentKind.Deposit or PaymentKind.Payment).Sum(p => p.AmountMinor);
        var refunds = current.Where(p => p.Kind is PaymentKind.Refund or PaymentKind.Reversal).Sum(p => p.AmountMinor);
        var previous = payments.Where(p => p.RecordedAt < from).Sum(p => Signed(p.Kind, p.AmountMinor));
        var days = new List<DailyAmount>();
        for (var d = from.Date; d < to; d = d.AddDays(1))
            days.Add(new DailyAmount(DateOnly.FromDateTime(d), current.Where(p => p.RecordedAt >= d && p.RecordedAt < d.AddDays(1)).Sum(p => Signed(p.Kind, p.AmountMinor))));

        // Appointments
        var apptQ = db.Appointments.AsNoTracking().Where(a => a.Start >= from && a.Start < to && a.Status != AppointmentStatus.Draft);
        if (locationId is not null) apptQ = apptQ.Where(a => a.LocationId == locationId || a.LocationId == null);
        var appts = await apptQ.Select(a => new { a.Id, a.CustomerId, a.Start, a.Status, a.TotalMinor }).ToListAsync(ct);
        var volume = new List<DailyVolume>();
        for (var d = from.Date; d < to; d = d.AddDays(1))
        {
            var dayAppts = appts.Where(a => a.Start >= d && a.Start < d.AddDays(1)).ToList();
            volume.Add(new DailyVolume(DateOnly.FromDateTime(d), dayAppts.Count, dayAppts.Count(a => a.Status == AppointmentStatus.Completed),
                dayAppts.Count(a => a.Status == AppointmentStatus.Cancelled), dayAppts.Count(a => a.Status == AppointmentStatus.NoShow)));
        }

        var statuses = appts.GroupBy(a => a.Status).Select(g => new StatusCount(g.Key, g.Count())).OrderBy(s => s.Status).ToList();
        var completed = appts.Count(a => a.Status == AppointmentStatus.Completed);
        var noShows = appts.Count(a => a.Status == AppointmentStatus.NoShow);
        var cancelled = appts.Count(a => a.Status == AppointmentStatus.Cancelled);

        // New vs returning: a customer is "new" when their first ever non-cancelled appointment falls inside the range.
        var customerIds = appts.Where(a => a.Status != AppointmentStatus.Cancelled).Select(a => a.CustomerId).Distinct().ToList();
        var firstVisits = await db.Appointments.AsNoTracking().Where(a => customerIds.Contains(a.CustomerId) && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Draft)
            .GroupBy(a => a.CustomerId).Select(g => new { g.Key, First = g.Min(a => a.Start) }).ToListAsync(ct);
        var newCustomers = firstVisits.Count(f => f.First >= from);
        var returning = customerIds.Count - newCustomers;

        // Services (value of completed segments)
        var segQ = db.AppointmentSegments.AsNoTracking().Where(s => s.Start >= from && s.Start < to && s.Appointment!.Status != AppointmentStatus.Draft);
        if (locationId is not null) segQ = segQ.Where(s => s.Appointment!.LocationId == locationId || s.Appointment.LocationId == null);
        var segs = await segQ.Select(s => new { s.ServiceName, s.Appointment!.Status, Line = s.UnitPriceMinor - s.DiscountMinor, s.DurationMinutes, s.ResourceId, s.BlockStart, s.BlockEnd, s.IsBlocking })
            .ToListAsync(ct);
        var services = segs.Where(s => s.Status != AppointmentStatus.Cancelled).GroupBy(s => s.ServiceName).Select(g => new ServicePerformance(g.Key, g.Count(),
                g.Count(x => x.Status == AppointmentStatus.Completed), g.Where(x => x.Status == AppointmentStatus.Completed).Sum(x => x.Line),
                g.Average(x => (double)x.DurationMinutes)))
            .OrderByDescending(s => s.RevenueMinor).ThenByDescending(s => s.Bookings).ToList();

        var staff = await StaffService.PerformanceAsync(db, from, to, null, ct);

        // Resource utilisation: booked blocked-time vs. opening hours × capacity minus maintenance.
        var hours = await db.BusinessHours.AsNoTracking().ToListAsync(ct);
        var resQ = db.Resources.AsNoTracking().Where(r => !r.IsArchived);
        if (locationId is not null) resQ = resQ.Where(r => r.LocationId == locationId || r.LocationId == null);
        var resources = await resQ.ToListAsync(ct);
        var maintenance = await db.ResourceMaintenance.AsNoTracking().Where(m => m.Start < to && m.End > from).ToListAsync(ct);
        var utilization = resources.Select(r =>
        {
            double available = 0;
            for (var d = from.Date; d < to; d = d.AddDays(1))
            {
                var h = hours.FirstOrDefault(x => x.DayOfWeek == d.DayOfWeek);
                if (h is null || !h.IsOpen) continue;
                var open = d + h.OpenTime.ToTimeSpan();
                var close = d + h.CloseTime.ToTimeSpan();
                var minutes = (close - open).TotalMinutes;
                foreach (var m in maintenance.Where(m => m.ResourceId == r.Id))
                {
                    var s = m.Start > open ? m.Start : open;
                    var e = m.End < close ? m.End : close;
                    if (e > s) minutes -= (e - s).TotalMinutes;
                }

                available += Math.Max(0, minutes) * Math.Max(1, r.Capacity);
            }

            var booked = segs.Where(s => s.ResourceId == r.Id && s.IsBlocking).Sum(s => (s.BlockEnd - s.BlockStart).TotalMinutes);
            return new ResourceUtilization(r.Id, r.Name, ResourceTypeNames.Display(r.Type), Math.Round(booked / 60, 1), Math.Round(available / 60, 1),
                available <= 0 ? 0 : Math.Min(1, booked / available));
        }).OrderByDescending(r => r.Utilization).ToList();

        // Outstanding balances (all time, as of now)
        var now = clock.Now;
        var outstandingRows = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Completed || a.Status == AppointmentStatus.InProgress || a.Status == AppointmentStatus.CheckedIn ||
                        ((a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.Pending) && a.Start < now))
            .Select(a => new
            {
                a.Id, a.Reference, a.Start, a.Status, a.CustomerId, Customer = a.Customer!.FullName, a.TotalMinor,
                Paid = a.Payments.Sum(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor),
            }).ToListAsync(ct);
        var outstanding = outstandingRows.Where(r => r.TotalMinor - r.Paid > 0).OrderBy(r => r.Start)
            .Select(r => new OutstandingItem(r.Id, r.Reference, r.Start, r.Status, r.CustomerId, r.Customer, r.TotalMinor, r.Paid, r.TotalMinor - r.Paid)).ToList();

        var completedTotals = appts.Where(a => a.Status == AppointmentStatus.Completed).Select(a => a.TotalMinor).ToList();
        return new AnalyticsReport(from, to, inflow - refunds, inflow, refunds, DashboardService.Change(inflow - refunds, previous), days,
            appts.Count, volume, statuses, customerIds.Count, newCustomers, returning,
            customerIds.Count == 0 ? 0 : returning / (double)customerIds.Count,
            completed + noShows == 0 ? 0 : noShows / (double)(completed + noShows),
            appts.Count == 0 ? 0 : cancelled / (double)appts.Count,
            completedTotals.Count == 0 ? 0 : completedTotals.Sum() / completedTotals.Count,
            services, staff, utilization, outstanding, outstanding.Sum(o => o.BalanceMinor));
    }

    /// <summary>Converts a report into tables. Exports use exactly the numbers shown on screen.</summary>
    public IReadOnlyList<Abstractions.TabularReport> ToTables(AnalyticsReport r)
    {
        var cf = settings.Currency;
        string M(long v) => cf.ToEditString(v);
        return
        [
            new("Summary", ["Metric", "Value"],
            [
                ["Period", $"{r.From:yyyy-MM-dd} to {r.To.AddDays(-1):yyyy-MM-dd}"],
                [$"Net revenue ({cf.Code})", M(r.RevenueMinor)],
                [$"Payments received ({cf.Code})", M(r.InflowMinor)],
                [$"Refunds & reversals ({cf.Code})", M(r.RefundsMinor)],
                ["Appointments", r.TotalAppointments],
                ["Unique customers", r.UniqueCustomers],
                ["New customers", r.NewCustomers],
                ["Returning customers", r.ReturningCustomers],
                ["Retention rate", $"{r.RetentionRate:P1}"],
                ["No-show rate", $"{r.NoShowRate:P1}"],
                ["Cancellation rate", $"{r.CancellationRate:P1}"],
                [$"Average ticket ({cf.Code})", M(r.AverageTicketMinor)],
                [$"Outstanding balances ({cf.Code})", M(r.OutstandingTotalMinor)],
            ]),
            new("Daily Revenue", ["Date", $"Net revenue ({cf.Code})"], r.RevenueByDay.Select(d => (IReadOnlyList<object?>)[d.Date.ToString("yyyy-MM-dd"), M(d.AmountMinor)]).ToList()),
            new("Appointment Volume", ["Date", "Appointments", "Completed", "Cancelled", "No-shows"],
                r.VolumeByDay.Select(d => (IReadOnlyList<object?>)[d.Date.ToString("yyyy-MM-dd"), d.Appointments, d.Completed, d.Cancelled, d.NoShows]).ToList()),
            new("Appointment Status", ["Status", "Count"], r.StatusBreakdown.Select(s => (IReadOnlyList<object?>)[AppointmentService.StatusTitle(s.Status), s.Count]).ToList()),
            new("Service Performance", ["Service", "Bookings", "Completed", $"Revenue ({cf.Code})", "Avg minutes"],
                r.Services.Select(s => (IReadOnlyList<object?>)[s.ServiceName, s.Bookings, s.Completed, M(s.RevenueMinor), Math.Round(s.AverageMinutes, 0)]).ToList()),
            new("Staff Performance", ["Staff", "Appointments", "Completed", "Cancelled", "No-shows", "Scheduled h", "Booked h", $"Service revenue ({cf.Code})", "Utilization"],
                r.Staff.Select(s => (IReadOnlyList<object?>)[s.Name, s.Appointments, s.Completed, s.Cancelled, s.NoShows, s.ScheduledHours, s.BookedHours, M(s.ServiceRevenueMinor), $"{s.Utilization:P0}"]).ToList()),
            new("Resource Utilization", ["Resource", "Type", "Booked h", "Available h", "Utilization"],
                r.Resources.Select(x => (IReadOnlyList<object?>)[x.Name, x.Type, x.BookedHours, x.AvailableHours, $"{x.Utilization:P0}"]).ToList()),
            new("Outstanding Payments", ["Reference", "Date", "Customer", "Status", $"Total ({cf.Code})", $"Paid ({cf.Code})", $"Balance ({cf.Code})"],
                r.Outstanding.Select(o => (IReadOnlyList<object?>)[o.Reference, o.Start.ToString("yyyy-MM-dd HH:mm"), o.CustomerName, AppointmentService.StatusTitle(o.Status), M(o.TotalMinor), M(o.PaidMinor), M(o.BalanceMinor)]).ToList()),
        ];
    }
}
