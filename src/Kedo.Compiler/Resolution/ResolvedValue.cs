using Kedo.Compiler.Model;
using Kedo.Schema;

namespace Kedo.Compiler.Resolution;

/// <summary>Which layer produced a resolved value (KEDO-02 §6).</summary>
public enum LayerKind
{
    /// <summary>Schema default.</summary>
    Default,
    /// <summary>Inherited from the parent chain.</summary>
    Parent,
    /// <summary>Contributed by a mixin.</summary>
    Mixin,
    /// <summary>Written in the object itself.</summary>
    Own,
}

/// <summary>Provenance of a resolved value: where it was authored and through which layer it arrived.</summary>
public sealed record Provenance(Origin Origin, LayerKind Layer, string LayerName)
{
    /// <summary>Human-readable form: <c>own (file:[a..b))</c> or <c>mixin elite (...)</c>.</summary>
    public override string ToString() => Layer switch
    {
        LayerKind.Mixin => $"mixin {LayerName} ({Origin})",
        LayerKind.Parent => $"parent {LayerName} ({Origin})",
        LayerKind.Default => "schema default",
        _ => $"own ({Origin})",
    };
}

/// <summary>A fully resolved value. Immutable; children are shared between an object and its descendants when unchanged.</summary>
public abstract record ResolvedValue(Provenance Provenance)
{
    /// <summary>Candidates this value shadowed, most recent first (for `kedo explain`). Empty for containers.</summary>
    public IReadOnlyList<ResolvedValue> Shadowed { get; init; } = Array.Empty<ResolvedValue>();

    /// <summary>Scalar text used for keys and canonical output.</summary>
    public abstract string CanonicalText { get; }

    /// <summary>
    /// Pin of a list item added with <c>+= first|last [..]</c> (ADR-0011). Kept through inheritance and merges so the item
    /// stays at its end of the list; resolution metadata, not content (the hash sees only the resulting order).
    /// </summary>
    public ListAnchor Anchor { get; init; }
}

/// <summary>Integer.</summary>
public sealed record RInt(Provenance Provenance, long Number) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Boolean.</summary>
public sealed record RBool(Provenance Provenance, bool Flag) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => Flag ? "true" : "false";
}

/// <summary>Text.</summary>
public sealed record RText(Provenance Provenance, string Text) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => "\"" + Text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Fixed-point decimal: <see cref="Units"/> / 10^<see cref="Scale"/> (ADR-0008).</summary>
public sealed record RDecimal(Provenance Provenance, long Units, int Scale) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => Format(Units, Scale);

    /// <summary>Decimal text with exactly <paramref name="scale"/> fractional digits (`125`, 2 → `1.25`).</summary>
    public static string Format(long units, int scale)
    {
        if (scale == 0)
        {
            return units.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var negative = units < 0;
        var digits = (negative ? -(decimal)units : units).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        return (negative ? "-" : "") + digits[..^scale] + "." + digits[^scale..];
    }
}

/// <summary>Duration in minutes.</summary>
public sealed record RDuration(Provenance Provenance, long Minutes) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "min";
}

/// <summary>Enumeration value.</summary>
public sealed record REnum(Provenance Provenance, string Name) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => Name;
}

/// <summary>Reference to an object or asset, by id.</summary>
public sealed record RRef(Provenance Provenance, string Type, string? Module, string Id) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => Module is null ? $"{Type}:{Id}" : $"{Type}:{Module}/{Id}";
}

/// <summary>List with the strategy it was created with (schema or inferred).</summary>
public sealed record RList(Provenance Provenance, IReadOnlyList<ResolvedValue> Items, ListStrategy Strategy, string KeyField) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => "[" + string.Join(", ", Items.Select(i => i.CanonicalText)) + "]";

    /// <summary>Key text of an item for keyed lists, or null. Text keys compare without quotes so `options[risk]` matches `id = "risk"` and `id = risk` alike.</summary>
    public static string? KeyOf(ResolvedValue item, string keyField)
        => item is RRecord r && r.Fields.TryGetValue(keyField, out var k) ? KeyText(k) : null;

    /// <summary>Text used for key comparisons.</summary>
    public static string KeyText(ResolvedValue v) => v is RText t ? t.Text : v.CanonicalText;
}

/// <summary>Record: ordered fields.</summary>
public sealed record RRecord(Provenance Provenance, OrderedFields Fields) : ResolvedValue(Provenance)
{
    /// <inheritdoc />
    public override string CanonicalText => "{ " + string.Join("  ", Fields.Select(kv => KeyText(kv.Key) + " = " + kv.Value.CanonicalText)) + " }";

