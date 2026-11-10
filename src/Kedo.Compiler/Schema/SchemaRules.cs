using System.Runtime.CompilerServices;
using System.Text;
using Kedo.Diagnostics;
using Kedo.Schema;
using Kedo.Text;

namespace Kedo.Compiler.Schema;

/// <summary>A part of an invariant message: literal text or a <c>{expr}</c> that shows a value.</summary>
internal readonly record struct MessagePart(string Text, SNode? Expr);

/// <summary>An invariant compiled against its owner.</summary>
internal sealed class CompiledInvariant
{
    public CompiledInvariant(InvariantSchema schema, SNode check, MessagePart[] message)
    {
        Schema = schema;
        Check = check;
        Message = message;
    }

    public InvariantSchema Schema { get; }

    public SNode Check { get; }

    /// <summary>Empty when the schema gives no message.</summary>
    public MessagePart[] Message { get; }

    /// <summary>A catalog invariant: its <c>forall</c>, evaluated object by object.</summary>
    public ForallNode? Forall { get; init; }
}

/// <summary>A computed field compiled against its record.</summary>
internal sealed class CompiledComputed
{
    public CompiledComputed(FieldSchema field, SNode expression, ExpressionSource source, SchemaExpressionCompiler compiler, RecordSchema owner)
    {
        Field = field;
        Expression = expression;
        Source = source;
        Compiler = compiler;
        Owner = owner;
        Provenance = new Kedo.Compiler.Resolution.Provenance(default, Kedo.Compiler.Resolution.LayerKind.Computed, field.Computed!);
    }

    /// <summary>Provenance of every value of this field.</summary>
    public Kedo.Compiler.Resolution.Provenance Provenance { get; }

    public FieldSchema Field { get; }

    public SNode Expression { get; }

    public ExpressionSource Source { get; }

    public SchemaExpressionCompiler Compiler { get; }

    public RecordSchema Owner { get; }
}

/// <summary>The rules of one record schema: its computed fields in evaluation order and its invariants.</summary>
internal sealed class RecordRules
{
    public List<CompiledComputed> Computed { get; } = new();

    public List<CompiledInvariant> Invariants { get; } = new();

    /// <summary>No computed field reads another computed field of the same record: all can read the record as resolved.</summary>
    public bool Independent { get; set; }
}

/// <summary>
/// The compiled schema expressions of a registry (ADR-0016), built once per registry and cached: computed fields
/// (K3012 for cycles) and invariants of types, components and records, and catalog invariants. K2013 for expressions
/// that do not type is reported when the rules are built: by <see cref="SchemaLoader"/> for a schema written in KEDO.
/// </summary>
internal sealed class SchemaRules
{
    private static readonly ConditionalWeakTable<SchemaRegistry, SchemaRules> Cache = new();
    private static readonly ConditionalWeakTable<object, ExpressionSource> Sources = new();
    private static readonly ConditionalWeakTable<InvariantSchema, ExpressionSource> MessageSources = new();

    private readonly Dictionary<RecordSchema, RecordRules> _records = new(ReferenceEqualityComparer.Instance);

    public static SchemaRules Empty { get; } = new();

    /// <summary>Catalog invariants (<c>kedo_invariant</c>).</summary>
    public List<CompiledInvariant> Catalog { get; } = new();

    public bool IsEmpty => _records.Count == 0 && Catalog.Count == 0;

    public bool HasComputed { get; private set; }

    public bool HasInvariants { get; private set; }

    /// <summary>Some rule walks every object of a type: changes of any object of it are dependencies.</summary>
    public bool HasForall { get; private set; }

    public RecordRules? For(RecordSchema? schema) => schema is not null && _records.TryGetValue(schema, out var r) ? r : null;

    /// <summary>Remembers where a descriptor's expression was written, for exact positions.</summary>
    public static void Remember(object descriptor, ExpressionSource source) => Sources.AddOrUpdate(descriptor, source);

