using Kedo.Compiler.Model;
using Kedo.Diagnostics;
using Kedo.Schema;

namespace Kedo.Compiler.Resolution;

/// <summary>
/// Reads the duration base unit (<c>base_unit</c> of the manifest, ADR-0014) from every file that is resolved together.
/// </summary>
internal static class ModuleTimeUnit
{
    private const string Field = "base_unit";

    /// <summary>
    /// The unit for the content of <paramref name="files"/>, read in input order: minutes when no manifest sets one, an
    /// invalid value is K2011 (ignored), and a second, different valid unit is K2012 (<paramref name="conflict"/> is
    /// true and minutes are used).
    /// </summary>
    public static DurationUnit Determine(IReadOnlyList<FileDeclarations> files, DiagnosticBag diagnostics, out bool conflict)
    {
        conflict = false;
        var found = false;
        var unit = DurationUnit.Minute;
        Origin first = default;
        List<Value>? invalid = null;
        foreach (var file in files)
        {
            if (file.Manifest is null)
            {
                continue;
            }

            foreach (var entry in file.Manifest.Entries)
            {
                if (entry is not FieldEntry { Name: Field } field)
                {
                    continue;
                }

                var value = field.Value;
                if (value is not EnumValue named || !DurationUnits.TryParseSuffix(named.Name, out var parsed))
                {
                    (invalid ??= new List<Value>()).Add(value);
                    continue;
                }

                if (!found)
                {
                    found = true;
                    unit = parsed;
                    first = value.Origin;
                }
                else if (parsed != unit && !conflict)
                {
                    conflict = true;
                    diagnostics.Add(new Diagnostic("K2012", Severity.Error,
                        $"`{Field} = {named.Name}` differs from `{Field} = {DurationUnits.Suffix(unit)}` declared in another manifest; content resolved together counts durations in one unit",
                        value.Origin.File, value.Origin.Span,
                        new[] { new DiagnosticLabel(first.File, first.Span, "first declared here") }, new[] { "declare the same `base_unit` in every manifest" }));
                }
            }
        }

        var result = conflict ? DurationUnit.Minute : unit;
        if (invalid is not null)
        {
            foreach (var value in invalid)
            {
                diagnostics.Warning("K2011", $"`{Field} = {Describe(value)}` is not a duration unit (`ms`, `s`, `min`, `h` or `d`); durations stay in `{DurationUnits.Suffix(result)}`", value.Origin.File, value.Origin.Span);
            }
        }

        return result;
    }

    private static string Describe(Value value) => value switch
    {
        EnumValue e => e.Name,
        DurationValue d => $"{d.Amount}{d.Unit}",
        IntValue i => i.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TextValue t => $"\"{t.Text}\"",
        BoolValue b => b.Flag ? "true" : "false",
        _ => "...",
    };
}
