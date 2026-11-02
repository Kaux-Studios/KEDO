using System.Globalization;

namespace Kedo.Schema;

/// <summary>Base unit in which a module stores its durations (ADR-0014).</summary>
public enum DurationUnit : byte
{
    /// <summary>Minutes; the default, as in KEDO 1.0.</summary>
    Minute = 0,
    /// <summary>Milliseconds.</summary>
    Millisecond = 1,
    /// <summary>Seconds.</summary>
    Second = 2,
    /// <summary>Hours.</summary>
    Hour = 3,
    /// <summary>Days.</summary>
    Day = 4,
}

/// <summary>Exact conversions and text forms for <see cref="DurationUnit"/>.</summary>
public static class DurationUnits
{
    /// <summary>Milliseconds contained in one <paramref name="unit"/>.</summary>
    public static long MillisecondsPer(DurationUnit unit) => unit switch
    {
        DurationUnit.Millisecond => 1,
        DurationUnit.Second => 1_000,
        DurationUnit.Minute => 60_000,
        DurationUnit.Hour => 3_600_000,
        DurationUnit.Day => 86_400_000,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    /// <summary>Literal suffix of <paramref name="unit"/>: `ms`, `s`, `min`, `h` or `d`.</summary>
    public static string Suffix(DurationUnit unit) => unit switch
    {
        DurationUnit.Millisecond => "ms",
        DurationUnit.Second => "s",
        DurationUnit.Minute => "min",
        DurationUnit.Hour => "h",
        DurationUnit.Day => "d",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    /// <summary>Parses one of the five exact suffixes.</summary>
    public static bool TryParseSuffix(string? suffix, out DurationUnit unit)
    {
        switch (suffix)
        {
            case "ms": unit = DurationUnit.Millisecond; return true;
            case "s": unit = DurationUnit.Second; return true;
            case "min": unit = DurationUnit.Minute; return true;
            case "h": unit = DurationUnit.Hour; return true;
            case "d": unit = DurationUnit.Day; return true;
            default: unit = DurationUnit.Minute; return false;
        }
    }

    /// <summary>Converts exactly; false when the result is not a whole number of <paramref name="to"/> or overflows.</summary>
    public static bool TryConvert(long amount, DurationUnit from, DurationUnit to, out long result)
    {
        Int128 total = (Int128)amount * MillisecondsPer(from);
        long divisor = MillisecondsPer(to);
        if (total % divisor != 0)
        {
            result = 0;
            return false;
        }
        Int128 quotient = total / divisor;
        if (quotient < long.MinValue || quotient > long.MaxValue)
        {
            result = 0;
            return false;
        }
        result = (long)quotient;
        return true;
    }

    /// <summary>Converts truncating toward zero; saturates at the <see cref="long"/> limits on overflow.</summary>
    public static long ConvertTruncating(long amount, DurationUnit from, DurationUnit to)
    {
        Int128 quotient = (Int128)amount * MillisecondsPer(from) / MillisecondsPer(to);
        if (quotient > long.MaxValue) return long.MaxValue;
        if (quotient < long.MinValue) return long.MinValue;
        return (long)quotient;
    }

    /// <summary>Canonical text, for example `350ms`.</summary>
    public static string Format(long amount, DurationUnit unit) => amount.ToString(CultureInfo.InvariantCulture) + Suffix(unit);
}
