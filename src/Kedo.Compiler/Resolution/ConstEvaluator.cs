using Kedo.Compiler.Model;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

namespace Kedo.Compiler.Resolution;

/// <summary>
/// Evaluates parenthesised expressions found in declaration bodies. Only constants are allowed there (KEDO-01 §7):
/// integer and duration arithmetic, comparisons, boolean logic, string literals and references. Variables, member
/// access and calls are errors (K5001); mixed kinds are errors (K5003).
/// </summary>
internal static class ConstEvaluator
{
    public static Value? Evaluate(ExprValue expr, DiagnosticBag diagnostics)
    {
        var tree = expr.Tree;
        var node = expr.Node;
        // Unwrap the ParenExpr itself.
        if (tree[node].Kind == NodeKind.ParenExpr && tree.Children(node).Length == 1)
        {
            node = tree.Children(node)[0];
        }

        return Eval(tree, node, expr.Origin.File, diagnostics);
    }

    private static Value? Eval(SyntaxTree t, int node, string? file, DiagnosticBag d)
    {
        var n = t[node];
        var origin = Origin.Of(t, node);
        switch (n.Kind)
        {
            case NodeKind.ParenExpr:
                return t.Children(node).Length == 1 ? Eval(t, t.Children(node)[0], file, d) : null;
            case NodeKind.LiteralExpr:
            {
                var tk = t.OwnTokens(node)[0];
                var text = t.TextOf(tk);
                if (t.Tokens[tk].Kind == TokenKind.Decimal)
                {
                    d.Error("K5007", $"decimal `{text}` cannot be used in an expression; decimals are data in KEDO 1.x (write the value without parentheses)", file, t.SpanOf(node));
                    return null;
                }

                return t.Tokens[tk].Kind switch
                {
                    TokenKind.Integer => new IntValue(origin, long.Parse(text.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture)),
                    TokenKind.Duration => ParseDuration(origin, text),
                    TokenKind.String => new TextValue(origin, text.Length >= 2 ? text[1..^1] : text),
                    TokenKind.KwTrue => new BoolValue(origin, true),
                    TokenKind.KwFalse => new BoolValue(origin, false),
                    _ => null,
                };
            }

            case NodeKind.RefExpr:
            {
                var text = t.TextOf(t.OwnTokens(node)[0]);
                var colon = text.IndexOf(':');
                var rest = text[(colon + 1)..];
                var slash = rest.IndexOf('/');
                return slash < 0 ? new RefValue(origin, text[..colon], null, rest) : new RefValue(origin, text[..colon], rest[..slash], rest[(slash + 1)..]);
            }

            case NodeKind.NameExpr:
                d.Error("K5001", $"`{t.TextOfNode(node)}` is not a constant; only literals, references and arithmetic are allowed in declaration bodies", file, t.SpanOf(node));
                return null;
            case NodeKind.MemberExpr or NodeKind.CallExpr:
                d.Error("K5001", "member access and calls are not allowed in declaration bodies; use a hook or function for logic", file, t.SpanOf(node));
                return null;
            case NodeKind.UnaryExpr:
            {
                var opKind = t.Tokens[t.OwnTokens(node)[0]].Kind;
                var operand = Eval(t, t.Children(node)[0], file, d);
                if (operand is null)
                {
                    return null;
                }

                if (opKind == TokenKind.Minus)
                {
                    return operand switch
                    {
                        IntValue i => new IntValue(origin, -i.Number),
                        DurationValue du => new DurationValue(origin, -du.Amount, du.Unit),
                        _ => Mismatch(d, file, t, node, "`-` needs a number or duration"),
                    };
                }

                return operand is BoolValue b ? new BoolValue(origin, !b.Flag) : Mismatch(d, file, t, node, "`not` needs a boolean");
            }

            case NodeKind.BinaryExpr:
            {
                var opKind = t.Tokens[t.OwnTokens(node)[0]].Kind;
                var children = t.Children(node);
                var lhs = Eval(t, children[0], file, d);
                var rhs = children.Length > 1 ? Eval(t, children[1], file, d) : null;
                if (lhs is null || rhs is null)
                {
                    return null;
                }

                return Binary(origin, opKind, lhs, rhs) ?? Mismatch(d, file, t, node, "operands have incompatible kinds");
            }

            default:
                d.Error("K1005", "this expression cannot be evaluated at resolution time", file, t.SpanOf(node));
                return null;
        }
    }

