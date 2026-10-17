namespace Kedo.Pack;

/// <summary>Value tags used in the pack encoding.</summary>
public enum PackTag : byte
{
    /// <summary>No value.</summary>
    None = 0,
    /// <summary>64-bit integer.</summary>
    Int = 1,
    /// <summary>Boolean.</summary>
    Bool = 2,
    /// <summary>Text (string table index).</summary>
    Text = 3,
    /// <summary>Duration in minutes.</summary>
    Duration = 4,
    /// <summary>Enum value (string table index).</summary>
    Enum = 5,
    /// <summary>Reference <c>type:id</c> (string table index).</summary>
    Ref = 6,
    /// <summary>List.</summary>
    List = 7,
    /// <summary>Record.</summary>
    Record = 8,
    /// <summary>Fixed-point decimal: units (i64) and scale (u8). Format 2 (ADR-0008).</summary>
    Decimal = 9,
}

/// <summary>A decoded value from a pack. Immutable; built on demand per object.</summary>
public abstract record PackValue
{
    /// <summary>Tag.</summary>
    public abstract PackTag Tag { get; }
}

/// <summary>Integer or duration (minutes).</summary>
public sealed record PackInt(long Number, bool IsDuration) : PackValue
{
    /// <inheritdoc />
    public override PackTag Tag => IsDuration ? PackTag.Duration : PackTag.Int;
}

/// <summary>Fixed-point decimal: <see cref="Units"/> / 10^<see cref="Scale"/> (ADR-0008).</summary>
public sealed record PackDecimal(long Units, byte Scale) : PackValue
{
    /// <inheritdoc />
    public override PackTag Tag => PackTag.Decimal;
}

/// <summary>Boolean.</summary>
public sealed record PackBool(bool Flag) : PackValue
{
    /// <inheritdoc />
    public override PackTag Tag => PackTag.Bool;
}

/// <summary>Text, enum value or reference, distinguished by tag.</summary>
public sealed record PackSymbol(string Text, PackTag Kind) : PackValue
{
    /// <inheritdoc />
    public override PackTag Tag => Kind;
}

/// <summary>List with its strategy and key field.</summary>
public sealed record PackList(IReadOnlyList<PackValue> Items, byte Strategy, string KeyField) : PackValue
{
    /// <inheritdoc />
    public override PackTag Tag => PackTag.List;

    /// <summary>Item whose key field equals <paramref name="key"/>, or null.</summary>
    public PackRecord? ByKey(string key)
    {
        foreach (var item in Items)
        {
            if (item is PackRecord r && r.Fields.TryGetValue(KeyField, out var k) && k is PackSymbol s && s.Text == key)
            {
                return r;
            }
        }

        return null;
    }
}

/// <summary>Record: ordered fields.</summary>
public sealed record PackRecord(IReadOnlyDictionary<string, PackValue> Fields, IReadOnlyList<string> Order) : PackValue
{
    /// <inheritdoc />
    public override PackTag Tag => PackTag.Record;

    /// <summary>Integer field or default.</summary>
    public long Int(string name, long fallback = 0) => Fields.TryGetValue(name, out var v) && v is PackInt i ? i.Number : fallback;

    /// <summary>Boolean field or default.</summary>
    public bool Bool(string name, bool fallback = false) => Fields.TryGetValue(name, out var v) && v is PackBool b ? b.Flag : fallback;

    /// <summary>Text, enum or reference field as string, or null.</summary>
    public string? Symbol(string name) => Fields.TryGetValue(name, out var v) && v is PackSymbol s ? s.Text : null;

    /// <summary>Nested record or null.</summary>
    public PackRecord? Record(string name) => Fields.TryGetValue(name, out var v) ? v as PackRecord : null;

    /// <summary>List or null.</summary>
    public PackList? List(string name) => Fields.TryGetValue(name, out var v) ? v as PackList : null;
}

/// <summary>Bytes and string table of a loaded pack, shared by its objects to decode on demand.</summary>
internal sealed class PackSource
{
    public PackSource(byte[] bytes, string[] strings)
    {
        Bytes = bytes;
        Strings = strings;
    }

    public byte[] Bytes { get; }

    public string[] Strings { get; }
}

/// <summary>An object in a pack. Its body and components are decoded on first access and then kept.</summary>
public sealed class PackObject
{
    private readonly PackSource _source;
    private readonly int _bodyOffset;
    private readonly int _componentsOffset;
    private PackRecord? _body;
    private IReadOnlyDictionary<string, PackRecord>? _components;

    internal PackObject(string type, string id, string hash, PackSource source, int bodyOffset, int componentsOffset, IReadOnlyList<string> hooks, int programIndex)
    {
        Type = type;
        Id = id;
        Hash = hash;
        _source = source;
        _bodyOffset = bodyOffset;
        _componentsOffset = componentsOffset;
        Hooks = hooks;
        ProgramIndex = programIndex;
    }

    /// <summary>Type id.</summary>
    public string Type { get; }

    /// <summary>Object id.</summary>
    public string Id { get; }

    /// <summary><c>type:id</c>.</summary>
    public string Key => $"{Type}:{Id}";

    /// <summary>Content hash (hex).</summary>
    public string Hash { get; }

    /// <summary>Resolved body (decoded on first access; values are immutable, so a concurrent first access is harmless).</summary>
    public PackRecord Body => _body ??= KedoPack.ReadRecordAt(_source.Bytes, _source.Strings, _bodyOffset);

    /// <summary>Components by type (decoded on first access).</summary>
    public IReadOnlyDictionary<string, PackRecord> Components => _components ??= KedoPack.ReadComponentsAt(_source.Bytes, _source.Strings, _componentsOffset);

    /// <summary>Hook ids this object subscribes to.</summary>
    public IReadOnlyList<string> Hooks { get; }

    /// <summary>Index of the object's behaviour program in the pack, or -1.</summary>
    public int ProgramIndex { get; }
}
