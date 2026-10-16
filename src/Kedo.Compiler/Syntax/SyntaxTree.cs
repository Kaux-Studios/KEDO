using Kedo.Text;

namespace Kedo.Compiler.Syntax;

/// <summary>Kinds of syntax nodes. Children are ordered as in the source.</summary>
public enum NodeKind : ushort
{
    /// <summary>Placeholder created during error recovery.</summary>
    Error = 0,
    /// <summary>Whole file: Header then declarations.</summary>
    File,
    /// <summary><c>kedo N</c> and <c>module name</c>.</summary>
    Header,
    /// <summary>Object declaration: [Annotation]* [abstract] Type Name [Extends] [With] Body.</summary>
    ObjectDecl,
    /// <summary><c>mixin Name [With] Body</c>.</summary>
    MixinDecl,
    /// <summary><c>asset Name Body</c>.</summary>
    AssetDecl,
    /// <summary><c>manifest Body</c>.</summary>
    ManifestDecl,
    /// <summary><c>function Name(Params) [-> Type] Block</c>.</summary>
    FunctionDecl,
    /// <summary><c>@name(args)</c>.</summary>
    Annotation,
    /// <summary>Type identifier of an object declaration.</summary>
    TypeName,
    /// <summary>Qualified identifier (<c>a.b.c</c>).</summary>
    Name,
    /// <summary><c>extends Name</c>.</summary>
    ExtendsClause,
    /// <summary><c>with Name, Name</c>.</summary>
    WithClause,
    /// <summary><c>{ entries }</c>; also used for record values.</summary>
    Body,
    /// <summary><c>name op value</c>; op is one of <c>= =! += -=</c>.</summary>
    Field,
    /// <summary><c>name[key] Body</c>.</summary>
    KeyedEntry,
    /// <summary><c>component name Body</c>.</summary>
    Component,
    /// <summary><c>remove component name</c> or <c>remove a.b</c>.</summary>
    Removal,
    /// <summary><c>on name(ctx) Block</c>.</summary>
    Hook,
    /// <summary><c>name: type</c>.</summary>
    Param,
    /// <summary>Parameter list of a function.</summary>
    ParamList,
    /// <summary>Return type of a function.</summary>
    ReturnType,
    /// <summary><c>{ statements }</c>.</summary>
    Block,
    /// <summary><c>let name = expr</c>.</summary>
    LetStmt,
    /// <summary><c>set target = expr</c>.</summary>
    SetStmt,
    /// <summary><c>wait expr</c>.</summary>
    WaitStmt,
    /// <summary><c>wait decision ref { options }</c>.</summary>
    WaitDecisionStmt,
    /// <summary><c>option name Block</c>.</summary>
    OptionClause,
    /// <summary><c>wait roll name(args) as name</c>.</summary>
    WaitRollStmt,
    /// <summary><c>wait signal ref [(args)] [as name]</c>.</summary>
    WaitSignalStmt,
    /// <summary><c>apply ref(args)</c>.</summary>
    ApplyStmt,
    /// <summary><c>emit ref(args)</c>.</summary>
    EmitStmt,
    /// <summary><c>if expr Block [else ...]</c>.</summary>
    IfStmt,
    /// <summary><c>else Block</c> or <c>else IfStmt</c>.</summary>
    ElseClause,
    /// <summary><c>stop</c>.</summary>
    StopStmt,
    /// <summary><c>return expr</c>.</summary>
    ReturnStmt,
    /// <summary>Expression used as a statement (a call).</summary>
    ExprStmt,
    /// <summary><c>lhs op rhs</c>.</summary>
    BinaryExpr,
    /// <summary><c>op operand</c> (<c>-</c>, <c>not</c>).</summary>
    UnaryExpr,
    /// <summary><c>callee(args)</c>.</summary>
    CallExpr,
    /// <summary><c>target.member</c>.</summary>
    MemberExpr,
    /// <summary>Arguments of a call.</summary>
    ArgList,
    /// <summary>Integer, duration, string or boolean literal.</summary>
    LiteralExpr,
    /// <summary>Bare identifier: enum value in bodies, variable in statements.</summary>
    NameExpr,
    /// <summary><c>type:id</c> reference.</summary>
    RefExpr,
    /// <summary><c>[ values ]</c>.</summary>
    ListExpr,
    /// <summary><c>( expr )</c>.</summary>
    ParenExpr,
}

