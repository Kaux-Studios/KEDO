namespace Kedo.Schema;

/// <summary>Kinds of field values a schema can declare.</summary>
public enum ValueKind
{
    /// <summary>Any value; only allowed in permissive schemas.</summary>
    Any,
    /// <summary>64-bit integer.</summary>
    Int,
    /// <summary>Boolean.</summary>
    Bool,
    /// <summary>UTF-8 text.</summary>
    Text,
    /// <summary>Duration in the module's base unit.</summary>
    Duration,
    /// <summary>Enumeration value; <see cref="FieldSchema.EnumId"/> names the enum.</summary>
    Enum,
    /// <summary>Reference to an object; <see cref="FieldSchema.TargetType"/> restricts the type (null = any).</summary>
    Reference,
    /// <summary>Reference to an asset; <see cref="FieldSchema.AssetKind"/> restricts the kind (null = any).</summary>
    Asset,
    /// <summary>Nested record; <see cref="FieldSchema.RecordId"/> names its schema.</summary>
    Record,
    /// <summary>List; <see cref="FieldSchema.Element"/> describes items and <see cref="FieldSchema.ListStrategy"/> the merge.</summary>
    List,
    /// <summary>Fixed-point decimal with <see cref="FieldSchema.Scale"/> fractional digits (ADR-0008).</summary>
    Decimal,
    /// <summary>Record with free keys; <see cref="FieldSchema.Element"/> describes values, <see cref="FieldSchema.KeysEnumId"/> may restrict keys (ADR-0009).</summary>
    Map,
}

/// <summary>How a list field merges across layers (KEDO-02 §3, ADR-0006).</summary>
public enum ListStrategy
{
    /// <summary>Replaced whole by a later layer (default).</summary>
    Atomic,
    /// <summary>Unique scalars; later layers add missing values; <c>-=</c> removes by value.</summary>
    Set,
    /// <summary>Records with a unique key; merged by key; <c>-=</c> removes by key.</summary>
    Keyed,
    /// <summary>Later layers concatenate after inherited items.</summary>
    Append,
}

/// <summary>Schema of one field of a type, component or record.</summary>
public sealed class FieldSchema
{
    /// <summary>Creates a field schema.</summary>
    public FieldSchema(string name, ValueKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>Field name as written in `.kedo` files.</summary>
    public string Name { get; }

    /// <summary>Value kind.</summary>
    public ValueKind Kind { get; }

    /// <summary>A concrete object must have this field (checked after resolution).</summary>
    public bool Required { get; init; }

    /// <summary>Documentation shown by editors and `kedo schema`.</summary>
    public string? Doc { get; init; }

    /// <summary>Inclusive minimum for <see cref="ValueKind.Int"/> and <see cref="ValueKind.Duration"/>.</summary>
    public long? Min { get; init; }

    /// <summary>Inclusive maximum for <see cref="ValueKind.Int"/> and <see cref="ValueKind.Duration"/>.</summary>
    public long? Max { get; init; }

    /// <summary>Default integer/duration value (schema layer).</summary>
    public long? DefaultInt { get; init; }

    /// <summary>Default boolean value (schema layer).</summary>
    public bool? DefaultBool { get; init; }

    /// <summary>Default text or enum value (schema layer).</summary>
    public string? DefaultText { get; init; }

    /// <summary>
    /// <c>default = []</c> on a list or <c>default = { }</c> on a map: the field starts empty, so content may omit it
    /// and a required field is still present.
    /// </summary>
    public bool DefaultEmpty { get; init; }

    /// <summary>Enum id for <see cref="ValueKind.Enum"/>.</summary>
    public string? EnumId { get; init; }

    /// <summary>Target type for <see cref="ValueKind.Reference"/>; null accepts any type.</summary>
    public string? TargetType { get; init; }

    /// <summary>Asset kind for <see cref="ValueKind.Asset"/>; null accepts any kind.</summary>
    public string? AssetKind { get; init; }

    /// <summary>Record schema id for <see cref="ValueKind.Record"/>.</summary>
    public string? RecordId { get; init; }

    /// <summary>Element schema for <see cref="ValueKind.List"/>.</summary>
    public FieldSchema? Element { get; init; }

    /// <summary>Merge strategy for <see cref="ValueKind.List"/>.</summary>
    public ListStrategy ListStrategy { get; init; } = ListStrategy.Atomic;

    /// <summary>Key field for <see cref="ListStrategy.Keyed"/> lists of records (default <c>id</c>).</summary>
    public string KeyField { get; init; } = "id";

    /// <summary>Fractional digits of a <see cref="ValueKind.Decimal"/> field (0-9, default 2).</summary>
    public int Scale { get; init; } = 2;

    /// <summary>Enum the keys of a <see cref="ValueKind.Map"/> must belong to; null accepts any identifier or text.</summary>
    public string? KeysEnumId { get; init; }

    /// <summary>Minimum number of items of a list or entries of a map.</summary>
    public int? MinItems { get; init; }

    /// <summary>Maximum number of items of a list or entries of a map.</summary>
    public int? MaxItems { get; init; }

    /// <summary>Minimum length of a text, in Unicode scalar values.</summary>
    public int? MinLength { get; init; }

    /// <summary>Maximum length of a text, in Unicode scalar values.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Allowed values of a text or int field (ints as their decimal text); null accepts any value.</summary>
    public IReadOnlyList<string>? OneOf { get; init; }

    /// <summary>Inclusive minimum of a <see cref="ValueKind.Decimal"/> field written as a decimal (`min = 0.01`); overrides <see cref="Min"/>.</summary>
    public decimal? MinDecimal { get; init; }

    /// <summary>Inclusive maximum of a <see cref="ValueKind.Decimal"/> field written as a decimal; overrides <see cref="Max"/>.</summary>
    public decimal? MaxDecimal { get; init; }
}

/// <summary>A named set of fields: the body of a type, a component or a nested record.</summary>
public sealed class RecordSchema
{
    private readonly Dictionary<string, FieldSchema> _fields = new(StringComparer.Ordinal);

