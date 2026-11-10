using System.Globalization;
using System.Text;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;
using Kedo.Text;

namespace Kedo.Compiler.Schema;

/// <summary>Where a schema expression was written: its text as written, the file and the byte offset of the text in it.</summary>
internal sealed record ExpressionSource(string Source, string? File, int Offset);

/// <summary>
/// Compiles the text of a schema expression (ADR-0016) to a typed <see cref="SNode"/> tree against the registry.
/// Everything that does not type is K2013 at the exact position in the schema file.
/// </summary>
internal sealed class SchemaExpressionCompiler
{
    private static readonly string[] Functions = { "all", "any", "count", "forall", "has", "increasing", "len", "max", "min", "subsequence", "sum", "unique" };

    private readonly SchemaRegistry _registry;
    private readonly DiagnosticBag _d;
    private readonly ExpressionSource _source;
    private SyntaxTree _t = null!;

    // Scope: the record bare names read (null with _scopeAny false: scalar items, use `it`).
    private RecordSchema? _scope;
    private bool _scopeAny;
    private SType? _it;
    private bool _inAggregate;
    private readonly SType? _root;

    private SchemaExpressionCompiler(SchemaRegistry registry, DiagnosticBag diagnostics, ExpressionSource source, RecordSchema? scope, bool scopeAny, SType? root)
    {
        _registry = registry;
        _d = diagnostics;
        _source = source;
        _scope = scope;
        _scopeAny = scopeAny || registry.Permissive;
        _root = root;
    }

    /// <summary>Fields of statically known records the expression reads (dependencies of computed fields, K3012).</summary>
    public List<(RecordSchema Record, string Field)> Reads { get; } = new();

    /// <summary>True when the expression has a <c>forall</c> (it depends on every object of a type).</summary>
    public bool UsesForall { get; private set; }

    /// <summary>Bodies of the types a <c>forall</c> walks (a computed field that reads them depends on all their computed fields).</summary>
    public List<RecordSchema> ForallTypes { get; } = new();

    /// <summary>
    /// Compiles; null after reporting K1xxx or K2013. <c>scope</c> is the record bare names read from (null with
    /// <c>scopeAny</c> for a record of unknown shape); <c>root</c> is the type of <c>root</c>, or null where it is not available.
    /// </summary>
    public static SNode? Compile(SchemaRegistry registry, DiagnosticBag diagnostics, ExpressionSource source, RecordSchema? scope, bool scopeAny, SType? root, out SchemaExpressionCompiler compiler)
    {
        var c = new SchemaExpressionCompiler(registry, diagnostics, source, scope, scopeAny, root);
        compiler = c;
        var bag = new DiagnosticBag();
        c._t = Parser.ParseExpressionText(SourceText.FromString(source.Source, source.File), bag);
        foreach (var d in bag.Items)
        {
            diagnostics.Add(d with { Span = new TextSpan(d.Span.Start + source.Offset, d.Span.Length) });
        }

        if (bag.HasErrors)
        {
            return null;
        }

        var children = c._t.Children(c._t.Root);
        return children.Length == 1 ? c.Value(children[0]) : null;
    }

    /// <summary>Canonical text of an expression: its tokens with normalised spacing, comments and line breaks dropped.</summary>
    public static string Canonical(string source)
    {
        var text = SourceText.FromString(source);
        var tokens = Lexer.Lex(text, new DiagnosticBag());
        var sb = new StringBuilder(source.Length);
        var prev = TokenKind.EndOfFile;
        var prevUnary = false;
        foreach (var tk in tokens)
        {
            if (tk.IsTrivia || tk.Kind == TokenKind.EndOfFile)
            {
                continue;
            }

            if (sb.Length > 0 && Space(prev, tk.Kind, prevUnary))
            {
                sb.Append(' ');
            }

            sb.Append(text.GetText(tk.Span));
            prevUnary = tk.Kind == TokenKind.Minus && (sb.Length == 1 || prev is TokenKind.LParen or TokenKind.Comma || IsOperator(prev));
            prev = tk.Kind;
        }

        return sb.ToString();
    }