    /// <summary>A field or map key as written: identifiers bare, anything else quoted (ADR-0009).</summary>
    public static string KeyText(string key)
        => key.Length > 0 && (char.IsAsciiLetterLower(key[0]) || key[0] == '_') && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            ? key
            : "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>An empty record.</summary>
    public static RRecord Empty(Provenance p) => new(p, OrderedFields.Empty);
}

/// <summary>Immutable insertion-ordered map of field name to value. Updates return a new map sharing unchanged entries.</summary>
public sealed class OrderedFields : IEnumerable<KeyValuePair<string, ResolvedValue>>
{
    private readonly List<KeyValuePair<string, ResolvedValue>> _items;

    private OrderedFields(List<KeyValuePair<string, ResolvedValue>> items)
    {
        _items = items;
    }

    /// <summary>The empty map.</summary>
    public static OrderedFields Empty { get; } = new(new List<KeyValuePair<string, ResolvedValue>>());

    /// <summary>Number of fields.</summary>
    public int Count => _items.Count;

    /// <summary>Field names in order.</summary>
    public IEnumerable<string> Keys => _items.Select(i => i.Key);

    /// <summary>Looks up a field.</summary>
    public bool TryGetValue(string name, out ResolvedValue value)
    {
        foreach (var kv in _items)
        {
            if (kv.Key == name)
            {
                value = kv.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <summary>Returns a copy with the field set (kept in place if it exists, appended otherwise).</summary>
    public OrderedFields With(string name, ResolvedValue value)
    {
        var copy = new List<KeyValuePair<string, ResolvedValue>>(_items.Count + 1);
        var replaced = false;
        foreach (var kv in _items)
        {
            if (kv.Key == name)
            {
                copy.Add(new KeyValuePair<string, ResolvedValue>(name, value));
                replaced = true;
            }
            else
            {
                copy.Add(kv);
            }
        }

        if (!replaced)
        {
            copy.Add(new KeyValuePair<string, ResolvedValue>(name, value));
        }

        return new OrderedFields(copy);
    }

    /// <summary>Returns a copy without the field.</summary>
    public OrderedFields Without(string name) => new(_items.Where(kv => kv.Key != name).ToList());

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, ResolvedValue>> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A resolved, validated object.</summary>
public sealed class ResolvedObject
{
    internal ResolvedObject(ObjectDeclaration declaration, RRecord body, OrderedFields components, IReadOnlyList<HookEntry> hooks, IReadOnlyList<FunctionEntry> functions, IReadOnlyList<string> chain, IReadOnlyList<string> mixins)
    {
        Declaration = declaration;
        Body = body;
        Components = components;
        Hooks = hooks;
        Functions = functions;
        Chain = chain;
        Mixins = mixins;
    }

    /// <summary>Authored declaration.</summary>
    public ObjectDeclaration Declaration { get; }

    /// <summary>Type id.</summary>
    public string Type => Declaration.Type;

    /// <summary>Object id.</summary>
    public string Id => Declaration.Id;

    /// <summary><c>type:id</c>.</summary>
    public string Key => Declaration.Key;

    /// <summary>True for abstract objects (not part of the catalog).</summary>
    public bool IsAbstract => Declaration.IsAbstract;

    /// <summary>Resolved body fields.</summary>
    public RRecord Body { get; }

    /// <summary>Resolved components by component type, inherited first.</summary>
    public OrderedFields Components { get; }

    /// <summary>Hooks declared on this object or inherited (own first).</summary>
    public IReadOnlyList<HookEntry> Hooks { get; }

    /// <summary>Functions declared on this object.</summary>
    public IReadOnlyList<FunctionEntry> Functions { get; }

    /// <summary>Inheritance chain from the root ancestor to this object (keys).</summary>
    public IReadOnlyList<string> Chain { get; }

    /// <summary>Flattened mixins applied, in order.</summary>
    public IReadOnlyList<string> Mixins { get; }

    /// <summary>Content hash (hex) of the canonical resolved form; set by the catalog hasher.</summary>
    public string Hash { get; internal set; } = "";

    /// <summary>True when stage-B validation found errors; the object stays resolved for tooling but is not exportable.</summary>
    public bool HasErrors { get; internal set; }

    /// <summary>Position of the declaration in the resolver input: files in the order given, declarations in file order (ADR-0010).</summary>
    public int Order { get; internal set; }
}
