using Microsoft.EntityFrameworkCore;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Common;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Entities;

namespace ScheduleOS.Application.Payments;

public sealed record RecordPaymentRequest
{
    public Guid SubmissionKey { get; init; } = Guid.NewGuid();
    public int AppointmentId { get; init; }
    public PaymentKind Kind { get; init; } = PaymentKind.Payment;
    public PaymentMethod Method { get; init; }
    public long AmountMinor { get; init; }
    public string? ExternalReference { get; init; }
    public string? Note { get; init; }
}

public sealed record PaymentQuery
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public PaymentMethod? Method { get; init; }
    public PaymentKind? Kind { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
}

public sealed record PaymentTotals(long InflowMinor, long RefundsMinor, long NetMinor, int Count);

public sealed record OutstandingItem(int AppointmentId, string Reference, DateTime Start, AppointmentStatus Status, int CustomerId, string CustomerName, long TotalMinor, long PaidMinor, long BalanceMinor);

public sealed class PaymentService(
    IAppDbContextFactory dbFactory,
    IWriteExecutor writer,
    SettingsService settings,
    ICurrentUser user,
    IClock clock,
    IDataChangeNotifier notifier)
{
    public async Task<string> RecordAsync(RecordPaymentRequest req, CancellationToken ct = default)
    {
        user.Require(Permission.RecordPayments, "record payments");
        if (req.Kind is not (PaymentKind.Deposit or PaymentKind.Payment)) throw new ValidationException("Use the refund workflow for refunds.");
        if (req.AmountMinor <= 0) throw new ValidationException("The amount must be greater than zero.");
        var cf = settings.Currency;

        var receipt = await writer.ExecuteAsync(async (db, c) =>
        {
            var existing = await db.Payments.Where(p => p.SubmissionKey == req.SubmissionKey).Select(p => p.ReceiptNumber).FirstOrDefaultAsync(c);
            if (existing is not null) throw new DuplicateSubmissionException(existing);

            var appt = await db.Appointments.Include(a => a.Payments).Include(a => a.Invoices)
                .FirstOrDefaultAsync(a => a.Id == req.AppointmentId, c) ?? throw new NotFoundException("Appointment", req.AppointmentId);
            if (!AppointmentLifecycle.AcceptsPayments(appt.Status)) throw new ValidationException("Payments cannot be recorded for draft appointments.");
            var balance = appt.BalanceMinor;
            if (balance <= 0) throw new ValidationException("This appointment is already fully paid.");
            if (req.AmountMinor > balance)
                throw new ValidationException($"The amount exceeds the outstanding balance of {cf.Format(balance)}. Overpayments are not accepted.");
            if (req.Kind == PaymentKind.Deposit && appt.Status is AppointmentStatus.Completed)
                throw new ValidationException("Record a regular payment for completed appointments.");

            var now = clock.Now;
            var payment = new Payment
            {
                SubmissionKey = req.SubmissionKey,
                ReceiptNumber = await Sequences.NextAsync(db, "RCT", now.Year, 5, c),
                AppointmentId = appt.Id,
                CustomerId = appt.CustomerId,
                Kind = req.Kind,
                Method = req.Method,
                AmountMinor = req.AmountMinor,
                ExternalReference = Normalization.Clean(req.ExternalReference),
                Note = Normalization.Clean(req.Note),
                RecordedAt = now,
                RecordedByUserId = user.UserId,
                RecordedByName = user.DisplayName,
            };
            db.Payments.Add(payment);
            appt.Payments.Add(payment);
            db.AppointmentEvents.Add(new AppointmentEvent
            {
                AppointmentId = appt.Id, Timestamp = now, Type = AppointmentEventType.PaymentRecorded, UserId = user.UserId, UserName = user.DisplayName,
                Description = $"{(req.Kind == PaymentKind.Deposit ? "Deposit" : "Payment")} of {cf.Format(req.AmountMinor)} via {MethodName(req.Method)} ({payment.ReceiptNumber})",
            });
            foreach (var inv in appt.Invoices.Where(i => i.Status != InvoiceStatus.Void)) inv.Status = InvoiceService.ComputeStatus(inv, appt.Payments);
            Audit.Add(db, user, clock, "Payment.Recorded", "Payment", null, $"{cf.Format(req.AmountMinor)} {MethodName(req.Method)} payment for {appt.Reference}", appt.CustomerId);
            await db.SaveChangesAsync(c);
            return payment.ReceiptNumber;
        }, ct);

        notifier.Publish(DataArea.Payments | DataArea.Appointments);
        return receipt;
    }

    /// <summary>Records money returned to the customer against a specific earlier payment.</summary>
    public async Task<string> RefundAsync(int paymentId, long amountMinor, PaymentMethod method, string reason, CancellationToken ct = default)
    {
        user.Require(Permission.RefundPayments, "record refunds");
        if (amountMinor <= 0) throw new ValidationException("The refund amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("Enter a reason for the refund.");
        return await CreateOutflowAsync(paymentId, PaymentKind.Refund, amountMinor, method, reason, ct);
    }

    /// <summary>Corrects a payment recorded by mistake. The original entry is kept; a reversal entry offsets it.</summary>
    public async Task<string> ReverseAsync(int paymentId, string reason, CancellationToken ct = default)
    {
        user.Require(Permission.ReversePayments, "reverse payments");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("Enter a reason for the reversal.");
        return await CreateOutflowAsync(paymentId, PaymentKind.Reversal, null, null, reason, ct);
    }

    private async Task<string> CreateOutflowAsync(int paymentId, PaymentKind kind, long? amountMinor, PaymentMethod? method, string reason, CancellationToken ct)
    {
        var cf = settings.Currency;
        var receipt = await writer.ExecuteAsync(async (db, c) =>
        {
            var original = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, c) ?? throw new NotFoundException("Payment", paymentId);
            if (!original.IsInflow) throw new ValidationException("Only payments and deposits can be refunded or reversed.");
            var alreadyOut = await db.Payments.Where(p => p.ReversesPaymentId == paymentId).SumAsync(p => p.AmountMinor, c);
            var refundable = original.AmountMinor - alreadyOut;
            if (refundable <= 0) throw new ValidationException("This payment has already been fully refunded or reversed.");
            var amount = amountMinor ?? refundable;
            if (amount > refundable)
                throw new ValidationException($"At most {cf.Format(refundable)} of this payment can be refunded.");

            var now = clock.Now;
            var entry = new Payment
            {
                SubmissionKey = Guid.NewGuid(),
                ReceiptNumber = await Sequences.NextAsync(db, kind == PaymentKind.Refund ? "RFD" : "REV", now.Year, 5, c),
                AppointmentId = original.AppointmentId,
                CustomerId = original.CustomerId,
                Kind = kind,
                Method = method ?? original.Method,
                AmountMinor = amount,
                Note = reason.Trim(),
                ReversesPaymentId = original.Id,
                RecordedAt = now,
                RecordedByUserId = user.UserId,
                RecordedByName = user.DisplayName,
            };
            db.Payments.Add(entry);
            if (original.AppointmentId is { } apptId)
            {
                db.AppointmentEvents.Add(new AppointmentEvent
                {
                    AppointmentId = apptId, Timestamp = now, UserId = user.UserId, UserName = user.DisplayName,
                    Type = kind == PaymentKind.Refund ? AppointmentEventType.RefundRecorded : AppointmentEventType.PaymentReversed,
                    Description = $"{(kind == PaymentKind.Refund ? "Refund" : "Reversal")} of {cf.Format(amount)} against {original.ReceiptNumber}: {reason.Trim()}",
                });
                var invoices = await db.Invoices.Where(i => i.AppointmentId == apptId && i.Status != InvoiceStatus.Void).ToListAsync(c);
                if (invoices.Count > 0)
                {
                    var payments = await db.Payments.Where(p => p.AppointmentId == apptId).ToListAsync(c);
                    payments.Add(entry);
                    foreach (var invoice in invoices) invoice.Status = InvoiceService.ComputeStatus(invoice, payments);
                }
            }

            Audit.Add(db, user, clock, kind == PaymentKind.Refund ? "Payment.Refunded" : "Payment.Reversed", "Payment", original.Id,
                $"{(kind == PaymentKind.Refund ? "Refund" : "Reversal")} of {cf.Format(amount)} ({original.ReceiptNumber})", original.CustomerId);
            await db.SaveChangesAsync(c);
            return entry.ReceiptNumber;
        }, ct);
        notifier.Publish(DataArea.Payments | DataArea.Appointments);
        return receipt;
    }

    public async Task<(PagedResult<PaymentDto> Page, PaymentTotals Totals)> ListAsync(PaymentQuery q, CancellationToken ct = default)
    {
        user.Require(Permission.ViewPayments, "view payments");
        await using var db = dbFactory.CreateReadContext();
        var query = db.Payments.AsNoTracking().AsQueryable();
        if (q.From is not null) query = query.Where(p => p.RecordedAt >= q.From);
        if (q.To is not null) query = query.Where(p => p.RecordedAt < q.To);
        if (q.Method is not null) query = query.Where(p => p.Method == q.Method);
        if (q.Kind is not null) query = query.Where(p => p.Kind == q.Kind);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var like = Normalization.LikePattern(q.Search);
            query = query.Where(p => EF.Functions.Like(p.ReceiptNumber, like, "\\") || EF.Functions.Like(p.Customer!.FullName, like, "\\") ||
                                     (p.Appointment != null && EF.Functions.Like(p.Appointment.Reference, like, "\\")));
        }

        var inflow = await query.Where(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment).SumAsync(p => p.AmountMinor, ct);
        var outflow = await query.Where(p => p.Kind == PaymentKind.Refund || p.Kind == PaymentKind.Reversal).SumAsync(p => p.AmountMinor, ct);
        var count = await query.CountAsync(ct);
        var page = Math.Max(1, q.Page);
        var rows = await query.Include(p => p.Customer).Include(p => p.Appointment).OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var outs = await db.Payments.AsNoTracking().Where(p => p.ReversesPaymentId != null && ids.Contains(p.ReversesPaymentId.Value)).ToListAsync(ct);
        var dtos = ToDtos(rows.Concat(outs.Where(o => !ids.Contains(o.Id))).ToList(), null, null).Where(d => ids.Contains(d.Id)).ToList();
        return (new PagedResult<PaymentDto>(dtos, count, page, q.PageSize), new PaymentTotals(inflow, outflow, inflow - outflow, count));
    }

    public async Task<IReadOnlyList<OutstandingItem>> GetOutstandingAsync(CancellationToken ct = default)
    {
        user.Require(Permission.ViewPayments, "view outstanding balances");
        await using var db = dbFactory.CreateReadContext();
        var now = clock.Now;
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatus.Completed || a.Status == AppointmentStatus.InProgress || a.Status == AppointmentStatus.CheckedIn ||
                         ((a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.Pending) && a.Start < now)))
            .Select(a => new
            {
                a.Id, a.Reference, a.Start, a.Status, a.CustomerId, Customer = a.Customer!.FullName, a.TotalMinor,
                Paid = a.Payments.Sum(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor),
            }).ToListAsync(ct);
        return rows.Where(r => r.TotalMinor - r.Paid > 0).OrderBy(r => r.Start)
            .Select(r => new OutstandingItem(r.Id, r.Reference, r.Start, r.Status, r.CustomerId, r.Customer, r.TotalMinor, r.Paid, r.TotalMinor - r.Paid)).ToList();
    }

    public async Task<IReadOnlyList<AppointmentListItem>> FindPayableAppointmentsAsync(string? term, CancellationToken ct = default)
    {
        user.Require(Permission.RecordPayments, "record payments");
        await using var db = dbFactory.CreateReadContext();
        var q = db.Appointments.AsNoTracking().Where(a => a.Status != AppointmentStatus.Draft);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var like = Normalization.LikePattern(term);
            q = q.Where(a => EF.Functions.Like(a.Reference, like, "\\") || EF.Functions.Like(a.Customer!.FullName, like, "\\"));
        }

        var rows = await q.OrderByDescending(a => a.Start).Take(200).Select(a => new
        {
            a.Id, a.Reference, a.Start, a.End, a.Status, a.CustomerId, Customer = a.Customer!.FullName, a.TotalMinor,
            Paid = a.Payments.Sum(p => p.Kind == PaymentKind.Deposit || p.Kind == PaymentKind.Payment ? p.AmountMinor : -p.AmountMinor),
            Services = a.Segments.OrderBy(s => s.SortOrder).Select(s => s.ServiceName).ToList(),
        }).ToListAsync(ct);
        return rows.Where(r => r.TotalMinor - r.Paid > 0).Take(50)
            .Select(r => new AppointmentListItem(r.Id, r.Reference, r.Start, r.End, r.Status, r.CustomerId, r.Customer,
                string.Join(", ", r.Services), "", null, r.TotalMinor, r.Paid, null)).ToList();
    }

    // ---------------------------------------------------------------- helpers

    public static IReadOnlyList<PaymentDto> ToDtos(IReadOnlyCollection<Payment> payments, string? appointmentReference, string? customerName)
    {
        var outflows = payments.Where(p => p.ReversesPaymentId != null).GroupBy(p => p.ReversesPaymentId!.Value)
            .ToDictionary(g => g.Key, g => (Sum: g.Sum(p => p.AmountMinor), Reversed: g.Any(p => p.Kind == PaymentKind.Reversal)));
        return payments.OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.Id).Select(p =>
        {
            var o = outflows.GetValueOrDefault(p.Id);
            return new PaymentDto(p.Id, p.ReceiptNumber, p.Kind, p.Method, p.AmountMinor, p.SignedAmountMinor, p.RecordedAt, p.RecordedByName,
                p.Note, p.ExternalReference, p.ReversesPaymentId, o.Reversed, p.IsInflow ? Math.Max(0, p.AmountMinor - o.Sum) : 0,
                p.AppointmentId, appointmentReference ?? p.Appointment?.Reference, p.CustomerId, customerName ?? p.Customer?.FullName ?? "");
        }).ToList();
    }

    public static string PaymentStatus(long totalMinor, long depositRequiredMinor, IReadOnlyCollection<Payment> payments)
    {
        var paid = PaymentMath.NetPaid(payments);
        if (totalMinor == 0) return "No Charge";
        if (paid <= 0) return payments.Any(p => !p.IsInflow) ? "Refunded" : "Unpaid";
        if (paid >= totalMinor) return "Paid";
        var onlyDeposits = payments.Where(p => p.IsInflow).All(p => p.Kind == PaymentKind.Deposit);
        if (onlyDeposits && depositRequiredMinor > 0 && paid >= depositRequiredMinor) return "Paid Deposit";
        return "Partially Paid";
    }

    public static string MethodName(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.BankTransfer => "Bank Transfer",
        PaymentMethod.CardExternal => "Card (external terminal)",
        _ => "Other",
    };

    public static string KindName(PaymentKind k) => k switch
    {
        PaymentKind.Deposit => "Deposit",
        PaymentKind.Payment => "Payment",
        PaymentKind.Refund => "Refund",
        _ => "Reversal",
    };
}