    private static bool IsOperator(TokenKind k) => k is TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Percent
        or TokenKind.EqualsEquals or TokenKind.BangEquals or TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals
        or TokenKind.KwAnd or TokenKind.KwOr or TokenKind.KwNot;

    private static bool Space(TokenKind prev, TokenKind next, bool prevUnary)
    {
        if (prevUnary || prev is TokenKind.LParen or TokenKind.LBracket or TokenKind.Dot || next is TokenKind.RParen or TokenKind.RBracket or TokenKind.Comma or TokenKind.Dot)
        {
            return false;
        }

        return !(next == TokenKind.LParen && prev is TokenKind.Identifier or TokenKind.RParen);
    }

    // ----- compilation -----

    private SNode? Error(int node, string message)
    {
        var span = _t.SpanOf(node);
        _d.Error("K2013", message, _source.File, new TextSpan(span.Start + _source.Offset, span.Length));
        return null;
    }

    private string TextOf(int node) => Canonical(_t.TextOfNode(node));

    /// <summary>A value: a bare name must be a field (or <c>it</c>, <c>index</c>, <c>root</c>).</summary>
    private SNode? Value(int node)
    {
        var compiled = Compile(node, enumLiteral: false);
        return compiled;
    }

    private SNode? Compile(int node, bool enumLiteral)
    {
        var n = _t[node];
        switch (n.Kind)
        {
            case NodeKind.ParenExpr:
            {
                var children = _t.Children(node);
                return children.Length == 1 ? Compile(children[0], enumLiteral) : Error(node, "empty parentheses");
            }

            case NodeKind.LiteralExpr:
                return Literal(node);
            case NodeKind.NameExpr:
                return Name(node, enumLiteral);
            case NodeKind.RefExpr:
                return Reference(node);
            case NodeKind.MemberExpr:
            {
                var target = Value(_t.Children(node)[0]);
                var own = _t.OwnTokens(node);
                return target is null ? null : Member(node, target, _t.TextOf(own[^1]));
            }

            case NodeKind.CallExpr:
                return Call(node);
            case NodeKind.UnaryExpr:
            {
                var not = _t.Tokens[_t.OwnToken(node, 0)].Kind == TokenKind.KwNot;
                var operand = Value(_t.Children(node)[0]);
                if (operand is null)
                {
                    return null;
                }

                var k = operand.Type.Kind;
                if (not && k is not (SKind.Bool or SKind.Any))
                {
                    return Error(node, $"`not` needs a boolean; `{TextOf(_t.Children(node)[0])}` is {operand.Type.Describe()}");
                }

                if (!not && k is not (SKind.Int or SKind.Decimal or SKind.Duration or SKind.Any))
                {
                    return Error(node, $"`-` needs a number or a duration; `{TextOf(_t.Children(node)[0])}` is {operand.Type.Describe()}");
                }

                return new UnaryNode(not ? SType.Bool : operand.Type, not, operand);
            }

            case NodeKind.BinaryExpr:
                return Binary(node);
            default:
                return Error(node, "lists, records and statements are not allowed in a schema expression");
        }
    }

