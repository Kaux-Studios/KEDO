namespace Kedo.Compiler.Syntax;

/// <summary>
/// Builds the flat arena from parser events. Nodes are appended at close (post-order), so the root is last and
/// every child index is smaller than its parent's. Supports wrapping an already closed node (Pratt left recursion).
/// </summary>
internal sealed class TreeBuilder
{
    private readonly List<Node> _nodes = new();
    private readonly List<int> _children = new();
    private readonly List<Frame> _stack = new();

    private sealed class Frame
    {
        public NodeKind Kind;
        public int FirstToken;
        public int LastToken = -1;
        public readonly List<int> Children = new();
    }

    /// <summary>Number of open frames.</summary>
    public int Depth => _stack.Count;

    /// <summary>Opens a node whose first token will be <paramref name="firstToken"/>.</summary>
    public void Open(NodeKind kind, int firstToken)
    {
        _stack.Add(new Frame { Kind = kind, FirstToken = firstToken });
    }

    /// <summary>Opens a node that will contain the most recently closed node as its first child.</summary>
    public void OpenBefore(NodeKind kind)
    {
        var parent = _stack[^1];
        var lhs = parent.Children[^1];
        parent.Children.RemoveAt(parent.Children.Count - 1);
        var frame = new Frame { Kind = kind, FirstToken = _nodes[lhs].FirstToken, LastToken = _nodes[lhs].LastToken };
        frame.Children.Add(lhs);
        _stack.Add(frame);
    }

    /// <summary>Records that a token was consumed inside the innermost open node.</summary>
    public void Token(int tokenIndex)
    {
        var frame = _stack[^1];
        if (frame.LastToken < tokenIndex)
        {
            frame.LastToken = tokenIndex;
        }
    }

    /// <summary>Closes the innermost node, optionally changing its kind, and returns its index.</summary>
    public int Close(NodeKind? kind = null)
    {
        var frame = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        var firstChild = _children.Count;
        _children.AddRange(frame.Children);
        var lastToken = frame.LastToken;
        if (lastToken < frame.FirstToken && frame.Children.Count > 0)
        {
            lastToken = _nodes[frame.Children[^1]].LastToken;
        }

        var node = new Node(kind ?? frame.Kind, frame.FirstToken, lastToken, firstChild, frame.Children.Count);
        _nodes.Add(node);
        var index = _nodes.Count - 1;
        if (_stack.Count > 0)
        {
            var parent = _stack[^1];
            parent.Children.Add(index);
            if (parent.LastToken < lastToken)
            {
                parent.LastToken = lastToken;
            }
        }

        return index;
    }

    /// <summary>Materialises the tree. The stack must be empty.</summary>
    public SyntaxTree Finish(SourceText source, Token[] tokens)
    {
        if (_stack.Count != 0)
        {
            throw new InvalidOperationException($"{_stack.Count} node(s) left open");
        }

        return new SyntaxTree(source, tokens, _nodes.ToArray(), _children.ToArray());
    }
}