    /// <summary>Remembers where an invariant's message was written.</summary>
    public static void RememberMessage(InvariantSchema invariant, ExpressionSource source) => MessageSources.AddOrUpdate(invariant, source);

    /// <summary>The rules of a registry; built (reporting K2013 and K3012 to <paramref name="diagnostics"/>) on the first call.</summary>
    public static SchemaRules Get(SchemaRegistry registry, DiagnosticBag diagnostics)
    {
        if (Cache.TryGetValue(registry, out var rules))
        {
            return rules;
        }

        rules = Build(registry, diagnostics);
        Cache.AddOrUpdate(registry, rules);
        return rules;
    }

    private static ExpressionSource SourceOf(object descriptor, string text) => Sources.TryGetValue(descriptor, out var s) ? s : new ExpressionSource(text, null, 0);

    private static void Report(DiagnosticBag d, string code, ExpressionSource at, string message)
        => d.Error(code, message, at.File, new TextSpan(at.Offset, Encoding.UTF8.GetByteCount(at.Source)));

    private static SchemaRules Build(SchemaRegistry registry, DiagnosticBag d)
    {
        var hasRules = registry.Invariants.Count > 0
            || registry.Types.Values.Any(t => HasRules(t.Body))
            || registry.Components.Values.Any(HasRules)
            || registry.Records.Values.Any(HasRules);
        if (!hasRules)
        {
            return Empty;
        }

        var rules = new SchemaRules();
        foreach (var type in registry.Types.Values)
        {
            rules.Add(registry, d, type.Body, $"type `{type.Id}`", computable: true, new SType(SKind.Record) { Record = type.Body });
        }

        foreach (var component in registry.Components.Values)
        {
            rules.Add(registry, d, component, $"component `{component.Id}`", computable: true, new SType(SKind.Record));
        }

        foreach (var record in registry.Records.Values)
        {
            rules.Add(registry, d, record, $"record `{record.Id}`", computable: false, new SType(SKind.Record));
        }

        foreach (var inv in registry.Invariants)
        {
            var source = SourceOf(inv, inv.Check);
            if (!inv.Check.StartsWith("forall(", StringComparison.Ordinal))
            {
                Report(d, "K2013", source, $"catalog invariant `{inv.Id}` must be `forall(type, condition)`; a rule about one object goes in the `invariants` of its type");
                continue;
            }

            var node = SchemaExpressionCompiler.Compile(registry, d, source, null, false, null, out var compiler);
            if (node is null)
            {
                continue;
            }

            if (node is not ForallNode forall)
            {
                Report(d, "K2013", source, $"catalog invariant `{inv.Id}` must be `forall(type, condition)`; a rule about one object goes in the `invariants` of its type");
                continue;
            }

            var scope = registry.FindType(forall.TypeId)?.Body;
            rules.Catalog.Add(new CompiledInvariant(inv, node, rules.MessageOf(registry, d, inv, scope, scope is null, null)) { Forall = forall });
            rules.HasInvariants = true;
            rules.HasForall |= compiler.UsesForall;
        }

        rules.Order(d);
        return rules;
    }

    private static bool HasRules(RecordSchema r) => r.Invariants.Count > 0 || r.Fields.Any(f => f.Computed is not null);

    private RecordRules Rules(RecordSchema schema)
    {
        if (!_records.TryGetValue(schema, out var r))
        {
            _records[schema] = r = new RecordRules();
        }

        return r;
    }

