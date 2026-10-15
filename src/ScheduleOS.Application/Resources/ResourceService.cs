using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Staff;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Application.Resources;

public enum ResourceState { Available, InUse, Maintenance, Blocked }

public sealed record ResourceListItem(
    int Id,
    string Name,
    ResourceType Type,
    string TypeName,
    int? LocationId,
    string? LocationName,
    int Capacity,
    bool IsActive,
    bool IsArchived,
    ResourceState State,
    string StateText,
    string? NextBooking,
    string? Notes,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableUntil);

public sealed class ResourceEditDto
{
    public string Name { get; set; } = "";
    public ResourceType Type { get; set; }
    public int? LocationId { get; set; }
    public int Capacity { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public TimeOnly? AvailableFrom { get; set; }
    public TimeOnly? AvailableUntil { get; set; }
}

public sealed record MaintenanceDto(int Id, int ResourceId, string ResourceName, DateTime Start, DateTime End, string Reason);

public sealed class ResourceService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    public async Task<IReadOnlyList<ResourceListItem>> ListAsync(bool includeArchived = false, int? locationId = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var now = clock.Now;
        var q = db.Resources.AsNoTracking().Include(r => r.Location).AsQueryable();
        if (!includeArchived) q = q.Where(r => !r.IsArchived);
        if (locationId is not null) q = q.Where(r => r.LocationId == locationId || r.LocationId == null);
        var resources = await q.OrderBy(r => r.Type).ThenBy(r => r.Name).ToListAsync(ct);
        var ids = resources.Select(r => r.Id).ToList();
        var maintenanceNow = await db.ResourceMaintenance.AsNoTracking().Where(m => ids.Contains(m.ResourceId) && m.Start <= now && m.End > now).ToListAsync(ct);
        var inUse = await db.AppointmentSegments.AsNoTracking().Where(s => s.ResourceId != null && ids.Contains(s.ResourceId.Value) && s.IsBlocking &&
                                                                          s.BlockStart <= now && s.BlockEnd > now)
            .Select(s => s.ResourceId!.Value).ToListAsync(ct);
        var endOfDay = now.Date.AddDays(1);
        var next = await db.AppointmentSegments.AsNoTracking().Where(s => s.ResourceId != null && ids.Contains(s.ResourceId.Value) && s.IsBlocking &&
                                                                         s.Start > now && s.Start < endOfDay)
            .GroupBy(s => s.ResourceId!.Value).Select(g => new { g.Key, Next = g.Min(s => s.Start) }).ToDictionaryAsync(x => x.Key, x => x.Next, ct);

        return resources.Select(r =>
        {
            var m = maintenanceNow.FirstOrDefault(x => x.ResourceId == r.Id);
            var state = !r.IsActive ? ResourceState.Blocked : m is not null ? ResourceState.Maintenance :
                inUse.Count(x => x == r.Id) >= Math.Max(1, r.Capacity) ? ResourceState.InUse : ResourceState.Available;
            var text = state switch
            {
                ResourceState.Blocked => "Unavailable",
                ResourceState.Maintenance => "Maintenance",
                ResourceState.InUse => "In Use",
                _ => "Available",
            };
            return new ResourceListItem(r.Id, r.Name, r.Type, ResourceTypeNames.Display(r.Type), r.LocationId, r.Location?.Name, r.Capacity,
                r.IsActive, r.IsArchived, state, text, next.TryGetValue(r.Id, out var n) ? $"Next booking {n:t}" : null, r.Notes,
                r.AvailableFrom, r.AvailableUntil);
        }).ToList();
    }

    public async Task<IReadOnlyList<Lookup>> LookupAsync(IReadOnlyCollection<ResourceType>? types = null, int? locationId = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var q = db.Resources.AsNoTracking().Where(r => !r.IsArchived && r.IsActive);
        if (types is { Count: > 0 }) q = q.Where(r => types.Contains(r.Type));
        if (locationId is not null) q = q.Where(r => r.LocationId == locationId || r.LocationId == null);
        var rows = await q.OrderBy(r => r.Name).ToListAsync(ct);
        return rows.Select(r => new Lookup(r.Id, r.Name, ResourceTypeNames.Display(r.Type))).ToList();
    }

