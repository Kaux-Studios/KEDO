using System.Text;

namespace Kedo.Compiler.Syntax;

/// <summary>Human-readable, line-oriented dump of a syntax tree used by `kedo parse` and the golden corpus.</summary>
public static class SyntaxDump
{
    /// <summary>Writes one line per node: indentation, kind, and the node's own significant tokens in backticks.</summary>
    public static string Dump(SyntaxTree tree)
    {
        var sb = new StringBuilder();
        Write(tree, tree.Root, 0, sb);
        return sb.ToString();
    }

    private static void Write(SyntaxTree tree, int node, int depth, StringBuilder sb)
    {
        var n = tree[node];
        sb.Append(' ', depth * 2).Append(n.Kind);
        var own = tree.OwnTokens(node);
        if (own.Count > 0)
        {
            sb.Append(' ');
            for (var i = 0; i < own.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                var t = tree.Tokens[own[i]];
                sb.Append('`').Append(t.Kind == TokenKind.EndOfFile ? "<eof>" : tree.TextOf(own[i])).Append('`');
            }
        }

        sb.Append('\n');
        foreach (var child in tree.Children(node))
        {
            Write(tree, child, depth + 1, sb);
        }
    }
}