    private SNode? Literal(int node)
    {
        var tk = _t.OwnToken(node, 0);
        var text = _t.TextOf(tk);
        switch (_t.Tokens[tk].Kind)
        {
            case TokenKind.Integer:
                return long.TryParse(text.Replace("_", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                    ? new ConstNode(SType.Int, SValue.OfInt(i))
                    : Error(node, $"integer `{text}` does not fit in 64 bits");
            case TokenKind.Decimal:
            {
                var digits = text.Replace("_", "");
                var dot = digits.IndexOf('.');
                var scale = digits.Length - dot - 1;
                return long.TryParse(digits.Remove(dot, 1), NumberStyles.None, CultureInfo.InvariantCulture, out var units)
                    ? new ConstNode(SType.Decimal(scale), new SValue(SKind.Decimal, units, scale, null))
                    : Error(node, $"decimal `{text}` does not fit in 64 bits");
            }

            case TokenKind.Duration:
            {
                var k = 0;
                while (k < text.Length && (char.IsAsciiDigit(text[k]) || text[k] == '_'))
                {
                    k++;
                }

                if (!long.TryParse(text[..k].Replace("_", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                    || !DurationUnits.TryParseSuffix(text[k..], out var unit)
                    || !DurationUnits.TryConvert(amount, unit, DurationUnit.Millisecond, out var ms))
                {
                    return Error(node, $"duration `{text}` does not fit in 64 bits of milliseconds");
                }

                return new ConstNode(SType.Duration, new SValue(SKind.Duration, ms, 0, null));
            }

            case TokenKind.String:
                return new ConstNode(SType.Text, new SValue(SKind.Text, 0, 0, Unescape(text)));
            case TokenKind.KwTrue:
                return new ConstNode(SType.Bool, SValue.OfBool(true));
            case TokenKind.KwFalse:
                return new ConstNode(SType.Bool, SValue.OfBool(false));
            default:
                return Error(node, $"`{text}` is not a value");
        }
    }

    private static string Unescape(string literal)
    {
        var body = literal.Length >= 2 ? literal[1..^1] : literal;
        if (!body.Contains('\\'))
        {
            return body;
        }

        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length)
            {
                i++;
                sb.Append(body[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => body[i] });
            }
            else
            {
                sb.Append(body[i]);
            }
        }

        return sb.ToString();
    }

    private SNode? Name(int node, bool enumLiteral)
    {
        var name = _t.TextOf(_t.OwnToken(node, 0));
        switch (name)
        {
            case "it":
                return _it is null ? Error(node, "`it` is only available inside an aggregate over a list or `forall`, such as `all(levels, it > 0)`") : new ItNode(_it);
            case "index":
                return _inAggregate ? new IndexNode() : Error(node, "`index` is only available inside an aggregate over a list, such as `all(sectors, sector == index + 1)`");
            case "root":
                return _root is null ? Error(node, "`root` is only available in rules of a type, a component or a record") : new RootNode(_root);
        }

        if (_scopeAny)
        {
            return new FieldNode(SType.Any, null, name, name, required: false);
        }

        if (_scope?.Find(name) is { } fs)
        {
            Reads.Add((_scope, name));
            return new FieldNode(SType.Of(fs, _registry), null, name, name, fs.Required);
        }

        if (enumLiteral)
        {
            return new EnumLiteralNode(name, node);
        }

        return UnknownName(node, name);
    }

    private SNode? UnknownName(int node, string name)
    {
        if (_scope is null)
        {
            return Error(node, $"`{name}` is not a field: the items here are not records; use `it` for the item");
        }

        if (Array.IndexOf(Functions, name) >= 0)
        {
            return Error(node, $"`{name}` is a function; call it: `{name}(…)`");
        }

        var suggestion = Resolver.Suggest(name, _scope.Fields.Select(f => f.Name));
        return Error(node, $"`{name}` is not a field of `{_scope.Id}`{(suggestion is null ? "" : $"; did you mean `{suggestion}`?")}");
    }

    private SNode? Reference(int node)
    {
        var text = _t.TextOf(_t.OwnToken(node, 0));
        var colon = text.IndexOf(':');
        var type = text[..colon];
        var rest = text[(colon + 1)..];
        string? module = null;
        var slash = rest.LastIndexOf('/');
        if (slash >= 0)
        {
            module = rest[..slash];
            rest = rest[(slash + 1)..];
        }

        // `type:id.field.path`: object ids have no dots, so the first dot after the id starts the path.
        var parts = rest.Split('.');
        if (!_registry.Permissive && type != "asset" && _registry.FindType(type) is null)
        {
            var suggestion = Resolver.Suggest(type, _registry.Types.Keys);
            return Error(node, $"unknown type `{type}` in `{text}`{(suggestion is null ? "" : $"; did you mean `{suggestion}`?")}");
        }

        var origin = new Provenance(default, LayerKind.Default, "");
        SNode current = new ConstNode(new SType(SKind.Ref) { Target = type }, new SValue(SKind.Ref, 0, 0, new RRef(origin, type, module, parts[0])));
        foreach (var field in parts.Skip(1))
        {
            var next = Member(node, current, field);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private SNode? Member(int node, SNode target, string name)
    {
        var text = TextOf(node);
        var t = target.Type;
        RecordSchema? record;
        switch (t.Kind)
        {
            case SKind.Any:
                return new FieldNode(SType.Any, target, name, text, required: false);
            case SKind.Record when t.IsMap:
                return new FieldNode(SType.Of(t.Element, _registry), target, name, text, required: false);
            case SKind.Record:
                record = t.Record;
                break;
            case SKind.Ref:
                if (t.Target is null || _registry.Permissive)
                {
                    return new FieldNode(SType.Any, target, name, text, required: false);
                }

                record = _registry.FindType(t.Target)?.Body;
                break;
            default:
                return Error(node, $"`{text}`: {t.Describe()} has no fields; only records and references do");
        }

        if (record is null)
        {
            return new FieldNode(SType.Any, target, name, text, required: false);
        }

        if (record.Find(name) is not { } fs)
        {
            var suggestion = Resolver.Suggest(name, record.Fields.Select(f => f.Name));
            return Error(node, $"`{name}` is not a field of `{record.Id}`{(suggestion is null ? "" : $"; did you mean `{suggestion}`?")}");
        }

        Reads.Add((record, name));
        return new FieldNode(SType.Of(fs, _registry), target, name, text, fs.Required);
    }

    private SNode? Binary(int node)
    {
        var op = _t.Tokens[_t.OwnToken(node, 0)].Kind;
        var children = _t.Children(node);
        if (op is TokenKind.EqualsEquals or TokenKind.BangEquals)
        {
            var l = Compile(children[0], enumLiteral: true);
            var r = Compile(children[1], enumLiteral: true);
            if (l is null || r is null)
            {
                return null;
            }

            if (l is EnumLiteralNode both && r is EnumLiteralNode)
            {
                return UnknownName(both.Node, both.Name);
            }

            l = EnumValue(l, r.Type);
            r = EnumValue(r, l?.Type);
            if (l is null || r is null)
            {
                return null;
            }

            if (!Comparable(l.Type, r.Type))
            {
                return Error(node, $"`{BinaryNode.Op(op)}` cannot compare {l.Type.Describe()} with {r.Type.Describe()}");
            }

            return new BinaryNode(SType.Bool, op, l, r);
        }

        var left = Value(children[0]);
        var right = Value(children[1]);
        if (left is null || right is null)
        {
            return null;
        }

        SKind a = left.Type.Kind, b = right.Type.Kind;
        var any = a == SKind.Any || b == SKind.Any;
        switch (op)
        {
            case TokenKind.KwAnd or TokenKind.KwOr:
                if (!any && (a != SKind.Bool || b != SKind.Bool))
                {
                    return Error(node, $"`{BinaryNode.Op(op)}` needs booleans, got {left.Type.Describe()} and {right.Type.Describe()}");
                }

                return new BinaryNode(SType.Bool, op, left, right);
            case TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals:
                if (!any && !(left.Type.IsNumeric && right.Type.IsNumeric) && !(a == SKind.Duration && b == SKind.Duration))
                {
                    return Error(node, $"`{BinaryNode.Op(op)}` orders two numbers or two durations, got {left.Type.Describe()} and {right.Type.Describe()}");
                }

                return new BinaryNode(SType.Bool, op, left, right);
            default:
            {
                if (any)
                {
                    return new BinaryNode(SType.Any, op, left, right);
                }

                var type = ArithmeticType(op, left.Type, right.Type, out var why);
                return type is null ? Error(node, why!) : new BinaryNode(type, op, left, right);
            }
        }
    }

    /// <summary>An <see cref="EnumLiteralNode"/> becomes a constant of the enum on the other side, or K2013.</summary>
    private SNode? EnumValue(SNode? node, SType? other)
    {
        if (node is not EnumLiteralNode lit)
        {
            return node;
        }

        if (other is { Kind: SKind.Enum } && other.EnumId is { } eid && _registry.FindEnum(eid) is { } e)
        {
            if (!e.Values.Contains(lit.Name))
            {
                var suggestion = Resolver.Suggest(lit.Name, e.Values);
                return Error(lit.Node, $"`{lit.Name}` is not a field here nor a value of enum `{eid}` ({string.Join(", ", e.Values)}){(suggestion is null ? "" : $"; did you mean `{suggestion}`?")}");
            }

            return new ConstNode(new SType(SKind.Enum) { EnumId = eid }, new SValue(SKind.Enum, 0, 0, lit.Name));
        }

        if (other is { Kind: SKind.Enum or SKind.Any })
        {
            return new ConstNode(new SType(SKind.Enum), new SValue(SKind.Enum, 0, 0, lit.Name));
        }

        return UnknownName(lit.Node, lit.Name);
    }

    private static bool Comparable(SType l, SType r)
    {
        if (l.Kind == SKind.Any || r.Kind == SKind.Any || (l.IsNumeric && r.IsNumeric))
        {
            return true;
        }

        if (l.Kind != r.Kind)
        {
            return false;
        }

        return l.Kind switch
        {
            SKind.Enum => l.EnumId is null || r.EnumId is null || l.EnumId == r.EnumId,
            SKind.Bool or SKind.Text or SKind.Duration or SKind.Ref => true,
            _ => false,
        };
    }

    internal static SType? ArithmeticType(TokenKind op, SType l, SType r, out string? why)
    {
        why = null;
        var name = BinaryNode.Op(op);
        switch (l.Kind, r.Kind)
        {
            case (SKind.Int, SKind.Int):
                return SType.Int;
            case (SKind.Decimal or SKind.Int, SKind.Decimal or SKind.Int):
                if (op is TokenKind.Plus or TokenKind.Minus)
                {
                    return SType.Decimal(Math.Max(l.Scale, r.Scale));
                }

                if (op == TokenKind.Star && (l.Kind == SKind.Int || r.Kind == SKind.Int))
                {
                    return SType.Decimal(Math.Max(l.Scale, r.Scale));
                }

                why = $"`{name}` on a decimal has no exact result: decimals add, subtract, compare and multiply by an integer (ADR-0008)";
                return null;
            case (SKind.Duration, SKind.Duration) when op is TokenKind.Plus or TokenKind.Minus:
                return SType.Duration;
            case (SKind.Duration, SKind.Int) or (SKind.Int, SKind.Duration) when op == TokenKind.Star:
                return SType.Duration;
            case (SKind.Duration, _) or (_, SKind.Duration):
                why = $"`{name}` cannot combine {l.Describe()} and {r.Describe()}: durations add, subtract, compare and multiply by an integer";
                return null;
            case (SKind.Text, SKind.Text) when op == TokenKind.Plus:
                return SType.Text;
            default:
                why = $"`{name}` cannot combine {l.Describe()} and {r.Describe()}";
                return null;
        }
    }

    // ----- functions -----

    private SNode? Call(int node)
    {
        var children = _t.Children(node);
        var callee = children[0];
        if (_t[callee].Kind != NodeKind.NameExpr)
        {
            return Error(callee, "only the built-in functions can be called in a schema expression");
        }

        var name = _t.TextOf(_t.OwnToken(callee, 0));
        var args = children.Length > 1 ? _t.Children(children[1]).ToArray() : Array.Empty<int>();
        bool Arity(int min, int max) => args.Length >= min && args.Length <= max;
        string Usage(string usage) => $"`{name}` takes {usage}";

        switch (name)
        {
            case "count":
            {
                if (!Arity(1, 2))
                {
                    return Error(node, Usage("a list and an optional condition: `count(list)`, `count(list, condition)`"));
                }

                var list = ListArg(args[0], name);
                if (list is null)
                {
                    return null;
                }

                var cond = args.Length == 2 ? InItems(list, () => Condition(args[1])) : null;
                return args.Length == 2 && cond is null ? null : new AggregateNode(SType.Int, AggregateKind.Count, list, cond, default);
            }

            case "all" or "any":
            {
                if (!Arity(2, 2))
                {
                    return Error(node, Usage($"a list and a condition: `{name}(list, condition)`"));
                }

                var list = ListArg(args[0], name);
                var cond = list is null ? null : InItems(list, () => Condition(args[1]));
                return cond is null ? null : new AggregateNode(SType.Bool, name == "all" ? AggregateKind.All : AggregateKind.Any, list!, cond, default);
            }

            case "sum" or "min" or "max" or "unique" or "increasing":
            {
                if (!Arity(1, 3))
                {
                    return Error(node, Usage($"a list, an optional value of each item and an optional filter: `{name}(list)`, `{name}(list, value)`, `{name}(list, value, condition)`"));
                }

                var list = ListArg(args[0], name);
                if (list is null)
                {
                    return null;
                }

                SNode? body = null;
                SType itemType;
                if (args.Length >= 2)
                {
                    body = InItems(list, () => Value(args[1]));
                    if (body is null)
                    {
                        return null;
                    }

                    itemType = body.Type;
                }
                else
                {
                    itemType = ItemType(list.Type);
                }

                SNode? where = null;
                if (args.Length == 3)
                {
                    where = InItems(list, () => Condition(args[2]));
                    if (where is null)
                    {
                        return null;
                    }
                }

                var kind = name switch { "sum" => AggregateKind.Sum, "min" => AggregateKind.Min, "max" => AggregateKind.Max, "unique" => AggregateKind.Unique, _ => AggregateKind.Increasing };
                var k = itemType.Kind;
                if (kind == AggregateKind.Unique)
                {
                    if (k is SKind.Record or SKind.List)
                    {
                        return Error(node, $"`unique` compares scalar values; give it the value of each item: `unique(list, id)`");
                    }

                    return new AggregateNode(SType.Bool, kind, list, body, default, where);
                }

                if (k is not (SKind.Int or SKind.Decimal or SKind.Duration or SKind.Any))
                {
                    return Error(node, $"`{name}` needs numbers or durations; the items give {itemType.Describe()}{(args.Length == 1 && k == SKind.Record ? $"; say which value: `{name}({TextOf(args[0])}, field)`" : "")}");
                }

                var zero = k switch
                {
                    SKind.Decimal => new SValue(SKind.Decimal, 0, itemType.Scale, null),
                    SKind.Duration => new SValue(SKind.Duration, 0, 0, null),
                    _ => SValue.OfInt(0),
                };
                return new AggregateNode(kind == AggregateKind.Increasing ? SType.Bool : itemType, kind, list, body, zero, where);
            }

            case "forall":
            {
                if (!Arity(2, 2) || _t[args[0]].Kind != NodeKind.NameExpr)
                {
                    return Error(node, Usage("a type and a condition: `forall(card, weight > 0)`"));
                }

                var typeId = _t.TextOf(_t.OwnToken(args[0], 0));
                var type = _registry.FindType(typeId);
                if (type is null && !_registry.Permissive)
                {
                    var suggestion = Resolver.Suggest(typeId, _registry.Types.Keys);
                    return Error(args[0], $"unknown type `{typeId}`{(suggestion is null ? "" : $"; did you mean `{suggestion}`?")}");
                }

                var saved = (_scope, _scopeAny, _it, _inAggregate);
                _scope = type?.Body;
                _scopeAny = type is null || _registry.Permissive;
                _it = new SType(SKind.Record) { Record = type?.Body };
                _inAggregate = true;
                var body = Condition(args[1]);
                (_scope, _scopeAny, _it, _inAggregate) = saved;
                UsesForall = true;
                if (type is not null)
                {
                    ForallTypes.Add(type.Body);
                }

                return body is null ? null : new ForallNode(typeId, body) { BodyText = TextOf(args[1]) };
            }

            case "has":
            {
                if (!Arity(1, 1) || _t[args[0]].Kind is not (NodeKind.NameExpr or NodeKind.MemberExpr or NodeKind.RefExpr))
                {
                    return Error(node, Usage("a field: `has(optional)`, `has(boss.phase)`"));
                }

                var path = Value(args[0]);
                return path is null ? null : new HasNode(path);
            }

            case "len":
            {
                if (!Arity(1, 1))
                {
                    return Error(node, Usage("a text or a list: `len(title)`"));
                }

                var operand = Value(args[0]);
                if (operand is null)
                {
                    return null;
                }

                return operand.Type.Kind is SKind.Text or SKind.List or SKind.Any
                    ? new LenNode(operand)
                    : Error(args[0], $"`len` needs a text or a list; `{TextOf(args[0])}` is {operand.Type.Describe()}");
            }

            case "subsequence":
            {
                if (!Arity(4, 4))
                {
                    return Error(node, Usage("two lists, each with the value of its items: `subsequence(sectors, it, map.order, id)`"));
                }

                var a = ListArg(args[0], name);
                var ea = a is null ? null : InItems(a, () => Value(args[1]));
                var b = ListArg(args[2], name);
                var eb = b is null ? null : InItems(b, () => Value(args[3]));
                if (ea is null || eb is null)
                {
                    return null;
                }

                return Comparable(ea.Type, eb.Type) ? new SubsequenceNode(a!, ea, b!, eb) : Error(node, $"`subsequence` cannot compare {ea.Type.Describe()} with {eb.Type.Describe()}");
            }

            default:
            {
                var suggestion = Resolver.Suggest(name, Functions);
                return Error(callee, $"unknown function `{name}`{(suggestion is null ? "" : $"; did you mean `{suggestion}`?")} (schema expressions have {string.Join(", ", Functions)})");
            }
        }
    }

    private SNode? ListArg(int node, string function)
    {
        var list = Value(node);
        if (list is null)
        {
            return null;
        }

        return list.Type.Kind is SKind.List or SKind.Any
            ? list
            : Error(node, $"`{function}` needs a list; `{TextOf(node)}` is {list.Type.Describe()}");
    }

    private SType ItemType(SType list)
    {
        if (list.Kind != SKind.List || list.Element is not { } el)
        {
            return SType.Any;
        }

        return SType.Of(el, _registry);
    }

    private SNode? Condition(int node)
    {
        var cond = Value(node);
        if (cond is null)
        {
            return null;
        }

        return cond.Type.Kind is SKind.Bool or SKind.Any ? cond : Error(node, $"a condition must be a boolean; `{TextOf(node)}` is {cond.Type.Describe()}");
    }

    /// <summary>Compiles a body with the items of <paramref name="list"/> as its scope.</summary>
    private SNode? InItems(SNode list, Func<SNode?> body)
    {
        var saved = (_scope, _scopeAny, _it, _inAggregate);
        var item = ItemType(list.Type);
        _it = item;
        _inAggregate = true;
        if (item.Kind == SKind.Any)
        {
            _scope = null;
            _scopeAny = true;
        }
        else if (item.Kind == SKind.Record && !item.IsMap)
        {
            _scope = item.Record;
            _scopeAny = item.Record is null || _registry.Permissive;
        }
        else
        {
            _scope = null;
            _scopeAny = false;
        }

        try
        {
            return body();
        }
        finally
        {
            (_scope, _scopeAny, _it, _inAggregate) = saved;
        }
    }
}