    private static Value? Mismatch(DiagnosticBag d, string? file, SyntaxTree t, int node, string why)
    {
        d.Error("K5003", $"invalid expression: {why}", file, t.SpanOf(node));
        return null;
    }

    private static DurationValue ParseDuration(Origin origin, string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_')) i++;
        return new DurationValue(origin, long.Parse(text[..i].Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture), text[i..]);
    }

    private static Value? Binary(Origin o, TokenKind op, Value l, Value r)
    {
        switch (l, r)
        {
            case (IntValue a, IntValue b):
                return op switch
                {
                    TokenKind.Plus => new IntValue(o, a.Number + b.Number),
                    TokenKind.Minus => new IntValue(o, a.Number - b.Number),
                    TokenKind.Star => new IntValue(o, a.Number * b.Number),
                    TokenKind.Slash => b.Number == 0 ? null : new IntValue(o, a.Number / b.Number),
                    TokenKind.Percent => b.Number == 0 ? null : new IntValue(o, a.Number % b.Number),
                    TokenKind.EqualsEquals => new BoolValue(o, a.Number == b.Number),
                    TokenKind.BangEquals => new BoolValue(o, a.Number != b.Number),
                    TokenKind.Less => new BoolValue(o, a.Number < b.Number),
                    TokenKind.LessEquals => new BoolValue(o, a.Number <= b.Number),
                    TokenKind.Greater => new BoolValue(o, a.Number > b.Number),
                    TokenKind.GreaterEquals => new BoolValue(o, a.Number >= b.Number),
                    _ => null,
                };
            case (DurationValue a, DurationValue b):
                return op switch
                {
                    TokenKind.Plus => new DurationValue(o, a.Minutes + b.Minutes, "min"),
                    TokenKind.Minus => new DurationValue(o, a.Minutes - b.Minutes, "min"),
                    TokenKind.EqualsEquals => new BoolValue(o, a.Minutes == b.Minutes),
                    TokenKind.BangEquals => new BoolValue(o, a.Minutes != b.Minutes),
                    TokenKind.Less => new BoolValue(o, a.Minutes < b.Minutes),
                    TokenKind.LessEquals => new BoolValue(o, a.Minutes <= b.Minutes),
                    TokenKind.Greater => new BoolValue(o, a.Minutes > b.Minutes),
                    TokenKind.GreaterEquals => new BoolValue(o, a.Minutes >= b.Minutes),
                    _ => null,
                };
            case (DurationValue a, IntValue b) when op is TokenKind.Star or TokenKind.Slash:
                return op == TokenKind.Star ? new DurationValue(o, a.Minutes * b.Number, "min") : b.Number == 0 ? null : new DurationValue(o, a.Minutes / b.Number, "min");
            case (IntValue a, DurationValue b) when op == TokenKind.Star:
                return new DurationValue(o, a.Number * b.Minutes, "min");
            case (BoolValue a, BoolValue b):
                return op switch
                {
                    TokenKind.KwAnd => new BoolValue(o, a.Flag && b.Flag),
                    TokenKind.KwOr => new BoolValue(o, a.Flag || b.Flag),
                    TokenKind.EqualsEquals => new BoolValue(o, a.Flag == b.Flag),
                    TokenKind.BangEquals => new BoolValue(o, a.Flag != b.Flag),
                    _ => null,
                };
            case (TextValue a, TextValue b):
                return op switch
                {
                    TokenKind.Plus => new TextValue(o, a.Text + b.Text),
                    TokenKind.EqualsEquals => new BoolValue(o, a.Text == b.Text),
                    TokenKind.BangEquals => new BoolValue(o, a.Text != b.Text),
                    _ => null,
                };
            default:
                return null;
        }
    }
}
