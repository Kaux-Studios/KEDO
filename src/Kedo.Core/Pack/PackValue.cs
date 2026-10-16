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

/// <summary>An object in a pack.</summary>
public sealed class PackObject
{
    internal PackObject(string type, string id, string hash, PackRecord body, IReadOnlyDictionary<string, PackRecord> components, IReadOnlyList<string> hooks, int programIndex)
    {
        Type = type;
        Id = id;
        Hash = hash;
        Body = body;
        Components = components;
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

    /// <summary>Resolved body.</summary>
    public PackRecord Body { get; }

    /// <summary>Components by type.</summary>
    public IReadOnlyDictionary<string, PackRecord> Components { get; }

    /// <summary>Hook ids this object subscribes to.</summary>
    public IReadOnlyList<string> Hooks { get; }

    /// <summary>Index of the object's behaviour program in the pack, or -1.</summary>
    public int ProgramIndex { get; }
}
