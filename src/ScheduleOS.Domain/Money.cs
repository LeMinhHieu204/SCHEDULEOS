namespace ScheduleOS.Domain;

/// <summary>
/// Money is always stored as a 64-bit integer number of minor currency units (e.g. cents).
/// Floating point types are never used for monetary values.
/// </summary>
public static class Money
{
    /// <summary>Applies a rate expressed in basis points (1/100 of a percent), rounding half away from zero.</summary>
    public static long ApplyBasisPoints(long amountMinor, int basisPoints)
    {
        if (basisPoints == 0 || amountMinor == 0) return 0;
        var product = (decimal)amountMinor * basisPoints / 10_000m;
        return (long)Math.Round(product, 0, MidpointRounding.AwayFromZero);
    }

    public static long FromMajor(decimal major, int decimals) =>
        (long)Math.Round(major * Pow10(decimals), 0, MidpointRounding.AwayFromZero);

    public static decimal ToMajor(long minor, int decimals) => minor / (decimal)Pow10(decimals);

    private static long Pow10(int decimals)
    {
        long result = 1;
        for (var i = 0; i < decimals; i++) result *= 10;
        return result;
    }
}
