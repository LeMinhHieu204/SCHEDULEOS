namespace ScheduleOS.Domain.Entities;

/// <summary>Gap-free counters for human readable references (AP-2026-0001, INV-2026-0001, ...).</summary>
public class NumberSequence
{
    public string Key { get; set; } = "";
    public int NextValue { get; set; } = 1;
}
