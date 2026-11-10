using System.Globalization;
using System.Text;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;
using Kedo.Text;

namespace Kedo.Compiler.Schema;

// Schema expressions (ADR-0016): the `check` of an invariant and the `computed` of a field, written without quotes in
// parentheses. They are compiled once per registry to a typed tree (K2013 for what does not type) and evaluated over
// resolved values: exact 64-bit integers, fixed-point decimals, durations in milliseconds, booleans, texts, enums and
// references. The evaluator walks the tree with struct values and allocates nothing per list item.

/// <summary>Value classes of the schema expression language.</summary>
internal enum SKind : byte
{
    Absent,
    Int,
    Decimal,
    Duration,
    Bool,
    Text,
    Enum,
    Ref,
    Record,
    List,
    Any,
}

/// <summary>Static type of a schema expression.</summary>
internal sealed class SType
{
    public static readonly SType Any = new(SKind.Any);
    public static readonly SType Int = new(SKind.Int);
    public static readonly SType Bool = new(SKind.Bool);
    public static readonly SType Text = new(SKind.Text);
    public static readonly SType Duration = new(SKind.Duration);

    public SType(SKind kind) => Kind = kind;

    public SKind Kind { get; }

    public int Scale { get; init; }

    public string? EnumId { get; init; }

    /// <summary>Target type of a reference; null for any type.</summary>
    public string? Target { get; init; }

    /// <summary>Schema of a record; null for a record of unknown shape.</summary>
    public RecordSchema? Record { get; init; }

    /// <summary>Element descriptor of a list or of the values of a map.</summary>
    public FieldSchema? Element { get; init; }

    public bool IsMap { get; init; }

    public bool IsNumeric => Kind is SKind.Int or SKind.Decimal;

    public static SType Decimal(int scale) => new(SKind.Decimal) { Scale = scale };

    public static SType Of(FieldSchema? f, SchemaRegistry registry) => f is null ? Any : f.Kind switch
    {
        ValueKind.Int => Int,
        ValueKind.Bool => Bool,
        ValueKind.Text => Text,
        ValueKind.Duration => Duration,
        ValueKind.Decimal => Decimal(f.Scale),
        ValueKind.Enum => new SType(SKind.Enum) { EnumId = f.EnumId },
        ValueKind.Reference => new SType(SKind.Ref) { Target = f.TargetType },
        ValueKind.Asset => new SType(SKind.Ref) { Target = "asset" },
        ValueKind.Record => new SType(SKind.Record) { Record = f.RecordId is { } rid ? registry.FindRecord(rid) : null },
        ValueKind.Map => new SType(SKind.Record) { IsMap = true, Element = f.Element },
        ValueKind.List => new SType(SKind.List) { Element = f.Element },
        _ => Any,
    };

    public string Describe() => Kind switch
    {
        SKind.Int => "an integer",
        SKind.Decimal => "a decimal",
        SKind.Duration => "a duration",
        SKind.Bool => "a boolean",
        SKind.Text => "a text",
        SKind.Enum => EnumId is null ? "an enum value" : $"a `{EnumId}`",
        SKind.Ref => Target is null ? "a reference" : $"a reference to `{Target}`",
        SKind.Record => IsMap ? "a map" : Record is null ? "a record" : $"a `{Record.Id}` record",
        SKind.List => "a list",
        _ => "a value",
    };
}

