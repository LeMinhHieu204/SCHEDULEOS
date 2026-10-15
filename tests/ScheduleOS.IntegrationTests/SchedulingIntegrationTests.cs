using Microsoft.Data.Sqlite;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Resources;
using ScheduleOS.Application.Staff;
using ScheduleOS.Domain;

namespace ScheduleOS.IntegrationTests;

public class SchedulingIntegrationTests
{
    private static DateTime Mon(int h, int m = 0) => TestHost.DefaultNow.Date.AddHours(h).AddMinutes(m);

    [Fact]
    public async Task Appointment_is_persisted_with_segments_history_and_reference()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();

        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var d = await appts.GetDetailAsync(r.Id);

        Assert.Matches(@"^AP-\d{4}-0001$", d.Reference);
        Assert.Equal(AppointmentStatus.Confirmed, d.Status);
        Assert.Equal(Mon(10), d.Start);
        Assert.Equal(Mon(11), d.End);
        Assert.Equal(12_000, d.TotalMinor);
        Assert.Equal(3_000, d.DepositRequiredMinor);
        Assert.Single(d.Segments);
        Assert.Contains(d.History, e => e.Type == AppointmentEventType.Created);
        Assert.Equal("Unpaid", d.PaymentStatus);
    }

    [Fact]
    public async Task Staff_double_booking_is_prevented()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));

        var ex = await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(10, 30), f.Alex, f.RoomB)));
        Assert.Contains(ex.Conflicts, c => c.Type == ConflictType.StaffOverlap);
        // The cleanup buffer (15 min) also blocks a booking that starts exactly at the previous service end.
        await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(11), f.Alex, f.RoomB)));
        // 11:15 is free again once the cleanup buffer has elapsed (a 30-minute consult ends before the 12:00 break).
        await appts.CreateAsync(f.Booking(Mon(11, 15), f.Alex, null, f.Consult));
    }

    [Fact]
    public async Task Room_double_booking_is_prevented()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));

        var ex = await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(10), f.Blair, f.RoomA)));
        Assert.Contains(ex.Conflicts, c => c.Type == ConflictType.ResourceOverlap);
        await appts.CreateAsync(f.Booking(Mon(10), f.Blair, f.RoomB));
    }

    [Fact]
    public async Task Leave_and_maintenance_restrictions_are_enforced()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();

        await h.Get<StaffService>().AddTimeOffAsync(f.Alex, Mon(0), Mon(24), TimeOffType.Leave, "Holiday");
        var leave = await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA)));
        Assert.Contains(leave.Conflicts, c => c.Type == ConflictType.StaffTimeOff);

        await h.Get<ResourceService>().AddMaintenanceAsync(f.RoomB, Mon(12, 30), Mon(14), "Equipment maintenance");
        var maint = await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(13, 30), f.Blair, f.RoomB)));
        Assert.Contains(maint.Conflicts, c => c.Type == ConflictType.ResourceMaintenance);

        // Overriding warnings never bypasses blocking rules.
        await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(13, 30), f.Blair, f.RoomB) with { OverrideWarnings = true }));
    }

    [Fact]
    public async Task Outside_working_hours_requires_an_explicit_authorised_override()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();

        var ex = await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(17, 30), f.Alex, f.RoomA)));
        Assert.All(ex.Conflicts, c => Assert.Equal(ConflictSeverity.Warning, c.Severity));
        var r = await appts.CreateAsync(f.Booking(Mon(17, 30), f.Alex, f.RoomA) with { OverrideWarnings = true });
        var d = await appts.GetDetailAsync(r.Id);
        Assert.Contains(d.History, e => e.Description.Contains("overridden"));
    }

    [Fact]
    public async Task Duplicate_submission_is_rejected()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var request = f.Booking(Mon(10), f.Alex, f.RoomA);
        var first = await appts.CreateAsync(request);
        var ex = await Assert.ThrowsAsync<DuplicateSubmissionException>(() => appts.CreateAsync(request));
        Assert.Equal(first.Reference, ex.Reference);
        Assert.Equal(1L, await h.SqlScalarAsync<long>("SELECT COUNT(*) FROM Appointments"));
    }

    [Fact]
    public async Task Reschedule_validates_conflicts_and_stale_versions()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var a = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        await appts.CreateAsync(f.Booking(Mon(14), f.Alex, f.RoomB));
        var d = await appts.GetDetailAsync(a.Id);

        var conflict = await Assert.ThrowsAsync<SchedulingConflictException>(() =>
            appts.RescheduleAsync(new RescheduleRequest { AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(14, 30) }));
        Assert.Contains(conflict.Conflicts, c => c.Type == ConflictType.StaffOverlap);
        Assert.Equal(Mon(10), (await appts.GetDetailAsync(a.Id)).Start); // unchanged

        await appts.RescheduleAsync(new RescheduleRequest { AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(15, 30) });
        var moved = await appts.GetDetailAsync(a.Id);
        Assert.Equal(Mon(15, 30), moved.Start);
        Assert.Contains(moved.History, e => e.Type == AppointmentEventType.Rescheduled);

        // Using the old version again is rejected (optimistic concurrency).
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            appts.RescheduleAsync(new RescheduleRequest { AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(9) }));
    }

    [Fact]
    public async Task Reassignment_and_resize_go_through_validation()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var a = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        await appts.CreateAsync(f.Booking(Mon(11, 30), f.Blair, null, f.Consult));
        var d = await appts.GetDetailAsync(a.Id);

        // Casey is not qualified for massage.
        await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.RescheduleAsync(new RescheduleRequest
        {
            AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(10), StaffBySegment = new Dictionary<int, int> { [0] = f.Casey },
        }));

        await appts.RescheduleAsync(new RescheduleRequest
        {
            AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(10), StaffBySegment = new Dictionary<int, int> { [0] = f.Blair },
        });
        d = await appts.GetDetailAsync(a.Id);
        Assert.Equal(f.Blair, d.Segments[0].StaffMemberId);

        // Extending to 90 minutes would overlap Blair's 11:30 booking (cleanup buffer included).
        await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.RescheduleAsync(new RescheduleRequest
        {
            AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(10), NewDurationMinutes = 90,
        }));
        await appts.RescheduleAsync(new RescheduleRequest { AppointmentId = a.Id, ExpectedVersion = d.Version, NewStart = Mon(10), NewDurationMinutes = 75 });
        Assert.Equal(Mon(11, 15), (await appts.GetDetailAsync(a.Id)).End);
    }

    [Fact]
    public async Task Multi_service_appointment_uses_sequential_segments_with_different_staff()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var r = await appts.CreateAsync(new CreateAppointmentRequest
        {
            CustomerId = f.Customer1, Start = Mon(9), InitialStatus = AppointmentStatus.Confirmed,
            Services = [new ServiceLineRequest(f.Massage, f.Alex, f.RoomA), new ServiceLineRequest(f.Facial, f.Blair, f.Treatment1)],
        });
        var d = await appts.GetDetailAsync(r.Id);
        Assert.Equal(2, d.Segments.Count);
        Assert.Equal(Mon(10, 20), d.Segments[1].Start); // 10:00 + 15 cleanup + 5 prep
        Assert.Equal(Mon(11, 5), d.End);
        Assert.Equal(21_000, d.TotalMinor);

        // Blair is now busy for the facial (10:15–11:15 blocked) but free during the massage.
        await Assert.ThrowsAsync<SchedulingConflictException>(() => appts.CreateAsync(f.Booking(Mon(10, 30), f.Blair, null, f.Consult)));
        await appts.CreateAsync(f.Booking(Mon(9), f.Blair, null, f.Consult));
    }

    [Fact]
    public async Task Lifecycle_transitions_are_enforced_and_recorded()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA, status: AppointmentStatus.Pending));

        async Task Move(AppointmentStatus to, string? reason = null) =>
            await appts.ChangeStatusAsync(r.Id, (await appts.GetDetailAsync(r.Id)).Version, to, reason);

        await Assert.ThrowsAsync<InvalidTransitionException>(() => Move(AppointmentStatus.Completed));
        await Move(AppointmentStatus.Confirmed);
        await Move(AppointmentStatus.CheckedIn);
        await Move(AppointmentStatus.InProgress);
        await Move(AppointmentStatus.Completed);
        var d = await appts.GetDetailAsync(r.Id);
        Assert.Equal(AppointmentStatus.Completed, d.Status);
        Assert.Equal(4, d.History.Count(e => e.Type == AppointmentEventType.StatusChanged));
        await Assert.ThrowsAsync<InvalidTransitionException>(() => Move(AppointmentStatus.Pending));

        // Controlled correction
        await appts.CorrectStatusAsync(r.Id, d.Version, "Completed by mistake");
        Assert.Equal(AppointmentStatus.InProgress, (await appts.GetDetailAsync(r.Id)).Status);
    }

    [Fact]
    public async Task Cancellation_frees_the_slot_and_no_show_waits_for_grace_period()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var v = (await appts.GetDetailAsync(r.Id)).Version;

        await Assert.ThrowsAsync<ValidationException>(() => appts.ChangeStatusAsync(r.Id, v, AppointmentStatus.Cancelled, ""));
        await appts.ChangeStatusAsync(r.Id, v, AppointmentStatus.Cancelled, "Customer is ill");
        await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA, customer: f.Customer2));

        var other = await appts.CreateAsync(f.Booking(Mon(14), f.Blair, f.RoomB));
        var ov = (await appts.GetDetailAsync(other.Id)).Version;
        await Assert.ThrowsAsync<ValidationException>(() => appts.ChangeStatusAsync(other.Id, ov, AppointmentStatus.NoShow));
        h.Clock.Now = Mon(14, 20);
        await appts.ChangeStatusAsync(other.Id, ov, AppointmentStatus.NoShow);
        Assert.Equal(AppointmentStatus.NoShow, (await appts.GetDetailAsync(other.Id)).Status);
    }

    [Fact]
    public async Task Reinstating_a_cancelled_booking_rechecks_the_slot()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        await appts.ChangeStatusAsync(r.Id, (await appts.GetDetailAsync(r.Id)).Version, AppointmentStatus.Cancelled, "Cancelled");
        await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA, customer: f.Customer2));
        await Assert.ThrowsAsync<SchedulingConflictException>(() =>
            appts.CorrectStatusAsync(r.Id, appts.GetDetailAsync(r.Id).Result.Version, "Customer called back"));
    }

    [Fact]
    public async Task Updating_services_recalculates_schedule_and_price()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var d = await appts.GetDetailAsync(r.Id);
        await appts.UpdateServicesAsync(new UpdateServicesRequest
        {
            AppointmentId = r.Id, ExpectedVersion = d.Version,
            Services = [new ServiceLineRequest(f.Massage, f.Alex, f.RoomA, null, 2_000), new ServiceLineRequest(f.Consult, f.Alex, null)],
        });
        d = await appts.GetDetailAsync(r.Id);
        Assert.Equal(2, d.Segments.Count);
        Assert.Equal(12_000 - 2_000 + 4_000, d.TotalMinor);
        Assert.Equal(Mon(11, 45), d.End); // 60 + 15 cleanup + 30
    }

    [Fact]
    public async Task Find_available_time_returns_conflict_free_suggestions()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        await appts.CreateAsync(f.Booking(Mon(9), f.Alex, f.RoomA));
        var slots = await h.Get<SchedulingService>().FindSlotsAsync(new FindSlotsRequest([f.Massage], DateOnly.FromDateTime(Mon(0)), f.Alex, new TimeOnly(9, 0), null, 1, 10));
        Assert.NotEmpty(slots);
        foreach (var s in slots)
        {
            var check = await h.Get<SchedulingService>().CheckAsync(new ConflictCheckRequest(s.Start,
                [new ServiceLineRequest(f.Massage, s.Assignments[0].StaffMemberId, s.Assignments[0].ResourceId)], null));
            Assert.True(check.IsClear, string.Join("; ", check.Conflicts.Select(c => c.Message)));
        }
    }

    [Fact]
    public async Task Calendar_returns_segments_blocks_and_conflict_flags()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var appts = h.Get<AppointmentService>();
        var r = await appts.CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var data = await appts.GetCalendarAsync(new CalendarQuery(Mon(0), Mon(0).AddDays(7), null, null, null, null));
        Assert.Contains(data.Segments, s => s.AppointmentId == r.Id && !s.HasConflict);
        Assert.Contains(data.Blocks, b => b.Kind == CalendarBlockKind.Break && b.StaffMemberId == f.Alex);
        Assert.Contains(data.Blocks, b => b.Kind == CalendarBlockKind.OffShift && b.Start.DayOfWeek == DayOfWeek.Saturday);

        // Leave recorded after booking → the existing appointment is flagged.
        var affected = await h.Get<StaffService>().AddTimeOffAsync(f.Alex, Mon(9), Mon(12), TimeOffType.Sick, "Sick");
        Assert.Single(affected);
        data = await appts.GetCalendarAsync(new CalendarQuery(Mon(0), Mon(0).AddDays(7), null, null, null, null));
        Assert.True(data.Segments.Single(s => s.AppointmentId == r.Id).HasConflict);
        Assert.Single(await h.Get<SchedulingService>().ScanConflictsAsync(Mon(0), Mon(0).AddDays(7), null));
    }
}

