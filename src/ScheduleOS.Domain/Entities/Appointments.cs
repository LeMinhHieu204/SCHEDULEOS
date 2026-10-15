namespace ScheduleOS.Domain.Entities;

public class Appointment
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    /// <summary>Client-generated key used to reject duplicate submissions of the same booking form.</summary>
    public Guid SubmissionKey { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Start of the first service (customer-facing).</summary>
    public DateTime Start { get; set; }

    /// <summary>End of the last service (customer-facing).</summary>
    public DateTime End { get; set; }

    public AppointmentStatus Status { get; set; }
    public BookingSource Source { get; set; }
    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }

    public long SubtotalMinor { get; set; }
    public long DiscountMinor { get; set; }
    public int TaxRateBasisPoints { get; set; }
    public long TaxMinor { get; set; }
    public long TotalMinor { get; set; }
    public long DepositRequiredMinor { get; set; }

    public bool RemindByEmail { get; set; }
    public bool RemindBySms { get; set; }
    public int ReminderLeadHours { get; set; } = 24;

    public bool IsDemo { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token, incremented on every change.</summary>
    public int Version { get; set; }

    public List<AppointmentSegment> Segments { get; set; } = [];
    public List<AppointmentEvent> Events { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<Reminder> Reminders { get; set; } = [];
    /// <summary>Issued invoices; at most one is not void at any time.</summary>
    public List<Invoice> Invoices { get; set; } = [];

    public TimeSpan Duration => End - Start;

    /// <summary>Recomputes overall times and pricing from the service segments.</summary>
    public void RecalculateFromSegments()
    {
        if (Segments.Count == 0)
        {
            SubtotalMinor = DiscountMinor = TaxMinor = TotalMinor = 0;
            return;
        }

        Start = Segments.Min(s => s.Start);
        End = Segments.Max(s => s.End);
        SubtotalMinor = Segments.Sum(s => s.UnitPriceMinor);
        DiscountMinor = Segments.Sum(s => s.DiscountMinor);
        var net = SubtotalMinor - DiscountMinor;
        TaxMinor = Money.ApplyBasisPoints(net, TaxRateBasisPoints);
        TotalMinor = net + TaxMinor;
        DepositRequiredMinor = Math.Min(Segments.Sum(s => s.DepositMinor), TotalMinor);
    }

    public void ChangeStatus(AppointmentStatus to)
    {
        if (!AppointmentLifecycle.CanTransition(Status, to)) throw new InvalidTransitionException(Status, to);
        Status = to;
        var blocking = AppointmentLifecycle.IsBlocking(to);
        foreach (var s in Segments) s.IsBlocking = blocking;
    }

    public void ApplyCorrection()
    {
        var target = AppointmentLifecycle.CorrectionTarget(Status)
                     ?? throw new DomainException($"No correction is defined for status {Status}.");
        Status = target;
        foreach (var s in Segments) s.IsBlocking = AppointmentLifecycle.IsBlocking(target);
    }

    /// <summary>Net amount collected: deposits and payments minus refunds and reversals.</summary>
    public long PaidMinor => PaymentMath.NetPaid(Payments);

    public long BalanceMinor => TotalMinor - PaidMinor;
}

/// <summary>
/// One service performed within an appointment, with its assigned staff member and room/resource.
/// Segments are scheduled sequentially; preparation/cleanup buffers extend the blocked interval.
/// </summary>
public class AppointmentSegment
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int SortOrder { get; set; }

    public int ServiceId { get; set; }
    public Service? Service { get; set; }
    /// <summary>Snapshot of the service name at booking time (historical record).</summary>
    public string ServiceName { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int PrepMinutes { get; set; }
    public int CleanupMinutes { get; set; }
    public long UnitPriceMinor { get; set; }
    public long DiscountMinor { get; set; }
    public long DepositMinor { get; set; }

    public int StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }
    public int? ResourceId { get; set; }
    public Resource? Resource { get; set; }

    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Start including preparation buffer.</summary>
    public DateTime BlockStart { get; set; }
    /// <summary>End including cleanup buffer.</summary>
    public DateTime BlockEnd { get; set; }

    /// <summary>Denormalised from the appointment status; used by the conflict queries and DB triggers.</summary>
    public bool IsBlocking { get; set; }

    public long LineTotalMinor => UnitPriceMinor - DiscountMinor;
}

/// <summary>Immutable, timestamped history entry for an appointment.</summary>
public class AppointmentEvent
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public DateTime Timestamp { get; set; }
    public AppointmentEventType Type { get; set; }
    public AppointmentStatus? FromStatus { get; set; }
    public AppointmentStatus? ToStatus { get; set; }
    public string Description { get; set; } = "";
    public int? UserId { get; set; }
    public string? UserName { get; set; }
}