/// <summary>Runtime value. Int: N; Decimal: N units at Scale; Duration: N milliseconds; Bool: N 0 or 1; Text and Enum: O string; Ref: O <see cref="RRef"/>; Record: O <see cref="RRecord"/>; List: O <see cref="RList"/>.</summary>
internal readonly struct SValue
{
    public SValue(SKind kind, long n, int scale, object? o)
    {
        Kind = kind;
        N = n;
        Scale = scale;
        O = o;
    }

    public SKind Kind { get; }

    public long N { get; }

    public int Scale { get; }

    public object? O { get; }

    public bool Flag => N != 0;

    public static SValue OfInt(long n) => new(SKind.Int, n, 0, null);

    public static SValue OfBool(bool b) => new(SKind.Bool, b ? 1 : 0, 0, null);

    public static SValue From(ResolvedValue v) => v switch
    {
        RInt i => OfInt(i.Number),
        RDecimal d => new SValue(SKind.Decimal, d.Units, d.Scale, null),
        RDuration du => new SValue(SKind.Duration, checked(du.Amount * DurationUnits.MillisecondsPer(du.Unit)), 0, null),
        RBool b => OfBool(b.Flag),
        RText t => new SValue(SKind.Text, 0, 0, t.Text),
        REnum e => new SValue(SKind.Enum, 0, 0, e.Name),
        RRef r => new SValue(SKind.Ref, 0, 0, r),
        RRecord rec => new SValue(SKind.Record, 0, 0, rec),
        RList l => new SValue(SKind.List, 0, 0, l),
        _ => new SValue(SKind.Any, 0, 0, v),
    };

    public string Format() => Kind switch
    {
        SKind.Int => N.ToString(CultureInfo.InvariantCulture),
        SKind.Decimal => RDecimal.Format(N, Scale),
        SKind.Duration => DurationLowering.FormatMs(N),
        SKind.Bool => Flag ? "true" : "false",
        SKind.Text or SKind.Enum => (string)O!,
        SKind.Ref => ((RRef)O!).CanonicalText,
        SKind.Record => ((RRecord)O!).CanonicalText,
        SKind.List => ((RList)O!).CanonicalText,
        SKind.Absent => "(not set)",
        _ => O?.ToString() ?? "",
    };
}

/// <summary>Evaluation state, reused across objects and rules of one catalog.</summary>
internal sealed class SchemaEvalContext
{
    private Dictionary<string, List<ResolvedObject>>? _byType;

    public SchemaEvalContext(ResolvedCatalog catalog) => Catalog = catalog;

    public ResolvedCatalog Catalog { get; }

    /// <summary>The object the rule is anchored on.</summary>
    public ResolvedObject? Object { get; set; }

    /// <summary><c>root</c>: the body of the anchor object.</summary>
    public RRecord? Root { get; set; }

    /// <summary>The record bare names read from.</summary>
    public RRecord? Scope { get; set; }

    /// <summary><c>it</c> inside an aggregate.</summary>
    public SValue It { get; set; }

    /// <summary><c>index</c> inside an aggregate (0-based).</summary>
    public long Index { get; set; }

    /// <summary>Nesting of aggregates; reads are recorded only outside them.</summary>
    public int Depth { get; set; }

    /// <summary>Inside <c>has(…)</c>: an absent field gives <see cref="SKind.Absent"/> instead of failing.</summary>
    public int Probing { get; set; }

    public bool Failed { get; private set; }

    /// <summary>Why the evaluation failed; null when another diagnostic already says it (no cascade, ADR-0010).</summary>
    public string? Error { get; private set; }

    /// <summary>Keys this evaluation read through references, and <c>*type</c> for <c>forall</c> (incremental dependencies).</summary>
    public HashSet<string>? References { get; set; }

    /// <summary>Called before reading an object through a reference or <c>forall</c>: computes its fields; false on a cycle.</summary>
    public Func<ResolvedObject, bool>? Prepare { get; set; }

    /// <summary>Fields of the anchor read by the rule, for the labels of K4008 (explain pass only).</summary>
    public List<(string Name, ResolvedValue Value)>? Reads { get; set; }

    public void Reset(ResolvedObject obj, RRecord root, RRecord scope)
    {
        Object = obj;
        Root = root;
        Scope = scope;
        It = new SValue(SKind.Record, 0, 0, scope);
        Index = 0;
        Depth = 0;
        Probing = 0;
        Failed = false;
        Error = null;
        Reads = null;
    }

    public void Fail(string? message)
    {
        if (!Failed)
        {
            Failed = true;
            Error = message;
        }
    }

