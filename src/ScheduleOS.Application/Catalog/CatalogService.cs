using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Application.Catalog;

public sealed record ServiceListItem(
    int Id,
    string Name,
    int? CategoryId,
    string? CategoryName,
    string CategoryColor,
    string? Description,
    int DurationMinutes,
    int PrepMinutes,
    int CleanupMinutes,
    long PriceMinor,
    long DepositMinor,
    bool IsActive,
    bool IsArchived,
    int StaffCount,
    string RequiredResources,
    int BookingsLast30Days);

/// <summary>Service data needed by the booking form.</summary>
public sealed record BookableService(int Id, string Name, string? CategoryName, int DurationMinutes, int PrepMinutes, int CleanupMinutes,
    long PriceMinor, long DepositMinor, IReadOnlyList<ResourceType> RequiredResourceTypes)
{
    public override string ToString() => Name;
}

public sealed class ServiceEditDto
{
    public string Name { get; set; } = "";
    public int? CategoryId { get; set; }
    public string? Description { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public int PrepMinutes { get; set; }
    public int CleanupMinutes { get; set; } = 10;
    public long PriceMinor { get; set; }
    public long DepositMinor { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ImagePath { get; set; }
    public List<ResourceType> RequiredResourceTypes { get; set; } = [];
    public List<int> StaffIds { get; set; } = [];
}

public sealed record CategoryDto(int Id, string Name, string ColorHex, int ServiceCount);

public sealed class CatalogService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    public async Task<IReadOnlyList<ServiceListItem>> ListAsync(string? search = null, int? categoryId = null, bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var q = db.Services.AsNoTracking().AsQueryable();
        if (!includeArchived) q = q.Where(s => !s.IsArchived);
        if (categoryId is not null) q = q.Where(s => s.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = Normalization.LikePattern(search);
            q = q.Where(s => EF.Functions.Like(s.Name, like, "\\") || EF.Functions.Like(s.Description ?? "", like, "\\"));
        }

        var since = clock.Now.Date.AddDays(-30);
        var rows = await q.OrderBy(s => s.Category!.SortOrder).ThenBy(s => s.Name).Select(s => new
        {
            s.Id, s.Name, s.CategoryId, Category = s.Category != null ? s.Category.Name : null, Color = s.Category != null ? s.Category.ColorHex : "#008C95",
            s.Description, s.DurationMinutes, s.PrepMinutes, s.CleanupMinutes, s.PriceMinor, s.DepositMinor, s.IsActive, s.IsArchived,
            StaffCount = s.QualifiedStaff.Count(k => !k.StaffMember!.IsArchived),
            Requirements = s.ResourceRequirements.Select(r => r.ResourceType).ToList(),
        }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var bookings = await db.AppointmentSegments.Where(x => ids.Contains(x.ServiceId) && x.Start >= since && x.IsBlocking)
            .GroupBy(x => x.ServiceId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return rows.Select(r => new ServiceListItem(r.Id, r.Name, r.CategoryId, r.Category, r.Color, r.Description, r.DurationMinutes, r.PrepMinutes,
            r.CleanupMinutes, r.PriceMinor, r.DepositMinor, r.IsActive, r.IsArchived, r.StaffCount,
            r.Requirements.Count == 0 ? "—" : string.Join(" / ", r.Requirements.Select(ResourceTypeNames.Display)),
            bookings.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<IReadOnlyList<BookableService>> GetBookableAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var rows = await db.Services.AsNoTracking().Include(s => s.Category).Include(s => s.ResourceRequirements)
            .Where(s => s.IsActive && !s.IsArchived).OrderBy(s => s.Category!.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
        return rows.Select(s => new BookableService(s.Id, s.Name, s.Category?.Name, s.DurationMinutes, s.PrepMinutes, s.CleanupMinutes,
            s.PriceMinor, s.DepositMinor, s.ResourceRequirements.Select(r => r.ResourceType).Distinct().ToList())).ToList();
    }

    public async Task<(ServiceEditDto Dto, int Version, bool IsArchived)> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var s = await db.Services.AsNoTracking().Include(x => x.ResourceRequirements).Include(x => x.QualifiedStaff)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Service", id);
        return (new ServiceEditDto
        {
            Name = s.Name, CategoryId = s.CategoryId, Description = s.Description, DurationMinutes = s.DurationMinutes, PrepMinutes = s.PrepMinutes,
            CleanupMinutes = s.CleanupMinutes, PriceMinor = s.PriceMinor, DepositMinor = s.DepositMinor, IsActive = s.IsActive, ImagePath = s.ImagePath,
            RequiredResourceTypes = s.ResourceRequirements.Select(r => r.ResourceType).Distinct().ToList(),
            StaffIds = s.QualifiedStaff.Select(k => k.StaffMemberId).ToList(),
        }, s.Version, s.IsArchived);
    }

    public async Task<int> CreateAsync(ServiceEditDto dto, CancellationToken ct = default)
    {
        user.Require(Permission.ManageServices, "add services");
        Validate(dto);
        var id = await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.Services.AnyAsync(s => s.Name == dto.Name.Trim() && !s.IsArchived, c)) throw new ValidationException("A service with this name already exists.");
            var s = new Service { CreatedAt = clock.Now, Version = 1 };
            Apply(s, dto);
            foreach (var t in dto.RequiredResourceTypes.Distinct()) s.ResourceRequirements.Add(new ServiceResourceRequirement { ResourceType = t });
            foreach (var sid in dto.StaffIds.Distinct()) s.QualifiedStaff.Add(new StaffSkill { StaffMemberId = sid });
            db.Services.Add(s);
            await db.SaveChangesAsync(c);
            Audit.Add(db, user, clock, "Service.Created", "Service", s.Id, $"Service {s.Name} added");
            await db.SaveChangesAsync(c);
            return s.Id;
        }, ct);
        notifier.Publish(DataArea.Services);
        return id;
    }

    /// <summary>Updates the catalog entry. Existing appointments keep their own snapshot of name, duration and price.</summary>
    public async Task UpdateAsync(int id, int expectedVersion, ServiceEditDto dto, CancellationToken ct = default)
    {
        user.Require(Permission.ManageServices, "edit services");
        Validate(dto);
        await writer.ExecuteAsync(async (db, c) =>
        {
            var s = await db.Services.Include(x => x.ResourceRequirements).Include(x => x.QualifiedStaff).FirstOrDefaultAsync(x => x.Id == id, c)
                    ?? throw new NotFoundException("Service", id);
            if (s.Version != expectedVersion) throw new ConcurrencyConflictException("This service was changed by someone else. Reload and try again.");
            if (await db.Services.AnyAsync(x => x.Id != id && x.Name == dto.Name.Trim() && !x.IsArchived, c)) throw new ValidationException("A service with this name already exists.");
            var oldPrice = s.PriceMinor;
            Apply(s, dto);
            db.ServiceResourceRequirements.RemoveRange(s.ResourceRequirements);
            s.ResourceRequirements.Clear();
            foreach (var t in dto.RequiredResourceTypes.Distinct()) s.ResourceRequirements.Add(new ServiceResourceRequirement { ResourceType = t });
            var wanted = dto.StaffIds.Distinct().ToHashSet();
            db.StaffSkills.RemoveRange(s.QualifiedStaff.Where(k => !wanted.Contains(k.StaffMemberId)));
            foreach (var sid in wanted.Where(w => s.QualifiedStaff.All(k => k.StaffMemberId != w))) s.QualifiedStaff.Add(new StaffSkill { StaffMemberId = sid });
            s.Version++;
            Audit.Add(db, user, clock, "Service.Updated", "Service", id,
                oldPrice != s.PriceMinor ? $"Service {s.Name} updated (price changed)" : $"Service {s.Name} updated");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Services | DataArea.Staff);
    }

    /// <summary>Archiving keeps the row so historical appointments remain intact; services are never hard-deleted once used.</summary>
    public async Task SetArchivedAsync(int id, bool archived, CancellationToken ct = default)
    {
        user.Require(Permission.ManageServices, "archive services");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var s = await db.Services.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Service", id);
            s.IsArchived = archived;
            if (archived) s.IsActive = false;
            s.Version++;
            Audit.Add(db, user, clock, archived ? "Service.Archived" : "Service.Restored", "Service", id, $"Service {s.Name} {(archived ? "archived" : "restored")}");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Services);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await db.ServiceCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.ColorHex, db.Services.Count(s => s.CategoryId == c.Id && !s.IsArchived))).ToListAsync(ct);
    }