public class ConcurrencyTests
{
    private static DateTime Mon(int h, int m = 0) => TestHost.DefaultNow.Date.AddHours(h).AddMinutes(m);

    [Fact]
    public async Task Competing_bookings_for_the_same_staff_from_independent_connections_allow_exactly_one()
    {
        await using var a = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(a);
        await using var b = await a.CreatePeerAsync();
        var serviceA = a.Get<AppointmentService>();
        var serviceB = b.Get<AppointmentService>();

        for (var round = 0; round < 6; round++)
        {
            var start = Mon(9).AddMinutes(90 * round);
            if (start.Hour == 12) continue;
            using var gate = new Barrier(2);
            var t1 = Task.Run(async () => { gate.SignalAndWait(); return await Try(() => serviceA.CreateAsync(f.Booking(start, f.Alex, f.RoomA))); });
            var t2 = Task.Run(async () => { gate.SignalAndWait(); return await Try(() => serviceB.CreateAsync(f.Booking(start, f.Alex, f.RoomB, customer: f.Customer2))); });
            var results = await Task.WhenAll(t1, t2);
            Assert.Equal(1, results.Count(r => r is null));
            var failure = Assert.Single(results, r => r is not null);
            var conflict = Assert.IsType<SchedulingConflictException>(failure);
            Assert.Contains(conflict.Conflicts, c => c.Type == ConflictType.StaffOverlap);
        }

        Assert.Equal(0L, await a.SqlScalarAsync<long>(OverlapSql("StaffMemberId")));
    }

