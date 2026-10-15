using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Payments;

public sealed record InvoiceLineDto(string Description, string? StaffName, int Quantity, long UnitPriceMinor, long DiscountMinor, long LineTotalMinor);

public sealed record DocumentPaymentLine(string Reference, DateTime Date, string Kind, string Method, long SignedAmountMinor);

/// <summary>Everything needed to render, print or export an invoice. Never contains internal staff notes.</summary>
public sealed record InvoiceDocument(
    int InvoiceId,
    string Number,
    DateTime IssuedAt,
    InvoiceStatus Status,
    string BusinessName,
    string? BusinessAddress,
    string? BusinessPhone,
    string? BusinessEmail,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string AppointmentReference,
    DateTime AppointmentStart,
    IReadOnlyList<InvoiceLineDto> Lines,
    long SubtotalMinor,
    long DiscountMinor,
    string TaxLabel,
    int TaxRateBasisPoints,
    long TaxMinor,
    long TotalMinor,
    long PaidMinor,
    long BalanceMinor,
    IReadOnlyList<DocumentPaymentLine> Payments,
    string? CustomerNotes,
    string Footer,
    bool IsDemo,
    CurrencyFormat Currency)
{
    public string Title => "Invoice";
}

public sealed record ReceiptDocument(
    string ReceiptNumber,
    DateTime RecordedAt,
    string Kind,
    string Method,
    long AmountMinor,
    string BusinessName,
    string? BusinessAddress,
    string CustomerName,
    string? AppointmentReference,
    string? ExternalReference,
    string? Note,
    string? RecordedBy,
    long AppointmentTotalMinor,
    long AppointmentPaidMinor,
    CurrencyFormat Currency);

public interface IDocumentPdfWriter
{
    Task WriteInvoiceAsync(string path, InvoiceDocument document, CancellationToken ct = default);
    Task WriteReceiptAsync(string path, ReceiptDocument document, CancellationToken ct = default);
}

