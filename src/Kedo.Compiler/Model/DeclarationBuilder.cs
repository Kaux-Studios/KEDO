using System.Globalization;
using System.Text;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Text;

namespace Kedo.Compiler.Model;

/// <summary>Turns a syntax tree into declarations. Syntax errors already reported by the parser are not repeated;
/// malformed nodes are skipped.</summary>
public static class DeclarationBuilder
{
    /// <summary>Builds the declarations of a file.</summary>
    public static FileDeclarations Build(SyntaxTree tree, DiagnosticBag diagnostics)
    {
        var b = new Builder(tree, diagnostics);
        return b.BuildFile();
    }

    private sealed class Builder
    {
        private readonly SyntaxTree _t;
        private readonly DiagnosticBag _d;
        private string _module = "";

        public Builder(SyntaxTree tree, DiagnosticBag diagnostics)
        {
            _t = tree;
            _d = diagnostics;
        }

        private string? File => _t.Source.Path;

        private Origin OriginOf(int node) => Origin.Of(_t, node);

        private string Text(int token) => _t.TextOf(token);

        public FileDeclarations BuildFile()
        {
            var objects = new List<ObjectDeclaration>();
            var mixins = new List<MixinDeclaration>();
            var functions = new List<FunctionEntry>();
            RecordValue? manifest = null;
            var pendingAnnotations = new List<Annotation>();

            foreach (var child in _t.Children(_t.Root))
            {
                var n = _t[child];
                switch (n.Kind)
                {
                    case NodeKind.Header:
                        var own = _t.OwnTokens(child);
                        var moduleIndex = own.FindIndex(tk => _t.Tokens[tk].Kind == TokenKind.KwModule);
                        if (moduleIndex >= 0 && moduleIndex + 1 < own.Count && _t.Tokens[own[moduleIndex + 1]].Kind == TokenKind.Identifier)
                        {
                            _module = Text(own[moduleIndex + 1]);
                        }

                        break;
                    case NodeKind.Annotation:
                        pendingAnnotations.Add(BuildAnnotation(child));
                        break;
                    case NodeKind.ObjectDecl:
                        var obj = BuildObject(child, pendingAnnotations.ToArray());
                        pendingAnnotations.Clear();
                        if (obj is not null)
                        {
                            objects.Add(obj);
                        }

                        break;
                    case NodeKind.MixinDecl:
                        var mixin = BuildMixin(child, pendingAnnotations.ToArray());
                        pendingAnnotations.Clear();
                        if (mixin is not null)
                        {
                            mixins.Add(mixin);
                        }

                        break;
                    case NodeKind.FunctionDecl:
                        pendingAnnotations.Clear();
                        var fn = BuildFunction(child);
                        if (fn is not null)
                        {
                            functions.Add(fn);
                        }

                        break;
                    case NodeKind.ManifestDecl:
                        pendingAnnotations.Clear();
                        var body = _t.FirstChildOfKind(child, NodeKind.Body);
                        if (body >= 0)
                        {
                            manifest = BuildRecord(body);
                        }

                        break;
                    default:
                        pendingAnnotations.Clear();
                        break;
                }
            }

            return new FileDeclarations(File, _module, objects, mixins, functions, manifest);
        }

        private string? DocBefore(int node)
        {
            var first = _t[node].FirstToken;
            var lines = new List<string>();
            for (var i = first - 1; i >= 0; i--)
            {
                var tk = _t.Tokens[i];
                if (tk.Kind == TokenKind.DocComment)
                {
                    lines.Add(Text(i)[3..].Trim());
                }
                else if (tk.Kind is TokenKind.Whitespace or TokenKind.Newline)
                {
                    continue;
                }
                else
                {
                    break;
                }
            }

            if (lines.Count == 0)
            {
                return null;
            }

            lines.Reverse();
            return string.Join("\n", lines);
        }

