namespace ScheduleOS.Domain.Entities;

/// <summary>Persisted reminder queue entry. Delivery status reflects what actually happened.</summary>
public class Reminder
{
    public int Id { get; set; }
    public int? AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }
    public int? CustomerId { get; set; }
    public ReminderChannel Channel { get; set; }
    public ReminderType Type { get; set; }
    public DateTime DueAt { get; set; }
    public ReminderStatus Status { get; set; }
    public string? Recipient { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? ProcessedAt { get; set; }
    /// <summary>Deterministic key preventing the same reminder from being queued twice.</summary>
    public string DedupKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsDemo { get; set; }
}

/// <summary>In-application notification shown in the notification centre.</summary>
public class Notification
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public NotificationKind Kind { get; set; }
    public bool IsRead { get; set; }
    public bool IsDismissed { get; set; }
    public int? AppointmentId { get; set; }
    public int? ReminderId { get; set; }
    public bool IsDemo { get; set; }
}

/// <summary>Append-only audit trail; also feeds the dashboard "Customer Activity" panel.</summary>
public class AuditLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int? UserId { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public int? EntityId { get; set; }
    public int? CustomerId { get; set; }
    public string Summary { get; set; } = "";
    public bool IsDemo { get; set; }
}
