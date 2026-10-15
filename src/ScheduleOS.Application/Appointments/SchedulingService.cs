using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Application.Appointments;

/// <summary>Loads scheduling rules from SQLite and runs the pure scheduling engine against them.</summary>
public sealed class SchedulingService(IAppDbContextFactory dbFactory, SettingsService settings, IClock clock, ICurrentUser user)
{
    /// <summary>Builds a rules snapshot for [from, to). When called with a write-transaction context the data is re-read inside the transaction.</summary>
    internal static async Task<SchedulingContext> LoadContextAsync(IAppDbContext db, DateTime from, DateTime to, int? locationId,
        AppSettings s, DateTime now, CancellationToken ct)
    {
        var staff = await db.Staff.AsNoTracking().Where(x => !x.IsArchived)
            .Include(x => x.Shifts).Include(x => x.Skills).AsSplitQuery().ToListAsync(ct);
        var timeOff = await db.StaffTimeOff.AsNoTracking().Where(t => t.Start < to && t.End > from).ToListAsync(ct);
        var resources = await db.Resources.AsNoTracking().Where(r => !r.IsArchived).ToListAsync(ct);
        var maintenance = await db.ResourceMaintenance.AsNoTracking().Where(m => m.Start < to && m.End > from).ToListAsync(ct);
        var services = await db.Services.AsNoTracking().Include(x => x.ResourceRequirements).ToListAsync(ct);
        var hours = await db.BusinessHours.AsNoTracking().ToListAsync(ct);
        var fromDate = DateOnly.FromDateTime(from);
        var toDate = DateOnly.FromDateTime(to);
        var closures = await db.BusinessHolidays.AsNoTracking().Where(h => h.Date >= fromDate && h.Date <= toDate).ToListAsync(ct);
        var bookings = await db.AppointmentSegments.AsNoTracking()
            .Where(x => x.IsBlocking && x.BlockStart < to && x.BlockEnd > from)
            .Select(x => new ExistingBooking(x.AppointmentId, x.Appointment!.Reference, x.Appointment.Customer!.FullName,
                x.StaffMemberId, x.ResourceId, x.BlockStart, x.BlockEnd))
            .ToListAsync(ct);

        return new SchedulingContext
        {
            Staff = staff.ToDictionary(x => x.Id, x => new StaffRules
            {
                Id = x.Id,
                Name = x.FullName,
                IsBookable = x.IsBookable,
                LocationId = x.LocationId,
                Shifts = x.Shifts.Select(sh => new ShiftInfo(sh.DayOfWeek, sh.StartTime, sh.EndTime, sh.BreakStart, sh.BreakEnd)).ToList(),
                TimeOff = timeOff.Where(t => t.StaffMemberId == x.Id)
                    .Select(t => new BlockInfo(t.Start, t.End, string.IsNullOrWhiteSpace(t.Reason) ? t.Type.ToString() : $"{t.Type}: {t.Reason}")).ToList(),
                QualifiedServiceIds = x.Skills.Select(k => k.ServiceId).ToHashSet(),
            }),
            Resources = resources.ToDictionary(r => r.Id, r => new ResourceRules
            {
                Id = r.Id,
                Name = r.Name,
                Type = r.Type,
                Capacity = r.Capacity,
                IsBookable = r.IsBookable,
                LocationId = r.LocationId,
                AvailableFrom = r.AvailableFrom,
                AvailableUntil = r.AvailableUntil,
                Maintenance = maintenance.Where(m => m.ResourceId == r.Id).Select(m => new BlockInfo(m.Start, m.End, m.Reason)).ToList(),
            }),
            Services = services.ToDictionary(x => x.Id, x => ToRules(x)),
            OpeningHours = hours.Select(h => new OpeningHours(h.DayOfWeek, h.IsOpen, h.OpenTime, h.CloseTime)).ToList(),
            Closures = closures.GroupBy(c => c.Date).ToDictionary(g => g.Key, g => g.First().Name),
            ExistingBookings = bookings,
            LocationId = locationId,
            Now = now,
            MinimumLeadMinutes = s.MinimumLeadMinutes,
        };
    }

