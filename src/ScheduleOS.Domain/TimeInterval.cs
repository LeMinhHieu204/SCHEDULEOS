namespace ScheduleOS.Domain;

/// <summary>A half-open time interval [Start, End).</summary>
public readonly record struct TimeInterval
{
    public TimeInterval(DateTime start, DateTime end)
    {
        if (end < start) throw new ArgumentException("Interval end must not be before start.");
        Start = start;
        End = end;
    }

    public DateTime Start { get; }
    public DateTime End { get; }
    public TimeSpan Duration => End - Start;
    public bool IsEmpty => End == Start;

    /// <summary>Two intervals overlap when startA &lt; endB AND endA &gt; startB. Touching intervals do not overlap.</summary>
    public bool Overlaps(TimeInterval other) => Start < other.End && End > other.Start;

    public bool Contains(TimeInterval other) => Start <= other.Start && End >= other.End;

    public bool Contains(DateTime instant) => Start <= instant && instant < End;

    public static bool Overlaps(DateTime startA, DateTime endA, DateTime startB, DateTime endB) =>
        startA < endB && endA > startB;

    public override string ToString() => $"[{Start:yyyy-MM-dd HH:mm}, {End:HH:mm})";
}
