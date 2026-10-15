namespace ScheduleOS.Domain.Scheduling;

public sealed record ScheduleConflict(
    ConflictType Type,
    ConflictSeverity Severity,
    string Message,
    int? SegmentIndex = null,
    int? OtherAppointmentId = null);

/// <summary>Input for one service when planning a sequential appointment.</summary>
public sealed record PlannedService(
    int ServiceId,
    string ServiceName,
    int DurationMinutes,
    int PrepMinutes,
    int CleanupMinutes,
    int StaffMemberId,
    int? ResourceId);

public sealed record ProposedSegment(
    int Index,
    int ServiceId,
    string ServiceName,
    int StaffMemberId,
    int? ResourceId,
    DateTime Start,
    DateTime End,
    DateTime BlockStart,
    DateTime BlockEnd)
{
    public TimeInterval Service => new(Start, End);
    public TimeInterval Blocked => new(BlockStart, BlockEnd);
}

public sealed record ShiftInfo(DayOfWeek Day, TimeOnly Start, TimeOnly End, TimeOnly? BreakStart = null, TimeOnly? BreakEnd = null);

public sealed record BlockInfo(DateTime Start, DateTime End, string Description)
{
    public TimeInterval Interval => new(Start, End);
}

public sealed class StaffRules
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public bool IsBookable { get; init; } = true;
    public int? LocationId { get; init; }
    public IReadOnlyList<ShiftInfo> Shifts { get; init; } = [];
    public IReadOnlyList<BlockInfo> TimeOff { get; init; } = [];
    public IReadOnlySet<int> QualifiedServiceIds { get; init; } = new HashSet<int>();
}

public sealed class ResourceRules
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public ResourceType Type { get; init; }
    public int Capacity { get; init; } = 1;
    public bool IsBookable { get; init; } = true;
    public int? LocationId { get; init; }
    public TimeOnly? AvailableFrom { get; init; }
    public TimeOnly? AvailableUntil { get; init; }
    public IReadOnlyList<BlockInfo> Maintenance { get; init; } = [];
}

public sealed class ServiceRules
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public bool IsBookable { get; init; } = true;
    public int DurationMinutes { get; init; }
    public int PrepMinutes { get; init; }
    public int CleanupMinutes { get; init; }
    public IReadOnlyList<ResourceType> RequiredResourceTypes { get; init; } = [];
}

public sealed record OpeningHours(DayOfWeek Day, bool IsOpen, TimeOnly Open, TimeOnly Close);

public sealed record ExistingBooking(
    int AppointmentId,
    string Reference,
    string CustomerName,
    int StaffMemberId,
    int? ResourceId,
    DateTime BlockStart,
    DateTime BlockEnd);

/// <summary>Everything the conflict detector needs to know, loaded for a bounded time window.</summary>
public sealed class SchedulingContext
{
    public Dictionary<int, StaffRules> Staff { get; init; } = [];
    public Dictionary<int, ResourceRules> Resources { get; init; } = [];
    public Dictionary<int, ServiceRules> Services { get; init; } = [];
    public IReadOnlyList<OpeningHours> OpeningHours { get; init; } = [];
    public IReadOnlyDictionary<DateOnly, string> Closures { get; init; } = new Dictionary<DateOnly, string>();
    public IReadOnlyList<ExistingBooking> ExistingBookings { get; init; } = [];
    public int? LocationId { get; init; }
    public DateTime Now { get; init; }
    public int MinimumLeadMinutes { get; init; }
}

public sealed record SlotAssignment(int ServiceId, string ServiceName, int StaffMemberId, string StaffName, int? ResourceId, string? ResourceName, DateTime Start, DateTime End);

public sealed record SlotSuggestion(DateTime Start, DateTime End, IReadOnlyList<SlotAssignment> Assignments)
{
    public string StaffSummary => string.Join(", ", Assignments.Select(a => a.StaffName).Distinct());
    public string ResourceSummary => string.Join(", ", Assignments.Where(a => a.ResourceName != null).Select(a => a.ResourceName!).Distinct());
}