        private ObjectDeclaration? BuildObject(int node, Annotation[] annotations)
        {
            var own = _t.OwnTokens(node);
            var isAbstract = own.Count > 0 && _t.Tokens[own[0]].Kind == TokenKind.KwAbstract;
            var typeNode = _t.FirstChildOfKind(node, NodeKind.TypeName);
            var nameNode = _t.FirstChildOfKind(node, NodeKind.Name);
            var bodyNode = _t.FirstChildOfKind(node, NodeKind.Body);
            if (typeNode < 0 || nameNode < 0 || bodyNode < 0)
            {
                return null;
            }

            var typeTokens = _t.OwnTokens(typeNode);
            var id = NameText(nameNode);
            if (typeTokens.Count == 0 || id is null)
            {
                return null;
            }

            string? parent = null;
            var extendsNode = _t.FirstChildOfKind(node, NodeKind.ExtendsClause);
            if (extendsNode >= 0)
            {
                var pn = _t.FirstChildOfKind(extendsNode, NodeKind.Name);
                parent = pn >= 0 ? NameText(pn) : null;
            }

            var mixins = new List<string>();
            var withNode = _t.FirstChildOfKind(node, NodeKind.WithClause);
            if (withNode >= 0)
            {
                foreach (var c in _t.Children(withNode))
                {
                    var m = NameText(c);
                    if (m is not null)
                    {
                        mixins.Add(m);
                    }
                }
            }

            return new ObjectDeclaration(OriginOf(node), _module, Text(typeTokens[0]), id, isAbstract, parent, mixins, BuildEntries(bodyNode), annotations, DocBefore(node));
        }

        private MixinDeclaration? BuildMixin(int node, Annotation[] annotations)
        {
            var nameNode = _t.FirstChildOfKind(node, NodeKind.Name);
            var bodyNode = _t.FirstChildOfKind(node, NodeKind.Body);
            if (nameNode < 0 || bodyNode < 0)
            {
                return null;
            }

            var id = NameText(nameNode);
            if (id is null)
            {
                return null;
            }

            var mixins = new List<string>();
            var withNode = _t.FirstChildOfKind(node, NodeKind.WithClause);
            if (withNode >= 0)
            {
                foreach (var c in _t.Children(withNode))
                {
                    var m = NameText(c);
                    if (m is not null)
                    {
                        mixins.Add(m);
                    }
                }
            }

            return new MixinDeclaration(OriginOf(node), _module, id, mixins, BuildEntries(bodyNode), annotations, DocBefore(node));
        }

        private string? NameText(int nameNode)
        {
            var own = _t.OwnTokens(nameNode);
            if (own.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var tk in own)
            {
                sb.Append(Text(tk));
            }

            return sb.ToString();
        }

        private Annotation BuildAnnotation(int node)
        {
            var own = _t.OwnTokens(node);
            var name = own.Count > 1 ? Text(own[1]) : "";
            var args = new List<(string?, Value)>();
            var argList = _t.FirstChildOfKind(node, NodeKind.ArgList);
            if (argList >= 0)
            {
                foreach (var c in _t.Children(argList))
                {
                    if (_t[c].Kind == NodeKind.Field)
                    {
                        var fo = _t.OwnTokens(c);
                        var valueNode = _t.Children(c).Length > 0 ? _t.Children(c)[0] : -1;
                        if (valueNode >= 0)
                        {
                            args.Add((Text(fo[0]), BuildValue(valueNode)));
                        }
                    }
                    else
                    {
                        args.Add((null, BuildValue(c)));
                    }
                }
            }

            return new Annotation(OriginOf(node), name, args);
        }

        private List<Entry> BuildEntries(int bodyNode)
        {
            var entries = new List<Entry>();
            foreach (var c in _t.Children(bodyNode))
            {
                var e = BuildEntry(c);
                if (e is not null)
                {
                    entries.Add(e);
                }
            }

            return entries;
        }

