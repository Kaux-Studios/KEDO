using System.Text;
using Kedo.Compiler.Formatting;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

namespace Kedo.Compiler.Editing;

/// <summary>Result of a source edit: the new canonical text, or an error that explains why nothing changed.</summary>
public sealed record EditResult(string? Text, string? Error)
{
    /// <summary>True when <see cref="Text"/> holds the edited source.</summary>
    public bool Ok => Text is not null;

    internal static EditResult Fail(string error) => new(null, error);
}

/// <summary>
/// Structural edits of a .kedo source for forms and tools (#14): they touch the syntax tree of one declaration and
/// reprint the file with the canonical formatter, so comments, order and the author's line breaks survive and a form
/// edit is the same diff a person would write. Values are KEDO literals (`12`, `"text"`, `asset:dunes`, `[a, b]`).
/// An edit never leaves new syntax errors: when the result does not parse cleanly the edit is refused.
/// </summary>
public static class SourceEditor
{
    /// <summary>Writes <c>path op value</c> into the object's own body (creating nested records as needed).</summary>
    /// <param name="source">Text of the .kedo file.</param>
    /// <param name="type">Object type.</param>
    /// <param name="id">Object id.</param>
    /// <param name="path">Field path, dot separated (`stats.hp`); map keys that are not identifiers are quoted.</param>
    /// <param name="value">Value literal.</param>
    /// <param name="op">`=`, `=!`, `+=` or `-=`.</param>
    public static EditResult SetField(string source, string type, string id, string path, string value, string op = "=")
    {
        if (op is not ("=" or "=!" or "+=" or "-="))
        {
            return EditResult.Fail($"unknown operator `{op}`; use =, =!, += or -=");
        }

        if (ValueError(value) is { } invalid)
        {
            return EditResult.Fail(invalid);
        }

        var segments = Segments(path);
        if (segments.Count == 0)
        {
            return EditResult.Fail("empty field path");
        }

        var (tree, obj, error) = Find(source, type, id);
        if (obj < 0)
        {
            return EditResult.Fail(error!);
        }

        var bytes = tree.Source.Bytes;
        var body = tree.FirstChildOfKind(obj, NodeKind.Body);
        for (var i = 0; i < segments.Count; i++)
        {
            var field = FieldNode(tree, body, segments[i]);
            if (i == segments.Count - 1)
            {
                var entry = $"{Key(segments[i])} {op} {value}";
                return field >= 0 ? Finish(Splice(bytes, tree.SpanOf(field), entry), source) : Finish(Insert(tree, body, entry), source);
            }

            var nested = field >= 0 ? tree.FirstChildOfKind(field, NodeKind.Body) : -1;
            if (nested < 0)
            {
                // Missing or not a record: write the rest of the path as nested records in one entry.
                var text = $"{Key(segments[^1])} {op} {value}";
                for (var j = segments.Count - 2; j > i; j--)
                {
                    text = $"{Key(segments[j])} = {{ {text} }}";
                }

                var entry = $"{Key(segments[i])} = {{ {text} }}";
                return field >= 0 ? Finish(Splice(bytes, tree.SpanOf(field), entry), source) : Finish(Insert(tree, body, entry), source);
            }

            body = nested;
        }

        return EditResult.Fail("unreachable");
    }

    /// <summary>Removes the entry of <paramref name="path"/> from the object's own body, so the value is inherited again.</summary>
    public static EditResult RemoveField(string source, string type, string id, string path)
    {
        var segments = Segments(path);
        var (tree, obj, error) = Find(source, type, id);
        if (obj < 0)
        {
            return EditResult.Fail(error!);
        }

        var body = tree.FirstChildOfKind(obj, NodeKind.Body);
        var field = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            field = FieldNode(tree, body, segments[i]);
            if (field < 0)
            {
                return EditResult.Fail($"`{type}:{id}` does not write `{path}` itself; nothing to remove");
            }

            if (i < segments.Count - 1)
            {
                body = tree.FirstChildOfKind(field, NodeKind.Body);
                if (body < 0)
                {
                    return EditResult.Fail($"`{string.Join(".", segments.Take(i + 1))}` is not a record");
                }
            }
        }

