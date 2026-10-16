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

/// <summary>
/// Generic JSON → KEDO importer for migrating existing data-driven content (anexo C, Patriark Plan 02). It produces one
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
            return;
        }

        var fieldName = Identifier.IsMatch(name) ? name : Sanitize(name);
        sb.Append(' ', depth * 4).Append(fieldName).Append(" = ");
        WriteValue(sb, depth, fieldName, value, options);
        sb.Append('\n');
    }

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
                    // KEDO has no floats: keep two decimals as hundredths and flag it.
                    sb.Append(((long)Math.Round(value.GetDouble() * 100)).ToString(CultureInfo.InvariantCulture)).Append(" /* TODO: was ").Append(value.GetRawText()).Append(", stored as hundredths */");
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
                if (fields.Count == 0)
                {
                    sb.Append("{}");
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

                        var fname = Identifier.IsMatch(fields[k].Name) ? fields[k].Name : Sanitize(fields[k].Name);
                        sb.Append(fname).Append(" = ");
                        WriteValue(sb, depth, fname, fields[k].Value, options);
                    }

                    sb.Append(" }");
                }
                else
                {
                    sb.Append("{\n");
                    foreach (var f in fields)
                    {
                        WriteField(sb, depth + 1, f.Name, f.Value, options);
                    }

                    sb.Append(' ', depth * 4).Append('}');
                }

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