    /// <summary>Concrete objects of a type in catalog order.</summary>
    public List<ResolvedObject> OfType(string type)
    {
        if (_byType is null)
        {
            _byType = new Dictionary<string, List<ResolvedObject>>(StringComparer.Ordinal);
            foreach (var o in Catalog.Objects)
            {
                if (!o.IsAbstract)
                {
                    if (!_byType.TryGetValue(o.Type, out var list))
                    {
                        _byType[o.Type] = list = new List<ResolvedObject>();
                    }

                    list.Add(o);
                }
            }
        }

        return _byType.TryGetValue(type, out var found) ? found : new List<ResolvedObject>();
    }

    /// <summary>Evaluates a node; overflow becomes a failure, never an exception for the caller.</summary>
    public SValue Run(SNode node)
    {
        try
        {
            return node.Eval(this);
        }
        catch (OverflowException)
        {
            Fail("the arithmetic overflows 64 bits");
            return default;
        }
    }
}

// ----- nodes -----

internal abstract class SNode
{
    protected SNode(SType type) => Type = type;

    public SType Type { get; }

    public abstract SValue Eval(SchemaEvalContext c);
}

internal sealed class ConstNode : SNode
{
    private readonly SValue _value;

    public ConstNode(SType type, SValue value) : base(type) => _value = value;

    public override SValue Eval(SchemaEvalContext c) => _value;
}

/// <summary>A bare name that is not a field: an enum value once the other side of <c>==</c> says which enum.</summary>
internal sealed class EnumLiteralNode : SNode
{
    public EnumLiteralNode(string name, int node) : base(SType.Any)
    {
        Name = name;
        Node = node;
    }

    public string Name { get; }

    public int Node { get; }

    public override SValue Eval(SchemaEvalContext c) => new(SKind.Enum, 0, 0, Name);
}

internal sealed class ItNode : SNode
{
    public ItNode(SType type) : base(type)
    {
    }

    public override SValue Eval(SchemaEvalContext c) => c.It;
}

internal sealed class IndexNode : SNode
{
    public IndexNode() : base(SType.Int)
    {
    }

    public override SValue Eval(SchemaEvalContext c) => SValue.OfInt(c.Index);
}

internal sealed class RootNode : SNode
{
    public RootNode(SType type) : base(type)
    {
    }

    public override SValue Eval(SchemaEvalContext c) => c.Root is null ? default : new SValue(SKind.Record, 0, 0, c.Root);
}

/// <summary>A field of the scope (no target) or of a record or referenced object.</summary>
internal sealed class FieldNode : SNode
{
    private readonly SNode? _target;
    private readonly string _name;
    private readonly string _text;
    private readonly bool _required;
    private readonly bool _constantTarget;

    public FieldNode(SType type, SNode? target, string name, string text, bool required) : base(type)
    {
        _target = target;
        _name = name;
        _text = text;
        _required = required;
        _constantTarget = target is ConstNode;
    }

    public override SValue Eval(SchemaEvalContext c)
    {
        RRecord? record;
        if (_target is null)
        {
            record = c.Scope;
        }
        else
        {
            var t = _target.Eval(c);
            if (c.Failed || t.Kind == SKind.Absent)
            {
                return default;
            }

            if (t.Kind == SKind.Record)
            {
                record = (RRecord)t.O!;
            }
            else if (t.Kind == SKind.Ref)
            {
                record = Deref((RRef)t.O!, c);
                if (record is null)
                {
                    return default;
                }
            }
            else
            {
                c.Fail($"`{_text}`: {Describe(t.Kind)} has no fields");
                return default;
            }
        }

        if (record is null || !record.Fields.TryGetValue(_name, out var v))
        {
            if (c.Probing == 0)
            {
                // A missing required field is K4002 already; say nothing more.
                c.Fail(_required ? null : $"`{_text}` is not set; guard the rule with `has({_text})`");
            }

            return default;
        }

        if (c.Reads is not null && c.Depth == 0 && ReferenceEquals(record, c.Root) && c.Reads.Count < 8 && !c.Reads.Exists(r => r.Name == _name))
        {
            c.Reads.Add((_name, v));
        }

        return SValue.From(v);
    }