    public async Task<(ResourceEditDto Dto, int Version)> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var r = await db.Resources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Resource", id);
        return (new ResourceEditDto
        {
            Name = r.Name, Type = r.Type, LocationId = r.LocationId, Capacity = r.Capacity, IsActive = r.IsActive, Notes = r.Notes,
            AvailableFrom = r.AvailableFrom, AvailableUntil = r.AvailableUntil,
        }, r.Version);
    }

    public async Task<int> CreateAsync(ResourceEditDto dto, CancellationToken ct = default)
    {
        user.Require(Permission.ManageResources, "add rooms and resources");
        Validate(dto);
        var id = await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.Resources.AnyAsync(r => r.Name == dto.Name.Trim() && !r.IsArchived, c)) throw new ValidationException("A resource with this name already exists.");
            var r = new Resource { Version = 1 };
            Apply(r, dto);
            db.Resources.Add(r);
            await db.SaveChangesAsync(c);
            Audit.Add(db, user, clock, "Resource.Created", "Resource", r.Id, $"Resource {r.Name} added");
            await db.SaveChangesAsync(c);
            return r.Id;
        }, ct);
        notifier.Publish(DataArea.Resources);
        return id;
    }

    public async Task<IReadOnlyList<AffectedAppointment>> UpdateAsync(int id, int expectedVersion, ResourceEditDto dto, CancellationToken ct = default)
    {
        user.Require(Permission.ManageResources, "edit rooms and resources");
        Validate(dto);
        var affected = await writer.ExecuteAsync(async (db, c) =>
        {
            var r = await db.Resources.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Resource", id);
            if (r.Version != expectedVersion) throw new ConcurrencyConflictException("This resource was changed by someone else. Reload and try again.");
            if (await db.Resources.AnyAsync(x => x.Id != id && x.Name == dto.Name.Trim() && !x.IsArchived, c)) throw new ValidationException("A resource with this name already exists.");
            if (dto.Capacity < r.Capacity)
            {
                var now = clock.Now;
                var upcoming = await db.AppointmentSegments.Where(s => s.ResourceId == id && s.IsBlocking && s.BlockEnd > now)
                    .Select(s => new TimeInterval(s.BlockStart, s.BlockEnd)).ToListAsync(c);
                foreach (var iv in upcoming)
                    if (ConflictDetector.PeakConcurrency(upcoming, iv) > dto.Capacity)
                        throw new ValidationException($"Upcoming bookings already use {r.Name} beyond a capacity of {dto.Capacity}.");
            }

            Apply(r, dto);
            r.Version++;
            Audit.Add(db, user, clock, "Resource.Updated", "Resource", id, $"Resource {r.Name} updated{(dto.IsActive ? "" : " (blocked)")}");
            await db.SaveChangesAsync(c);
            return dto.IsActive ? [] : await UpcomingOnAsync(db, id, clock.Now, DateTime.MaxValue, c);
        }, ct);
        notifier.Publish(DataArea.Resources | DataArea.Appointments);
        return affected;
    }

    public async Task SetArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        user.Require(Permission.ManageResources, "archive rooms and resources");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var r = await db.Resources.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Resource", id);
            if (archived && await db.AppointmentSegments.AnyAsync(s => s.ResourceId == id && s.IsBlocking && s.Start > clock.Now, c))
                throw new ValidationException($"{r.Name} still has upcoming bookings. Reassign them first.");
            r.IsArchived = archived;
            r.Version++;
            Audit.Add(db, user, clock, archived ? "Resource.Archived" : "Resource.Restored", "Resource", id, $"Resource {r.Name} {(archived ? "archived" : "restored")}");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Resources);
    }

    public async Task<IReadOnlyList<MaintenanceDto>> GetMaintenanceAsync(int? resourceId = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var since = clock.Now.Date.AddDays(-30);
        var q = db.ResourceMaintenance.AsNoTracking().Where(m => m.End >= since);
        if (resourceId is not null) q = q.Where(m => m.ResourceId == resourceId);
        return await q.OrderBy(m => m.Start).Select(m => new MaintenanceDto(m.Id, m.ResourceId, m.Resource!.Name, m.Start, m.End, m.Reason)).ToListAsync(ct);
    }

    /// <summary>Blocks a resource for a period. Returns already-booked appointments that overlap and must be moved.</summary>
    public async Task<IReadOnlyList<AffectedAppointment>> AddMaintenanceAsync(int resourceId, DateTime start, DateTime end, string reason, CancellationToken ct = default)
    {
        user.Require(Permission.ManageResources, "schedule maintenance");
        if (end <= start) throw new ValidationException("The maintenance period must end after it starts.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("Enter a reason.");
        var affected = await writer.ExecuteAsync(async (db, c) =>
        {
            var r = await db.Resources.FirstOrDefaultAsync(x => x.Id == resourceId, c) ?? throw new NotFoundException("Resource", resourceId);
            db.ResourceMaintenance.Add(new ResourceMaintenance { ResourceId = resourceId, Start = start, End = end, Reason = reason.Trim() });
            Audit.Add(db, user, clock, "Resource.Maintenance", "Resource", resourceId, $"{r.Name} blocked {start:g} – {end:g}: {reason.Trim()}");
            await db.SaveChangesAsync(c);
            return await UpcomingOnAsync(db, resourceId, start, end, c);
        }, ct);
        notifier.Publish(DataArea.Resources | DataArea.Appointments);
        return affected;
    }

    public async Task RemoveMaintenanceAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.ManageResources, "remove maintenance blocks");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var m = await db.ResourceMaintenance.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Maintenance period", id);
            db.ResourceMaintenance.Remove(m);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Resources | DataArea.Appointments);
    }

    private static async Task<IReadOnlyList<AffectedAppointment>> UpcomingOnAsync(IAppDbContext db, int resourceId, DateTime from, DateTime to, CancellationToken ct) =>
        await db.AppointmentSegments.Where(s => s.ResourceId == resourceId && s.IsBlocking && s.BlockEnd > from && s.BlockStart < to)
            .Select(s => new AffectedAppointment(s.AppointmentId, s.Appointment!.Reference, s.Start, s.Appointment.Customer!.FullName))
            .Distinct().ToListAsync(ct);

    private static void Validate(ResourceEditDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Resource name is required.");
        if (dto.Capacity is < 1 or > 100) errors.Add("Capacity must be between 1 and 100.");
        if (dto.AvailableFrom.HasValue != dto.AvailableUntil.HasValue) errors.Add("Enter both 'available from' and 'available until', or neither.");
        if (dto.AvailableFrom is { } f && dto.AvailableUntil is { } u && u <= f) errors.Add("Availability end must be after its start.");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static void Apply(Resource r, ResourceEditDto dto)
    {
        r.Name = dto.Name.Trim();
        r.Type = dto.Type;
        r.LocationId = dto.LocationId;
        r.Capacity = dto.Capacity;
        r.IsActive = dto.IsActive;
        r.Notes = Normalization.Clean(dto.Notes);
        r.AvailableFrom = dto.AvailableFrom;
        r.AvailableUntil = dto.AvailableUntil;
    }
}