    internal static ServiceRules ToRules(Service x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        IsBookable = x.IsBookable,
        DurationMinutes = x.DurationMinutes,
        PrepMinutes = x.PrepMinutes,
        CleanupMinutes = x.CleanupMinutes,
        RequiredResourceTypes = x.ResourceRequirements.Select(r => r.ResourceType).Distinct().ToList(),
    };

    /// <summary>Plans sequential segments for a booking request using the current service catalog.</summary>
    internal static async Task<(IReadOnlyList<ProposedSegment> Segments, List<Service> Services)> PlanAsync(
        IAppDbContext db, DateTime start, IReadOnlyList<ServiceLineRequest> lines, CancellationToken ct)
    {
        if (lines.Count == 0) throw new ValidationException("Select at least one service.");
        var ids = lines.Select(l => l.ServiceId).Distinct().ToList();
        var services = await db.Services.AsNoTracking().Include(s => s.ResourceRequirements).Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        var planned = new List<PlannedService>();
        var ordered = new List<Service>();
        foreach (var line in lines)
        {
            var svc = services.FirstOrDefault(s => s.Id == line.ServiceId) ?? throw new NotFoundException("Service", line.ServiceId);
            ordered.Add(svc);
            planned.Add(new PlannedService(svc.Id, svc.Name, svc.DurationMinutes, svc.PrepMinutes, svc.CleanupMinutes, line.StaffMemberId, line.ResourceId));
        }

        return (SegmentPlanner.Plan(start, planned), ordered);
    }

    internal static DateTime WindowStart(IEnumerable<ProposedSegment> segs) => segs.Min(s => s.BlockStart).Date;
    internal static DateTime WindowEnd(IEnumerable<ProposedSegment> segs) => segs.Max(s => s.BlockEnd).Date.AddDays(1);

    /// <summary>Validates a proposed booking without saving anything ("Conflict Check").</summary>
    public async Task<ConflictCheckResult> CheckAsync(ConflictCheckRequest request, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var (segments, services) = await PlanAsync(db, request.Start, request.Services, ct);
        var ctx = await LoadContextAsync(db, WindowStart(segments), WindowEnd(segments), request.LocationId, settings.Current, clock.Now, ct);
        var conflicts = ConflictDetector.Detect(segments, ctx, request.ExcludeAppointmentId, checkLeadTime: true);
        return BuildResult(segments, services, request.Services, ctx, conflicts, settings.Current.TaxRateBasisPoints);
    }

    internal static ConflictCheckResult BuildResult(IReadOnlyList<ProposedSegment> segments, IReadOnlyList<Service> services,
        IReadOnlyList<ServiceLineRequest> lines, SchedulingContext ctx, IReadOnlyList<ScheduleConflict> conflicts, int taxBasisPoints)
    {
        var previews = segments.Select(s => new SegmentPreview(s.Index, s.ServiceName,
            ctx.Staff.TryGetValue(s.StaffMemberId, out var st) ? st.Name : "Unassigned",
            s.ResourceId is { } rid && ctx.Resources.TryGetValue(rid, out var r) ? r.Name : null,
            s.Start, s.End, s.BlockStart, s.BlockEnd)).ToList();
        long subtotal = 0, discount = 0, deposit = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            subtotal += lines[i].UnitPriceMinor ?? services[i].PriceMinor;
            discount += lines[i].DiscountMinor;
            deposit += services[i].DepositMinor;
        }

