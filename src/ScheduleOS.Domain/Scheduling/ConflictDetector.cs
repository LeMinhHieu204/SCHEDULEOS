namespace ScheduleOS.Domain.Scheduling;

/// <summary>
/// Pure scheduling rule engine. Given proposed segments and a context snapshot it returns every
/// rule that would be violated. Intervals are half-open: [start, end).
/// </summary>
public static class ConflictDetector
{
    public static IReadOnlyList<ScheduleConflict> Detect(
        IReadOnlyList<ProposedSegment> segments,
        SchedulingContext ctx,
        int? excludeAppointmentId = null,
        bool checkLeadTime = true)
    {
        var conflicts = new List<ScheduleConflict>();
        var others = ctx.ExistingBookings.Where(b => b.AppointmentId != excludeAppointmentId).ToList();

        if (checkLeadTime && segments.Count > 0)
        {
            var first = segments.Min(s => s.Start);
            if (first < ctx.Now)
                conflicts.Add(new(ConflictType.LeadTime, ConflictSeverity.Warning, $"The start time {first:g} is in the past."));
            else if (ctx.MinimumLeadMinutes > 0 && first < ctx.Now.AddMinutes(ctx.MinimumLeadMinutes))
                conflicts.Add(new(ConflictType.LeadTime, ConflictSeverity.Warning,
                    $"Bookings require at least {ctx.MinimumLeadMinutes} minutes of lead time."));
        }

        foreach (var seg in segments)
        {
            if (seg.End <= seg.Start || seg.BlockEnd < seg.End || seg.BlockStart > seg.Start)
            {
                conflicts.Add(new(ConflictType.InvalidTime, ConflictSeverity.Blocking, $"{seg.ServiceName}: the time range is invalid.", seg.Index));
                continue;
            }

            ctx.Services.TryGetValue(seg.ServiceId, out var service);
            if (service is null || !service.IsBookable)
                conflicts.Add(new(ConflictType.ServiceInactive, ConflictSeverity.Blocking, $"{seg.ServiceName} is not an active, bookable service.", seg.Index));

            CheckBusiness(seg, ctx, conflicts);
            CheckStaff(seg, service, ctx, others, conflicts);
            CheckResource(seg, service, ctx, others, conflicts);
        }

        CheckInternalOverlaps(segments, conflicts);
        return conflicts;
    }

    public static bool HasBlocking(IEnumerable<ScheduleConflict> conflicts) =>
        conflicts.Any(c => c.Severity == ConflictSeverity.Blocking);

    private static void CheckBusiness(ProposedSegment seg, SchedulingContext ctx, List<ScheduleConflict> conflicts)
    {
        var date = DateOnly.FromDateTime(seg.Start);
        if (ctx.Closures.TryGetValue(date, out var closureName))
        {
            conflicts.Add(new(ConflictType.BusinessClosed, ConflictSeverity.Blocking,
                $"The business is closed on {date:d} ({closureName}).", seg.Index));
            return;
        }

        if (ctx.OpeningHours.Count == 0) return;
        var hours = ctx.OpeningHours.FirstOrDefault(h => h.Day == seg.Start.DayOfWeek);
        if (hours is null || !hours.IsOpen)
        {
            conflicts.Add(new(ConflictType.OutsideBusinessHours, ConflictSeverity.Warning,
                $"{seg.ServiceName}: the business is not open on {seg.Start:dddd}s.", seg.Index));
            return;
        }

        var open = seg.Start.Date + hours.Open.ToTimeSpan();
        var close = seg.Start.Date + hours.Close.ToTimeSpan();
        if (seg.Start < open || seg.End > close)
            conflicts.Add(new(ConflictType.OutsideBusinessHours, ConflictSeverity.Warning,
                $"{seg.ServiceName} ({seg.Start:t}–{seg.End:t}) falls outside opening hours {hours.Open:t}–{hours.Close:t}.", seg.Index));
    }