public sealed record LocationDto(int Id, string Name, string? Address, string? Phone, bool IsActive);

public sealed class LocationService(IAppDbContextFactory dbFactory, IWriteExecutor writer, ICurrentUser user, IClock clock, IDataChangeNotifier notifier)
{
    public async Task<IReadOnlyList<LocationDto>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var q = db.Locations.AsNoTracking().AsQueryable();
        if (!includeInactive) q = q.Where(l => l.IsActive);
        return await q.OrderBy(l => l.Id).Select(l => new LocationDto(l.Id, l.Name, l.Address, l.Phone, l.IsActive)).ToListAsync(ct);
    }

    public async Task<int> SaveAsync(int? id, string name, string? address, string? phone, bool isActive, CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "manage locations");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("Location name is required.");
        var result = await writer.ExecuteAsync(async (db, c) =>
        {
            Location loc;
            if (id is null)
            {
                loc = new Location();
                db.Locations.Add(loc);
            }
            else loc = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, c) ?? throw new NotFoundException("Location", id);

            if (!isActive && loc.IsActive && await db.Locations.CountAsync(l => l.IsActive, c) <= 1)
                throw new ValidationException("At least one location must remain active.");
            loc.Name = name.Trim();
            loc.Address = Normalization.Clean(address);
            loc.Phone = Normalization.Clean(phone);
            loc.IsActive = isActive;
            Audit.Add(db, user, clock, "Location.Saved", "Location", id, $"Location {loc.Name} saved");
            await db.SaveChangesAsync(c);
            return loc.Id;
        }, ct);
        notifier.Publish(DataArea.Settings);
        return result;
    }
}