    private void Add(SchemaRegistry registry, DiagnosticBag d, RecordSchema schema, string what, bool computable, SType root)
    {
        if (!HasRules(schema))
        {
            return;
        }

        foreach (var f in schema.Fields)
        {
            if (f.Computed is null)
            {
                continue;
            }

            var source = SourceOf(f, f.Computed);
            if (!computable)
            {
                Report(d, "K2013", source, $"field `{f.Name}` of {what} cannot be computed: compute it on the type or component that holds the records (`sum(list, {f.Name})`…)");
                continue;
            }

            if (f.Kind is ValueKind.List or ValueKind.Record or ValueKind.Map or ValueKind.Any or ValueKind.Expression)
            {
                Report(d, "K2013", source, $"computed field `{f.Name}` of {what} is of kind `{f.Kind.ToString().ToLowerInvariant()}`; a computed field is int, decimal, duration, bool, text, enum or reference");
                continue;
            }

            var node = SchemaExpressionCompiler.Compile(registry, d, source, schema, registry.Permissive, root, out var compiler);
            if (node is null)
            {
                continue;
            }

            if (Mismatch(f, node.Type) is { } why)
            {
                Report(d, "K2013", source, $"computed field `{f.Name}` of {what} is {why}");
                continue;
            }

            Rules(schema).Computed.Add(new CompiledComputed(f, node, source, compiler, schema));
            HasComputed = true;
            HasForall |= compiler.UsesForall;
        }

        foreach (var inv in schema.Invariants)
        {
            var source = SourceOf(inv, inv.Check);
            var node = SchemaExpressionCompiler.Compile(registry, d, source, schema, registry.Permissive, root, out var compiler);
            if (node is null)
            {
                continue;
            }

            if (node.Type.Kind is not (SKind.Bool or SKind.Any))
            {
                Report(d, "K2013", source, $"the check of invariant `{inv.Id}` of {what} must be a boolean; it gives {node.Type.Describe()}");
                continue;
            }

            Rules(schema).Invariants.Add(new CompiledInvariant(inv, node, MessageOf(registry, d, inv, schema, registry.Permissive, root)));
            HasInvariants = true;
            HasForall |= compiler.UsesForall;
        }
    }

    /// <summary>Why the expression's type does not fit the field, or null when it fits.</summary>
    private static string? Mismatch(FieldSchema f, SType t)
    {
        if (t.Kind == SKind.Any)
        {
            return null;
        }

        var fits = f.Kind switch
        {
            ValueKind.Int => t.Kind == SKind.Int,
            ValueKind.Decimal => t.Kind == SKind.Int || (t.Kind == SKind.Decimal && t.Scale <= f.Scale),
            ValueKind.Duration => t.Kind == SKind.Duration,
            ValueKind.Bool => t.Kind == SKind.Bool,
            ValueKind.Text => t.Kind == SKind.Text,
            ValueKind.Enum => t.Kind == SKind.Enum && (t.EnumId is null || f.EnumId is null || t.EnumId == f.EnumId),
            ValueKind.Reference => t.Kind == SKind.Ref && (t.Target is null || f.TargetType is null || t.Target == f.TargetType),
            ValueKind.Asset => t.Kind == SKind.Ref,
            _ => false,
        };
        if (fits)
        {
            return null;
        }

        var kind = f.Kind == ValueKind.Decimal ? $"decimal with {f.Scale} decimal places" : f.Kind.ToString().ToLowerInvariant();
        return $"{kind}, but the expression gives {t.Describe()}{(t.Kind == SKind.Decimal && f.Kind == ValueKind.Decimal ? $" with {t.Scale} decimal places" : "")}";
    }