        var tax = Money.ApplyBasisPoints(subtotal - discount, taxBasisPoints);
        var total = subtotal - discount + tax;
        return new ConflictCheckResult(previews, conflicts, segments.Min(s => s.Start), segments.Max(s => s.End),
            subtotal, discount, tax, total, Math.Min(deposit, total));
    }

    /// <summary>Find Available Time: compatible staff and rooms for the requested services.</summary>
    public async Task<IReadOnlyList<SlotSuggestion>> FindSlotsAsync(FindSlotsRequest request, CancellationToken ct = default)
    {
        if (request.ServiceIds.Count == 0) throw new ValidationException("Select at least one service.");
        await using var db = dbFactory.CreateReadContext();
        var s = settings.Current;
        var start = request.PreferredDate.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(Math.Clamp(request.DaysToSearch, 1, 31));
        var ctx = await LoadContextAsync(db, start, end, request.LocationId, s, clock.Now, ct);

        var slotRequests = new List<SlotServiceRequest>();
        foreach (var id in request.ServiceIds)
        {
            if (!ctx.Services.TryGetValue(id, out var svc)) throw new NotFoundException("Service", id);
            var candidates = ctx.Staff.Values
                .Where(st => st.IsBookable && st.QualifiedServiceIds.Contains(id) &&
                             (request.LocationId is null || st.LocationId is null || st.LocationId == request.LocationId))
                .OrderByDescending(st => st.Id == request.PreferredStaffId).ThenBy(st => st.Name)
                .Select(st => st.Id).ToList();
            if (request.PreferredStaffId is { } pref && candidates.Contains(pref) && request.ServiceIds.Count == 1)
                candidates = [pref, .. candidates.Where(c => c != pref)];
            slotRequests.Add(new SlotServiceRequest(svc, candidates));
        }

        var results = new List<SlotSuggestion>();
        for (var day = start; day < end && results.Count < request.MaxResults; day = day.AddDays(1))
        {
            var dayStart = day.AddHours(s.CalendarStartHour);
            var dayEnd = day.AddHours(s.CalendarEndHour);
            var found = SlotFinder.Find(slotRequests, dayStart, dayEnd, s.SlotIntervalMinutes, ctx,
                request.MaxResults * 4, request.ExcludeAppointmentId);
            results.AddRange(found);
        }

        // Rank: preferred staff first, then closeness to the preferred date/time.
        var preferredInstant = request.PreferredDate.ToDateTime(request.PreferredTime ?? new TimeOnly(s.CalendarStartHour, 0));
        return results
            .OrderByDescending(r => request.PreferredStaffId is { } p && r.Assignments.All(a => a.StaffMemberId == p))
            .ThenBy(r => Math.Abs((r.Start - preferredInstant).TotalMinutes))
            .Take(request.MaxResults)
            .OrderBy(r => r.Start)
            .ToList();
    }

    /// <summary>Re-validates existing bookings in a range (e.g. after new leave or maintenance was recorded).</summary>
    public async Task<IReadOnlyList<ConflictReportItem>> ScanConflictsAsync(DateTime from, DateTime to, int? locationId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var ctx = await LoadContextAsync(db, from.Date, to.Date.AddDays(1), locationId, settings.Current, clock.Now, ct);
        var query = db.Appointments.AsNoTracking().Include(a => a.Segments).Include(a => a.Customer)
            .Where(a => a.Start < to && a.End > from && a.Segments.Any(s => s.IsBlocking));
        if (locationId is not null) query = query.Where(a => a.LocationId == locationId);
        if (!user.Has(Permission.ViewAllAppointments) && user.StaffMemberId is { } sid)
            query = query.Where(a => a.Segments.Any(s => s.StaffMemberId == sid));
        var appointments = await query.ToListAsync(ct);

        var report = new List<ConflictReportItem>();
        foreach (var a in appointments.OrderBy(a => a.Start))
        {
            var segs = a.Segments.OrderBy(s => s.SortOrder).Select((s, i) => new ProposedSegment(i, s.ServiceId, s.ServiceName,
                s.StaffMemberId, s.ResourceId, s.Start, s.End, s.BlockStart, s.BlockEnd)).ToList();
            var conflicts = ConflictDetector.Detect(segs, ctx, a.Id, checkLeadTime: false)
                .Where(c => c.Type is not ConflictType.ServiceInactive).ToList();
            if (conflicts.Count > 0) report.Add(new ConflictReportItem(a.Id, a.Reference, a.Start, a.Customer?.FullName ?? "", conflicts));
        }

        return report;
    }
}