    private RRecord? Deref(RRef r, SchemaEvalContext c)
    {
        var key = r.Module is null ? r.CanonicalText : $"{r.Type}:{r.Id}";
        c.References?.Add(key);
        if (!c.Catalog.ByKey.TryGetValue(key, out var target) || target.IsAbstract)
        {
            // A reference written in content that does not resolve is K4005 already; one written in the schema is not.
            c.Fail(_constantTarget ? $"`{key}` does not exist" : null);
            return null;
        }

        if (c.Prepare is not null && !c.Prepare(target))
        {
            c.Fail($"the computed fields of `{key}` depend on themselves");
            return null;
        }

        return target.Body;
    }

    internal static string Describe(SKind kind) => kind switch
    {
        SKind.Int => "an integer",
        SKind.Decimal => "a decimal",
        SKind.Duration => "a duration",
        SKind.Bool => "a boolean",
        SKind.Text => "a text",
        SKind.Enum => "an enum value",
        SKind.Ref => "a reference",
        SKind.Record => "a record",
        SKind.List => "a list",
        _ => "a value",
    };
}

internal sealed class UnaryNode : SNode
{
    private readonly bool _not;
    private readonly SNode _operand;

    public UnaryNode(SType type, bool not, SNode operand) : base(type)
    {
        _not = not;
        _operand = operand;
    }

    public override SValue Eval(SchemaEvalContext c)
    {
        var v = _operand.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        if (_not)
        {
            if (v.Kind != SKind.Bool)
            {
                c.Fail($"`not` needs a boolean, got {FieldNode.Describe(v.Kind)}");
                return default;
            }

            return SValue.OfBool(!v.Flag);
        }

        if (v.Kind is SKind.Int or SKind.Decimal or SKind.Duration)
        {
            return new SValue(v.Kind, checked(-v.N), v.Scale, null);
        }

        c.Fail($"`-` needs a number or a duration, got {FieldNode.Describe(v.Kind)}");
        return default;
    }
}

internal sealed class BinaryNode : SNode
{
    private readonly TokenKind _op;
    private readonly SNode _left;
    private readonly SNode _right;

    public BinaryNode(SType type, TokenKind op, SNode left, SNode right) : base(type)
    {
        _op = op;
        _left = left;
        _right = right;
    }

    public override SValue Eval(SchemaEvalContext c)
    {
        var l = _left.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        if (_op is TokenKind.KwAnd or TokenKind.KwOr)
        {
            if (l.Kind != SKind.Bool)
            {
                c.Fail($"`{Op(_op)}` needs booleans, got {FieldNode.Describe(l.Kind)}");
                return default;
            }

            if (_op == TokenKind.KwAnd ? !l.Flag : l.Flag)
            {
                return l;
            }

            var r0 = _right.Eval(c);
            if (!c.Failed && r0.Kind != SKind.Bool)
            {
                c.Fail($"`{Op(_op)}` needs booleans, got {FieldNode.Describe(r0.Kind)}");
            }

            return c.Failed ? default : r0;
        }

        var r = _right.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        switch (_op)
        {
            case TokenKind.EqualsEquals:
            case TokenKind.BangEquals:
            {
                var eq = Equal(l, r, out var comparable);
                if (!comparable)
                {
                    c.Fail($"`{Op(_op)}` cannot compare {FieldNode.Describe(l.Kind)} with {FieldNode.Describe(r.Kind)}");
                    return default;
                }

                return SValue.OfBool(_op == TokenKind.EqualsEquals ? eq : !eq);
            }

            case TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals:
            {
                if (!TryCompare(l, r, out var cmp))
                {
                    c.Fail($"`{Op(_op)}` cannot order {FieldNode.Describe(l.Kind)} and {FieldNode.Describe(r.Kind)}");
                    return default;
                }

                return SValue.OfBool(_op switch
                {
                    TokenKind.Less => cmp < 0,
                    TokenKind.LessEquals => cmp <= 0,
                    TokenKind.Greater => cmp > 0,
                    _ => cmp >= 0,
                });
            }

            default:
                return Arithmetic(_op, l, r, c);
        }
    }