        private Entry? BuildEntry(int node)
        {
            var n = _t[node];
            var own = _t.OwnTokens(node);
            var children = _t.Children(node);
            switch (n.Kind)
            {
                case NodeKind.Field:
                {
                    if (own.Count < 2 || children.Length == 0)
                    {
                        return null;
                    }

                    var op = _t.Tokens[own[1]].Kind switch
                    {
                        TokenKind.EqualsBang => FieldOp.Replace,
                        TokenKind.PlusEquals => FieldOp.Add,
                        TokenKind.MinusEquals => FieldOp.Remove,
                        _ => FieldOp.Assign,
                    };
                    return new FieldEntry(OriginOf(node), Text(own[0]), op, BuildValue(children[0]));
                }

                case NodeKind.KeyedEntry:
                {
                    if (own.Count == 0 || children.Length < 2)
                    {
                        return null;
                    }

                    var keyNode = children[0];
                    var key = _t[keyNode].Kind == NodeKind.Name ? NameText(keyNode) : StripQuotes(_t.TextOfNode(keyNode));
                    var body = _t.FirstChildOfKind(node, NodeKind.Body);
                    if (key is null || body < 0)
                    {
                        return null;
                    }

                    return new KeyedEntry(OriginOf(node), Text(own[0]), key, BuildRecord(body));
                }

                case NodeKind.Component:
                {
                    var body = _t.FirstChildOfKind(node, NodeKind.Body);
                    if (own.Count < 2 || body < 0)
                    {
                        return null;
                    }

                    return new ComponentEntry(OriginOf(node), Text(own[1]), BuildRecord(body));
                }

                case NodeKind.Removal:
                {
                    if (own.Count >= 3 && _t.Tokens[own[1]].Kind == TokenKind.KwComponent)
                    {
                        return new RemovalEntry(OriginOf(node), Text(own[2]), null);
                    }

                    var nameNode = _t.FirstChildOfKind(node, NodeKind.Name);
                    if (nameNode < 0)
                    {
                        return null;
                    }

                    var path = _t.OwnTokens(nameNode).Where(tk => _t.Tokens[tk].Kind == TokenKind.Identifier).Select(Text).ToArray();
                    return new RemovalEntry(OriginOf(node), null, path);
                }

                case NodeKind.Hook:
                {
                    var block = _t.FirstChildOfKind(node, NodeKind.Block);
                    var idents = own.Where(tk => _t.Tokens[tk].Kind == TokenKind.Identifier).Select(Text).ToArray();
                    if (block < 0 || idents.Length < 2)
                    {
                        return null;
                    }

                    return new HookEntry(OriginOf(node), idents[0], idents[1], _t, block);
                }

                case NodeKind.FunctionDecl:
                    return BuildFunction(node);
                default:
                    return null;
            }
        }

        private FunctionEntry? BuildFunction(int node)
        {
            var nameNode = _t.FirstChildOfKind(node, NodeKind.Name);
            var block = _t.FirstChildOfKind(node, NodeKind.Block);
            var name = nameNode >= 0 ? NameText(nameNode) : null;
            if (name is null || block < 0)
            {
                return null;
            }

            var parameters = new List<(string, string)>();
            var paramList = _t.FirstChildOfKind(node, NodeKind.ParamList);
            if (paramList >= 0)
            {
                foreach (var p in _t.Children(paramList))
                {
                    var idents = _t.OwnTokens(p).Where(tk => _t.Tokens[tk].Kind == TokenKind.Identifier).Select(Text).ToArray();
                    if (idents.Length == 2)
                    {
                        parameters.Add((idents[0], idents[1]));
                    }
                }
            }

            string? returnType = null;
            var rt = _t.FirstChildOfKind(node, NodeKind.ReturnType);
            if (rt >= 0)
            {
                returnType = _t.OwnTokens(rt).Where(tk => _t.Tokens[tk].Kind == TokenKind.Identifier).Select(Text).FirstOrDefault();
            }

            return new FunctionEntry(OriginOf(node), name, parameters, returnType, _t, block);
        }

        private RecordValue BuildRecord(int bodyNode) => new(OriginOf(bodyNode), BuildEntries(bodyNode));