public sealed class InvoiceService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    SettingsService settings,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    /// <summary>
    /// Issues (or returns) the invoice for an appointment. If services or prices changed since the
    /// invoice was issued, the old invoice is voided and a new number is issued so issued documents stay immutable.
    /// </summary>
    public async Task<int> IssueAsync(int appointmentId, CancellationToken ct = default)
    {
        user.Require(Permission.ViewPayments, "issue invoices");
        var id = await writer.ExecuteAsync(async (db, c) =>
        {
            var appt = await db.Appointments.Include(a => a.Customer).Include(a => a.Segments).ThenInclude(s => s.StaffMember)
                .Include(a => a.Payments).Include(a => a.Invoices).ThenInclude(i => i.Lines)
                .FirstOrDefaultAsync(a => a.Id == appointmentId, c) ?? throw new NotFoundException("Appointment", appointmentId);
            if (appt.Status is AppointmentStatus.Draft) throw new ValidationException("Draft appointments cannot be invoiced.");

            var lines = appt.Segments.OrderBy(s => s.SortOrder).Select(s => new InvoiceLine
            {
                Description = s.ServiceName, StaffName = s.StaffMember?.FullName, Quantity = 1,
                UnitPriceMinor = s.UnitPriceMinor, DiscountMinor = s.DiscountMinor, LineTotalMinor = s.LineTotalMinor,
            }).ToList();

            var current = appt.Invoices.Where(i => i.Status != InvoiceStatus.Void).OrderByDescending(i => i.IssuedAt).FirstOrDefault();
            if (current is not null && current.Status != InvoiceStatus.Void && Matches(current, appt, lines))
            {
                current.Status = ComputeStatus(current, appt.Payments);
                await db.SaveChangesAsync(c);
                return current.Id;
            }

            var now = clock.Now;
            if (current is not null)
            {
                // The superseded invoice is kept as a voided historical document.
                current.Status = InvoiceStatus.Void;
                db.AppointmentEvents.Add(new AppointmentEvent
                {
                    AppointmentId = appt.Id, Timestamp = now, Type = AppointmentEventType.InvoiceIssued, UserId = user.UserId, UserName = user.DisplayName,
                    Description = $"Invoice {current.Number} voided because the services or prices changed",
                });
            }

            var invoice = new Invoice
            {
                Number = await Sequences.NextAsync(db, "INV", now.Year, 4, c),
                AppointmentId = appt.Id,
                CustomerId = appt.CustomerId,
                IssuedAt = now,
                CustomerName = appt.Customer!.FullName,
                CustomerEmail = appt.Customer.Email,
                CustomerPhone = appt.Customer.Phone,
                SubtotalMinor = appt.SubtotalMinor,
                DiscountMinor = appt.DiscountMinor,
                TaxRateBasisPoints = appt.TaxRateBasisPoints,
                TaxMinor = appt.TaxMinor,
                TotalMinor = appt.TotalMinor,
                CustomerNotes = appt.CustomerNotes,
                IsDemo = appt.IsDemo,
                Lines = lines,
            };
            invoice.Status = ComputeStatus(invoice, appt.Payments);
            db.Invoices.Add(invoice);
            db.AppointmentEvents.Add(new AppointmentEvent
            {
                AppointmentId = appt.Id, Timestamp = now, Type = AppointmentEventType.InvoiceIssued, UserId = user.UserId, UserName = user.DisplayName,
                Description = $"Invoice {invoice.Number} issued for {settings.Currency.Format(invoice.TotalMinor)}",
            });
            Audit.Add(db, user, clock, "Invoice.Issued", "Invoice", null, $"Invoice {invoice.Number} issued for {appt.Reference}", appt.CustomerId);
            await db.SaveChangesAsync(c);
            return invoice.Id;
        }, ct);
        notifier.Publish(DataArea.Payments | DataArea.Appointments);
        return id;
    }

    public async Task<InvoiceDocument> GetDocumentAsync(int invoiceId, CancellationToken ct = default)
    {
        user.Require(Permission.ViewPayments, "view invoices");
        await using var db = dbFactory.CreateReadContext();
        var inv = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Appointment)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice", invoiceId);
        var payments = await db.Payments.AsNoTracking().Where(p => p.AppointmentId == inv.AppointmentId).OrderBy(p => p.RecordedAt).ToListAsync(ct);
        var paid = PaymentMath.NetPaid(payments);
        var b = settings.Business;
        return new InvoiceDocument(inv.Id, inv.Number, inv.IssuedAt, inv.Status == InvoiceStatus.Void ? InvoiceStatus.Void : ComputeStatus(inv, payments),
            b.Name, b.Address, b.Phone, b.Email, inv.CustomerName, inv.CustomerEmail, inv.CustomerPhone,
            inv.Appointment?.Reference ?? "", inv.Appointment?.Start ?? inv.IssuedAt,
            inv.Lines.Select(l => new InvoiceLineDto(l.Description, l.StaffName, l.Quantity, l.UnitPriceMinor, l.DiscountMinor, l.LineTotalMinor)).ToList(),
            inv.SubtotalMinor, inv.DiscountMinor, settings.Current.TaxLabel, inv.TaxRateBasisPoints, inv.TaxMinor, inv.TotalMinor,
            paid, inv.TotalMinor - paid,
            payments.Select(p => new DocumentPaymentLine(p.ReceiptNumber, p.RecordedAt, PaymentService.KindName(p.Kind), PaymentService.MethodName(p.Method), p.SignedAmountMinor)).ToList(),
            inv.CustomerNotes, settings.Current.InvoiceFooter, inv.IsDemo, settings.Currency);
    }

    public async Task<ReceiptDocument> GetReceiptAsync(int paymentId, CancellationToken ct = default)
    {
        user.Require(Permission.ViewPayments, "view receipts");
        await using var db = dbFactory.CreateReadContext();
        var p = await db.Payments.AsNoTracking().Include(x => x.Customer).Include(x => x.Appointment)
            .FirstOrDefaultAsync(x => x.Id == paymentId, ct) ?? throw new NotFoundException("Payment", paymentId);
        long total = 0, paid = 0;
        if (p.AppointmentId is { } aid)
        {
            total = p.Appointment!.TotalMinor;
            paid = PaymentMath.NetPaid(await db.Payments.AsNoTracking().Where(x => x.AppointmentId == aid && x.RecordedAt <= p.RecordedAt && x.Id <= p.Id).ToListAsync(ct));
        }

        var b = settings.Business;
        return new ReceiptDocument(p.ReceiptNumber, p.RecordedAt, PaymentService.KindName(p.Kind), PaymentService.MethodName(p.Method), p.AmountMinor,
            b.Name, b.Address, p.Customer?.FullName ?? "", p.Appointment?.Reference, p.ExternalReference, p.Note, p.RecordedByName, total, paid, settings.Currency);
    }

    public static InvoiceStatus ComputeStatus(Invoice invoice, IEnumerable<Payment> payments)
    {
        if (invoice.Status == InvoiceStatus.Void) return InvoiceStatus.Void;
        var list = payments.ToList();
        var paid = PaymentMath.NetPaid(list);
        if (paid >= invoice.TotalMinor && invoice.TotalMinor > 0) return InvoiceStatus.Paid;
        if (paid > 0) return InvoiceStatus.PartiallyPaid;
        return list.Any(p => !p.IsInflow) ? InvoiceStatus.Refunded : InvoiceStatus.Issued;
    }

    private static bool Matches(Invoice inv, Appointment appt, List<InvoiceLine> lines) =>
        inv.TotalMinor == appt.TotalMinor && inv.Lines.Count == lines.Count &&
        inv.Lines.OrderBy(l => l.Id).Zip(lines).All(p => p.First.Description == p.Second.Description && p.First.LineTotalMinor == p.Second.LineTotalMinor);
}