    private MessagePart[] MessageOf(SchemaRegistry registry, DiagnosticBag d, InvariantSchema inv, RecordSchema? scope, bool scopeAny, SType? root)
    {
        if (inv.Message is not { } message)
        {
            return Array.Empty<MessagePart>();
        }

        var at = MessageSources.TryGetValue(inv, out var m) ? m : new ExpressionSource(message, null, 0);
        var parts = new List<MessagePart>();
        var text = new StringBuilder();
        for (var i = 0; i < message.Length; i++)
        {
            var ch = message[i];
            if ((ch == '{' || ch == '}') && i + 1 < message.Length && message[i + 1] == ch)
            {
                text.Append(ch);
                i++;
                continue;
            }

            if (ch != '{')
            {
                text.Append(ch);
                continue;
            }

            var close = message.IndexOf('}', i + 1);
            if (close < 0)
            {
                Report(d, "K2013", at, $"the message of invariant `{inv.Id}` opens `{{` without closing it; write `{{{{` for a literal brace");
                return Array.Empty<MessagePart>();
            }

            if (text.Length > 0)
            {
                parts.Add(new MessagePart(text.ToString(), null));
                text.Clear();
            }

            // The literal starts with its quote: the text of the message begins one byte after the offset.
            var segment = message[(i + 1)..close];
            var offset = at.File is null ? 0 : at.Offset + 1 + Encoding.UTF8.GetByteCount(message[..(i + 1)]);
            var node = SchemaExpressionCompiler.Compile(registry, d, new ExpressionSource(segment, at.File, offset), scope, scopeAny, root, out _);
            if (node is null)
            {
                return Array.Empty<MessagePart>();
            }

            parts.Add(new MessagePart("", node));
            i = close;
        }

        if (text.Length > 0)
        {
            parts.Add(new MessagePart(text.ToString(), null));
        }

        return parts.ToArray();
    }

    /// <summary>K3012 for cycles between computed fields; then each record's computed fields in dependency order.</summary>
    private void Order(DiagnosticBag d)
    {
        var all = _records.Values.SelectMany(r => r.Computed).ToList();
        if (all.Count == 0)
        {
            return;
        }

        var byField = new Dictionary<(RecordSchema, string), CompiledComputed>();
        foreach (var c in all)
        {
            byField[(c.Owner, c.Field.Name)] = c;
        }

        List<CompiledComputed> DependsOn(CompiledComputed c)
        {
            var deps = new List<CompiledComputed>();
            foreach (var read in c.Compiler.Reads)
            {
                if (byField.TryGetValue(read, out var dep) && !deps.Contains(dep))
                {
                    deps.Add(dep);
                }
            }

            // `forall(type, …)` may read any computed field of that type.
            foreach (var body in c.Compiler.ForallTypes)
            {
                deps.AddRange(all.Where(o => ReferenceEquals(o.Owner, body) && !deps.Contains(o)));
            }

            return deps;
        }

        var state = new Dictionary<CompiledComputed, int>(ReferenceEqualityComparer.Instance);
        var cyclic = new HashSet<CompiledComputed>(ReferenceEqualityComparer.Instance);
        var stack = new List<CompiledComputed>();
        var order = new List<CompiledComputed>();

        void Visit(CompiledComputed c)
        {
            state[c] = 1;
            stack.Add(c);
            foreach (var dep in DependsOn(c))
            {
                if (!state.TryGetValue(dep, out var s))
                {
                    Visit(dep);
                }
                else if (s == 1)
                {
                    var cycle = stack.Skip(stack.IndexOf(dep)).ToList();
                    if (cycle.Any(x => !cyclic.Contains(x)))
                    {
                        var path = string.Join(" → ", cycle.Append(dep).Select(x => $"{x.Owner.Id}.{x.Field.Name}"));
                        Report(d, "K3012", dep.Source, $"computed fields depend on themselves: {path}");
                    }

                    cyclic.UnionWith(cycle);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[c] = 2;
            order.Add(c);
        }

        foreach (var c in all)
        {
            if (!state.ContainsKey(c))
            {
                Visit(c);
            }
        }

        foreach (var rules in _records.Values)
        {
            var mine = order.Where(c => rules.Computed.Contains(c) && !cyclic.Contains(c)).ToList();
            rules.Computed.Clear();
            rules.Computed.AddRange(mine);
            rules.Independent = !mine.Any(c => DependsOn(c).Any(dep => ReferenceEquals(dep.Owner, c.Owner)));
        }

        HasComputed = _records.Values.Any(r => r.Computed.Count > 0);
    }
}
