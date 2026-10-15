using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Staff;

public sealed record StaffListItem(
    int Id,
    string FullName,
    string Initials,
    string Position,
    string? Phone,
    string? Email,
    EmploymentStatus Status,
    string? LocationName,
    string ColorHex,
    int ServiceCount,
    string WeeklyHours,
    int TodayAppointments,
    bool IsArchived,
    bool OnLeaveToday);

public sealed class StaffEditDto
{
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Position { get; set; } = "";
    public string? SkillsText { get; set; }
    public EmploymentStatus EmploymentStatus { get; set; } = EmploymentStatus.Active;
    public int? LocationId { get; set; }
    public string ColorHex { get; set; } = "#008C95";
    public List<int> ServiceIds { get; set; } = [];
}

public sealed record ShiftDto(DayOfWeek Day, TimeOnly Start, TimeOnly End, TimeOnly? BreakStart, TimeOnly? BreakEnd);

public sealed record TimeOffDto(int Id, DateTime Start, DateTime End, TimeOffType Type, string? Reason);

public sealed record StaffPerformanceDto(
    int StaffId,
    string Name,
    string Initials,
    string ColorHex,
    int Appointments,
    int Completed,
    int Cancelled,
    int NoShows,
    double ScheduledHours,
    double BookedHours,
    long ServiceRevenueMinor,
    double Utilization,
    double AverageServiceMinutes)
{
    public double CancellationRate => Appointments == 0 ? 0 : (Cancelled + NoShows) / (double)Appointments;
}

public sealed record StaffDetailDto(int Id, int Version, StaffEditDto Profile, bool IsArchived, IReadOnlyList<ShiftDto> Shifts,
    IReadOnlyList<TimeOffDto> TimeOff, StaffPerformanceDto Performance30Days, IReadOnlyList<Lookup> Services);

public sealed record AffectedAppointment(int Id, string Reference, DateTime Start, string CustomerName);

