using System.Globalization;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Security;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public sealed record Lookup(int Id, string Name, string? Detail = null)
{
    public override string ToString() => Name;
}

public static class Audit
{
    public static void Add(IAppDbContext db, ICurrentUser user, IClock clock, string action, string entityType, int? entityId,
        string summary, int? customerId = null, bool isDemo = false)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = clock.Now,
            UserId = user.UserId,
            UserName = user.IsAuthenticated ? user.DisplayName : "System",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            CustomerId = customerId,
            Summary = summary,
            IsDemo = isDemo,
        });
    }
}

public static class Sequences
{
    /// <summary>Allocates the next number for a prefix and year. Must run inside a write transaction.</summary>
    public static async Task<string> NextAsync(IAppDbContext db, string prefix, int year, int width, CancellationToken ct)
    {
        var key = $"{prefix}-{year}";
        var seq = await db.NumberSequences.FindAsync([key], ct);
        if (seq is null)
        {
            seq = new NumberSequence { Key = key, NextValue = 1 };
            db.NumberSequences.Add(seq);
        }

        var value = seq.NextValue;
        seq.NextValue++;
        return $"{prefix}-{year}-{value.ToString(new string('0', width), CultureInfo.InvariantCulture)}";
    }
}

public static class Normalization
{
    public static string? Email(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>Keeps digits only (and a leading +) so formatting differences do not hide duplicates.</summary>
    public static string? Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var sb = new StringBuilder();
        foreach (var ch in phone.Trim())
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch == '+' && sb.Length == 0) sb.Append(ch);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Contains(' ')) return false;
        try
        {
            var addr = new MailAddress(email.Trim());
            return addr.Address == email.Trim() && addr.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static string LikePattern(string term) =>
        "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}

/// <summary>Formats minor currency units for display. Values are never converted to floating point.</summary>
public sealed class CurrencyFormat(string code, int decimals)
{
    private static readonly Dictionary<string, (string Symbol, bool Suffix)> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = ("$", false), ["AUD"] = ("A$", false), ["CAD"] = ("C$", false), ["NZD"] = ("NZ$", false),
        ["SGD"] = ("S$", false), ["HKD"] = ("HK$", false), ["EUR"] = ("€", false), ["GBP"] = ("£", false),
        ["JPY"] = ("¥", false), ["CNY"] = ("¥", false), ["INR"] = ("₹", false), ["KRW"] = ("₩", false),
        ["VND"] = ("₫", true), ["THB"] = ("฿", false), ["PHP"] = ("₱", false), ["CHF"] = ("CHF ", false),
    };

    public string Code { get; } = code;
    public int Decimals { get; } = decimals;

    public string Format(long minor)
    {
        var major = Money.ToMajor(Math.Abs(minor), Decimals);
        var number = major.ToString("N" + Decimals, CultureInfo.InvariantCulture);
        var sign = minor < 0 ? "-" : "";
        if (Symbols.TryGetValue(Code, out var s))
            return s.Suffix ? $"{sign}{number} {s.Symbol}" : $"{sign}{s.Symbol}{number}";
        return $"{sign}{number} {Code}";
    }

    public string FormatCompact(long minor)
    {
        var major = Money.ToMajor(Math.Abs(minor), Decimals);
        string number = major switch
        {
            >= 1_000_000_000m => (major / 1_000_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000m => (major / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + "M",
            >= 1_000m => (major / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "K",
            _ => major.ToString("0", CultureInfo.InvariantCulture),
        };
        var sign = minor < 0 ? "-" : "";
        if (Symbols.TryGetValue(Code, out var s)) return s.Suffix ? $"{sign}{number} {s.Symbol}" : $"{sign}{s.Symbol}{number}";
        return $"{sign}{number} {Code}";
    }

    /// <summary>Parses a user-entered major amount ("120.50") into minor units.</summary>
    public bool TryParse(string? text, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray()).Replace(",", "");
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var major)) return false;
        if (decimal.Round(major, Decimals) != major) return false;
        minor = Money.FromMajor(major, Decimals);
        return true;
    }

    public string ToEditString(long minor) => Money.ToMajor(minor, Decimals).ToString("F" + Decimals, CultureInfo.InvariantCulture);
}

public static class QueryExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T>(items, total, page, pageSize);
    }
}
