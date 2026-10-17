using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kedo.Compiler.Import;

/// <summary>Options of a JSON import.</summary>
public sealed class JsonImportOptions
{
    /// <summary>Module name written in the header.</summary>
    public string Module { get; init; } = "imported";

    /// <summary>Top-level collection → object type. Collections not listed become fields of the pack object.</summary>
    public Dictionary<string, string> Collections { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Field names whose string values are enumeration values (written bare) rather than text.</summary>
    public HashSet<string> EnumFields { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Type and id of the object that receives the remaining top-level keys.</summary>
    public string PackType { get; init; } = "pack";

    /// <summary>Id of the pack object; defaults to the JSON <c>version</c> field or <c>main</c>.</summary>
    public string? PackId { get; init; }

    /// <summary>Id field of collection items (default <c>id</c>).</summary>
    public string IdField { get; init; } = "id";
}

/// <summary>A string field whose values look like enumeration values (P5): identifiers that repeat.</summary>
/// <param name="Field">Field name as written in the JSON (what <c>--enums</c> takes).</param>
/// <param name="Values">Distinct values, ordinal order.</param>
/// <param name="Uses">How many times the field appears with a string value.</param>
public sealed record JsonEnumSuggestion(string Field, IReadOnlyList<string> Values, int Uses);

/// <summary>
/// Generic JSON → KEDO importer for migrating existing data-driven content (first used for Patriark's pack). It produces one
/// file per collection with ids preserved, plus a pack object holding every other top-level key. It is a starting
/// point, not a faithful schema: inheritance, mixins and asset keys are added by hand afterwards.
/// </summary>
public static class JsonImporter
{
    private static readonly Regex Identifier = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Imports a JSON document. Returns file name → KEDO source.</summary>
    public static IReadOnlyDictionary<string, string> Import(string json, JsonImportOptions options)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var pack = new StringBuilder();
        var packId = options.PackId ?? (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? Sanitize(v.GetString()!) : "main");
        pack.Append("kedo 1\nmodule ").Append(options.Module).Append("\n\n");
        pack.Append($"/// Imported from JSON: everything that was not mapped to a collection.\n{options.PackType} {packId} {{\n");

        foreach (var property in root.EnumerateObject())
        {
            if (options.Collections.TryGetValue(property.Name, out var type))
            {
                var sb = new StringBuilder();
                sb.Append("kedo 1\nmodule ").Append(options.Module).Append("\n\n");
                sb.Append($"// Imported from JSON collection `{property.Name}`.\n\n");
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(options.IdField, out var idProp))
                        {
                            continue;
                        }

                        WriteObject(sb, type, Sanitize(idProp.ToString()), item, options, skipField: options.IdField);
                    }
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in property.Value.EnumerateObject())
                    {
                        WriteObject(sb, type, Sanitize(entry.Name), entry.Value, options, skipField: null);
                    }
                }