        private Value BuildValue(int node)
        {
            var n = _t[node];
            var origin = OriginOf(node);
            switch (n.Kind)
            {
                case NodeKind.LiteralExpr:
                {
                    var own = _t.OwnTokens(node);
                    var negative = own.Count == 2 && _t.Tokens[own[0]].Kind == TokenKind.Minus;
                    var tk = negative ? own[1] : own[0];
                    var token = _t.Tokens[tk];
                    var text = Text(tk);
                    if (negative)
                    {
                        return token.Kind == TokenKind.Duration
                            ? ParseDuration(origin, text) is var d ? new DurationValue(origin, -d.Amount, d.Unit) : throw new InvalidOperationException()
                            : new IntValue(origin, -ParseInt(text, token.Span));
                    }

                    return token.Kind switch
                    {
                        TokenKind.Integer => new IntValue(origin, ParseInt(text, token.Span)),
                        TokenKind.Duration => ParseDuration(origin, text),
                        TokenKind.String => new TextValue(origin, Unescape(text)),
                        TokenKind.KwTrue => new BoolValue(origin, true),
                        TokenKind.KwFalse => new BoolValue(origin, false),
                        _ => new TextValue(origin, text),
                    };
                }

                case NodeKind.NameExpr:
                    return new EnumValue(origin, Text(_t.OwnTokens(node)[0]));
                case NodeKind.RefExpr:
                    return ParseRef(origin, Text(_t.OwnTokens(node)[0]));
                case NodeKind.ListExpr:
                {
                    var items = new List<Value>();
                    foreach (var c in _t.Children(node))
                    {
                        items.Add(BuildValue(c));
                    }

                    return new ListValue(origin, items);
                }

                case NodeKind.Body:
                    return BuildRecord(node);
                case NodeKind.ParenExpr:
                {
                    // A parenthesised literal is just the literal; anything else stays an expression.
                    var inner = _t.Children(node).Length == 1 ? _t.Children(node)[0] : -1;
                    if (inner >= 0 && _t[inner].Kind is NodeKind.LiteralExpr or NodeKind.RefExpr or NodeKind.NameExpr)
                    {
                        return BuildValue(inner);
                    }

                    return new ExprValue(origin, _t, node);
                }

                default:
                    return new ExprValue(origin, _t, node);
            }
        }

        private long ParseInt(string text, TextSpan span)
        {
            if (long.TryParse(text.Replace("_", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            _d.Error("K4001", $"integer `{text}` is out of range", File, span);
            return 0;
        }

        private DurationValue ParseDuration(Origin origin, string text)
        {
            var i = 0;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_')) i++;
            var amount = ParseInt(text[..i], origin.Span);
            return new DurationValue(origin, amount, text[i..]);
        }

        private static RefValue ParseRef(Origin origin, string text)
        {
            var colon = text.IndexOf(':');
            var type = text[..colon];
            var rest = text[(colon + 1)..];
            var slash = rest.IndexOf('/');
            return slash < 0 ? new RefValue(origin, type, null, rest) : new RefValue(origin, type, rest[..slash], rest[(slash + 1)..]);
        }

        private static string StripQuotes(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? Unescape(s) : s;

        private static string Unescape(string literal)
        {
            if (literal.Length < 2)
            {
                return literal;
            }

            var body = literal[1..];
            if (body.EndsWith('"'))
            {
                body = body[..^1];
            }

            if (!body.Contains('\\'))
            {
                return body;
            }

            var sb = new StringBuilder(body.Length);
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c != '\\' || i + 1 >= body.Length)
                {
                    sb.Append(c);
                    continue;
                }

                i++;
                switch (body[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u' when i + 1 < body.Length && body[i + 1] == '{':
                    {
                        var close = body.IndexOf('}', i);
                        if (close > i + 2 && int.TryParse(body[(i + 2)..close], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                        {
                            sb.Append(char.ConvertFromUtf32(cp));
                            i = close;
                        }

                        break;
                    }

                    default: sb.Append(body[i]); break;
                }
            }

            return sb.ToString();
        }
    }
}