    private static void CheckStaff(ProposedSegment seg, ServiceRules? service, SchedulingContext ctx,
        List<ExistingBooking> others, List<ScheduleConflict> conflicts)
    {
        if (!ctx.Staff.TryGetValue(seg.StaffMemberId, out var staff))
        {
            conflicts.Add(new(ConflictType.StaffInactive, ConflictSeverity.Blocking, $"{seg.ServiceName}: no valid staff member is assigned.", seg.Index));
            return;
        }

        if (!staff.IsBookable)
            conflicts.Add(new(ConflictType.StaffInactive, ConflictSeverity.Blocking, $"{staff.Name} is not currently bookable.", seg.Index));

        if (service is not null && !staff.QualifiedServiceIds.Contains(service.Id))
            conflicts.Add(new(ConflictType.StaffNotQualified, ConflictSeverity.Blocking, $"{staff.Name} is not qualified to perform {service.Name}.", seg.Index));

        if (ctx.LocationId is not null && staff.LocationId is not null && staff.LocationId != ctx.LocationId)
            conflicts.Add(new(ConflictType.StaffWrongLocation, ConflictSeverity.Warning, $"{staff.Name} is assigned to a different location.", seg.Index));

        // Working hours: the whole blocked interval (including buffers) must sit inside one shift.
        var blocked = seg.Blocked;
        var crossesMidnight = blocked.End.Date != blocked.Start.Date && blocked.End.TimeOfDay != TimeSpan.Zero;
        var shift = crossesMidnight ? null : staff.Shifts.FirstOrDefault(s =>
            s.Day == blocked.Start.DayOfWeek &&
            blocked.Start.Date + s.Start.ToTimeSpan() <= blocked.Start &&
            blocked.Start.Date + s.End.ToTimeSpan() >= blocked.End);

        if (shift is null)
        {
            var dayShifts = staff.Shifts.Where(s => s.Day == blocked.Start.DayOfWeek).ToList();
            var detail = dayShifts.Count == 0
                ? $"{staff.Name} does not work on {blocked.Start:dddd}s"
                : $"{staff.Name} works {string.Join(", ", dayShifts.Select(s => $"{s.Start:t}–{s.End:t}"))} on {blocked.Start:dddd}s";
            conflicts.Add(new(ConflictType.OutsideStaffHours, ConflictSeverity.Warning,
                $"{seg.ServiceName} ({blocked.Start:t}–{blocked.End:t}) is outside working hours: {detail}.", seg.Index));
        }
        else if (shift.BreakStart is { } bs && shift.BreakEnd is { } be && be > bs)
        {
            var brk = new TimeInterval(blocked.Start.Date + bs.ToTimeSpan(), blocked.Start.Date + be.ToTimeSpan());
            if (brk.Overlaps(blocked))
                conflicts.Add(new(ConflictType.StaffBreak, ConflictSeverity.Warning,
                    $"{seg.ServiceName} overlaps {staff.Name}'s break ({bs:t}–{be:t}).", seg.Index));
        }

        foreach (var off in staff.TimeOff.Where(o => o.Interval.Overlaps(blocked)))
            conflicts.Add(new(ConflictType.StaffTimeOff, ConflictSeverity.Blocking,
                $"{staff.Name} is unavailable ({off.Description}) from {off.Start:g} to {off.End:g}.", seg.Index));

        foreach (var b in others.Where(b => b.StaffMemberId == seg.StaffMemberId &&
                                            TimeInterval.Overlaps(b.BlockStart, b.BlockEnd, seg.BlockStart, seg.BlockEnd)))
            conflicts.Add(new(ConflictType.StaffOverlap, ConflictSeverity.Blocking,
                $"{staff.Name} is already booked {b.BlockStart:t}–{b.BlockEnd:t} ({b.Reference}, {b.CustomerName}).", seg.Index, b.AppointmentId));
    }

