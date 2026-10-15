using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Domain;

namespace ScheduleOS.Application.Reporting;

public enum SearchKind { Customer, Appointment, Staff, Service, Invoice, Payment }

public sealed record SearchResult(SearchKind Kind, int Id, string Title, string Subtitle);

public sealed class SearchService(IAppDbContextFactory dbFactory, ICurrentUser user)
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string term, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2) return [];
        await using var db = dbFactory.CreateReadContext();
        var like = Normalization.LikePattern(term);
        var phone = Normalization.Phone(term);
        var results = new List<SearchResult>();

        if (user.Has(Permission.ViewCustomers))
        {
            var showContact = user.Has(Permission.ViewCustomerContact);
            var customers = await db.Customers.AsNoTracking()
                .Where(c => !c.IsAnonymized && (EF.Functions.Like(c.FullName, like, "\\") ||
                                                (showContact && EF.Functions.Like(c.Email ?? "", like, "\\")) ||
                                                (showContact && phone != null && phone.Length >= 3 && c.NormalizedPhone != null && c.NormalizedPhone.Contains(phone))))
                .OrderBy(c => c.FullName).Take(6).Select(c => new { c.Id, c.FullName, c.Phone, c.Email, c.IsArchived }).ToListAsync(ct);
            results.AddRange(customers.Select(c => new SearchResult(SearchKind.Customer, c.Id, c.FullName,
                (showContact ? c.Phone ?? c.Email ?? "Customer" : "Customer") + (c.IsArchived ? " · archived" : ""))));
        }

        var appts = db.Appointments.AsNoTracking().Where(a => EF.Functions.Like(a.Reference, like, "\\") || EF.Functions.Like(a.Customer!.FullName, like, "\\"));
        if (!user.Has(Permission.ViewAllAppointments))
        {
            var own = user.StaffMemberId ?? -1;
            appts = appts.Where(a => a.Segments.Any(s => s.StaffMemberId == own));
        }

        var apptRows = await appts.OrderByDescending(a => a.Start).Take(6)
            .Select(a => new { a.Id, a.Reference, a.Start, a.Status, Customer = a.Customer!.FullName }).ToListAsync(ct);
        results.AddRange(apptRows.Select(a => new SearchResult(SearchKind.Appointment, a.Id, $"#{a.Reference} · {a.Customer}", $"{a.Start:ddd, MMM d yyyy h:mm tt} · {a.Status}")));

        if (user.Has(Permission.ViewStaff))
        {
            var staff = await db.Staff.AsNoTracking().Where(s => !s.IsArchived && (EF.Functions.Like(s.FullName, like, "\\") || EF.Functions.Like(s.Position, like, "\\")))
                .OrderBy(s => s.FullName).Take(4).Select(s => new { s.Id, s.FullName, s.Position }).ToListAsync(ct);
            results.AddRange(staff.Select(s => new SearchResult(SearchKind.Staff, s.Id, s.FullName, s.Position)));
        }

        var services = await db.Services.AsNoTracking().Where(s => !s.IsArchived && EF.Functions.Like(s.Name, like, "\\"))
            .OrderBy(s => s.Name).Take(4).Select(s => new { s.Id, s.Name, s.DurationMinutes }).ToListAsync(ct);
        results.AddRange(services.Select(s => new SearchResult(SearchKind.Service, s.Id, s.Name, $"Service · {s.DurationMinutes} min")));

        if (user.Has(Permission.ViewPayments))
        {
            var invoices = await db.Invoices.AsNoTracking().Where(i => EF.Functions.Like(i.Number, like, "\\"))
                .OrderByDescending(i => i.IssuedAt).Take(4).Select(i => new { i.Id, i.Number, i.CustomerName, i.Status }).ToListAsync(ct);
            results.AddRange(invoices.Select(i => new SearchResult(SearchKind.Invoice, i.Id, i.Number, $"Invoice · {i.CustomerName} · {i.Status}")));
            var receipts = await db.Payments.AsNoTracking().Where(p => EF.Functions.Like(p.ReceiptNumber, like, "\\"))
                .OrderByDescending(p => p.RecordedAt).Take(4).Select(p => new { p.Id, p.ReceiptNumber, p.AppointmentId, p.Kind }).ToListAsync(ct);
            results.AddRange(receipts.Select(p => new SearchResult(SearchKind.Payment, p.AppointmentId ?? 0, p.ReceiptNumber, $"{p.Kind} receipt")));
        }

        return results;
    }
}

public sealed record AuditEntryDto(DateTime Timestamp, string? UserName, string Action, string EntityType, int? EntityId, string Summary);

public sealed class AuditService(IAppDbContextFactory dbFactory, ICurrentUser user)
{
    public async Task<IReadOnlyList<AuditEntryDto>> ListAsync(DateTime? from, DateTime? to, string? search, int take = 500, CancellationToken ct = default)
    {
        user.Require(Permission.ViewAuditLog, "view the audit log");
        await using var db = dbFactory.CreateReadContext();
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (from is not null) q = q.Where(l => l.Timestamp >= from);
        if (to is not null) q = q.Where(l => l.Timestamp < to);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = Normalization.LikePattern(search);
            q = q.Where(l => EF.Functions.Like(l.Summary, like, "\\") || EF.Functions.Like(l.Action, like, "\\") || EF.Functions.Like(l.UserName ?? "", like, "\\"));
        }

        return await q.OrderByDescending(l => l.Timestamp).ThenByDescending(l => l.Id).Take(take)
            .Select(l => new AuditEntryDto(l.Timestamp, l.UserName, l.Action, l.EntityType, l.EntityId, l.Summary)).ToListAsync(ct);
    }
}
