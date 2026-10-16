using Kedo.Schema;

namespace Kedo.Compiler.Schema;

/// <summary>
/// The schema of schemas. A game may describe its content types in KEDO itself using the built-in types
/// <c>kedo_type</c>, <c>kedo_component</c>, <c>kedo_record</c>, <c>kedo_enum</c> and <c>kedo_hook</c>; those files are
/// resolved against this registry and turned into a <see cref="SchemaRegistry"/> by <see cref="SchemaLoader"/>.
/// Registering the same schema from C# through <see cref="IKedoModule"/> stays possible; both produce the same registry.
/// </summary>
public static class MetaSchema
{
    /// <summary>Value kinds accepted in <c>kind = ...</c>.</summary>
    public static readonly string[] Kinds = { "any", "int", "bool", "text", "duration", "enum", "reference", "asset", "record", "list" };

    /// <summary>List strategies accepted in <c>strategy = ...</c>.</summary>
    public static readonly string[] Strategies = { "atomic", "set", "keyed", "append" };

    /// <summary>Builds the meta registry.</summary>
    public static SchemaRegistry Build()
    {
        var r = new SchemaRegistry();
        r.AddEnum(new EnumSchema("kedo_kind", Kinds));
        r.AddEnum(new EnumSchema("kedo_strategy", Strategies));

        // A field descriptor. `element` is itself a field descriptor (for lists), so the record is recursive by name.
        var field = new RecordSchema("kedo_field") { Doc = "Descriptor of one field." }
            .Add(new FieldSchema("name", ValueKind.Enum) { Doc = "Field name as written in content files." })
            .Add(new FieldSchema("kind", ValueKind.Enum) { EnumId = "kedo_kind", Required = true })
            .Add(new FieldSchema("required", ValueKind.Bool) { DefaultBool = false })
            .Add(new FieldSchema("doc", ValueKind.Text))
            .Add(new FieldSchema("min", ValueKind.Int))
            .Add(new FieldSchema("max", ValueKind.Int))
            .Add(new FieldSchema("default", ValueKind.Any) { Doc = "Default value (int, bool, text or enum value)." })
            .Add(new FieldSchema("enum", ValueKind.Enum) { Doc = "Enum id for kind = enum." })
            .Add(new FieldSchema("target", ValueKind.Enum) { Doc = "Target type for kind = reference." })
            .Add(new FieldSchema("asset_kind", ValueKind.Enum) { Doc = "Asset kind for kind = asset." })
            .Add(new FieldSchema("record", ValueKind.Enum) { Doc = "Record id for kind = record." })
            .Add(new FieldSchema("strategy", ValueKind.Enum) { EnumId = "kedo_strategy", DefaultText = "atomic" })
            .Add(new FieldSchema("key", ValueKind.Enum) { DefaultText = "id", Doc = "Key field for keyed lists." })
            .Add(new FieldSchema("element", ValueKind.Record) { RecordId = "kedo_field", Doc = "Item descriptor for kind = list." });
        r.AddRecord(field);

        var fields = new FieldSchema("fields", ValueKind.List) { ListStrategy = ListStrategy.Keyed, KeyField = "name", Element = new FieldSchema("item", ValueKind.Record) { RecordId = "kedo_field" } };

        r.AddType(new TypeSchema("kedo_type", new RecordSchema("kedo_type") { Doc = "An object type the game exposes to content." }
            .Add(fields)
            .Add(new FieldSchema("doc", ValueKind.Text))
            .Add(new FieldSchema("closed", ValueKind.Bool) { DefaultBool = true })
            .Add(new FieldSchema("components", ValueKind.List) { ListStrategy = ListStrategy.Set, Element = new FieldSchema("item", ValueKind.Enum), Doc = "Allowed components; omit to allow any registered component." })
            .Add(new FieldSchema("hooks", ValueKind.List) { ListStrategy = ListStrategy.Set, Element = new FieldSchema("item", ValueKind.Enum), Doc = "Allowed hooks; omit to allow any registered hook." })));

        r.AddType(new TypeSchema("kedo_component", new RecordSchema("kedo_component") { Doc = "A component objects may carry." }
            .Add(fields)
            .Add(new FieldSchema("doc", ValueKind.Text))
            .Add(new FieldSchema("closed", ValueKind.Bool) { DefaultBool = true })));

        r.AddType(new TypeSchema("kedo_record", new RecordSchema("kedo_record") { Doc = "A nested record used by fields of kind record." }
            .Add(fields)
            .Add(new FieldSchema("doc", ValueKind.Text))
            .Add(new FieldSchema("closed", ValueKind.Bool) { DefaultBool = true })));

        r.AddType(new TypeSchema("kedo_enum", new RecordSchema("kedo_enum") { Doc = "An enumeration." }
            .Add(new FieldSchema("values", ValueKind.List) { ListStrategy = ListStrategy.Set, Required = true, Element = new FieldSchema("item", ValueKind.Enum) })
            .Add(new FieldSchema("doc", ValueKind.Text))));

        r.AddType(new TypeSchema("kedo_hook", new RecordSchema("kedo_hook") { Doc = "A hook content may subscribe to with `on`." }
            .Add(new FieldSchema("context", ValueKind.List) { ListStrategy = ListStrategy.Keyed, KeyField = "name", Element = new FieldSchema("item", ValueKind.Record) { RecordId = "kedo_field" }, Doc = "Fields of the context parameter." })
            .Add(new FieldSchema("allows_await", ValueKind.Bool) { DefaultBool = true })
            .Add(new FieldSchema("allows_effects", ValueKind.Bool) { DefaultBool = true })
            .Add(new FieldSchema("doc", ValueKind.Text))));

        return r;
    }
}