    public async Task<int> SaveCategoryAsync(int? id, string name, string colorHex, CancellationToken ct = default)
    {
        user.Require(Permission.ManageServices, "manage service categories");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("Category name is required.");
        var result = await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.ServiceCategories.AnyAsync(x => x.Name == name.Trim() && x.Id != id, c)) throw new ValidationException("This category already exists.");
            ServiceCategory cat;
            if (id is null)
            {
                cat = new ServiceCategory { SortOrder = await db.ServiceCategories.CountAsync(c) };
                db.ServiceCategories.Add(cat);
            }
            else cat = await db.ServiceCategories.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Category", id);

            cat.Name = name.Trim();
            cat.ColorHex = string.IsNullOrWhiteSpace(colorHex) ? "#008C95" : colorHex;
            await db.SaveChangesAsync(c);
            return cat.Id;
        }, ct);
        notifier.Publish(DataArea.Services);
        return result;
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.ManageServices, "manage service categories");
        await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.Services.AnyAsync(s => s.CategoryId == id, c)) throw new ValidationException("Move or archive the services in this category first.");
            var cat = await db.ServiceCategories.FirstOrDefaultAsync(x => x.Id == id, c) ?? throw new NotFoundException("Category", id);
            db.ServiceCategories.Remove(cat);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Services);
    }

    private static void Validate(ServiceEditDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Service name is required.");
        if (dto.DurationMinutes is < 5 or > 12 * 60) errors.Add("Duration must be between 5 minutes and 12 hours.");
        if (dto.PrepMinutes is < 0 or > 240 || dto.CleanupMinutes is < 0 or > 240) errors.Add("Buffers must be between 0 and 240 minutes.");
        if (dto.PriceMinor < 0) errors.Add("Price cannot be negative.");
        if (dto.DepositMinor < 0) errors.Add("Deposit cannot be negative.");
        if (dto.DepositMinor > dto.PriceMinor) errors.Add("The deposit cannot exceed the price.");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static void Apply(Service s, ServiceEditDto dto)
    {
        s.Name = dto.Name.Trim();
        s.CategoryId = dto.CategoryId;
        s.Description = Normalization.Clean(dto.Description);
        s.DurationMinutes = dto.DurationMinutes;
        s.PrepMinutes = dto.PrepMinutes;
        s.CleanupMinutes = dto.CleanupMinutes;
        s.PriceMinor = dto.PriceMinor;
        s.DepositMinor = dto.DepositMinor;
        s.IsActive = dto.IsActive;
        s.ImagePath = Normalization.Clean(dto.ImagePath);
    }
}
