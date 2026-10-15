using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.UnitTests;

public class SchedulingEngineTests
{
    // Monday 2030-03-04
    private static readonly DateTime Day = new(2030, 3, 4);
    private static DateTime At(int h, int m = 0) => Day.AddHours(h).AddMinutes(m);

    private static SchedulingContext Context(IEnumerable<ExistingBooking>? bookings = null, Action<ContextBuilder>? configure = null)
    {
        var b = new ContextBuilder();
        configure?.Invoke(b);
        return new SchedulingContext
        {
            Staff = b.Staff.ToDictionary(s => s.Id),
            Resources = b.Resources.ToDictionary(r => r.Id),
            Services = b.Services.ToDictionary(s => s.Id),
            OpeningHours = Enum.GetValues<DayOfWeek>().Select(d => new OpeningHours(d, d != DayOfWeek.Sunday, new TimeOnly(9, 0), new TimeOnly(19, 0))).ToList(),
            Closures = b.Closures,
            ExistingBookings = (bookings ?? []).ToList(),
            Now = Day.AddDays(-1),
        };
    }

    internal sealed class ContextBuilder
    {
        public List<StaffRules> Staff { get; } =
        [
            new() { Id = 1, Name = "Alex", Shifts = Weekdays(9, 18, 12, 13), QualifiedServiceIds = new HashSet<int> { 10, 11, 12 } },
            new() { Id = 2, Name = "Blair", Shifts = Weekdays(9, 18), QualifiedServiceIds = new HashSet<int> { 10, 11, 12 } },
        ];

        public List<ResourceRules> Resources { get; } =
        [
            new() { Id = 100, Name = "Room 1", Type = ResourceType.MassageRoom },
            new() { Id = 101, Name = "Room 2", Type = ResourceType.MassageRoom },
            new() { Id = 200, Name = "Studio", Type = ResourceType.TreatmentRoom, Capacity = 2 },
        ];

        public List<ServiceRules> Services { get; } =
        [
            new() { Id = 10, Name = "Massage", DurationMinutes = 60, CleanupMinutes = 15, RequiredResourceTypes = [ResourceType.MassageRoom] },
            new() { Id = 11, Name = "Facial", DurationMinutes = 45, PrepMinutes = 5, CleanupMinutes = 10, RequiredResourceTypes = [ResourceType.TreatmentRoom] },
            new() { Id = 12, Name = "Consult", DurationMinutes = 30 },
        ];

        public Dictionary<DateOnly, string> Closures { get; } = [];

        private static List<ShiftInfo> Weekdays(int from, int to, int? breakFrom = null, int? breakTo = null) =>
        [
            .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                .Select(d => new ShiftInfo(d, new TimeOnly(from, 0), new TimeOnly(to, 0),
                    breakFrom is null ? null : new TimeOnly(breakFrom.Value, 0), breakTo is null ? null : new TimeOnly(breakTo.Value, 0))),
        ];
    }

    private static ProposedSegment Seg(int staff, int? resource, DateTime start, int minutes, int serviceId = 10, int prep = 0, int cleanup = 0, int index = 0) =>
        new(index, serviceId, "Service " + serviceId, staff, resource, start, start.AddMinutes(minutes), start.AddMinutes(-prep), start.AddMinutes(minutes + cleanup));

    private static ExistingBooking Booking(int staff, int? resource, DateTime start, DateTime end, int appointmentId = 99) =>
        new(appointmentId, "AP-X", "Existing Customer", staff, resource, start, end);

