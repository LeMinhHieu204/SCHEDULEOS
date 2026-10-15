namespace ScheduleOS.Domain.Entities;

/// <summary>
/// Immutable payment ledger entry. Mistakes are corrected with a Reversal entry,
/// money returned to the customer with a Refund entry. Rows are never updated or deleted.
/// </summary>
public class Payment
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    /// <summary>Client-generated key that rejects duplicate submissions of the same payment form.</summary>
    public Guid SubmissionKey { get; set; }
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public PaymentKind Kind { get; set; }
    public PaymentMethod Method { get; set; }
    /// <summary>Always positive; the sign is implied by <see cref="Kind"/>.</summary>
    public long AmountMinor { get; set; }
    public string? ExternalReference { get; set; }
    public string? Note { get; set; }
    public int? ReversesPaymentId { get; set; }
    public DateTime RecordedAt { get; set; }
    public int? RecordedByUserId { get; set; }
    public string? RecordedByName { get; set; }
    public bool IsDemo { get; set; }

    public bool IsInflow => Kind is PaymentKind.Deposit or PaymentKind.Payment;
    public long SignedAmountMinor => IsInflow ? AmountMinor : -AmountMinor;
}

public static class PaymentMath
{
    public static long NetPaid(IEnumerable<Payment> payments) => payments.Sum(p => p.SignedAmountMinor);
}

public class Invoice
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int CustomerId { get; set; }
    public DateTime IssuedAt { get; set; }
    public InvoiceStatus Status { get; set; }
    public string CustomerName { get; set; } = "";
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public long SubtotalMinor { get; set; }
    public long DiscountMinor { get; set; }
    public int TaxRateBasisPoints { get; set; }
    public long TaxMinor { get; set; }
    public long TotalMinor { get; set; }
    public string? CustomerNotes { get; set; }
    public bool IsDemo { get; set; }
    public List<InvoiceLine> Lines { get; set; } = [];
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public string Description { get; set; } = "";
    public string? StaffName { get; set; }
    public int Quantity { get; set; } = 1;
    public long UnitPriceMinor { get; set; }
    public long DiscountMinor { get; set; }
    public long LineTotalMinor { get; set; }
}