    internal static string Op(TokenKind op) => op switch
    {
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.EqualsEquals => "==",
        TokenKind.BangEquals => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEquals => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEquals => ">=",
        TokenKind.KwAnd => "and",
        TokenKind.KwOr => "or",
        _ => op.ToString(),
    };

    internal static SValue Arithmetic(TokenKind op, SValue l, SValue r, SchemaEvalContext c)
    {
        switch (l.Kind, r.Kind)
        {
            case (SKind.Int, SKind.Int):
                switch (op)
                {
                    case TokenKind.Plus: return SValue.OfInt(checked(l.N + r.N));
                    case TokenKind.Minus: return SValue.OfInt(checked(l.N - r.N));
                    case TokenKind.Star: return SValue.OfInt(checked(l.N * r.N));
                    case TokenKind.Slash or TokenKind.Percent:
                        if (r.N == 0)
                        {
                            c.Fail("division by zero");
                            return default;
                        }

                        return SValue.OfInt(op == TokenKind.Slash ? checked(l.N / r.N) : l.N % r.N);
                }

                break;
            case (SKind.Decimal or SKind.Int, SKind.Decimal or SKind.Int):
                if (op is TokenKind.Plus or TokenKind.Minus)
                {
                    var scale = Math.Max(l.Scale, r.Scale);
                    var a = Rescale(l.N, l.Scale, scale);
                    var b = Rescale(r.N, r.Scale, scale);
                    return new SValue(SKind.Decimal, op == TokenKind.Plus ? checked(a + b) : checked(a - b), scale, null);
                }

                if (op == TokenKind.Star && (l.Kind == SKind.Int || r.Kind == SKind.Int))
                {
                    return new SValue(SKind.Decimal, checked(l.N * r.N), l.Kind == SKind.Decimal ? l.Scale : r.Scale, null);
                }

                break;
            case (SKind.Duration, SKind.Duration) when op is TokenKind.Plus or TokenKind.Minus:
                return new SValue(SKind.Duration, op == TokenKind.Plus ? checked(l.N + r.N) : checked(l.N - r.N), 0, null);
            case (SKind.Duration, SKind.Int) or (SKind.Int, SKind.Duration) when op == TokenKind.Star:
                return new SValue(SKind.Duration, checked(l.N * r.N), 0, null);
            case (SKind.Text, SKind.Text) when op == TokenKind.Plus:
                return new SValue(SKind.Text, 0, 0, (string)l.O! + (string)r.O!);
        }

        c.Fail($"`{Op(op)}` cannot combine {FieldNode.Describe(l.Kind)} and {FieldNode.Describe(r.Kind)}");
        return default;
    }

    internal static long Rescale(long units, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            units = checked(units * 10);
        }

        return units;
    }

    private static Int128 Wide(long units, int from, int to)
    {
        Int128 v = units;
        for (var i = from; i < to; i++)
        {
            v *= 10;
        }

        return v;
    }

    /// <summary>Orders two numbers (exactly, at the larger scale) or two durations.</summary>
    internal static bool TryCompare(SValue l, SValue r, out int cmp)
    {
        if (l.Kind is SKind.Int or SKind.Decimal && r.Kind is SKind.Int or SKind.Decimal)
        {
            var scale = Math.Max(l.Scale, r.Scale);
            cmp = Wide(l.N, l.Scale, scale).CompareTo(Wide(r.N, r.Scale, scale));
            return true;
        }

        if (l.Kind == SKind.Duration && r.Kind == SKind.Duration)
        {
            cmp = l.N.CompareTo(r.N);
            return true;
        }

        cmp = 0;
        return false;
    }

    internal static bool Equal(SValue l, SValue r, out bool comparable)
    {
        comparable = true;
        if (TryCompare(l, r, out var cmp))
        {
            return cmp == 0;
        }

        switch (l.Kind, r.Kind)
        {
            case (SKind.Bool, SKind.Bool):
                return l.Flag == r.Flag;
            case (SKind.Text, SKind.Text) or (SKind.Enum, SKind.Enum):
                return string.Equals((string)l.O!, (string)r.O!, StringComparison.Ordinal);
            case (SKind.Ref, SKind.Ref):
            {
                var a = (RRef)l.O!;
                var b = (RRef)r.O!;
                return a.Type == b.Type && a.Id == b.Id;
            }

            default:
                comparable = false;
                return false;
        }
    }
}

