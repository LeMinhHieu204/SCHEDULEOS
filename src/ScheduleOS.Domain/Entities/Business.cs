namespace ScheduleOS.Domain.Entities;

public class Business
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LogoPath { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public int CurrencyDecimals { get; set; } = 2;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool IsDemoWorkspace { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDemo { get; set; }
}

/// <summary>Opening hours of the business per day of week.</summary>
public class BusinessHours
{
    public int Id { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public bool IsOpen { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
}

/// <summary>A whole-day business closure (public holiday, renovation, ...).</summary>
public class BusinessHoliday
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class BusinessSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
