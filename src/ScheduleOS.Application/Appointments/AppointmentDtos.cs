using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Application.Appointments;

/// <summary>One requested service line in a booking form.</summary>
public sealed record ServiceLineRequest(
    int ServiceId,
    int StaffMemberId,
    int? ResourceId,
    long? UnitPriceMinor = null,
    long DiscountMinor = 0);

public sealed record CreateAppointmentRequest
{
    public Guid SubmissionKey { get; init; } = Guid.NewGuid();
    public int CustomerId { get; init; }
    public int? LocationId { get; init; }
    public DateTime Start { get; init; }
    public IReadOnlyList<ServiceLineRequest> Services { get; init; } = [];
    public AppointmentStatus InitialStatus { get; init; } = AppointmentStatus.Pending;
    public BookingSource Source { get; init; } = BookingSource.Manual;
    public string? CustomerNotes { get; init; }
    public string? InternalNotes { get; init; }
    public bool RemindByEmail { get; init; }
    public bool RemindBySms { get; init; }
    public int? ReminderLeadHours { get; init; }
    public bool OverrideWarnings { get; init; }
}

public sealed record RescheduleRequest
{
    public int AppointmentId { get; init; }
    public int ExpectedVersion { get; init; }
    public DateTime NewStart { get; init; }
    /// <summary>Optional reassignment per segment (by sort order). Null keeps the current assignment.</summary>
    public IReadOnlyDictionary<int, int>? StaffBySegment { get; init; }
    public IReadOnlyDictionary<int, int?>? ResourceBySegment { get; init; }
    /// <summary>Optional new duration for single-service appointments (calendar edge resize).</summary>
    public int? NewDurationMinutes { get; init; }
    public bool OverrideWarnings { get; init; }
    public string? Reason { get; init; }
}

public sealed record UpdateServicesRequest
{
    public int AppointmentId { get; init; }
    public int ExpectedVersion { get; init; }
    public IReadOnlyList<ServiceLineRequest> Services { get; init; } = [];
    public bool OverrideWarnings { get; init; }
}

public sealed record ConflictCheckRequest(
    DateTime Start,
    IReadOnlyList<ServiceLineRequest> Services,
    int? LocationId,
    int? ExcludeAppointmentId = null);

public sealed record SegmentPreview(int Index, string ServiceName, string StaffName, string? ResourceName, DateTime Start, DateTime End, DateTime BlockStart, DateTime BlockEnd);

public sealed record ConflictCheckResult(
    IReadOnlyList<SegmentPreview> Segments,
    IReadOnlyList<ScheduleConflict> Conflicts,
    DateTime Start,
    DateTime End,
    long SubtotalMinor,
    long DiscountMinor,
    long TaxMinor,
    long TotalMinor,
    long DepositMinor)
{
    public bool HasBlocking => Conflicts.Any(c => c.Severity == ConflictSeverity.Blocking);
    public bool HasWarnings => Conflicts.Any(c => c.Severity == ConflictSeverity.Warning);
    public bool IsClear => Conflicts.Count == 0;
    public int TotalMinutes => (int)(End - Start).TotalMinutes;
}

public sealed record FindSlotsRequest(
    IReadOnlyList<int> ServiceIds,
    DateOnly PreferredDate,
    int? PreferredStaffId,
    TimeOnly? PreferredTime,
    int? LocationId,
    int DaysToSearch = 7,
    int MaxResults = 12,
    int? ExcludeAppointmentId = null);

public sealed record AppointmentListItem(
    int Id,
    string Reference,
    DateTime Start,
    DateTime End,
    AppointmentStatus Status,
    int CustomerId,
    string CustomerName,
    string Services,
    string Staff,
    string? Resources,
    long TotalMinor,
    long PaidMinor,
    string? LocationName);