    /// <summary>Creates a record schema.</summary>
    public RecordSchema(string id)
    {
        Id = id;
    }

    /// <summary>Schema id (type id, component id or record id).</summary>
    public string Id { get; }

    /// <summary>Unknown fields are errors when closed (default).</summary>
    public bool Closed { get; init; } = true;

    /// <summary>Documentation.</summary>
    public string? Doc { get; init; }

    /// <summary>Declared fields in declaration order.</summary>
    public IReadOnlyCollection<FieldSchema> Fields => _fields.Values;

    /// <summary>Adds a field. Duplicate names throw: schemas are code, not content.</summary>
    public RecordSchema Add(FieldSchema field)
    {
        _fields.Add(field.Name, field);
        return this;
    }

    /// <summary>Looks up a field by name.</summary>
    public FieldSchema? Find(string name) => _fields.TryGetValue(name, out var f) ? f : null;
}

/// <summary>Enumeration declared by the game.</summary>
public sealed class EnumSchema
{
    /// <summary>Creates an enum schema.</summary>
    public EnumSchema(string id, IReadOnlyList<string> values)
    {
        Id = id;
        Values = values;
    }

    /// <summary>Enum id.</summary>
    public string Id { get; }

    /// <summary>Allowed values.</summary>
    public IReadOnlyList<string> Values { get; }
}

/// <summary>Schema of an object type: its fields, the components it may carry and the hooks it may declare.</summary>
public sealed class TypeSchema
{
    /// <summary>Creates a type schema.</summary>
    public TypeSchema(string id, RecordSchema body)
    {
        Id = id;
        Body = body;
    }

    /// <summary>Type id as written before the object name (<c>actor</c>, <c>event</c>).</summary>
    public string Id { get; }

    /// <summary>Fields of the object body.</summary>
    public RecordSchema Body { get; }

    /// <summary>Component ids this type may carry; null allows any registered component.</summary>
    public IReadOnlySet<string>? AllowedComponents { get; init; }

    /// <summary>Hook ids this type may declare; null allows any registered hook.</summary>
    public IReadOnlySet<string>? AllowedHooks { get; init; }