internal enum AggregateKind
{
    Count,
    Sum,
    All,
    Any,
    Unique,
    Increasing,
    Min,
    Max,
}

/// <summary>
/// <c>count</c>, <c>sum</c>, <c>all</c>, <c>any</c>, <c>unique</c>, <c>increasing</c>, <c>min</c>, <c>max</c> over a list;
/// the body (and the optional filter) see each item as their scope, <c>it</c> and <c>index</c>.
/// </summary>
internal sealed class AggregateNode : SNode
{
    private readonly AggregateKind _kind;
    private readonly SNode _list;
    private readonly SNode? _body;
    private readonly SNode? _where;
    private readonly SValue _zero;

    public AggregateNode(SType type, AggregateKind kind, SNode list, SNode? body, SValue zero, SNode? where = null) : base(type)
    {
        _kind = kind;
        _list = list;
        _body = body;
        _zero = zero;
        _where = where;
    }

    public override SValue Eval(SchemaEvalContext c)
    {
        var lv = _list.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        if (lv.Kind != SKind.List)
        {
            c.Fail($"`{Name(_kind)}` needs a list, got {FieldNode.Describe(lv.Kind)}");
            return default;
        }

        var items = ((RList)lv.O!).Items;
        if (_kind == AggregateKind.Count && _body is null)
        {
            return SValue.OfInt(items.Count);
        }

        var scope = c.Scope;
        var it = c.It;
        var index = c.Index;
        c.Depth++;
        try
        {
            return Fold(items, c);
        }
        finally
        {
            c.Depth--;
            c.Scope = scope;
            c.It = it;
            c.Index = index;
        }
    }

    /// <summary>Sets the scope to item <paramref name="i"/>; false when the filter leaves it out or the evaluation failed.</summary>
    private bool Include(IReadOnlyList<ResolvedValue> items, int i, SchemaEvalContext c, out SValue value)
    {
        var item = items[i];
        c.Scope = item as RRecord;
        c.It = SValue.From(item);
        c.Index = i;
        value = default;
        if (_where is not null)
        {
            var w = _where.Eval(c);
            if (c.Failed || !Bool(w, c))
            {
                return false;
            }
        }

        value = _body is null ? c.It : _body.Eval(c);
        return !c.Failed;
    }

    private SValue Fold(IReadOnlyList<ResolvedValue> items, SchemaEvalContext c)
    {
        switch (_kind)
        {
            case AggregateKind.Count:
            {
                // count(list, condition): the body is the condition.
                var n = 0L;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Include(items, i, c, out var v))
                    {
                        if (c.Failed)
                        {
                            return default;
                        }

                        continue;
                    }

                    var flag = Bool(v, c);
                    if (c.Failed)
                    {
                        return default;
                    }

                    n += flag ? 1 : 0;
                }

                return SValue.OfInt(n);
            }

            case AggregateKind.Sum:
            {
                var total = _zero;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Include(items, i, c, out var v))
                    {
                        if (c.Failed)
                        {
                            return default;
                        }

                        continue;
                    }

