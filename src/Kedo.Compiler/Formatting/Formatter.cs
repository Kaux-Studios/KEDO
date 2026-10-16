using System.Text;
using Kedo.Compiler.Syntax;

namespace Kedo.Compiler.Formatting;

/// <summary>
/// Canonical formatter without options (ADR-0007 D11). It rewrites only whitespace: every significant token and every
/// comment of the input appears in the output in the same order. The author's line breaks are respected (a token that
/// started a line keeps starting a line); blank lines collapse to at most one; indentation is four spaces per nesting
/// level; spacing between tokens follows fixed rules.
/// </summary>
public static class Formatter
{
    private const string Indent = "    ";

    /// <summary>Formats a parsed tree. Works on trees with syntax errors too (error regions keep their tokens).</summary>
    public static string Format(SyntaxTree tree)
    {
        var tokens = tree.Tokens;
        var depth = ComputeDepth(tree);
        var entryStart = new bool[tokens.Length];
        var unaryMinus = new bool[tokens.Length];
        MarkStructure(tree, entryStart, unaryMinus);

        var sb = new StringBuilder(tree.Source.Length + 64);
        var prevSig = -1;          // index of previous significant token
        var pendingNewlines = 0;    // newlines seen since the previous significant token or comment
        var atLineStart = true;     // nothing emitted on the current output line yet
        var afterComment = false;   // a comment was just emitted on the current line

        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case TokenKind.Whitespace:
                    continue;
                case TokenKind.Newline:
                    pendingNewlines++;
                    continue;
                case TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment:
                    if (atLineStart)
                    {
                        // first thing in the file or after a newline flush
                    }
                    else if (pendingNewlines > 0)
                    {
                        EmitNewlines(sb, pendingNewlines);
                        sb.Append(IndentFor(depth[i]));
                    }
                    else
                    {
                        sb.Append(afterComment ? " " : "  ");
                    }

                    if (atLineStart && sb.Length > 0 && pendingNewlines > 0)
                    {
                        EmitNewlines(sb, pendingNewlines);
                        sb.Append(IndentFor(depth[i]));
                    }
                    else if (atLineStart && sb.Length > 0)
                    {
                        sb.Append(IndentFor(depth[i]));
                    }

                    sb.Append(tree.TextOf(i).TrimEnd());
                    pendingNewlines = 0;
                    atLineStart = false;
                    afterComment = t.Kind != TokenKind.BlockComment;
                    continue;
                case TokenKind.EndOfFile:
                    break;
            }

            if (t.Kind == TokenKind.EndOfFile)
            {
                break;
            }

            // Separator before a significant token.
            if (prevSig < 0 && !afterComment && sb.Length == 0)
            {
                // first token of the file
            }
            else if (pendingNewlines > 0 || afterComment)
            {
                EmitNewlines(sb, Math.Max(1, pendingNewlines));
                sb.Append(IndentFor(depth[i]));
            }
            else if (prevSig >= 0)
            {
                sb.Append(Separator(tokens[prevSig].Kind, t.Kind, entryStart[i], unaryMinus[prevSig]));
            }

            sb.Append(tree.TextOf(i));
            prevSig = i;
            pendingNewlines = 0;
            atLineStart = false;
            afterComment = false;
        }

        // Exactly one trailing newline.
        var end = sb.Length;
        while (end > 0 && (sb[end - 1] == '\n' || sb[end - 1] == ' '))
        {
            end--;
        }

        sb.Length = end;
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static void EmitNewlines(StringBuilder sb, int count)
    {
        // Trim trailing spaces of the current line, then at most one blank line.
        var end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ')
        {
            end--;
        }

        sb.Length = end;
        sb.Append('\n');
        if (count >= 2 && sb.Length > 1)
        {
            sb.Append('\n');
        }
    }

    private static string IndentFor(int depth) => depth <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(Indent, depth));

    private static string Separator(TokenKind prev, TokenKind next, bool nextStartsEntry, bool prevIsUnaryMinus)
    {
        if (prevIsUnaryMinus)
        {
            return string.Empty;
        }

        switch (next)
        {
            case TokenKind.Comma or TokenKind.Semicolon or TokenKind.RParen or TokenKind.RBracket or TokenKind.Dot or TokenKind.Colon:
                return string.Empty;
            case TokenKind.LParen when prev is TokenKind.Identifier or TokenKind.Reference or TokenKind.RParen:
                return string.Empty;
            case TokenKind.LBracket when prev is TokenKind.Identifier:
                return string.Empty;     // keyed entry: options[risk]
        }

        switch (prev)
        {
            case TokenKind.LParen or TokenKind.LBracket or TokenKind.Dot or TokenKind.At:
                return string.Empty;
            case TokenKind.LBrace:
                return " ";
        }

        if (next == TokenKind.RBrace)
        {
            return " ";
        }

        return nextStartsEntry ? "  " : " ";
    }

    private static int[] ComputeDepth(SyntaxTree tree)
    {
        var diff = new int[tree.Tokens.Length + 1];
        for (var i = 0; i < tree.NodeCount; i++)
        {
            var n = tree[i];
            if (n.Kind is NodeKind.Body or NodeKind.Block or NodeKind.ListExpr or NodeKind.ArgList or NodeKind.ParamList or NodeKind.WaitDecisionStmt)
            {
                // Tokens strictly inside the delimiters are one level deeper; for WaitDecisionStmt the delimiters are the braces.
                var first = n.FirstToken;
                var last = n.LastToken;
                if (n.Kind == NodeKind.WaitDecisionStmt)
                {
                    first = FindToken(tree, n, TokenKind.LBrace);
                    if (first < 0)
                    {
                        continue;
                    }
                }

                if (last > first + 1)
                {
                    diff[first + 1]++;
                    diff[last]--;
                }
            }
        }

        var depth = new int[tree.Tokens.Length];
        var running = 0;
        for (var i = 0; i < depth.Length; i++)
        {
            running += diff[i];
            depth[i] = running;
        }

        return depth;
    }

    private static int FindToken(SyntaxTree tree, Node n, TokenKind kind)
    {
        for (var t = n.FirstToken; t <= n.LastToken; t++)
        {
            if (tree.Tokens[t].Kind == kind)
            {
                return t;
            }
        }

        return -1;
    }

    private static void MarkStructure(SyntaxTree tree, bool[] entryStart, bool[] unaryMinus)
    {
        for (var i = 0; i < tree.NodeCount; i++)
        {
            var n = tree[i];
            if (n.Kind == NodeKind.Body)
            {
                var first = true;
                foreach (var child in tree.Children(i))
                {
                    if (!first)
                    {
                        entryStart[tree[child].FirstToken] = true;
                    }

                    first = false;
                }
            }
            else if ((n.Kind == NodeKind.UnaryExpr || n.Kind == NodeKind.LiteralExpr) && tree.Tokens[n.FirstToken].Kind == TokenKind.Minus)
            {
                unaryMinus[n.FirstToken] = true;   // `-x` and negative literals `-1` print without a space
            }
        }
    }
}