        var span = tree.SpanOf(field);
        // Take the whitespace before the entry with it, so no blank gap is left behind.
        var start = span.Start;
        var bytes = tree.Source.Bytes;
        while (start > 0 && bytes[start - 1] is (byte)' ' or (byte)'\t')
        {
            start--;
        }

        if (start > 0 && bytes[start - 1] == (byte)'\n' && span.End < bytes.Length && bytes[span.End] is (byte)'\r' or (byte)'\n')
        {
            start--;
            if (start > 0 && bytes[start - 1] == (byte)'\r')
            {
                start--;
            }
        }

        return Finish(Splice(bytes, new Kedo.Text.TextSpan(start, span.End - start), ""), source);
    }

    /// <summary>Appends <c>type id [extends parent] { }</c> at the end of the file.</summary>
    public static EditResult AddObject(string source, string type, string id, string? parent = null)
    {
        var (tree, existing, _) = Find(source, type, id);
        if (existing >= 0)
        {
            return EditResult.Fail($"`{type}:{id}` already exists in this file");
        }

        var text = source.TrimEnd() + $"\n\n{type} {id}{(parent is null ? "" : $" extends {parent}")} {{ }}\n";
        var result = Finish(text, source);
        return result.Ok && Find(result.Text!, type, id).Object < 0 ? EditResult.Fail($"`{type} {id}` is not a valid declaration") : result;
    }

    /// <summary>Copies the declaration of <c>type:id</c> under <paramref name="newId"/>, right after it.</summary>
    public static EditResult DuplicateObject(string source, string type, string id, string newId)
    {
        var (tree, obj, error) = Find(source, type, id);
        if (obj < 0)
        {
            return EditResult.Fail(error!);
        }

        if (Find(source, type, newId).Object >= 0)
        {
            return EditResult.Fail($"`{type}:{newId}` already exists in this file");
        }

        var name = tree.FirstChildOfKind(obj, NodeKind.Name);
        var declaration = tree.SpanOf(obj);
        var nameSpan = tree.SpanOf(name);
        var copy = tree.Source.GetText(new Kedo.Text.TextSpan(declaration.Start, nameSpan.Start - declaration.Start)) + newId
            + tree.Source.GetText(new Kedo.Text.TextSpan(nameSpan.End, declaration.End - nameSpan.End));
        var bytes = tree.Source.Bytes;
        var text = Encoding.UTF8.GetString(bytes, 0, declaration.End) + "\n\n" + copy + Encoding.UTF8.GetString(bytes, declaration.End, bytes.Length - declaration.End);
        return Finish(text, source);
    }

    // ----- helpers -----

    private static (SyntaxTree Tree, int Object, string? Error) Find(string source, string type, string id)
    {
        var tree = Parser.Parse(SourceText.FromString(source), new DiagnosticBag());
        foreach (var child in tree.Children(tree.Root))
        {
            if (tree[child].Kind != NodeKind.ObjectDecl)
            {
                continue;
            }

            var typeNode = tree.FirstChildOfKind(child, NodeKind.TypeName);
            var nameNode = tree.FirstChildOfKind(child, NodeKind.Name);
            if (typeNode >= 0 && nameNode >= 0 && Significant(tree, typeNode) == type && Significant(tree, nameNode) == id)
            {
                return (tree, child, null);
            }
        }

        return (tree, -1, $"no declaration of `{type}:{id}` in this file");
    }

    /// <summary>Text of a node without trivia.</summary>
    private static string Significant(SyntaxTree tree, int node)
    {
        var sb = new StringBuilder();
        var n = tree[node];
        for (var t = n.FirstToken; t <= n.LastToken; t++)
        {
            if (!tree.Tokens[t].IsTrivia)
            {
                sb.Append(tree.TextOf(t));
            }
        }

        return sb.ToString();
    }

    /// <summary>The Field entry of a body with that name, or -1.</summary>
    private static int FieldNode(SyntaxTree tree, int body, string name)
    {
        foreach (var child in tree.Children(body))
        {
            if (tree[child].Kind != NodeKind.Field)
            {
                continue;
            }

            var own = tree.OwnTokens(child);
            if (own.Count == 0)
            {
                continue;
            }

            var text = tree.TextOf(own[0]);
            var key = tree.Tokens[own[0]].Kind == TokenKind.String ? text.Trim('"') : text;
            if (key == name)
            {
                return child;
            }
        }

        return -1;
    }

    /// <summary>Inserts an entry before the closing brace of a body: on its own line in a multi-line body, inline otherwise.</summary>
    private static string Insert(SyntaxTree tree, int body, string entry)
    {
        var bytes = tree.Source.Bytes;
        var close = tree[body].LastToken;
        var brace = tree.Tokens[close];
        if (brace.Kind != TokenKind.RBrace)
        {
            return Splice(bytes, new Kedo.Text.TextSpan(brace.End, 0), " " + entry);
        }

        if ((brace.Flags & TokenFlags.NewlineBefore) != 0)
        {
            var lineStart = brace.Start;
            while (lineStart > 0 && bytes[lineStart - 1] != (byte)'\n')
            {
                lineStart--;
            }

            return Splice(bytes, new Kedo.Text.TextSpan(lineStart, 0), entry + "\n");
        }

        return Splice(bytes, new Kedo.Text.TextSpan(brace.Start, 0), entry + " ");
    }

    private static string Splice(byte[] bytes, Kedo.Text.TextSpan span, string replacement)
        => Encoding.UTF8.GetString(bytes, 0, span.Start) + replacement + Encoding.UTF8.GetString(bytes, span.End, bytes.Length - span.End);

    /// <summary>Formats the edited text; refuses it when it brings syntax errors the original did not have.</summary>
    private static EditResult Finish(string text, string original)
    {
        var before = new DiagnosticBag();
        Parser.Parse(SourceText.FromString(original), before);
        var after = new DiagnosticBag();
        var tree = Parser.Parse(SourceText.FromString(text), after);
        if (after.Items.Count(d => d.IsError) > before.Items.Count(d => d.IsError))
        {
            var first = after.Items.First(d => d.IsError);
            return EditResult.Fail($"the edit would not parse: {first.Code} {first.Message}");
        }

        return new EditResult(Formatter.Format(tree), null);
    }

    /// <summary>Why a value literal is not one, or null.</summary>
    private static string? ValueError(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "empty value";
        }

        var bag = new DiagnosticBag();
        var tree = Parser.Parse(SourceText.FromString($"kedo 1\nmodule m\nprobe p {{ v = {value}\n}}\n"), bag);
        if (bag.HasErrors)
        {
            return $"`{value}` is not a KEDO value: {bag.Items.First(d => d.IsError).Message}";
        }

        // Exactly one entry: `1  other = 2` would smuggle a second field in.
        var obj = tree.Children(tree.Root).ToArray().FirstOrDefault(c => tree[c].Kind == NodeKind.ObjectDecl);
        var body = tree.FirstChildOfKind(obj, NodeKind.Body);
        return tree.Children(body).Length == 1 ? null : $"`{value}` is more than one value";
    }

    private static List<string> Segments(string path)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in path)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == '.' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.Where(s => s.Length > 0).ToList();
    }

    /// <summary>A field or map key as written: identifiers bare, anything else quoted.</summary>
    private static string Key(string key)
        => key.Length > 0 && (char.IsAsciiLetterLower(key[0]) || key[0] == '_') && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            ? key
            : "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