                    total = BinaryNode.Arithmetic(TokenKind.Plus, total, v, c);
                    if (c.Failed)
                    {
                        return default;
                    }
                }

                return total;
            }

            case AggregateKind.All or AggregateKind.Any:
            {
                var all = _kind == AggregateKind.All;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Include(items, i, c, out var v))
                    {
                        if (c.Failed)
                        {
                            return default;
                        }

                        continue;
                    }

                    var flag = Bool(v, c);
                    if (c.Failed)
                    {
                        return default;
                    }

                    if (flag != all)
                    {
                        return SValue.OfBool(!all);
                    }
                }

                return SValue.OfBool(all);
            }

            case AggregateKind.Unique:
            {
                // Lists in content are short; pairwise comparison allocates nothing.
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Include(items, i, c, out var a))
                    {
                        if (c.Failed)
                        {
                            return default;
                        }

                        continue;
                    }

                    for (var j = i + 1; j < items.Count; j++)
                    {
                        if (!Include(items, j, c, out var b))
                        {
                            if (c.Failed)
                            {
                                return default;
                            }

                            continue;
                        }

                        var equal = BinaryNode.Equal(a, b, out var comparable);
                        if (!comparable)
                        {
                            c.Fail($"`unique` cannot compare {FieldNode.Describe(a.Kind)} with {FieldNode.Describe(b.Kind)}");
                            return default;
                        }

                        if (equal)
                        {
                            return SValue.OfBool(false);
                        }
                    }
                }

                return SValue.OfBool(true);
            }

            case AggregateKind.Increasing:
            {
                SValue previous = default;
                var any = false;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Include(items, i, c, out var v))
                    {
                        if (c.Failed)
                        {
                            return default;
                        }

                        continue;
                    }

                    if (any)
                    {
                        if (!BinaryNode.TryCompare(previous, v, out var cmp))
                        {
                            c.Fail($"`increasing` cannot order {FieldNode.Describe(previous.Kind)} and {FieldNode.Describe(v.Kind)}");
                            return default;
                        }

                        if (cmp >= 0)
                        {
                            return SValue.OfBool(false);
                        }
                    }

                    previous = v;
                    any = true;
                }

                return SValue.OfBool(true);
            }

            default:
            {
                SValue best = default;
                var any = false;
                for (var i = 0; i < items.Count; i++)
                {
                    if (!Include(items, i, c, out var v))
                    {
                        if (c.Failed)
                        {
                            return default;
                        }

                        continue;
                    }

                    if (!any)
                    {
                        best = v;
                        any = true;
                        continue;
                    }

                    if (!BinaryNode.TryCompare(v, best, out var cmp))
                    {
                        c.Fail($"`{Name(_kind)}` cannot order {FieldNode.Describe(v.Kind)} and {FieldNode.Describe(best.Kind)}");
                        return default;
                    }

                    if (_kind == AggregateKind.Min ? cmp < 0 : cmp > 0)
                    {
                        best = v;
                    }
                }

                if (!any)
                {
                    c.Fail($"`{Name(_kind)}` of an empty list{(_where is null ? "" : " (no item passes the filter)")}");
                    return default;
                }

                return best;
            }
        }
    }

    private static bool Bool(SValue v, SchemaEvalContext c)
    {
        if (v.Kind != SKind.Bool)
        {
            c.Fail($"the condition gives {FieldNode.Describe(v.Kind)}, not a boolean");
            return false;
        }

        return v.Flag;
    }

    internal static string Name(AggregateKind kind) => kind.ToString().ToLowerInvariant();
}

/// <summary><c>forall(type, condition)</c>: the condition holds for every concrete object of the type.</summary>
internal sealed class ForallNode : SNode
{
    public ForallNode(string typeId, SNode body) : base(SType.Bool)
    {
        TypeId = typeId;
        Body = body;
    }

    public string TypeId { get; }

    public SNode Body { get; }

    /// <summary>Canonical text of the condition, for the default message.</summary>
    public string BodyText { get; init; } = "";