                files[property.Name + ".kedo"] = sb.ToString();
                continue;
            }

            WriteField(pack, 1, property.Name, property.Value, options);
        }

        pack.Append("}\n");
        files[options.PackType + ".kedo"] = pack.ToString();
        return files;
    }

    private static void WriteObject(StringBuilder sb, string type, string id, JsonElement item, JsonImportOptions options, string? skipField)
    {
        sb.Append(type).Append(' ').Append(id).Append(" {\n");
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in item.EnumerateObject())
            {
                if (field.Name == skipField)
                {
                    continue;
                }

                WriteField(sb, 1, field.Name, field.Value, options);
            }
        }
        else
        {
            WriteField(sb, 1, "value", item, options);
        }

        sb.Append("}\n\n");
    }

    private static void WriteField(StringBuilder sb, int depth, string name, JsonElement value, JsonImportOptions options)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            // KEDO has no null: the field is left out, and the comment keeps the difference visible in review.
            sb.Append(' ', depth * 4).Append("// ").Append(name).Append(" = null in the JSON: KEDO has no null, the field is omitted\n");
            return;
        }

        sb.Append(' ', depth * 4).Append(Key(name)).Append(" = ");
        WriteValue(sb, depth, name, value, options);
        sb.Append('\n');
    }

    /// <summary>A key as KEDO writes it: identifiers bare, anything else quoted (ADR-0009), never renamed.</summary>
    private static string Key(string name)
        => Identifier.IsMatch(name) ? name : "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void WriteValue(StringBuilder sb, int depth, string fieldName, JsonElement value, JsonImportOptions options)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var i))
                {
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    // Fixed-point decimal (ADR-0008), written as the JSON wrote it so `1.0` stays `1.0`.
                    var raw = value.GetRawText();
                    var text = raw.Contains('e') || raw.Contains('E')
                        ? (value.TryGetDecimal(out var m) ? m.ToString(CultureInfo.InvariantCulture) : raw)
                        : raw;
                    var dot = text.IndexOf('.');
                    if (dot > 0 && text.Length - dot - 1 <= 9 && !text.Contains('e') && !text.Contains('E'))
                    {
                        sb.Append(text);
                    }
                    else
                    {
                        sb.Append(((long)Math.Round(value.GetDouble())).ToString(CultureInfo.InvariantCulture)).Append(" /* TODO: was ").Append(raw).Append(", more than 9 decimal places */");
                    }
                }

                break;
            case JsonValueKind.String:
            {
                var s = value.GetString()!;
                if (options.EnumFields.Contains(fieldName) && Identifier.IsMatch(s))
                {
                    sb.Append(s);
                }
                else
                {
                    sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append('"');
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var items = value.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    sb.Append("[]");
                    break;
                }

                var allObjects = items.All(x => x.ValueKind == JsonValueKind.Object);
                if (allObjects)
                {
                    sb.Append("[\n");
                    foreach (var item in items)
                    {
                        sb.Append(' ', (depth + 1) * 4);
                        WriteValue(sb, depth + 1, fieldName, item, options);
                        sb.Append('\n');
                    }

                    sb.Append(' ', depth * 4).Append(']');
                }
                else
                {
                    sb.Append('[');
                    for (var k = 0; k < items.Count; k++)
                    {
                        if (k > 0)
                        {
                            sb.Append(", ");
                        }

                        WriteValue(sb, depth, fieldName, items[k], options);
                    }

                    sb.Append(']');
                }

                break;
            }

            case JsonValueKind.Object:
            {
                var fields = value.EnumerateObject().Where(f => f.Value.ValueKind != JsonValueKind.Null).ToList();
                var nulls = value.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.Null).Select(f => f.Name).ToList();
                if (fields.Count == 0)
                {
                    sb.Append(nulls.Count == 0 ? "{}" : $"{{ /* {string.Join(", ", nulls)} = null in the JSON: omitted */ }}");
                    break;
                }

                var inline = fields.Count <= 6 && fields.All(f => f.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) || (f.Value.ValueKind == JsonValueKind.Array && f.Value.GetArrayLength() == 0));
                if (inline)
                {
                    sb.Append("{ ");
                    for (var k = 0; k < fields.Count; k++)
                    {
                        if (k > 0)
                        {
                            sb.Append("  ");
                        }

                        sb.Append(Key(fields[k].Name)).Append(" = ");
                        WriteValue(sb, depth, fields[k].Name, fields[k].Value, options);
                    }

                    if (nulls.Count > 0)
                    {
                        sb.Append("  /* ").Append(string.Join(", ", nulls)).Append(" = null in the JSON: omitted */");
                    }

                    sb.Append(" }");
                }
                else
                {
                    sb.Append("{\n");
                    foreach (var f in value.EnumerateObject())
                    {
                        WriteField(sb, depth + 1, f.Name, f.Value, options);
                    }

                    sb.Append(' ', depth * 4).Append('}');
                }

                break;
            }
        }
    }

    /// <summary>
    /// String fields that are not in <see cref="JsonImportOptions.EnumFields"/> but look like enumerations: every value is an
    /// identifier, the field appears at least three times and its values repeat (at most half as many distinct values as
    /// uses, and at most 32). The id field is excluded. Ordered by field name; the caller decides whether to pass them as
    /// <c>--enums</c>, since only the game knows whether `"water"` names a resource or is text.
    /// </summary>
    public static IReadOnlyList<JsonEnumSuggestion> SuggestEnumFields(string json, JsonImportOptions options)
    {
        using var doc = JsonDocument.Parse(json);
        var stats = new Dictionary<string, (SortedSet<string> Values, int Uses, bool AllIdentifiers)>(StringComparer.Ordinal);
        Collect(doc.RootElement, null, stats);
        return stats
            .Where(kv => kv.Key != options.IdField && !options.EnumFields.Contains(kv.Key) && kv.Value.AllIdentifiers
                && kv.Value.Uses >= 3 && kv.Value.Values.Count <= Math.Min(32, kv.Value.Uses / 2))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new JsonEnumSuggestion(kv.Key, kv.Value.Values.ToList(), kv.Value.Uses))
            .ToList();
    }

    private static void Collect(JsonElement value, string? field, Dictionary<string, (SortedSet<string> Values, int Uses, bool AllIdentifiers)> stats)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject())
                {
                    Collect(p.Value, p.Name, stats);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Collect(item, field, stats);
                }

                break;
            case JsonValueKind.String when field is not null:
            {
                var s = value.GetString()!;
                var entry = stats.TryGetValue(field, out var e) ? e : (Values: new SortedSet<string>(StringComparer.Ordinal), Uses: 0, AllIdentifiers: true);
                entry.Values.Add(s);
                stats[field] = (entry.Values, entry.Uses + 1, entry.AllIdentifiers && Identifier.IsMatch(s));
                break;
            }
        }
    }

    /// <summary>Makes a JSON key or id usable as a KEDO identifier: lowercase, `-` and spaces to `_`, dots kept.</summary>
    public static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            sb.Append(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_' || ch == '.' ? ch : '_');
        }

        var result = sb.ToString();
        if (result.Length == 0 || char.IsAsciiDigit(result[0]))
        {
            result = "_" + result;
        }

        return result;
    }
}