public sealed record AppointmentQuery
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public AppointmentStatus? Status { get; init; }
    public int? StaffMemberId { get; init; }
    public int? CustomerId { get; init; }
    public int? LocationId { get; init; }
    public string? Search { get; init; }
    public bool Descending { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record AppointmentActions(
    bool CanConfirm,
    bool CanCheckIn,
    bool CanStart,
    bool CanComplete,
    bool CanCancel,
    bool CanNoShow,
    bool CanReschedule,
    bool CanEditServices,
    bool CanEditNotes,
    bool CanEditInternalNotes,
    bool CanRecordPayment,
    bool CanRefund,
    bool CanReverse,
    AppointmentStatus? CorrectionTarget);

public sealed record SegmentDto(
    int Id,
    int SortOrder,
    int ServiceId,
    string ServiceName,
    string? CategoryName,
    int StaffMemberId,
    string StaffName,
    string StaffPosition,
    string StaffColor,
    int? ResourceId,
    string? ResourceName,
    string? ResourceType,
    DateTime Start,
    DateTime End,
    int DurationMinutes,
    int PrepMinutes,
    int CleanupMinutes,
    long UnitPriceMinor,
    long DiscountMinor,
    long LineTotalMinor);

public sealed record PaymentDto(
    int Id,
    string ReceiptNumber,
    PaymentKind Kind,
    PaymentMethod Method,
    long AmountMinor,
    long SignedAmountMinor,
    DateTime RecordedAt,
    string? RecordedBy,
    string? Note,
    string? ExternalReference,
    int? ReversesPaymentId,
    bool IsReversed,
    long RefundableMinor,
    int? AppointmentId,
    string? AppointmentReference,
    int CustomerId,
    string CustomerName);

public sealed record AppointmentEventDto(DateTime Timestamp, AppointmentEventType Type, string Title, string Description, string? UserName);

public sealed record ReminderDto(int Id, ReminderChannel Channel, ReminderType Type, DateTime DueAt, ReminderStatus Status, string? Recipient, string Subject, string? LastError, int Attempts, DateTime? ProcessedAt, int? AppointmentId, string? AppointmentReference);

public sealed record CustomerSummaryDto(
    int Id,
    string FullName,
    string Initials,
    string? Phone,
    string? Email,
    string? Address,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Segments,
    int CompletedVisits,
    long TotalPaidMinor,
    DateTime CustomerSince,
    string? Notes,
    bool ContactHidden);

public sealed record InvoiceSummaryDto(int Id, string Number, DateTime IssuedAt, InvoiceStatus Status, long TotalMinor);

public sealed record AppointmentDetailDto(
    int Id,
    string Reference,
    int Version,
    AppointmentStatus Status,
    BookingSource Source,
    DateTime Start,
    DateTime End,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime UpdatedAt,
    string? LocationName,
    int? LocationId,
    string? CustomerNotes,
    string? InternalNotes,
    bool InternalNotesVisible,
    string? CancellationReason,
    CustomerSummaryDto Customer,
    IReadOnlyList<SegmentDto> Segments,
    long SubtotalMinor,
    long DiscountMinor,
    int TaxRateBasisPoints,
    long TaxMinor,
    long TotalMinor,
    long DepositRequiredMinor,
    long PaidMinor,
    long BalanceMinor,
    string PaymentStatus,
    IReadOnlyList<PaymentDto> Payments,
    InvoiceSummaryDto? Invoice,
    IReadOnlyList<AppointmentEventDto> History,
    IReadOnlyList<ReminderDto> Reminders,
    bool RemindByEmail,
    bool RemindBySms,
    int ReminderLeadHours,
    AppointmentActions Actions)
{
    public int TotalMinutes => (int)(End - Start).TotalMinutes;
}

public sealed record CalendarQuery(DateTime From, DateTime To, int? LocationId, IReadOnlyList<int>? StaffIds, int? ServiceCategoryId, int? ServiceId, bool IncludeCancelled = false);

public sealed record CalendarSegment(
    int AppointmentId,
    int SegmentId,
    string Reference,
    int Version,
    AppointmentStatus Status,
    int CustomerId,
    string CustomerName,
    string ServiceName,
    string? CategoryColor,
    int StaffMemberId,
    string StaffName,
    string StaffColor,
    int? ResourceId,
    string? ResourceName,
    DateTime Start,
    DateTime End,
    DateTime AppointmentStart,
    DateTime AppointmentEnd,
    int SegmentCount,
    bool HasConflict,
    int SortOrder = 0);

public enum CalendarBlockKind { Break, TimeOff, Maintenance, Closed, OffShift }

public sealed record CalendarBlock(CalendarBlockKind Kind, DateTime Start, DateTime End, int? StaffMemberId, int? ResourceId, string Label);

public sealed record CalendarColumnInfo(int Id, string Name, string Subtitle, string Color);

public sealed record CalendarData(
    IReadOnlyList<CalendarSegment> Segments,
    IReadOnlyList<CalendarBlock> Blocks,
    IReadOnlyList<CalendarColumnInfo> Staff,
    IReadOnlyList<CalendarColumnInfo> Resources);

public sealed record ConflictReportItem(int AppointmentId, string Reference, DateTime Start, string CustomerName, IReadOnlyList<ScheduleConflict> Conflicts);
