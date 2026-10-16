using System.Text.Json;
using Kedo.Schema;

namespace Kedo.Compiler.Schema;

/// <summary>
/// Emits a registry as a JSON Schema bundle for editors (form generation) and agents: one JSON document with
/// <c>types</c>, <c>components</c>, <c>hooks</c> and shared <c>$defs</c> for records and enums. Field kinds map to JSON
/// Schema types; KEDO-specific facts (list strategy, key field, reference target, asset kind) travel in <c>x-kedo</c>.
/// </summary>
public static class JsonSchemaWriter
{
    /// <summary>Writes the bundle.</summary>
    public static void Write(SchemaRegistry registry, Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
        w.WriteString("$id", "kedo:schema");
        w.WriteNumber("kedo", Kedo.KedoVersion.LanguageMajor);

        w.WriteStartObject("types");
        foreach (var t in registry.Types.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            w.WritePropertyName(t.Id);
            WriteRecord(w, t.Body, t.Doc ?? t.Body.Doc, t.AllowedComponents, t.AllowedHooks);
        }

        w.WriteEndObject();

        w.WriteStartObject("components");
        foreach (var c in registry.Components.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            w.WritePropertyName(c.Id);
            WriteRecord(w, c, c.Doc, null, null);
        }

        w.WriteEndObject();

        w.WriteStartObject("hooks");
        foreach (var h in registry.Hooks.Values.OrderBy(h => h.Id, StringComparer.Ordinal))
        {
            w.WriteStartObject(h.Id);
            if (h.Doc is not null)
            {
                w.WriteString("description", h.Doc);
            }

            w.WriteBoolean("allows_await", h.AllowsAwait);
            w.WriteBoolean("allows_effects", h.AllowsEffects);
            w.WritePropertyName("context");
            WriteRecord(w, h.Context, null, null, null);
            w.WriteEndObject();
        }

        w.WriteEndObject();

        w.WriteStartObject("$defs");
        foreach (var r in registry.Records.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            w.WritePropertyName("record:" + r.Id);
            WriteRecord(w, r, r.Doc, null, null);
        }

        foreach (var e in registry.Enums.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            w.WriteStartObject("enum:" + e.Id);
            w.WriteString("type", "string");
            w.WriteStartArray("enum");
            foreach (var v in e.Values)
            {
                w.WriteStringValue(v);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteRecord(Utf8JsonWriter w, RecordSchema record, string? doc, IReadOnlySet<string>? components, IReadOnlySet<string>? hooks)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteString("title", record.Id);
        if (doc is not null)
        {
            w.WriteString("description", doc);
        }

        w.WriteStartObject("properties");
        var required = new List<string>();
        foreach (var f in record.Fields)
        {
            w.WritePropertyName(f.Name);
            WriteField(w, f);
            if (f.Required)
            {
                required.Add(f.Name);
            }
        }

        w.WriteEndObject();
        if (required.Count > 0)
        {
            w.WriteStartArray("required");
            foreach (var r in required)
            {
                w.WriteStringValue(r);
            }

            w.WriteEndArray();
        }

        w.WriteBoolean("additionalProperties", !record.Closed);
        if (components is not null || hooks is not null)
        {
            w.WriteStartObject("x-kedo");
            if (components is not null)
            {
                w.WriteStartArray("components");
                foreach (var c in components.OrderBy(c => c, StringComparer.Ordinal))
                {
                    w.WriteStringValue(c);
                }

                w.WriteEndArray();
            }

            if (hooks is not null)
            {
                w.WriteStartArray("hooks");
                foreach (var h in hooks.OrderBy(h => h, StringComparer.Ordinal))
                {
                    w.WriteStringValue(h);
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteField(Utf8JsonWriter w, FieldSchema f)
    {
        w.WriteStartObject();
        if (f.Doc is not null)
        {
            w.WriteString("description", f.Doc);
        }

        switch (f.Kind)
        {
            case ValueKind.Int:
                w.WriteString("type", "integer");
                if (f.Min is { } min) w.WriteNumber("minimum", min);
                if (f.Max is { } max) w.WriteNumber("maximum", max);
                if (f.DefaultInt is { } di) w.WriteNumber("default", di);
                break;
            case ValueKind.Bool:
                w.WriteString("type", "boolean");
                if (f.DefaultBool is { } db) w.WriteBoolean("default", db);
                break;
            case ValueKind.Text:
                w.WriteString("type", "string");
                if (f.DefaultText is { } dt) w.WriteString("default", dt);
                break;
            case ValueKind.Duration:
                w.WriteString("type", "string");
                w.WriteString("pattern", "^[0-9][0-9_]*(s|min|h|d)$");
                if (f.DefaultInt is { } dd) w.WriteString("default", dd + "min");
                break;
            case ValueKind.Enum:
                if (f.EnumId is not null)
                {
                    w.WriteString("$ref", "#/$defs/enum:" + f.EnumId);
                }
                else
                {
                    w.WriteString("type", "string");
                    w.WriteString("pattern", "^[a-z_][a-z0-9_]*$");
                }

                if (f.DefaultText is { } de) w.WriteString("default", de);
                break;
            case ValueKind.Reference:
                w.WriteString("type", "string");
                w.WriteString("pattern", f.TargetType is null ? "^[a-z_][a-z0-9_]*:[a-z0-9_./]+$" : $"^{f.TargetType}:[a-z0-9_./]+$");
                break;
            case ValueKind.Asset:
                w.WriteString("type", "string");
                w.WriteString("pattern", "^asset:[a-z0-9_./]+$");
                break;
            case ValueKind.Record:
                w.WriteString("$ref", "#/$defs/record:" + f.RecordId);
                break;
            case ValueKind.List:
                w.WriteString("type", "array");
                if (f.Element is not null)
                {
                    w.WritePropertyName("items");
                    WriteField(w, f.Element);
                }

                break;
            default:
                break;
        }

        w.WriteStartObject("x-kedo");
        w.WriteString("kind", f.Kind.ToString().ToLowerInvariant());
        if (f.Kind == ValueKind.List)
        {
            w.WriteString("strategy", f.ListStrategy.ToString().ToLowerInvariant());
            if (f.ListStrategy == ListStrategy.Keyed)
            {
                w.WriteString("key", f.KeyField);
            }
        }

        if (f.TargetType is not null) w.WriteString("target", f.TargetType);
        if (f.AssetKind is not null) w.WriteString("asset_kind", f.AssetKind);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
