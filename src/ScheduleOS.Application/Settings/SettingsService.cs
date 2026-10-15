using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Settings;

public sealed class SettingsService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    private static readonly PropertyInfo[] Props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite).ToArray();

    private static readonly string[] AppearanceKeys = [nameof(AppSettings.Theme), nameof(AppSettings.Density), nameof(AppSettings.SidebarCollapsed)];

    public AppSettings Current { get; private set; } = new();
    public BusinessProfileDto Business { get; private set; } = new();
    public CurrencyFormat Currency { get; private set; } = new("USD", 2);

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var rows = await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = new AppSettings();
        foreach (var p in Props)
            if (rows.TryGetValue(p.Name, out var raw) && TryConvert(raw, p.PropertyType, out var value))
                p.SetValue(settings, value);
        Current = settings;

        var b = await db.Businesses.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        Business = b is null ? new BusinessProfileDto() : new BusinessProfileDto
        {
            Name = b.Name, Address = b.Address, Phone = b.Phone, Email = b.Email, LogoPath = b.LogoPath,
            CurrencyCode = b.CurrencyCode, CurrencyDecimals = b.CurrencyDecimals, TimeZoneId = b.TimeZoneId,
            IsDemoWorkspace = b.IsDemoWorkspace,
        };
        Currency = new CurrencyFormat(Business.CurrencyCode, Business.CurrencyDecimals);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "change business settings");
        Validate(settings);
        await PersistAsync(settings, Props.Select(p => p.Name), "Updated application settings", ct);
    }

    /// <summary>Appearance preferences can be changed by any signed-in user.</summary>
    public async Task SaveAppearanceAsync(ColorTheme theme, DensityMode density, bool sidebarCollapsed, CancellationToken ct = default)
    {
        var s = Current.Clone();
        s.Theme = theme;
        s.Density = density;
        s.SidebarCollapsed = sidebarCollapsed;
        await PersistAsync(s, AppearanceKeys, null, ct);
    }

    /// <summary>Internal bookkeeping values (e.g. last automatic backup) that are not user-facing settings.</summary>
    public async Task SaveSystemValueAsync(string key, string value, CancellationToken ct = default)
    {
        await writer.ExecuteAsync(async (db, c) =>
        {
            var row = await db.Settings.FindAsync([key], c);
            if (row is null) db.Settings.Add(new BusinessSetting { Key = key, Value = value, UpdatedAt = clock.Now });
            else
            {
                row.Value = value;
                row.UpdatedAt = clock.Now;
            }

            await db.SaveChangesAsync(c);
        }, ct);
        var p = Props.FirstOrDefault(x => x.Name == key);
        if (p is not null && TryConvert(value, p.PropertyType, out var v)) p.SetValue(Current, v);
    }

    public async Task SaveBusinessProfileAsync(BusinessProfileDto profile, CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "change the business profile");
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add("Business name is required.");
        if (string.IsNullOrWhiteSpace(profile.CurrencyCode) || profile.CurrencyCode.Length != 3) errors.Add("Currency must be a 3-letter ISO code.");
        if (profile.CurrencyDecimals is < 0 or > 3) errors.Add("Currency decimals must be between 0 and 3.");
        if (!string.IsNullOrWhiteSpace(profile.Email) && !Normalization.IsValidEmail(profile.Email)) errors.Add("Business email is not valid.");
        if (errors.Count > 0) throw new ValidationException(errors);

        await writer.ExecuteAsync(async (db, c) =>
        {
            var b = await db.Businesses.OrderBy(x => x.Id).FirstOrDefaultAsync(c);
            if (b is null)
            {
                b = new Business { CreatedAt = clock.Now };
                db.Businesses.Add(b);
            }
            else if (b.CurrencyDecimals != profile.CurrencyDecimals && await db.Payments.AnyAsync(c))
            {
                throw new ValidationException("Currency decimals cannot be changed after payments have been recorded.");
            }

            b.Name = profile.Name.Trim();
            b.Address = profile.Address?.Trim();
            b.Phone = profile.Phone?.Trim();
            b.Email = profile.Email?.Trim();
            b.LogoPath = profile.LogoPath;
            b.CurrencyCode = profile.CurrencyCode.Trim().ToUpperInvariant();
            b.CurrencyDecimals = profile.CurrencyDecimals;
            b.TimeZoneId = profile.TimeZoneId;
            Audit.Add(db, user, clock, "Settings.Business", "Business", b.Id, "Updated business profile");
            await db.SaveChangesAsync(c);
        }, ct);
        await LoadAsync(ct);
        notifier.Publish(DataArea.Settings);
    }

    public async Task<IReadOnlyList<OpeningHoursDto>> GetOpeningHoursAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        var rows = await db.BusinessHours.AsNoTracking().ToListAsync(ct);
        return Enum.GetValues<DayOfWeek>()
            .Select(d => rows.FirstOrDefault(r => r.DayOfWeek == d) is { } r
                ? new OpeningHoursDto(d, r.IsOpen, r.OpenTime, r.CloseTime)
                : new OpeningHoursDto(d, false, new TimeOnly(9, 0), new TimeOnly(18, 0)))
            .OrderBy(h => ((int)h.Day + 6) % 7)
            .ToList();
    }

    public async Task SaveOpeningHoursAsync(IReadOnlyList<OpeningHoursDto> hours, CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "change opening hours");
        foreach (var h in hours.Where(h => h.IsOpen && h.Close <= h.Open))
            throw new ValidationException($"{h.Day}: closing time must be after opening time.");

        await writer.ExecuteAsync(async (db, c) =>
        {
            var rows = await db.BusinessHours.ToListAsync(c);
            foreach (var h in hours)
            {
                var row = rows.FirstOrDefault(r => r.DayOfWeek == h.Day);
                if (row is null) db.BusinessHours.Add(row = new BusinessHours { DayOfWeek = h.Day });
                row.IsOpen = h.IsOpen;
                row.OpenTime = h.Open;
                row.CloseTime = h.Close;
            }

            Audit.Add(db, user, clock, "Settings.Hours", "BusinessHours", null, "Updated opening hours");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Settings);
    }

    public async Task<IReadOnlyList<HolidayDto>> GetHolidaysAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateReadContext();
        return await db.BusinessHolidays.AsNoTracking().OrderBy(h => h.Date)
            .Select(h => new HolidayDto(h.Id, h.Date, h.Name)).ToListAsync(ct);
    }

    public async Task AddHolidayAsync(DateOnly date, string name, CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "add business closures");
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("Enter a name for the closure.");
        await writer.ExecuteAsync(async (db, c) =>
        {
            if (await db.BusinessHolidays.AnyAsync(h => h.Date == date, c)) throw new ValidationException($"{date:d} is already marked as closed.");
            db.BusinessHolidays.Add(new BusinessHoliday { Date = date, Name = name.Trim() });
            Audit.Add(db, user, clock, "Settings.Holiday", "BusinessHoliday", null, $"Added closure {date:d} ({name})");
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Settings);
    }

    public async Task RemoveHolidayAsync(int id, CancellationToken ct = default)
    {
        user.Require(Permission.ManageSettings, "remove business closures");
        await writer.ExecuteAsync(async (db, c) =>
        {
            var h = await db.BusinessHolidays.FindAsync([id], c) ?? throw new NotFoundException("Closure", id);
            db.BusinessHolidays.Remove(h);
            await db.SaveChangesAsync(c);
        }, ct);
        notifier.Publish(DataArea.Settings);
    }

    private async Task PersistAsync(AppSettings settings, IEnumerable<string> keys, string? auditSummary, CancellationToken ct)
    {
        var keyList = keys.ToList();
        await writer.ExecuteAsync(async (db, c) =>
        {
            var existing = await db.Settings.ToDictionaryAsync(s => s.Key, c);
            foreach (var p in Props.Where(p => keyList.Contains(p.Name)))
            {
                var value = Convert.ToString(p.GetValue(settings), CultureInfo.InvariantCulture) ?? "";
                if (existing.TryGetValue(p.Name, out var row))
                {
                    if (row.Value == value) continue;
                    row.Value = value;
                    row.UpdatedAt = clock.Now;
                }
                else db.Settings.Add(new BusinessSetting { Key = p.Name, Value = value, UpdatedAt = clock.Now });
            }

            if (auditSummary is not null) Audit.Add(db, user, clock, "Settings.Update", "Settings", null, auditSummary);
            await db.SaveChangesAsync(c);
        }, ct);

        foreach (var p in Props.Where(p => keyList.Contains(p.Name))) p.SetValue(Current, p.GetValue(settings));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        notifier.Publish(DataArea.Settings);
    }

    private static void Validate(AppSettings s)
    {
        var errors = new List<string>();
        if (s.SlotIntervalMinutes is not (5 or 10 or 15 or 20 or 30 or 60)) errors.Add("Slot interval must be 5, 10, 15, 20, 30 or 60 minutes.");
        if (s.CalendarStartHour is < 0 or > 23 || s.CalendarEndHour is < 1 or > 24 || s.CalendarEndHour <= s.CalendarStartHour)
            errors.Add("Calendar day start must be before day end.");
        if (s.DefaultPrepMinutes is < 0 or > 240 || s.DefaultCleanupMinutes is < 0 or > 240) errors.Add("Buffers must be between 0 and 240 minutes.");
        if (s.MinimumLeadMinutes is < 0 or > 60 * 24 * 30) errors.Add("Lead time is out of range.");
        if (s.TaxRateBasisPoints is < 0 or > 5000) errors.Add("Tax rate must be between 0% and 50%.");
        if (s.ReminderLeadHours is < 1 or > 168) errors.Add("Reminder lead time must be between 1 and 168 hours.");
        if (s.SessionLockMinutes is < 1 or > 480) errors.Add("Session lock must be between 1 and 480 minutes.");
        if (s.BackupRetentionCount is < 1 or > 365) errors.Add("Backup retention must be between 1 and 365.");
        if (s.AutoBackupIntervalHours is < 1 or > 24 * 30) errors.Add("Backup interval must be between 1 hour and 30 days.");
        if (s.SmtpPort is < 1 or > 65535) errors.Add("SMTP port is invalid.");
        if (!string.IsNullOrWhiteSpace(s.SmtpFromAddress) && !Normalization.IsValidEmail(s.SmtpFromAddress)) errors.Add("SMTP sender address is invalid.");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private static bool TryConvert(string raw, Type type, out object? value)
    {
        try
        {
            if (type == typeof(string)) value = raw;
            else if (type.IsEnum) value = Enum.Parse(type, raw);
            else value = Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            value = null;
            return false;
        }
    }
}
