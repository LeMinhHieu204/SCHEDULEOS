namespace ScheduleOS.Domain.Entities;

public class ServiceCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ColorHex { get; set; } = "#008C95";
    public int SortOrder { get; set; }
    public bool IsDemo { get; set; }
}

public class Service
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? CategoryId { get; set; }
    public ServiceCategory? Category { get; set; }
    public string? Description { get; set; }
    public int DurationMinutes { get; set; }
    public int PrepMinutes { get; set; }
    public int CleanupMinutes { get; set; }
    public long PriceMinor { get; set; }
    public long DepositMinor { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsArchived { get; set; }
    public string? ImagePath { get; set; }
    public bool IsDemo { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Version { get; set; }
    public List<ServiceResourceRequirement> ResourceRequirements { get; set; } = [];
    public List<StaffSkill> QualifiedStaff { get; set; } = [];

    public bool IsBookable => IsActive && !IsArchived;
}

/// <summary>A service needs one resource of the given type for its whole blocked interval.</summary>
public class ServiceResourceRequirement
{
    public int Id { get; set; }
    public int ServiceId { get; set; }
    public Service? Service { get; set; }
    public ResourceType ResourceType { get; set; }
}

public class Resource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ResourceType Type { get; set; }
    public int? LocationId { get; set; }
    public Location? Location { get; set; }
    public int Capacity { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsArchived { get; set; }
    public string? Notes { get; set; }
    public TimeOnly? AvailableFrom { get; set; }
    public TimeOnly? AvailableUntil { get; set; }
    public bool IsDemo { get; set; }
    public int Version { get; set; }
    public List<ResourceMaintenance> Maintenance { get; set; } = [];

    public bool IsBookable => IsActive && !IsArchived;
}

/// <summary>A period during which a resource cannot be booked (maintenance or a manual block).</summary>
public class ResourceMaintenance
{
    public int Id { get; set; }
    public int ResourceId { get; set; }
    public Resource? Resource { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Reason { get; set; } = "";
}