public sealed class StaffService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    public static readonly string[] Palette =
        ["#008C95", "#3689E6", "#8B5CF6", "#E7A23A", "#E45662", "#15A77F", "#0EA5E9", "#D946EF", "#F97316", "#64748B"];

    public async Task<IReadOnlyList<StaffListItem>> ListAsync(string? search = null, bool includeArchived = false, int? locationId = null, CancellationToken ct = default)
    {
        user.Require(Permission.ViewStaff, "view staff");
        await using var db = dbFactory.CreateReadContext();
        var q = db.Staff.AsNoTracking().Include(s => s.Shifts).Include(s => s.Location).AsQueryable();
        if (!includeArchived) q = q.Where(s => !s.IsArchived);
        if (locationId is not null) q = q.Where(s => s.LocationId == locationId || s.LocationId == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = Normalization.LikePattern(search);
            q = q.Where(s => EF.Functions.Like(s.FullName, like, "\\") || EF.Functions.Like(s.Position, like, "\\"));
        }

        var staff = await q.OrderBy(s => s.FullName).ToListAsync(ct);
        var ids = staff.Select(s => s.Id).ToList();
        var skillCounts = await db.StaffSkills.Where(k => ids.Contains(k.StaffMemberId)).GroupBy(k => k.StaffMemberId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var today = clock.Now.Date;
        var tomorrow = today.AddDays(1);
        var todayCounts = await db.AppointmentSegments.Where(s => s.IsBlocking && s.Start >= today && s.Start < tomorrow && ids.Contains(s.StaffMemberId))
            .GroupBy(s => s.StaffMemberId).Select(g => new { g.Key, Count = g.Select(x => x.AppointmentId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var now = clock.Now;
        var onLeave = await db.StaffTimeOff.Where(t => t.Start <= now && t.End > now).Select(t => t.StaffMemberId).Distinct().ToListAsync(ct);
        var hideContact = !user.Has(Permission.ManageStaff);

        return staff.Select(s => new StaffListItem(s.Id, s.FullName, s.Initials, s.Position, hideContact ? null : s.Phone, hideContact ? null : s.Email,
            s.EmploymentStatus, s.Location?.Name, s.ColorHex, skillCounts.GetValueOrDefault(s.Id), WeeklyHours(s.Shifts),
            todayCounts.GetValueOrDefault(s.Id), s.IsArchived, onLeave.Contains(s.Id))).ToList();
    }

    /// <summary>Staff qualified for a service (or all bookable staff when no service is given), for booking pickers.</summary>
    public async Task<IReadOnlyList<Lookup>> LookupAsync(int? serviceId = null, int? locationId = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var q = db.Staff.AsNoTracking().Where(s => !s.IsArchived && s.EmploymentStatus != EmploymentStatus.Inactive);
        if (serviceId is not null) q = q.Where(s => s.Skills.Any(k => k.ServiceId == serviceId));
        if (locationId is not null) q = q.Where(s => s.LocationId == locationId || s.LocationId == null);
        return await q.OrderBy(s => s.FullName).Select(s => new Lookup(s.Id, s.FullName, s.Position)).ToListAsync(ct);
    }

    public async Task<StaffDetailDto> GetAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.ViewStaff, "view staff");
        await using var db = dbFactory.CreateReadContext();
        var s = await db.Staff.AsNoTracking().Include(x => x.Skills).ThenInclude(k => k.Service).Include(x => x.Shifts)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Staff member", id);
        var since = clock.Now.Date.AddDays(-60);
        var timeOff = await db.StaffTimeOff.AsNoTracking().Where(t => t.StaffMemberId == id && t.End >= since).OrderBy(t => t.Start)
            .Select(t => new TimeOffDto(t.Id, t.Start, t.End, t.Type, t.Reason)).ToListAsync(ct);
        var perf = (await PerformanceAsync(db, clock.Now.Date.AddDays(-30), clock.Now.Date.AddDays(1), [id], ct)).FirstOrDefault()
                   ?? new StaffPerformanceDto(id, s.FullName, s.Initials, s.ColorHex, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var hide = !user.Has(Permission.ManageStaff);
        var profile = new StaffEditDto
        {
            FullName = s.FullName, Phone = hide ? null : s.Phone, Email = hide ? null : s.Email, Position = s.Position, SkillsText = s.SkillsText,
            EmploymentStatus = s.EmploymentStatus, LocationId = s.LocationId, ColorHex = s.ColorHex, ServiceIds = s.Skills.Select(k => k.ServiceId).ToList(),
        };
        return new StaffDetailDto(s.Id, s.Version, profile, s.IsArchived,
            s.Shifts.OrderBy(x => ((int)x.DayOfWeek + 6) % 7).ThenBy(x => x.StartTime).Select(x => new ShiftDto(x.DayOfWeek, x.StartTime, x.EndTime, x.BreakStart, x.BreakEnd)).ToList(),
            timeOff, perf, s.Skills.Where(k => k.Service != null).Select(k => new Lookup(k.ServiceId, k.Service!.Name)).OrderBy(l => l.Name).ToList());
    }

    public async Task<int> CreateAsync(StaffEditDto dto, IReadOnlyList<ShiftDto>? shifts = null, CancellationToken ct = default)
    {
        user.Require(Permission.ManageStaff, "add staff members");
        Validate(dto);
        if (shifts is not null) ValidateShifts(shifts);
        var id = await writer.ExecuteAsync(async (db, c) =>
        {
            var s = new StaffMember { CreatedAt = clock.Now, Version = 1 };
            Apply(s, dto);
            foreach (var sid in dto.ServiceIds.Distinct()) s.Skills.Add(new StaffSkill { ServiceId = sid });
            foreach (var sh in shifts ?? DefaultShifts()) s.Shifts.Add(ToEntity(sh));
            db.Staff.Add(s);
            await db.SaveChangesAsync(c);
            Audit.Add(db, user, clock, "Staff.Created", "Staff", s.Id, $"Staff member {s.FullName} added");
            await db.SaveChangesAsync(c);
            return s.Id;
        }, ct);
        notifier.Publish(DataArea.Staff);
        return id;
    }

    public async Task UpdateAsync(int id, int expectedVersion, StaffEditDto dto, CancellationToken ct = default)
    {
        user.Require(Permission.ManageStaff, "edit staff members");
        Validate(dto);
        await writer.ExecuteAsync(async (db, c) =>
        {
            var s = await db.Staff.Include(x => x.Skills).FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Staff member", id);
            if (s.Version != expectedVersion) throw new ConcurrencyConflictException("This staff profile was changed by someone else. Reload and try again.");
            Apply(s, dto);
            var wanted = dto.ServiceIds.Distinct().ToHashSet();
            db.StaffSkills.RemoveRange(s.Skills.Where(k => !wanted.Contains(k.ServiceId)));
            foreach (var sid in wanted.Where(w => s.Skills.All(k => k.ServiceId != w))) s.Skills.Add(new StaffSkill { ServiceId = sid });
            s.Version++;
            Audit.Add(db, user, clock, "Staff.Updated", "Staff", id, $"Staff member {s.FullName} updated");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Staff | DataArea.Appointments);
    }

    public async Task SetArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        user.Require(Permission.ManageStaff, "archive staff members");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var s = await db.Staff.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Staff member", id);
            if (archived && await db.AppointmentSegments.AnyAsync(x => x.StaffMemberId == id && x.IsBlocking && x.Start > clock.Now, c))
                throw new ValidationException($"{s.FullName} still has upcoming appointments. Reassign them first.");
            s.IsArchived = archived;
            s.Version++;
            Audit.Add(db, user, clock, archived ? "Staff.Archived" : "Staff.Restored", "Staff", id, $"Staff member {s.FullName} {(archived ? "archived" : "restored")}");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Staff);
    }

    /// <summary>Replaces the weekly working schedule. Returns upcoming appointments that now fall outside working hours.</summary>
    public async Task<IReadOnlyList<AffectedAppointment>> SaveShiftsAsync(int staffId, IReadOnlyList<ShiftDto> shifts, CancellationToken ct = default)
    {
        user.Require(Permission.ManageStaff, "change working schedules");
        ValidateShifts(shifts);
        await writer.ExecuteAsync(async (db, c) =>
        {
            var s = await db.Staff.Include(x => x.Shifts).FirstOrDefaultAsync(x => x.Id == staffId, c) ?? throw new NotFoundException("Staff member", staffId);
            db.StaffShifts.RemoveRange(s.Shifts);
            s.Shifts.Clear();
            foreach (var sh in shifts) s.Shifts.Add(ToEntity(sh));
            s.Version++;
            Audit.Add(db, user, clock, "Staff.Schedule", "Staff", staffId, $"Working schedule updated for {s.FullName}");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Staff | DataArea.Appointments);

        await using var read = dbFactory.CreateReadContext();
        var upcoming = await read.AppointmentSegments.AsNoTracking().Where(x => x.StaffMemberId == staffId && x.IsBlocking && x.Start > clock.Now)
            .Select(x => new { x.AppointmentId, x.Appointment!.Reference, x.Start, x.BlockStart, x.BlockEnd, Customer = x.Appointment.Customer!.FullName })
            .ToListAsync(ct);
        return upcoming.Where(u => !shifts.Any(sh => sh.Day == u.BlockStart.DayOfWeek &&
                                                      u.BlockStart.TimeOfDay >= sh.Start.ToTimeSpan() &&
                                                      u.BlockEnd.Date == u.BlockStart.Date && u.BlockEnd.TimeOfDay <= sh.End.ToTimeSpan()))
            .Select(u => new AffectedAppointment(u.AppointmentId, u.Reference, u.Start, u.Customer)).DistinctBy(a => a.Id).ToList();
    }

    /// <summary>Records leave / time off. Returns already-booked appointments that overlap and must be rescheduled.</summary>
    public async Task<IReadOnlyList<AffectedAppointment>> AddTimeOffAsync(int staffId, DateTime start, DateTime end, TimeOffType type, string? reason, CancellationToken ct = default)
    {
        user.Require(Permission.ManageStaff, "record time off");
        if (end <= start) throw new ValidationException("Time off must end after it starts.");
        if ((end - start).TotalDays > 366) throw new ValidationException("Time off cannot exceed one year.");
        var affected = await writer.ExecuteAsync(async (db, c) =>
        {
            var s = await db.Staff.FirstOrDefaultAsync(x => x.Id == staffId, c) ?? throw new NotFoundException("Staff member", staffId);
            if (await db.StaffTimeOff.AnyAsync(t => t.StaffMemberId == staffId && t.Start < end && t.End > start, c))
                throw new ValidationException("This period overlaps existing time off.");
            db.StaffTimeOff.Add(new StaffTimeOff { StaffMemberId = staffId, Start = start, End = end, Type = type, Reason = Normalization.Clean(reason) });
            Audit.Add(db, user, clock, "Staff.TimeOff", "Staff", staffId, $"{type} recorded for {s.FullName} {start:g} – {end:g}");
            await db.SaveChangesAsync(c);
            return await db.AppointmentSegments.Where(x => x.StaffMemberId == staffId && x.IsBlocking && x.BlockStart < end && x.BlockEnd > start)
                .Select(x => new AffectedAppointment(x.AppointmentId, x.Appointment!.Reference, x.Start, x.Appointment.Customer!.FullName))
                .Distinct().ToListAsync(c);
        }, ct);
        notifier.Publish(DataArea.Staff | DataArea.Appointments);
        return affected;
    }

    public async Task RemoveTimeOffAsync(int timeOffId, CancellationToken ct = default)
    {
        user.Require(Permission.ManageStaff, "remove time off");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var t = await db.StaffTimeOff.FirstOrDefaultAsync(x => x.Id == timeOffId, c) ?? throw new NotFoundException("Time off", timeOffId);
            db.StaffTimeOff.Remove(t);
            Audit.Add(db, user, clock, "Staff.TimeOffRemoved", "Staff", t.StaffMemberId, $"Time off {t.Start:d} – {t.End:d} removed");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Staff | DataArea.Appointments);
    }

    public async Task<IReadOnlyList<StaffPerformanceDto>> GetPerformanceAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        user.Require(Permission.ViewStaff, "view staff performance");
        await using var db = dbFactory.CreateReadContext();
        var perf = await PerformanceAsync(db, from, to, null, ct);
        if (!user.Has(Permission.ViewReports) && user.StaffMemberId is { } own) perf = perf.Where(p => p.StaffId == own).ToList();
        return perf;
    }

    /// <summary>
    /// Performance per staff member. Revenue is the value of completed service segments performed by the employee
    /// (line total after discount, before tax), so multi-staff appointments are split correctly per segment.
    /// </summary>
    internal static async Task<List<StaffPerformanceDto>> PerformanceAsync(IAppDbContext db, DateTime from, DateTime to, IReadOnlyCollection<int>? staffIds, CancellationToken ct)
    {
        var staffQ = db.Staff.AsNoTracking().Include(s => s.Shifts).Where(s => !s.IsArchived);
        if (staffIds is not null) staffQ = staffQ.Where(s => staffIds.Contains(s.Id));
        var staff = await staffQ.ToListAsync(ct);
        var ids = staff.Select(s => s.Id).ToList();
        var segs = await db.AppointmentSegments.AsNoTracking().Where(s => ids.Contains(s.StaffMemberId) && s.Start >= from && s.Start < to)
            .Select(s => new { s.StaffMemberId, s.AppointmentId, s.Appointment!.Status, s.Start, s.End, s.UnitPriceMinor, s.DiscountMinor })
            .ToListAsync(ct);
        var timeOff = await db.StaffTimeOff.AsNoTracking().Where(t => ids.Contains(t.StaffMemberId) && t.Start < to && t.End > from).ToListAsync(ct);

        return staff.Select(s =>
        {
            var mine = segs.Where(x => x.StaffMemberId == s.Id).ToList();
            var byAppt = mine.GroupBy(x => x.AppointmentId).Select(g => g.First().Status).ToList();
            var completed = mine.Where(x => x.Status == AppointmentStatus.Completed).ToList();
            var bookedMinutes = mine.Where(x => AppointmentLifecycle.IsBlocking(x.Status)).Sum(x => (x.End - x.Start).TotalMinutes);
            var scheduledMinutes = ScheduledMinutes(s.Shifts, timeOff.Where(t => t.StaffMemberId == s.Id).ToList(), from, to);
            return new StaffPerformanceDto(s.Id, s.FullName, s.Initials, s.ColorHex,
                byAppt.Count,
                byAppt.Count(x => x == AppointmentStatus.Completed),
                byAppt.Count(x => x == AppointmentStatus.Cancelled),
                byAppt.Count(x => x == AppointmentStatus.NoShow),
                Math.Round(scheduledMinutes / 60, 1),
                Math.Round(bookedMinutes / 60, 1),
                completed.Sum(x => x.UnitPriceMinor - x.DiscountMinor),
                scheduledMinutes <= 0 ? 0 : Math.Min(1, bookedMinutes / scheduledMinutes),
                completed.Count == 0 ? 0 : completed.Average(x => (x.End - x.Start).TotalMinutes));
        }).OrderByDescending(p => p.ServiceRevenueMinor).ThenByDescending(p => p.Completed).ToList();
    }

    internal static double ScheduledMinutes(IEnumerable<StaffShift> shifts, IReadOnlyList<StaffTimeOff> timeOff, DateTime from, DateTime to)
    {
        double total = 0;
        var shiftList = shifts.ToList();
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            foreach (var sh in shiftList.Where(x => x.DayOfWeek == day.DayOfWeek))
            {
                var start = day + sh.StartTime.ToTimeSpan();
                var end = day + sh.EndTime.ToTimeSpan();
                var minutes = (end - start).TotalMinutes;
                if (sh.BreakStart is { } bs && sh.BreakEnd is { } be && be > bs) minutes -= (be - bs).TotalMinutes;
                foreach (var off in timeOff)
                {
                    var os = off.Start > start ? off.Start : start;
                    var oe = off.End < end ? off.End : end;
                    if (oe > os) minutes -= (oe - os).TotalMinutes;
                }

                total += Math.Max(0, minutes);
            }
        }

        return total;
    }

    public static IReadOnlyList<ShiftDto> DefaultShifts() =>
    [
        .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(d => new ShiftDto(d, new TimeOnly(9, 0), new TimeOnly(18, 0), new TimeOnly(12, 0), new TimeOnly(13, 0))),
    ];

    private static void ValidateShifts(IReadOnlyList<ShiftDto> shifts)
    {
        foreach (var g in shifts.GroupBy(s => s.Day))
        {
            var list = g.OrderBy(s => s.Start).ToList();
            foreach (var s in list)
            {
                if (s.End <= s.Start) throw new ValidationException($"{s.Day}: shift end must be after shift start.");
                if (s.BreakStart.HasValue != s.BreakEnd.HasValue) throw new ValidationException($"{s.Day}: enter both break start and end.");
                if (s.BreakStart is { } bs && s.BreakEnd is { } be && (be <= bs || bs < s.Start || be > s.End))
                    throw new ValidationException($"{s.Day}: the break must lie inside the shift.");
            }

            for (var i = 1; i < list.Count; i++)
                if (list[i].Start < list[i - 1].End) throw new ValidationException($"{g.Key}: shifts overlap.");
        }
    }

    private static void Validate(StaffEditDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.FullName)) errors.Add("Full name is required.");
        if (string.IsNullOrWhiteSpace(dto.Position)) errors.Add("Position is required.");
        if (!string.IsNullOrWhiteSpace(dto.Email) && !Normalization.IsValidEmail(dto.Email)) errors.Add("The email address is not valid.");
        if (string.IsNullOrWhiteSpace(dto.ColorHex) || !System.Text.RegularExpressions.Regex.IsMatch(dto.ColorHex, "^#[0-9A-Fa-f]{6}$")) errors.Add("Choose a valid colour.");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static void Apply(StaffMember s, StaffEditDto dto)
    {
        s.FullName = dto.FullName.Trim();
        s.Phone = Normalization.Clean(dto.Phone);
        s.Email = Normalization.Clean(dto.Email);
        s.Position = dto.Position.Trim();
        s.SkillsText = Normalization.Clean(dto.SkillsText);
        s.EmploymentStatus = dto.EmploymentStatus;
        s.LocationId = dto.LocationId;
        s.ColorHex = dto.ColorHex.ToUpperInvariant();
    }

    private static StaffShift ToEntity(ShiftDto sh) => new()
    {
        DayOfWeek = sh.Day, StartTime = sh.Start, EndTime = sh.End, BreakStart = sh.BreakStart, BreakEnd = sh.BreakEnd,
    };

    private static string WeeklyHours(IEnumerable<StaffShift> shifts)
    {
        var minutes = shifts.Sum(s => (s.EndTime - s.StartTime).TotalMinutes -
                                      (s.BreakStart is { } bs && s.BreakEnd is { } be ? (be - bs).TotalMinutes : 0));
        var days = shifts.Select(s => s.DayOfWeek).Distinct().Count();
        return days == 0 ? "No schedule" : $"{minutes / 60:0.#} h / week · {days} days";
    }
}
