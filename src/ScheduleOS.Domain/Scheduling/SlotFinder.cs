namespace ScheduleOS.Domain.Scheduling;

/// <summary>Request for one service in a "find available time" search.</summary>
public sealed record SlotServiceRequest(ServiceRules Service, IReadOnlyList<int> CandidateStaffIds);

/// <summary>
/// Searches for start times where every requested service can be performed sequentially
/// without any conflict (including soft rules such as working hours and breaks).
/// </summary>
public static class SlotFinder
{
    public static IReadOnlyList<SlotSuggestion> Find(
        IReadOnlyList<SlotServiceRequest> services,
        DateTime windowStart,
        DateTime windowEnd,
        int stepMinutes,
        SchedulingContext ctx,
        int maxResults = 12,
        int? excludeAppointmentId = null)
    {
        if (services.Count == 0 || stepMinutes <= 0 || windowEnd <= windowStart) return [];

        var results = new List<SlotSuggestion>();
        var planned = services.Select(s => new PlannedService(s.Service.Id, s.Service.Name, s.Service.DurationMinutes,
            s.Service.PrepMinutes, s.Service.CleanupMinutes, 0, null)).ToList();
        var total = SegmentPlanner.TotalMinutes(planned);

        for (var t = windowStart; t.AddMinutes(total) <= windowEnd && results.Count < maxResults; t = t.AddMinutes(stepMinutes))
        {
            if (t < ctx.Now) continue;
            var timing = SegmentPlanner.Plan(t, planned);
            var assignments = new List<SlotAssignment>();
            var chosen = new List<ProposedSegment>();
            var ok = true;

            for (var i = 0; i < services.Count && ok; i++)
            {
                var pick = PickAssignment(services[i], timing[i], ctx, chosen, excludeAppointmentId);
                if (pick is null)
                {
                    ok = false;
                    break;
                }

                chosen.Add(pick);
                ctx.Resources.TryGetValue(pick.ResourceId ?? -1, out var res);
                assignments.Add(new SlotAssignment(pick.ServiceId, pick.ServiceName, pick.StaffMemberId,
                    ctx.Staff[pick.StaffMemberId].Name, pick.ResourceId, res?.Name, pick.Start, pick.End));
            }

            if (ok) results.Add(new SlotSuggestion(timing[0].Start, timing[^1].End, assignments));
        }

        return results;
    }

    private static ProposedSegment? PickAssignment(SlotServiceRequest request, ProposedSegment timing,
        SchedulingContext ctx, List<ProposedSegment> alreadyChosen, int? excludeAppointmentId)
    {
        var required = request.Service.RequiredResourceTypes;
        var resourceCandidates = required.Count == 0
            ? new List<int?> { null }
            : ctx.Resources.Values.Where(r => r.IsBookable && required.Contains(r.Type) &&
                                              (ctx.LocationId is null || r.LocationId is null || r.LocationId == ctx.LocationId))
                .OrderBy(r => r.Name).Select(r => (int?)r.Id).ToList();

        foreach (var staffId in request.CandidateStaffIds)
        foreach (var resourceId in resourceCandidates)
        {
            var candidate = timing with { StaffMemberId = staffId, ResourceId = resourceId };
            var proposal = alreadyChosen.Append(candidate).ToList();
            if (ConflictDetector.Detect(proposal, ctx, excludeAppointmentId, checkLeadTime: false).Count == 0)
                return candidate;
        }

        return null;
    }
}
