namespace Kedo.Compiler.Syntax;

/// <summary>
/// Builds the flat arena from parser events. Nodes are appended at close (post-order), so the root is last and
/// every child index is smaller than its parent's. Supports wrapping an already closed node (Pratt left recursion).
/// Open frames are structs whose children live in one shared stack (#16: no list allocated per node).
/// </summary>
internal sealed class TreeBuilder
{
    private readonly List<Node> _nodes;
    private readonly List<int> _children;
    private readonly List<int> _childStack = new(64);
    private Frame[] _stack = new Frame[32];
    private int _depth;

    private struct Frame
    {
        public NodeKind Kind;
        public int FirstToken;
        public int LastToken;
        public int ChildStart;   // index in _childStack of this frame's first child
    }

    /// <summary>Creates a builder; <paramref name="tokenCount"/> sizes the arena (about one node per two tokens).</summary>
    public TreeBuilder(int tokenCount = 0)
    {
        _nodes = new List<Node>(Math.Max(16, tokenCount / 2));
        _children = new List<int>(Math.Max(16, tokenCount / 2));
    }

    /// <summary>Number of open frames.</summary>
    public int Depth => _depth;

    /// <summary>Opens a node whose first token will be <paramref name="firstToken"/>.</summary>
    public void Open(NodeKind kind, int firstToken)
    {
        Push(new Frame { Kind = kind, FirstToken = firstToken, LastToken = -1, ChildStart = _childStack.Count });
    }

    /// <summary>Opens a node that will contain the most recently closed node as its first child.</summary>
    public void OpenBefore(NodeKind kind)
    {
        // The last child of the open parent is on top of the child stack; the new frame starts there, so it takes it.
        var lhs = _childStack[^1];
        Push(new Frame { Kind = kind, FirstToken = _nodes[lhs].FirstToken, LastToken = _nodes[lhs].LastToken, ChildStart = _childStack.Count - 1 });
    }

    /// <summary>Records that a token was consumed inside the innermost open node.</summary>
    public void Token(int tokenIndex)
    {
        ref var frame = ref _stack[_depth - 1];
        if (frame.LastToken < tokenIndex)
        {
            frame.LastToken = tokenIndex;
        }
    }

    /// <summary>Closes the innermost node, optionally changing its kind, and returns its index.</summary>
    public int Close(NodeKind? kind = null)
    {
        var frame = _stack[--_depth];
        var childCount = _childStack.Count - frame.ChildStart;
        var firstChild = _children.Count;
        for (var i = frame.ChildStart; i < _childStack.Count; i++)
        {
            _children.Add(_childStack[i]);
        }

        var lastToken = frame.LastToken;
        if (lastToken < frame.FirstToken && childCount > 0)
        {
            lastToken = _nodes[_childStack[^1]].LastToken;
        }

        _childStack.RemoveRange(frame.ChildStart, childCount);
        _nodes.Add(new Node(kind ?? frame.Kind, frame.FirstToken, lastToken, firstChild, childCount));
        var index = _nodes.Count - 1;
        if (_depth > 0)
        {
            _childStack.Add(index);
            ref var parent = ref _stack[_depth - 1];
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
        if (_depth != 0)
        {
            throw new InvalidOperationException($"{_depth} node(s) left open");
        }

        return new SyntaxTree(source, tokens, _nodes.ToArray(), _children.ToArray());
    }

    private void Push(Frame frame)
    {
        if (_depth == _stack.Length)
        {
            Array.Resize(ref _stack, _stack.Length * 2);
        }

        _stack[_depth++] = frame;
    }
}
