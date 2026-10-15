namespace ScheduleOS.Domain.Entities;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; }
    public UserRole Role { get; set; }
    public int? StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? NormalizedPhone { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public DateOnly? Birthday { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public int? PreferredStaffId { get; set; }
    public StaffMember? PreferredStaff { get; set; }
    public bool AllowEmail { get; set; } = true;
    public bool AllowSms { get; set; } = true;
    public bool IsArchived { get; set; }
    public bool IsAnonymized { get; set; }
    public bool IsDemo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
    public List<CustomerTag> Tags { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];

    public string Initials => NameHelper.Initials(FullName);
}

public class CustomerTag
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Tag { get; set; } = "";
}

public class StaffMember
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Position { get; set; } = "";
    public string? SkillsText { get; set; }
    public EmploymentStatus EmploymentStatus { get; set; } = EmploymentStatus.Active;
    public int? LocationId { get; set; }
    public Location? Location { get; set; }
    public string ColorHex { get; set; } = "#008C95";
    public bool IsArchived { get; set; }
    public bool IsDemo { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
    public List<StaffSkill> Skills { get; set; } = [];
    public List<StaffShift> Shifts { get; set; } = [];
    public List<StaffTimeOff> TimeOff { get; set; } = [];

    public string Initials => NameHelper.Initials(FullName);
    public bool IsBookable => !IsArchived && EmploymentStatus != EmploymentStatus.Inactive;
}

/// <summary>Qualification of a staff member to perform a given service.</summary>
public class StaffSkill
{
    public int Id { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }
    public int ServiceId { get; set; }
    public Service? Service { get; set; }
}

/// <summary>Recurring weekly working shift, with an optional break.</summary>
public class StaffShift
{
    public int Id { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public TimeOnly? BreakStart { get; set; }
    public TimeOnly? BreakEnd { get; set; }
}

/// <summary>Leave, sick days and other one-off unavailability (temporary exceptions).</summary>
public class StaffTimeOff
{
    public int Id { get; set; }
    public int StaffMemberId { get; set; }
    public StaffMember? StaffMember { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public TimeOffType Type { get; set; }
    public string? Reason { get; set; }
}

public static class NameHelper
{
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1
            ? parts[0][..1].ToUpperInvariant()
            : (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }
}