    private static void CheckResource(ProposedSegment seg, ServiceRules? service, SchedulingContext ctx,
        List<ExistingBooking> others, List<ScheduleConflict> conflicts)
    {
        var required = service?.RequiredResourceTypes ?? [];
        if (seg.ResourceId is null)
        {
            if (required.Count > 0)
                conflicts.Add(new(ConflictType.MissingResource, ConflictSeverity.Blocking,
                    $"{seg.ServiceName} requires a {Describe(required)}.", seg.Index));
            return;
        }

        if (!ctx.Resources.TryGetValue(seg.ResourceId.Value, out var res))
        {
            conflicts.Add(new(ConflictType.ResourceUnavailable, ConflictSeverity.Blocking, $"{seg.ServiceName}: the selected room/resource does not exist.", seg.Index));
            return;
        }

        if (!res.IsBookable)
            conflicts.Add(new(ConflictType.ResourceUnavailable, ConflictSeverity.Blocking, $"{res.Name} is blocked or inactive.", seg.Index));

        if (required.Count > 0 && !required.Contains(res.Type))
            conflicts.Add(new(ConflictType.ResourceTypeMismatch, ConflictSeverity.Blocking,
                $"{res.Name} cannot be used for {seg.ServiceName} (requires a {Describe(required)}).", seg.Index));

        if (ctx.LocationId is not null && res.LocationId is not null && res.LocationId != ctx.LocationId)
            conflicts.Add(new(ConflictType.ResourceWrongLocation, ConflictSeverity.Blocking, $"{res.Name} belongs to a different location.", seg.Index));

        var blocked = seg.Blocked;
        if (res.AvailableFrom is { } from && res.AvailableUntil is { } until &&
            (blocked.Start < blocked.Start.Date + from.ToTimeSpan() || blocked.End > blocked.Start.Date + until.ToTimeSpan()))
            conflicts.Add(new(ConflictType.ResourceUnavailable, ConflictSeverity.Blocking,
                $"{res.Name} is only available {from:t}–{until:t}.", seg.Index));

        foreach (var m in res.Maintenance.Where(m => m.Interval.Overlaps(blocked)))
            conflicts.Add(new(ConflictType.ResourceMaintenance, ConflictSeverity.Blocking,
                $"{res.Name} is unavailable ({m.Description}) from {m.Start:g} to {m.End:g}.", seg.Index));

        var overlapping = others.Where(b => b.ResourceId == res.Id &&
                                            TimeInterval.Overlaps(b.BlockStart, b.BlockEnd, seg.BlockStart, seg.BlockEnd)).ToList();
        if (overlapping.Count == 0) return;

        var peak = PeakConcurrency(overlapping.Select(o => new TimeInterval(o.BlockStart, o.BlockEnd)), blocked);
        if (peak + 1 > Math.Max(1, res.Capacity))
        {
            if (res.Capacity <= 1)
            {
                foreach (var b in overlapping)
                    conflicts.Add(new(ConflictType.ResourceOverlap, ConflictSeverity.Blocking,
                        $"{res.Name} is already booked {b.BlockStart:t}–{b.BlockEnd:t} ({b.Reference}).", seg.Index, b.AppointmentId));
            }
            else
            {
                conflicts.Add(new(ConflictType.ResourceCapacity, ConflictSeverity.Blocking,
                    $"{res.Name} is at its capacity of {res.Capacity} during {blocked.Start:t}–{blocked.End:t}.", seg.Index));
            }
        }
    }

    private static void CheckInternalOverlaps(IReadOnlyList<ProposedSegment> segments, List<ScheduleConflict> conflicts)
    {
        for (var i = 0; i < segments.Count; i++)
        for (var j = i + 1; j < segments.Count; j++)
        {
            var a = segments[i];
            var b = segments[j];
            if (!a.Blocked.Overlaps(b.Blocked)) continue;
            if (a.StaffMemberId == b.StaffMemberId)
                conflicts.Add(new(ConflictType.StaffOverlap, ConflictSeverity.Blocking,
                    $"{a.ServiceName} and {b.ServiceName} overlap for the same staff member.", b.Index));
            if (a.ResourceId is not null && a.ResourceId == b.ResourceId)
                conflicts.Add(new(ConflictType.ResourceOverlap, ConflictSeverity.Blocking,
                    $"{a.ServiceName} and {b.ServiceName} overlap in the same room/resource.", b.Index));
        }
    }

    /// <summary>Maximum number of intervals simultaneously active inside <paramref name="window"/>.</summary>
    public static int PeakConcurrency(IEnumerable<TimeInterval> intervals, TimeInterval window)
    {
        var events = new List<(DateTime At, int Delta)>();
        foreach (var iv in intervals)
        {
            if (!iv.Overlaps(window)) continue;
            events.Add((iv.Start < window.Start ? window.Start : iv.Start, +1));
            events.Add((iv.End > window.End ? window.End : iv.End, -1));
        }

        // Ends sort before starts at the same instant: half-open intervals that touch do not overlap.
        var peak = 0;
        var current = 0;
        foreach (var e in events.OrderBy(e => e.At).ThenBy(e => e.Delta))
        {
            current += e.Delta;
            peak = Math.Max(peak, current);
        }

        return peak;
    }

    private static string Describe(IReadOnlyList<ResourceType> types) =>
        string.Join(" or ", types.Select(t => ResourceTypeNames.Display(t).ToLowerInvariant()));
}

public static class ResourceTypeNames
{
    public static string Display(ResourceType type) => type switch
    {
        ResourceType.TreatmentRoom => "Treatment Room",
        ResourceType.MassageRoom => "Massage Room",
        ResourceType.HairStation => "Hair Station",
        ResourceType.NailStation => "Nail Station",
        ResourceType.Equipment => "Equipment",
        _ => "Other Resource",
    };
}