/// <summary>A node of the flat syntax arena. Token indices address the full token array (including trivia).</summary>
public readonly record struct Node(NodeKind Kind, int FirstToken, int LastToken, int FirstChild, int ChildCount)
{
    /// <summary>True when the node has no children.</summary>
    public bool IsLeaf => ChildCount == 0;
}

/// <summary>
/// Immutable syntax tree of one file: tokens (with trivia), nodes in post-order (root last) and a flat child-index array.
/// Built by <see cref="Parser"/>. Consumers walk with <see cref="Children"/> and <see cref="Tokens"/>.
/// </summary>
public sealed class SyntaxTree
{
    private readonly Node[] _nodes;
    private readonly int[] _children;

    internal SyntaxTree(SourceText source, Token[] tokens, Node[] nodes, int[] children)
    {
        Source = source;
        Tokens = tokens;
        _nodes = nodes;
        _children = children;
    }

    /// <summary>Source the tree was parsed from.</summary>
    public SourceText Source { get; }

    /// <summary>All tokens, trivia included; last is <see cref="TokenKind.EndOfFile"/>.</summary>
    public Token[] Tokens { get; }

    /// <summary>Number of nodes.</summary>
    public int NodeCount => _nodes.Length;

    /// <summary>Index of the root (<see cref="NodeKind.File"/>) node.</summary>
    public int Root => _nodes.Length - 1;

    /// <summary>Node by index.</summary>
    public Node this[int index] => _nodes[index];

    /// <summary>Child node indices of a node, in source order.</summary>
    public ReadOnlySpan<int> Children(int node)
    {
        var n = _nodes[node];
        return _children.AsSpan(n.FirstChild, n.ChildCount);
    }

    /// <summary>First child whose kind matches, or -1.</summary>
    public int FirstChildOfKind(int node, NodeKind kind)
    {
        foreach (var child in Children(node))
        {
            if (_nodes[child].Kind == kind)
            {
                return child;
            }
        }

        return -1;
    }

    /// <summary>Tokens covered by a node, trivia included.</summary>
    public ReadOnlySpan<Token> TokensOf(int node)
    {
        var n = _nodes[node];
        return n.LastToken < n.FirstToken ? ReadOnlySpan<Token>.Empty : Tokens.AsSpan(n.FirstToken, n.LastToken - n.FirstToken + 1);
    }

    /// <summary>Significant (non-trivia) tokens covered by a node that are not covered by any child.</summary>
    public List<int> OwnTokens(int node)
    {
        var n = _nodes[node];
        var result = new List<int>();
        var children = Children(node);
        var c = 0;
        for (var t = n.FirstToken; t <= n.LastToken; t++)
        {
            while (c < children.Length && _nodes[children[c]].LastToken < t)
            {
                c++;
            }

            if (c < children.Length && _nodes[children[c]].FirstToken <= t && t <= _nodes[children[c]].LastToken)
            {
                t = _nodes[children[c]].LastToken;
                continue;
            }

            if (!Tokens[t].IsTrivia)
            {
                result.Add(t);
            }
        }

        return result;
    }

    /// <summary>Byte span of a node (first to last token, trivia included).</summary>
    public TextSpan SpanOf(int node)
    {
        var n = _nodes[node];
        if (n.LastToken < n.FirstToken)
        {
            return new TextSpan(Tokens[Math.Min(n.FirstToken, Tokens.Length - 1)].Start, 0);
        }

        var first = Tokens[n.FirstToken];
        var last = Tokens[n.LastToken];
        return new TextSpan(first.Start, last.End - first.Start);
    }

    /// <summary>Source text of a token.</summary>
    public string TextOf(int token) => Source.GetText(Tokens[token].Span);

    /// <summary>Source text of a node, trivia included.</summary>
    public string TextOfNode(int node) => Source.GetText(SpanOf(node));
}