    /// <summary>Documentation.</summary>
    public string? Doc { get; init; }
}

/// <summary>Descriptor of a hook the game exposes to content.</summary>
public sealed class HookSchema
{
    /// <summary>Creates a hook schema.</summary>
    public HookSchema(string id, RecordSchema context)
    {
        Id = id;
        Context = context;
    }

    /// <summary>Hook id as written after <c>on</c>.</summary>
    public string Id { get; }

    /// <summary>Fields available on the context parameter.</summary>
    public RecordSchema Context { get; }

    /// <summary>The hook may suspend (<c>wait</c>).</summary>
    public bool AllowsAwait { get; init; } = true;

    /// <summary>The hook may apply effects.</summary>
    public bool AllowsEffects { get; init; } = true;

    /// <summary>Documentation.</summary>
    public string? Doc { get; init; }
}

/// <summary>
/// Explicit, reflection-free registry of everything a game exposes to KEDO content (ADR-0007 D9).
/// A permissive registry (no types registered, <see cref="Permissive"/> true) accepts any type and field and infers
/// list strategies from the data; it exists for tooling and tests, never for shipping content.
/// </summary>
public sealed class SchemaRegistry
{
    private readonly Dictionary<string, TypeSchema> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordSchema> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordSchema> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumSchema> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HookSchema> _hooks = new(StringComparer.Ordinal);

    /// <summary>Accept unknown types, components and fields; infer list strategies.</summary>
    public bool Permissive { get; init; }

    /// <summary>Registered types.</summary>
    public IReadOnlyDictionary<string, TypeSchema> Types => _types;

    /// <summary>Registered components.</summary>
    public IReadOnlyDictionary<string, RecordSchema> Components => _components;

    /// <summary>Registered nested record schemas.</summary>
    public IReadOnlyDictionary<string, RecordSchema> Records => _records;

    /// <summary>Registered enums.</summary>
    public IReadOnlyDictionary<string, EnumSchema> Enums => _enums;

    /// <summary>Registered hooks.</summary>
    public IReadOnlyDictionary<string, HookSchema> Hooks => _hooks;

    /// <summary>Registers an object type.</summary>
    public SchemaRegistry AddType(TypeSchema type) { _types.Add(type.Id, type); return this; }

    /// <summary>Registers a component.</summary>
    public SchemaRegistry AddComponent(RecordSchema component) { _components.Add(component.Id, component); return this; }

    /// <summary>Registers a nested record schema.</summary>
    public SchemaRegistry AddRecord(RecordSchema record) { _records.Add(record.Id, record); return this; }

    /// <summary>Registers an enum.</summary>
    public SchemaRegistry AddEnum(EnumSchema e) { _enums.Add(e.Id, e); return this; }

    /// <summary>Registers a hook.</summary>
    public SchemaRegistry AddHook(HookSchema hook) { _hooks.Add(hook.Id, hook); return this; }

    /// <summary>Type by id or null.</summary>
    public TypeSchema? FindType(string id) => _types.TryGetValue(id, out var t) ? t : null;

    /// <summary>Component by id or null.</summary>
    public RecordSchema? FindComponent(string id) => _components.TryGetValue(id, out var c) ? c : null;

    /// <summary>Record by id or null.</summary>
    public RecordSchema? FindRecord(string id) => _records.TryGetValue(id, out var r) ? r : null;

    /// <summary>Enum by id or null.</summary>
    public EnumSchema? FindEnum(string id) => _enums.TryGetValue(id, out var e) ? e : null;

    /// <summary>Hook by id or null.</summary>
    public HookSchema? FindHook(string id) => _hooks.TryGetValue(id, out var h) ? h : null;
}

/// <summary>A module the game ships: registers its schema into a registry. Called explicitly; no reflection.</summary>
public interface IKedoModule
{
    /// <summary>Module id (the <c>module</c> name in `.kedo` files).</summary>
    string Id { get; }

    /// <summary>Registers types, components, records, enums and hooks.</summary>
    void Register(SchemaRegistry registry);
}
