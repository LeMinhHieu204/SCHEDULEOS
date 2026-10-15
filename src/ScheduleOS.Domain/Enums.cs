namespace ScheduleOS.Domain;

public enum UserRole
{
    Owner = 1,
    Manager = 2,
    Receptionist = 3,
    Staff = 4,
}

public enum AppointmentStatus
{
    Draft = 0,
    Pending = 1,
    Confirmed = 2,
    CheckedIn = 3,
    InProgress = 4,
    Completed = 5,
    Cancelled = 6,
    NoShow = 7,
}

public enum BookingSource
{
    Manual = 0,
    Phone = 1,
    WalkIn = 2,
    Online = 3,
    Other = 4,
}

public enum EmploymentStatus
{
    Active = 0,
    OnLeave = 1,
    Inactive = 2,
}

public enum ResourceType
{
    TreatmentRoom = 0,
    MassageRoom = 1,
    HairStation = 2,
    NailStation = 3,
    Equipment = 4,
    Other = 5,
}

public enum PaymentKind
{
    Deposit = 0,
    Payment = 1,
    Refund = 2,
    Reversal = 3,
}

public enum PaymentMethod
{
    Cash = 0,
    BankTransfer = 1,
    CardExternal = 2,
    Other = 3,
}

public enum InvoiceStatus
{
    Issued = 0,
    PartiallyPaid = 1,
    Paid = 2,
    Void = 3,
    Refunded = 4,
}

public enum ReminderChannel
{
    InApp = 0,
    Email = 1,
    Sms = 2,
}

public enum ReminderType
{
    Confirmation = 0,
    Upcoming = 1,
    Cancellation = 2,
    Reschedule = 3,
    FollowUp = 4,
}

public enum ReminderStatus
{
    Scheduled = 0,
    Sent = 1,
    NotSent = 2,
    Failed = 3,
    Cancelled = 4,
}

public enum AppointmentEventType
{
    Created = 0,
    StatusChanged = 1,
    Rescheduled = 2,
    ServicesChanged = 3,
    Reassigned = 4,
    NotesUpdated = 5,
    PaymentRecorded = 6,
    RefundRecorded = 7,
    PaymentReversed = 8,
    InvoiceIssued = 9,
    ReminderScheduled = 10,
    ReminderSent = 11,
    ReminderNotSent = 12,
    ReminderFailed = 13,
    FollowUpAdded = 14,
    StatusCorrected = 15,
}

public enum TimeOffType
{
    Leave = 0,
    Sick = 1,
    Holiday = 2,
    Training = 3,
    Other = 4,
}

public enum NotificationKind
{
    Info = 0,
    Reminder = 1,
    Warning = 2,
    Success = 3,
    Error = 4,
}

public enum ConflictType
{
    InvalidTime,
    StaffOverlap,
    ResourceOverlap,
    ResourceCapacity,
    OutsideStaffHours,
    StaffBreak,
    StaffTimeOff,
    StaffInactive,
    StaffNotQualified,
    StaffWrongLocation,
    OutsideBusinessHours,
    BusinessClosed,
    ResourceMaintenance,
    ResourceUnavailable,
    ResourceTypeMismatch,
    MissingResource,
    ResourceWrongLocation,
    ServiceInactive,
    LeadTime,
}

public enum ConflictSeverity
{
    /// <summary>Can be overridden by an authorised user (e.g. overtime).</summary>
    Warning = 0,

    /// <summary>Can never be saved.</summary>
    Blocking = 1,
}