    [Fact]
    public async Task Competing_bookings_for_the_same_room_allow_exactly_one()
    {
        await using var a = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(a);
        await using var b = await a.CreatePeerAsync();
        using var gate = new Barrier(2);
        var t1 = Task.Run(async () => { gate.SignalAndWait(); return await Try(() => a.Get<AppointmentService>().CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA))); });
        var t2 = Task.Run(async () => { gate.SignalAndWait(); return await Try(() => b.Get<AppointmentService>().CreateAsync(f.Booking(Mon(10), f.Blair, f.RoomA, customer: f.Customer2))); });
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(1, results.Count(r => r is null));
        Assert.IsType<SchedulingConflictException>(results.Single(r => r is not null));
        Assert.Equal(0L, await a.SqlScalarAsync<long>(OverlapSql("ResourceId")));
    }

    [Fact]
    public async Task Concurrent_reschedules_into_the_same_slot_allow_exactly_one()
    {
        await using var a = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(a);
        await using var b = await a.CreatePeerAsync();
        var x = await a.Get<AppointmentService>().CreateAsync(f.Booking(Mon(9), f.Alex, f.RoomA));
        var y = await a.Get<AppointmentService>().CreateAsync(f.Booking(Mon(14), f.Alex, f.RoomB, customer: f.Customer2));
        var vx = (await a.Get<AppointmentService>().GetDetailAsync(x.Id)).Version;
        var vy = (await a.Get<AppointmentService>().GetDetailAsync(y.Id)).Version;

        using var gate = new Barrier(2);
        var t1 = Task.Run(async () => { gate.SignalAndWait(); return await Try(() => a.Get<AppointmentService>().RescheduleAsync(new RescheduleRequest { AppointmentId = x.Id, ExpectedVersion = vx, NewStart = Mon(16) })); });
        var t2 = Task.Run(async () => { gate.SignalAndWait(); return await Try(() => b.Get<AppointmentService>().RescheduleAsync(new RescheduleRequest { AppointmentId = y.Id, ExpectedVersion = vy, NewStart = Mon(16, 15) })); });
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(0L, await a.SqlScalarAsync<long>(OverlapSql("StaffMemberId")));
    }

    [Fact]
    public async Task Concurrent_edits_of_the_same_appointment_detect_stale_data()
    {
        await using var a = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(a);
        await using var b = await a.CreatePeerAsync();
        var x = await a.Get<AppointmentService>().CreateAsync(f.Booking(Mon(9), f.Alex, f.RoomA, status: AppointmentStatus.Pending));
        var v = (await a.Get<AppointmentService>().GetDetailAsync(x.Id)).Version;

        await a.Get<AppointmentService>().ChangeStatusAsync(x.Id, v, AppointmentStatus.Confirmed);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            b.Get<AppointmentService>().RescheduleAsync(new RescheduleRequest { AppointmentId = x.Id, ExpectedVersion = v, NewStart = Mon(15) }));
    }

    [Fact]
    public async Task Database_trigger_rejects_overlaps_even_when_application_checks_are_bypassed()
    {
        await using var h = await TestHost.CreateAsync();
        var f = await Fixture.CreateAsync(h);
        var r = await h.Get<AppointmentService>().CreateAsync(f.Booking(Mon(10), f.Alex, f.RoomA));
        var r2 = await h.Get<AppointmentService>().CreateAsync(f.Booking(Mon(15), f.Blair, f.RoomB, customer: f.Customer2));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => h.SqlExecAsync(
            $"UPDATE AppointmentSegments SET StaffMemberId = {f.Alex}, BlockStart = '{Mon(10, 30):yyyy-MM-dd HH:mm:ss}', BlockEnd = '{Mon(11, 30):yyyy-MM-dd HH:mm:ss}', \"Start\" = '{Mon(10, 30):yyyy-MM-dd HH:mm:ss}', \"End\" = '{Mon(11, 30):yyyy-MM-dd HH:mm:ss}' WHERE AppointmentId = {r2.Id}"));
        Assert.Contains("SCHEDULEOS_STAFF_OVERLAP", ex.Message);

        var ex2 = await Assert.ThrowsAsync<SqliteException>(() => h.SqlExecAsync(
            $"UPDATE AppointmentSegments SET ResourceId = {f.RoomA}, BlockStart = '{Mon(10, 30):yyyy-MM-dd HH:mm:ss}', BlockEnd = '{Mon(11, 30):yyyy-MM-dd HH:mm:ss}', \"Start\" = '{Mon(10, 30):yyyy-MM-dd HH:mm:ss}', \"End\" = '{Mon(11, 30):yyyy-MM-dd HH:mm:ss}' WHERE AppointmentId = {r2.Id}"));
        Assert.Contains("SCHEDULEOS_RESOURCE_OVERLAP", ex2.Message);
        Assert.True(r.Id > 0);
    }

    private static string OverlapSql(string column) => $"""
        SELECT COUNT(*) FROM AppointmentSegments a JOIN AppointmentSegments b
          ON a.{column} = b.{column} AND a.Id < b.Id AND a.AppointmentId <> b.AppointmentId
         WHERE a.IsBlocking = 1 AND b.IsBlocking = 1 AND a.{column} IS NOT NULL
           AND a.BlockStart < b.BlockEnd AND a.BlockEnd > b.BlockStart
        """;

    private static async Task<Exception?> Try(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
