using System.Text.Json;
using Kedo.Compiler.Resolution;
using Kedo.Schema;

namespace Kedo.Compiler.Editing;

/// <summary>
/// What a form needs to edit one object (#14): its header and every field of its type's schema, in schema order, with
/// the resolved value as a KEDO literal and the layer it came from (`own`, `parent`, `mixin`, `default`, `variant` or
/// `absent`). Records nest their fields; lists and maps travel as literals. A list of records also lists its `items`,
/// each with its fields (and, in a keyed list, its `key`), so a form can edit one item through a `name[key].field` path.
/// Written as JSON by `kedo describe`.
/// </summary>
public static class ObjectDescriber
{
    /// <summary>Writes the description of <paramref name="obj"/>.</summary>
    public static void Write(Utf8JsonWriter w, ResolvedObject obj, SchemaRegistry schema)
    {
        w.WriteStartObject();
        w.WriteString("key", obj.Key);
        w.WriteString("type", obj.Type);
        w.WriteString("id", obj.Id);
        w.WriteBoolean("abstract", obj.IsAbstract);
        w.WriteString("file", obj.Declaration.Origin.File);
        w.WriteNumber("line", obj.Declaration.Origin.Line);
        if (obj.Declaration.Parent is { } parent)
        {
            w.WriteString("parent", parent);
        }

        w.WriteStartArray("mixins");
        foreach (var m in obj.Mixins)
        {
            w.WriteStringValue(m);
        }

        w.WriteEndArray();
        if (obj.Declaration.Doc is { } doc)
        {
            w.WriteString("doc", doc);
        }

        if (schema.FindType(obj.Type) is { } type && type.Doc is { } typeDoc)
        {
            w.WriteString("type_doc", typeDoc);
        }

        WriteFields(w, obj.Body, schema.FindType(obj.Type)?.Body, schema, obj.Key);
        w.WriteEndObject();
    }

    private static void WriteFields(Utf8JsonWriter w, RRecord record, RecordSchema? recordSchema, SchemaRegistry schema, string key)
    {
        w.WriteStartArray("fields");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in recordSchema?.Fields ?? Enumerable.Empty<FieldSchema>())
        {
            listed.Add(f.Name);
            record.Fields.TryGetValue(f.Name, out var value);
            WriteField(w, f.Name, f, value, schema, key);
        }

        foreach (var kv in record.Fields)
        {
            if (!listed.Contains(kv.Key))
            {
                WriteField(w, kv.Key, null, kv.Value, schema, key);
            }
        }

        w.WriteEndArray();
    }

    private static void WriteField(Utf8JsonWriter w, string name, FieldSchema? f, ResolvedValue? value, SchemaRegistry schema, string key)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        var kind = f?.Kind ?? Infer(value);
        w.WriteString("kind", kind.ToString().ToLowerInvariant());
        w.WriteBoolean("required", f?.Required ?? false);
        if (f?.Doc is { } doc)
        {
            w.WriteString("doc", doc);
        }

        if (f is { Kind: ValueKind.Enum, EnumId: { } enumId } && schema.FindEnum(enumId) is { } e)
        {
            w.WriteStartArray("enum");
            foreach (var v in e.Values)
            {
                w.WriteStringValue(v);
            }

            w.WriteEndArray();
        }

        if (f?.OneOf is { } oneOf)
        {
            w.WriteStartArray("one_of");
            foreach (var v in oneOf)
            {
                w.WriteStringValue(v);
            }

            w.WriteEndArray();
        }

        if (f?.TargetType is { } target)
        {
            w.WriteString("target", target);
        }

        if (f?.AssetKind is { } assetKind)
        {
            w.WriteString("asset_kind", assetKind);
        }

        if (f is { Kind: ValueKind.List })
        {
            w.WriteString("strategy", f.ListStrategy.ToString().ToLowerInvariant());
        }

        if (f?.Min is { } min)
        {
            w.WriteNumber("min", min);
        }

        if (f?.Max is { } max)
        {
            w.WriteNumber("max", max);
        }

        if (value is null)
        {
            w.WriteNull("value");
            w.WriteString("layer", "absent");
        }
        else
        {
            w.WriteString("value", value.CanonicalText);
            var p = value.Provenance;
            var layer = p.Layer switch
            {
                LayerKind.Default => "default",
                LayerKind.Mixin => "mixin",
                LayerKind.Parent => "parent",
                LayerKind.Variant => "variant",
                _ => p.LayerName.Length > 0 && p.LayerName != key ? "parent" : "own",
            };
            w.WriteString("layer", layer);
            if (p.Layer != LayerKind.Default)
            {
                w.WriteString("from", p.LayerName.Length > 0 ? p.LayerName : key);
                w.WriteString("origin", p.Origin.ToString());
            }

            if (value is RRecord nested && f is { Kind: ValueKind.Record, RecordId: { } rid })
            {
                WriteFields(w, nested, schema.FindRecord(rid), schema, key);
            }

            if (value is RList list && f is { Kind: ValueKind.List, Element: { Kind: ValueKind.Record, RecordId: { } eid } })
            {
                var keyed = list.Strategy == ListStrategy.Keyed;
                if (keyed)
                {
                    w.WriteString("key_field", list.KeyField);
                }

                w.WriteStartArray("items");
                foreach (var item in list.Items.OfType<RRecord>())
                {
                    w.WriteStartObject();
                    if (keyed && RList.KeyOf(item, list.KeyField) is { } itemKey)
                    {
                        w.WriteString("key", itemKey);
                    }

                    WriteFields(w, item, schema.FindRecord(eid), schema, key);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }
        }

        w.WriteEndObject();
    }

    private static ValueKind Infer(ResolvedValue? value) => value switch
    {
        RInt => ValueKind.Int,
        RBool => ValueKind.Bool,
        RText => ValueKind.Text,
        RDecimal => ValueKind.Decimal,
        RDuration => ValueKind.Duration,
        REnum => ValueKind.Enum,
        RRef r => r.Type == "asset" ? ValueKind.Asset : ValueKind.Reference,
        RList => ValueKind.List,
        RRecord => ValueKind.Record,
        _ => ValueKind.Any,
    };
}
