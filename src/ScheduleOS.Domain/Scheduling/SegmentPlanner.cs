namespace ScheduleOS.Domain.Scheduling;

/// <summary>
/// Plans multi-service appointments as sequential segments.
/// Segment i+1 starts after segment i has ended and its cleanup buffer plus the next service's
/// preparation buffer have elapsed. Each segment blocks its staff member and resource for
/// [start - prep, end + cleanup).
/// </summary>
public static class SegmentPlanner
{
    public static IReadOnlyList<ProposedSegment> Plan(DateTime start, IReadOnlyList<PlannedService> services)
    {
        if (services.Count == 0) throw new ValidationException("Select at least one service.");

        var result = new List<ProposedSegment>(services.Count);
        var cursor = start;
        for (var i = 0; i < services.Count; i++)
        {
            var s = services[i];
            if (s.DurationMinutes <= 0) throw new ValidationException($"Service '{s.ServiceName}' must have a positive duration.");
            if (s.PrepMinutes < 0 || s.CleanupMinutes < 0) throw new ValidationException("Buffers cannot be negative.");

            if (i > 0) cursor = cursor.AddMinutes(services[i - 1].CleanupMinutes + s.PrepMinutes);
            var segStart = cursor;
            var segEnd = segStart.AddMinutes(s.DurationMinutes);
            result.Add(new ProposedSegment(
                i, s.ServiceId, s.ServiceName, s.StaffMemberId, s.ResourceId,
                segStart, segEnd,
                segStart.AddMinutes(-s.PrepMinutes), segEnd.AddMinutes(s.CleanupMinutes)));
            cursor = segEnd;
        }

        return result;
    }

    /// <summary>Total customer-facing duration including inner buffers between sequential services.</summary>
    public static int TotalMinutes(IReadOnlyList<PlannedService> services)
    {
        if (services.Count == 0) return 0;
        var total = services.Sum(s => s.DurationMinutes);
        for (var i = 1; i < services.Count; i++) total += services[i - 1].CleanupMinutes + services[i].PrepMinutes;
        return total;
    }
}
