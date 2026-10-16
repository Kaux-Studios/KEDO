using Kedo.Compiler.Syntax;
using Kedo.Text;

namespace Kedo.Compiler.Model;

/// <summary>Where something was authored: file, byte span and one-based line/column of the start.</summary>
public readonly record struct Origin(string? File, TextSpan Span, int Line, int Column)
{
    /// <summary>Origin of a syntax node.</summary>
    public static Origin Of(SyntaxTree tree, int node)
    {
        var span = tree.SpanOf(node);
        var pos = tree.Source.GetLinePosition(span.Start);
        return new Origin(tree.Source.Path, span, pos.Line, pos.Column);
    }

    /// <inheritdoc />
    public override string ToString() => $"{(File is null ? "<memory>" : Path.GetFileName(File))}:{Line}:{Column}";
}

/// <summary>Assignment operators of a field entry.</summary>
public enum FieldOp
{
    /// <summary><c>=</c>: scalar replace, record merge, list per strategy.</summary>
    Assign,
    /// <summary><c>=!</c>: replace whole record or list.</summary>
    Replace,
    /// <summary><c>+=</c>: add items.</summary>
    Add,
    /// <summary><c>-=</c>: remove items.</summary>
    Remove,
}

/// <summary>Authored value (before resolution). Immutable.</summary>
public abstract record Value(Origin Origin);

/// <summary>Integer literal.</summary>
public sealed record IntValue(Origin Origin, long Number) : Value(Origin);

/// <summary>Duration literal in its written unit.</summary>
public sealed record DurationValue(Origin Origin, long Amount, string Unit) : Value(Origin)
{
    /// <summary>Amount expressed in minutes (the default base unit).</summary>
    public long Minutes => Unit switch { "s" => Amount / 60, "h" => Amount * 60, "d" => Amount * 1440, _ => Amount };
}

/// <summary>Fixed-point decimal literal: <see cref="Units"/> / 10^<see cref="Scale"/> (ADR-0008).</summary>
public sealed record DecimalValue(Origin Origin, long Units, int Scale) : Value(Origin);

/// <summary>String literal (unescaped).</summary>
public sealed record TextValue(Origin Origin, string Text) : Value(Origin);

/// <summary>Boolean literal.</summary>
public sealed record BoolValue(Origin Origin, bool Flag) : Value(Origin);

/// <summary>Bare identifier in a body: an enumeration value.</summary>
public sealed record EnumValue(Origin Origin, string Name) : Value(Origin);

/// <summary><c>type:id</c> or <c>type:module/id</c>.</summary>
public sealed record RefValue(Origin Origin, string Type, string? Module, string Id) : Value(Origin)
{
    /// <summary>Text as written.</summary>
    public override string ToString() => Module is null ? $"{Type}:{Id}" : $"{Type}:{Module}/{Id}";
}

/// <summary><c>[ ... ]</c>.</summary>
public sealed record ListValue(Origin Origin, IReadOnlyList<Value> Items) : Value(Origin);

/// <summary><c>{ ... }</c> used as a value.</summary>
public sealed record RecordValue(Origin Origin, IReadOnlyList<Entry> Entries) : Value(Origin);

/// <summary>Parenthesised expression kept as syntax; evaluated at resolution (constants only).</summary>
public sealed record ExprValue(Origin Origin, SyntaxTree Tree, int Node) : Value(Origin);

/// <summary>Entry of a body.</summary>
public abstract record Entry(Origin Origin);

/// <summary><c>name op value</c>.</summary>
public sealed record FieldEntry(Origin Origin, string Name, FieldOp Op, Value Value) : Entry(Origin);

/// <summary><c>name[key] { ... }</c>.</summary>
public sealed record KeyedEntry(Origin Origin, string Name, string Key, RecordValue Body) : Entry(Origin);

/// <summary><c>component type { ... }</c>.</summary>
public sealed record ComponentEntry(Origin Origin, string ComponentType, RecordValue Body) : Entry(Origin);

/// <summary><c>remove component type</c> (<see cref="ComponentType"/> set) or <c>remove a.b</c> (<see cref="Path"/> set).</summary>
public sealed record RemovalEntry(Origin Origin, string? ComponentType, IReadOnlyList<string>? Path) : Entry(Origin);

/// <summary><c>on hook(ctx) { ... }</c>; the block stays as syntax until the behaviour compiler (#6).</summary>
public sealed record HookEntry(Origin Origin, string HookId, string ContextName, SyntaxTree Tree, int BlockNode) : Entry(Origin);

/// <summary><c>function name(params) -> type { ... }</c>; the block stays as syntax until #6.</summary>
public sealed record FunctionEntry(Origin Origin, string Name, IReadOnlyList<(string Name, string Type)> Parameters, string? ReturnType, SyntaxTree Tree, int BlockNode) : Entry(Origin);

/// <summary><c>@name(args)</c>.</summary>
public sealed record Annotation(Origin Origin, string Name, IReadOnlyList<(string? Name, Value Value)> Arguments);

/// <summary>An object declaration: <c>[abstract] type id [extends p] [with m...] { ... }</c>.</summary>
public sealed record ObjectDeclaration(
    Origin Origin,
    string Module,
    string Type,
    string Id,
    bool IsAbstract,
    string? Parent,
    IReadOnlyList<string> Mixins,
    IReadOnlyList<Entry> Entries,
    IReadOnlyList<Annotation> Annotations,
    string? Doc)
{
    /// <summary><c>type:id</c>.</summary>
    public string Key => $"{Type}:{Id}";
}

/// <summary>A mixin declaration.</summary>
public sealed record MixinDeclaration(
    Origin Origin,
    string Module,
    string Id,
    IReadOnlyList<string> Mixins,
    IReadOnlyList<Entry> Entries,
    IReadOnlyList<Annotation> Annotations,
    string? Doc);

/// <summary>Declarations of one file.</summary>
public sealed record FileDeclarations(
    string? Path,
    string Module,
    IReadOnlyList<ObjectDeclaration> Objects,
    IReadOnlyList<MixinDeclaration> Mixins,
    IReadOnlyList<FunctionEntry> Functions,
    RecordValue? Manifest);