    [Fact]
    public void Clean_booking_has_no_conflicts()
    {
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10), 60, cleanup: 15)], Context());
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Overlapping_staff_booking_is_blocking()
    {
        var ctx = Context([Booking(1, 101, At(10, 30), At(11, 30))]);
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10), 60)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.StaffOverlap && c.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Touching_intervals_do_not_conflict()
    {
        // Half-open intervals: [9,10) and [10,11) do not overlap.
        var ctx = Context([Booking(1, 100, At(9), At(10))]);
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10), 60)], ctx);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Cleanup_buffer_creates_overlap_with_next_booking()
    {
        // Service ends at 11:00 but cleanup runs until 11:15; the next booking starts at 11:00.
        var ctx = Context([Booking(1, 100, At(11), At(12))]);
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10), 60, cleanup: 15)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.StaffOverlap);
        Assert.Contains(conflicts, c => c.Type == ConflictType.ResourceOverlap);
    }

    [Fact]
    public void Same_appointment_is_excluded_when_rescheduling()
    {
        var ctx = Context([Booking(1, 100, At(10), At(11), appointmentId: 5)]);
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10, 30), 60)], ctx, excludeAppointmentId: 5);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Resource_with_capacity_one_cannot_be_double_booked()
    {
        var ctx = Context([Booking(2, 100, At(10), At(11))]);
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10, 15), 30)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.ResourceOverlap);
        Assert.DoesNotContain(conflicts, c => c.Type == ConflictType.StaffOverlap);
    }

    [Fact]
    public void Resource_capacity_is_enforced_at_peak_concurrency()
    {
        // Studio has capacity 2. One booking overlaps → ok; two simultaneous bookings → over capacity.
        var one = Context([Booking(2, 200, At(10), At(11))]);
        Assert.Empty(ConflictDetector.Detect([Seg(1, 200, At(10), 45, serviceId: 11)], one));

        var two = Context([Booking(2, 200, At(10), At(11), 7), Booking(3, 200, At(10, 30), At(11, 30), 8)]);
        Assert.Contains(ConflictDetector.Detect([Seg(1, 200, At(10, 30), 30, serviceId: 11)], two), c => c.Type == ConflictType.ResourceCapacity);

        // Sequential (non-simultaneous) bookings keep peak at 1 so capacity 2 is fine.
        var sequential = Context([Booking(2, 200, At(9), At(10), 7), Booking(3, 200, At(10), At(11), 8)]);
        Assert.Empty(ConflictDetector.Detect([Seg(1, 200, At(9, 30), 60, serviceId: 11)], sequential));
    }

    [Fact]
    public void Outside_working_hours_is_an_overridable_warning()
    {
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(17, 30), 60)], Context());
        var c = Assert.Single(conflicts, x => x.Type == ConflictType.OutsideStaffHours);
        Assert.Equal(ConflictSeverity.Warning, c.Severity);
    }

    [Fact]
    public void Staff_not_working_that_day_is_reported()
    {
        var saturday = Day.AddDays(5).AddHours(10);
        var conflicts = ConflictDetector.Detect([Seg(1, 100, saturday, 60)], Context());
        Assert.Contains(conflicts, c => c.Type == ConflictType.OutsideStaffHours && c.Message.Contains("does not work"));
    }

    [Fact]
    public void Break_overlap_is_reported()
    {
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(11, 30), 60)], Context());
        Assert.Contains(conflicts, c => c.Type == ConflictType.StaffBreak);
    }

    [Fact]
    public void Time_off_is_blocking()
    {
        var ctx = Context(configure: b => b.Staff[0] = new StaffRules
        {
            Id = 1, Name = "Alex", Shifts = b.Staff[1].Shifts, QualifiedServiceIds = new HashSet<int> { 10 },
            TimeOff = [new BlockInfo(Day, Day.AddDays(1), "Leave")],
        });
        var conflicts = ConflictDetector.Detect([Seg(1, 100, At(10), 60)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.StaffTimeOff && c.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Resource_maintenance_is_blocking()
    {
        var ctx = Context(configure: b => b.Resources[0] = new ResourceRules
        {
            Id = 100, Name = "Room 1", Type = ResourceType.MassageRoom, Maintenance = [new BlockInfo(At(12, 30), At(14), "Maintenance")],
        });
        var conflicts = ConflictDetector.Detect([Seg(2, 100, At(13), 60)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.ResourceMaintenance && c.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Unqualified_staff_and_wrong_resource_type_are_blocking()
    {
        var ctx = Context(configure: b => b.Staff[1] = new StaffRules { Id = 2, Name = "Blair", Shifts = b.Staff[0].Shifts, QualifiedServiceIds = new HashSet<int>() });
        var conflicts = ConflictDetector.Detect([Seg(2, 200, At(9), 60)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.StaffNotQualified);
        Assert.Contains(conflicts, c => c.Type == ConflictType.ResourceTypeMismatch);
    }

    [Fact]
    public void Missing_required_resource_is_blocking()
    {
        var conflicts = ConflictDetector.Detect([Seg(1, null, At(9), 60)], Context());
        Assert.Contains(conflicts, c => c.Type == ConflictType.MissingResource);
    }

    [Fact]
    public void Business_closure_and_hours_are_checked()
    {
        var closed = Context(configure: b => b.Closures[DateOnly.FromDateTime(Day)] = "Public holiday");
        Assert.Contains(ConflictDetector.Detect([Seg(1, 100, At(10), 60)], closed), c => c.Type == ConflictType.BusinessClosed);

        var late = ConflictDetector.Detect([Seg(1, 100, At(18, 30), 60)], Context());
        Assert.Contains(late, c => c.Type == ConflictType.OutsideBusinessHours);
    }

    [Fact]
    public void Past_start_is_flagged_by_lead_time_check()
    {
        var ctx = Context();
        var conflicts = ConflictDetector.Detect([Seg(1, 100, Day.AddDays(-2).AddHours(10), 60)], ctx);
        Assert.Contains(conflicts, c => c.Type == ConflictType.LeadTime);
    }

    [Fact]
    public void Segment_planner_sequences_services_with_buffers()
    {
        var segs = SegmentPlanner.Plan(At(10),
        [
            new PlannedService(10, "Massage", 60, 0, 15, 1, 100),
            new PlannedService(11, "Facial", 45, 5, 10, 2, 200),
        ]);
        Assert.Equal(At(10), segs[0].Start);
        Assert.Equal(At(11), segs[0].End);
        Assert.Equal(At(11, 15), segs[0].BlockEnd);
        Assert.Equal(At(11, 20), segs[1].Start);     // 11:00 + 15 cleanup + 5 prep
        Assert.Equal(At(11, 15), segs[1].BlockStart);
        Assert.Equal(At(12, 5), segs[1].End);
        Assert.Equal(At(12, 15), segs[1].BlockEnd);
        Assert.Equal(125, SegmentPlanner.TotalMinutes(
        [
            new PlannedService(10, "Massage", 60, 0, 15, 1, null),
            new PlannedService(11, "Facial", 45, 5, 10, 2, null),
        ]));
    }

    [Fact]
    public void Segment_planner_rejects_invalid_input()
    {
        Assert.Throws<ValidationException>(() => SegmentPlanner.Plan(At(10), []));
        Assert.Throws<ValidationException>(() => SegmentPlanner.Plan(At(10), [new PlannedService(1, "Bad", 0, 0, 0, 1, null)]));
    }

    [Fact]
    public void Two_sequential_services_with_different_staff_validate_independently()
    {
        // Blair is busy during the facial window → only the second segment conflicts.
        var ctx = Context([Booking(2, null, At(11, 30), At(12))]);
        var segs = SegmentPlanner.Plan(At(10),
        [
            new PlannedService(10, "Massage", 60, 0, 15, 1, 100),
            new PlannedService(11, "Facial", 45, 5, 10, 2, 200),
        ]);
        var conflicts = ConflictDetector.Detect(segs, ctx);
        var staffConflict = Assert.Single(conflicts, c => c.Type == ConflictType.StaffOverlap);
        Assert.Equal(1, staffConflict.SegmentIndex);
    }

    [Fact]
    public void Slot_finder_returns_only_conflict_free_slots_and_alternatives()
    {
        var ctx = Context([Booking(1, 100, At(9), At(12)), Booking(2, 101, At(9), At(10))]);
        var svc = ctx.Services[10];
        var slots = SlotFinder.Find([new SlotServiceRequest(svc, [1, 2])], At(9), At(18), 15, ctx, maxResults: 50);
        Assert.NotEmpty(slots);
        foreach (var slot in slots)
        {
            var a = slot.Assignments.Single();
            var seg = Seg(a.StaffMemberId, a.ResourceId, a.Start, 60, cleanup: 15);
            Assert.Empty(ConflictDetector.Detect([seg], ctx, checkLeadTime: false));
        }

        // At 10:00 Alex is busy, so the slot must be served by Blair.
        var ten = slots.First(s => s.Start == At(10));
        Assert.Equal(2, ten.Assignments.Single().StaffMemberId);
    }

    [Fact]
    public void Peak_concurrency_treats_touching_intervals_as_sequential()
    {
        var window = new TimeInterval(At(9), At(12));
        Assert.Equal(1, ConflictDetector.PeakConcurrency([new(At(9), At(10)), new(At(10), At(11))], window));
        Assert.Equal(2, ConflictDetector.PeakConcurrency([new(At(9), At(10, 30)), new(At(10), At(11))], window));
    }
}
