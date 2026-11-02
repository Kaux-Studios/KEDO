using Kedo.Diagnostics;
using Kedo.Schema;
using Kedo.Text;

namespace Kedo.Compiler.Resolution;

/// <summary>Lowers an authored duration literal to a whole count of the module's base unit (ADR-0014).</summary>
internal static class DurationLowering
{
    /// <summary>
    /// Exact value of <paramref name="amount"/> <paramref name="suffix"/> in <paramref name="unit"/>. When it is not a whole
    /// number, minutes truncate with the K4014 warning (1.0 behaviour) and every other unit gets the K4014 error and <c>null</c>;
    /// a count that does not fit in 64 bits is a K4014 error and <c>null</c>. A <c>null</c> <paramref name="diagnostics"/> reports
    /// nothing; <paramref name="reportedSites"/> keeps a literal reached more than once (mixins) from being reported twice;
    /// <paramref name="sourceText"/> is the literal as written (`1_500ms`), quoted in the messages when known.
    /// </summary>
    public static long? ToUnit(long amount, string suffix, DurationUnit unit, string? file, TextSpan span, DiagnosticBag? diagnostics, ISet<string>? reportedSites = null, string? sourceText = null)
    {
        if (!DurationUnits.TryParseSuffix(suffix, out var from))
        {
            System.Diagnostics.Debug.Fail($"unknown duration suffix `{suffix}`");
            return amount;
        }

        if (DurationUnits.TryConvert(amount, from, unit, out var exact))
        {
            return exact;
        }

        var literal = sourceText ?? $"{amount}{suffix}";
        var total = (Int128)amount * DurationUnits.MillisecondsPer(from);
        var divisor = DurationUnits.MillisecondsPer(unit);
        var quotient = total / divisor;
        var fits = quotient >= long.MinValue && quotient <= long.MaxValue;
        var u = DurationUnits.Suffix(unit);

        if (!fits)
        {
            Report(diagnostics, reportedSites, file, span, error: true, $"`{literal}` does not fit in a 64-bit count of `{u}`; use a larger `base_unit` or a smaller amount");
            return null;
        }

        if (unit == DurationUnit.Minute)
        {
            var n = (long)quotient;
            Report(diagnostics, reportedSites, file, span, error: false,
                $"`{literal}` is not a whole number of `min` (the module's `base_unit`) and counts as `{n}min`; write it in `min`, or set a smaller `base_unit` in the manifest");
            return n;
        }

        Report(diagnostics, reportedSites, file, span, error: true,
            $"`{literal}` is not a whole number of `{u}` (the module's `base_unit`); write it in `{u}` or a larger unit, or set a smaller `base_unit` in the manifest");
        return null;
    }

    /// <summary>A millisecond count in the largest unit that divides it exactly (`10min`, `2h`, `350ms`); zero is `0ms`.</summary>
    public static string FormatMs(long milliseconds)
    {
        foreach (var unit in new[] { DurationUnit.Day, DurationUnit.Hour, DurationUnit.Minute, DurationUnit.Second })
        {
            var per = DurationUnits.MillisecondsPer(unit);
            if (milliseconds != 0 && milliseconds % per == 0)
            {
                return DurationUnits.Format(milliseconds / per, unit);
            }
        }

        return DurationUnits.Format(milliseconds, DurationUnit.Millisecond);
    }

    private static void Report(DiagnosticBag? diagnostics, ISet<string>? sites, string? file, TextSpan span, bool error, string message)
    {
        if (diagnostics is null || (sites is not null && !sites.Add($"{file}|{span.Start}")))
        {
            return;
        }

        if (error)
        {
            diagnostics.Error("K4014", message, file, span);
        }
        else
        {
            diagnostics.Warning("K4014", message, file, span);
        }
    }
}