    public override SValue Eval(SchemaEvalContext c)
    {
        c.References?.Add("*" + TypeId);
        var scope = c.Scope;
        var it = c.It;
        var index = c.Index;
        c.Depth++;
        try
        {
            var objects = c.OfType(TypeId);
            for (var i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                if (c.Prepare is not null && !c.Prepare(o))
                {
                    c.Fail($"the computed fields of `{o.Key}` depend on themselves");
                    return default;
                }

                c.Scope = o.Body;
                c.It = new SValue(SKind.Record, 0, 0, o.Body);
                c.Index = i;
                var v = Body.Eval(c);
                if (c.Failed)
                {
                    return default;
                }

                if (v.Kind != SKind.Bool)
                {
                    c.Fail($"the condition gives {FieldNode.Describe(v.Kind)}, not a boolean");
                    return default;
                }

                if (!v.Flag)
                {
                    return SValue.OfBool(false);
                }
            }

            return SValue.OfBool(true);
        }
        finally
        {
            c.Depth--;
            c.Scope = scope;
            c.It = it;
            c.Index = index;
        }
    }
}

/// <summary><c>has(path)</c>: the field is set.</summary>
internal sealed class HasNode : SNode
{
    private readonly SNode _path;

    public HasNode(SNode path) : base(SType.Bool) => _path = path;

    public override SValue Eval(SchemaEvalContext c)
    {
        c.Probing++;
        var v = _path.Eval(c);
        c.Probing--;
        return c.Failed ? default : SValue.OfBool(v.Kind != SKind.Absent);
    }
}

/// <summary><c>len(text)</c> in Unicode scalars, or <c>len(list)</c> in items.</summary>
internal sealed class LenNode : SNode
{
    private readonly SNode _operand;

    public LenNode(SNode operand) : base(SType.Int) => _operand = operand;

    public override SValue Eval(SchemaEvalContext c)
    {
        var v = _operand.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        switch (v.Kind)
        {
            case SKind.Text:
            {
                var n = 0L;
                foreach (var _ in ((string)v.O!).EnumerateRunes())
                {
                    n++;
                }

                return SValue.OfInt(n);
            }

            case SKind.List:
                return SValue.OfInt(((RList)v.O!).Items.Count);
            default:
                c.Fail($"`len` needs a text or a list, got {FieldNode.Describe(v.Kind)}");
                return default;
        }
    }
}

/// <summary><c>subsequence(a, ea, b, eb)</c>: the values <c>ea</c> of <c>a</c> appear among the values <c>eb</c> of <c>b</c>, in the same order.</summary>
internal sealed class SubsequenceNode : SNode
{
    private readonly SNode _a;
    private readonly SNode _ea;
    private readonly SNode _b;
    private readonly SNode _eb;

    public SubsequenceNode(SNode a, SNode ea, SNode b, SNode eb) : base(SType.Bool)
    {
        _a = a;
        _ea = ea;
        _b = b;
        _eb = eb;
    }

    public override SValue Eval(SchemaEvalContext c)
    {
        var av = _a.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        var bv = _b.Eval(c);
        if (c.Failed)
        {
            return default;
        }

        if (av.Kind != SKind.List || bv.Kind != SKind.List)
        {
            c.Fail("`subsequence` needs two lists");
            return default;
        }

        var a = ((RList)av.O!).Items;
        var b = ((RList)bv.O!).Items;
        var scope = c.Scope;
        var it = c.It;
        var index = c.Index;
        c.Depth++;
        try
        {
            var j = 0;
            for (var i = 0; i < a.Count; i++)
            {
                var x = Project(a, i, _ea, c);
                if (c.Failed)
                {
                    return default;
                }

                var found = false;
                while (j < b.Count)
                {
                    var y = Project(b, j++, _eb, c);
                    if (c.Failed)
                    {
                        return default;
                    }

                    if (BinaryNode.Equal(x, y, out _))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return SValue.OfBool(false);
                }

                // The projection of b moved the scope; the next item of a sets it again.
            }

            return SValue.OfBool(true);
        }
        finally
        {
            c.Depth--;
            c.Scope = scope;
            c.It = it;
            c.Index = index;
        }
    }

    private static SValue Project(IReadOnlyList<ResolvedValue> items, int i, SNode body, SchemaEvalContext c)
    {
        var item = items[i];
        c.Scope = item as RRecord;
        c.It = SValue.From(item);
        c.Index = i;
        return body.Eval(c);
    }
}
