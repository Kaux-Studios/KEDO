using System.Buffers;
using Kedo.Diagnostics;
using Kedo.Text;

namespace Kedo.Compiler.Syntax;

/// <summary>
/// Single-pass lexer over UTF-8 bytes. Produces every token including trivia; never throws on bad input
/// (malformed tokens are flagged and reported as diagnostics). Identifier classification into keywords
/// happens here so the parser works with one token of lookahead.
/// </summary>
public static class Lexer
{
    private static readonly SearchValues<byte> IdentStart = SearchValues.Create("abcdefghijklmnopqrstuvwxyz_"u8);
    private static readonly SearchValues<byte> IdentContinue = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_"u8);
    private static readonly SearchValues<byte> RefContinue = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_./"u8);
    private static readonly SearchValues<byte> Digits = SearchValues.Create("0123456789_"u8);
    private static readonly SearchValues<byte> UpperLetters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ"u8);

    /// <summary>Lexes a whole source. The last token is always <see cref="TokenKind.EndOfFile"/>.</summary>
    public static Token[] Lex(SourceText source, DiagnosticBag diagnostics)
    {
        var text = source.Bytes.AsSpan();
        var tokens = new List<Token>(Math.Max(16, text.Length / 4));
        var pos = 0;
        var newlineBefore = true;

        while (pos < text.Length)
        {
            var b = text[pos];
            var start = pos;
            TokenKind kind;
            var flags = TokenFlags.None;

            switch (b)
            {
                case (byte)' ' or (byte)'\t':
                    while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t')) pos++;
                    kind = TokenKind.Whitespace;
                    break;
                case (byte)'\n':
                    pos++;
                    kind = TokenKind.Newline;
                    newlineBefore = true;
                    break;
                case (byte)'\r':
                    pos++;
                    if (pos < text.Length && text[pos] == '\n') pos++;
                    kind = TokenKind.Newline;
                    newlineBefore = true;
                    break;
                case (byte)'/' when pos + 1 < text.Length && text[pos + 1] == '/':
                    kind = pos + 2 < text.Length && text[pos + 2] == '/' && (pos + 3 >= text.Length || text[pos + 3] != '/')
                        ? TokenKind.DocComment
                        : TokenKind.LineComment;
                    while (pos < text.Length && text[pos] != '\n' && text[pos] != '\r') pos++;
                    break;
                case (byte)'/' when pos + 1 < text.Length && text[pos + 1] == '*':
                    pos += 2;
                    kind = TokenKind.BlockComment;
                    var closed = false;
                    while (pos < text.Length)
                    {
                        if (text[pos] == '*' && pos + 1 < text.Length && text[pos + 1] == '/')
                        {
                            pos += 2;
                            closed = true;
                            break;
                        }

                        pos++;
                    }

                    if (!closed)
                    {
                        flags |= TokenFlags.Malformed;
                        diagnostics.Error("K1004", "unterminated block comment", source.Path, new TextSpan(start, pos - start));
                    }

                    break;
                case (byte)'"':
                    kind = TokenKind.String;
                    pos++;
                    var terminated = false;
                    while (pos < text.Length)
                    {
                        var c = text[pos];
                        if (c == '"')
                        {
                            pos++;
                            terminated = true;
                            break;
                        }

                        if (c == '\n' || c == '\r')
                        {
                            break;
                        }

                        if (c == '\\')
                        {
                            pos++;
                            if (pos < text.Length)
                            {
                                var e = text[pos];
                                if (e is (byte)'"' or (byte)'\\' or (byte)'n' or (byte)'t')
                                {
                                    pos++;
                                }
                                else if (e == 'u' && pos + 1 < text.Length && text[pos + 1] == '{')
                                {
                                    pos += 2;
                                    var hexStart = pos;
                                    while (pos < text.Length && IsHex(text[pos])) pos++;
                                    if (pos < text.Length && text[pos] == '}' && pos > hexStart && pos - hexStart <= 6)
                                    {
                                        pos++;
                                    }
                                    else
                                    {
                                        flags |= TokenFlags.Malformed;
                                        diagnostics.Error("K1004", "invalid `\\u{...}` escape in string", source.Path, new TextSpan(hexStart - 3, Math.Max(1, pos - hexStart + 3)));
                                    }
                                }
                                else
                                {
                                    flags |= TokenFlags.Malformed;
                                    diagnostics.Error("K1004", $"invalid escape `\\{(char)e}` in string", source.Path, new TextSpan(pos - 1, 2));
                                    pos++;
                                }
                            }

                            continue;
                        }

                        pos++;
                    }

                    if (!terminated)
                    {
                        flags |= TokenFlags.Malformed;
                        diagnostics.Error("K1004", "unterminated string", source.Path, new TextSpan(start, pos - start));
                    }

                    break;
                case (byte)'{': pos++; kind = TokenKind.LBrace; break;
                case (byte)'}': pos++; kind = TokenKind.RBrace; break;
                case (byte)'[': pos++; kind = TokenKind.LBracket; break;
                case (byte)']': pos++; kind = TokenKind.RBracket; break;
                case (byte)'(': pos++; kind = TokenKind.LParen; break;
                case (byte)')': pos++; kind = TokenKind.RParen; break;
                case (byte)',': pos++; kind = TokenKind.Comma; break;
                case (byte)';': pos++; kind = TokenKind.Semicolon; break;
                case (byte)':': pos++; kind = TokenKind.Colon; break;
                case (byte)'.': pos++; kind = TokenKind.Dot; break;
                case (byte)'@': pos++; kind = TokenKind.At; break;
                case (byte)'*': pos++; kind = TokenKind.Star; break;
                case (byte)'/': pos++; kind = TokenKind.Slash; break;
                case (byte)'%': pos++; kind = TokenKind.Percent; break;
                case (byte)'=':
                    pos++;
                    if (pos < text.Length && text[pos] == '=') { pos++; kind = TokenKind.EqualsEquals; }
                    else if (pos < text.Length && text[pos] == '!') { pos++; kind = TokenKind.EqualsBang; }
                    else kind = TokenKind.Equals;
                    break;
                case (byte)'!':
                    pos++;
                    if (pos < text.Length && text[pos] == '=') { pos++; kind = TokenKind.BangEquals; }
                    else { kind = TokenKind.Unknown; diagnostics.Error("K1002", "unexpected character `!`", source.Path, new TextSpan(start, 1)); }
                    break;
                case (byte)'<':
                    pos++;
                    if (pos < text.Length && text[pos] == '=') { pos++; kind = TokenKind.LessEquals; }
                    else kind = TokenKind.Less;
                    break;
                case (byte)'>':
                    pos++;
                    if (pos < text.Length && text[pos] == '=') { pos++; kind = TokenKind.GreaterEquals; }
                    else kind = TokenKind.Greater;
                    break;
                case (byte)'+':
                    pos++;
                    if (pos < text.Length && text[pos] == '=') { pos++; kind = TokenKind.PlusEquals; }
                    else kind = TokenKind.Plus;
                    break;
                case (byte)'-':
                    pos++;
                    if (pos < text.Length && text[pos] == '=') { pos++; kind = TokenKind.MinusEquals; }
                    else if (pos < text.Length && text[pos] == '>') { pos++; kind = TokenKind.Arrow; }
                    else kind = TokenKind.Minus;
                    break;
                default:
                    if (Digits.Contains(b) && b != '_')
                    {
                        while (pos < text.Length && Digits.Contains(text[pos])) pos++;
                        kind = TokenKind.Integer;

                        // `1.25` is a decimal (ADR-0008), except as a segment of a qualified id (`place.1.5`).
                        if (pos + 1 < text.Length && text[pos] == '.' && Digits.Contains(text[pos + 1]) && text[pos + 1] != '_' && (start == 0 || text[start - 1] != '.'))
                        {
                            pos++;
                            var fractionStart = pos;
                            while (pos < text.Length && Digits.Contains(text[pos])) pos++;
                            kind = TokenKind.Decimal;
                            var fractionDigits = 0;
                            for (var i = fractionStart; i < pos; i++)
                            {
                                if (text[i] != '_') fractionDigits++;
                            }

                            if (fractionDigits > 9)
                            {
                                flags |= TokenFlags.Malformed;
                                diagnostics.Error("K1006", $"decimal `{source.GetText(new TextSpan(start, pos - start))}` has {fractionDigits} fractional digits; the limit is 9", source.Path, new TextSpan(start, pos - start));
                            }
                        }

                        var unitStart = pos;
                        while (pos < text.Length && IdentContinue.Contains(text[pos])) pos++;
                        if (pos > unitStart)
                        {
                            var unit = text[unitStart..pos];
                            var isUnit = unit.SequenceEqual("s"u8) || unit.SequenceEqual("min"u8) || unit.SequenceEqual("h"u8) || unit.SequenceEqual("d"u8);
                            if (isUnit && kind == TokenKind.Decimal)
                            {
                                kind = TokenKind.Unknown;
                                flags |= TokenFlags.Malformed;
                                diagnostics.Error("K1002", $"durations are whole numbers: write `{source.GetText(new TextSpan(start, pos - start))}` in a smaller unit", source.Path, new TextSpan(start, pos - start));
                            }
                            else if (isUnit)
                            {
                                kind = TokenKind.Duration;
                            }
                            else
                            {
                                kind = TokenKind.Unknown;
                                flags |= TokenFlags.Malformed;
                                diagnostics.Error("K1002", $"invalid number suffix `{source.GetText(new TextSpan(unitStart, pos - unitStart))}`; durations use `s`, `min`, `h` or `d`", source.Path, new TextSpan(unitStart, pos - unitStart));
                            }
                        }
                    }
                    else if (IdentStart.Contains(b))
                    {
                        while (pos < text.Length && IdentContinue.Contains(text[pos])) pos++;
                        if (pos + 1 < text.Length && text[pos] == ':' && (IdentStart.Contains(text[pos + 1]) || Digits.Contains(text[pos + 1])))
                        {
                            pos++;
                            while (pos < text.Length && RefContinue.Contains(text[pos])) pos++;
                            kind = TokenKind.Reference;
                        }
                        else
                        {
                            kind = ClassifyWord(text[start..pos]);
                        }
                    }
                    else if (UpperLetters.Contains(b))
                    {
                        while (pos < text.Length && (IdentContinue.Contains(text[pos]) || UpperLetters.Contains(text[pos]))) pos++;
                        kind = TokenKind.Unknown;
                        flags |= TokenFlags.Malformed;
                        diagnostics.Error("K1003", $"identifier `{source.GetText(new TextSpan(start, pos - start))}` must be lowercase (`[a-z_][a-z0-9_]*`)", source.Path, new TextSpan(start, pos - start));
                    }
                    else
                    {
                        // Skip one UTF-8 scalar so a diagnostic points at the whole character.
                        pos++;
                        while (pos < text.Length && (text[pos] & 0xC0) == 0x80) pos++;
                        kind = TokenKind.Unknown;
                        diagnostics.Error("K1002", $"unexpected character `{source.GetText(new TextSpan(start, pos - start))}`", source.Path, new TextSpan(start, pos - start));
                    }

                    break;
            }

            if (newlineBefore && kind is not (TokenKind.Whitespace or TokenKind.Newline or TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment))
            {
                flags |= TokenFlags.NewlineBefore;
                newlineBefore = false;
            }

            tokens.Add(new Token(kind, flags, start, pos - start));
        }

        tokens.Add(new Token(TokenKind.EndOfFile, newlineBefore ? TokenFlags.NewlineBefore : TokenFlags.None, pos, 0));
        return tokens.ToArray();
    }

    private static bool IsHex(byte c) => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static TokenKind ClassifyWord(ReadOnlySpan<byte> word)
    {
        switch (word.Length)
        {
            case 2:
                if (word.SequenceEqual("on"u8)) return TokenKind.KwOn;
                if (word.SequenceEqual("if"u8)) return TokenKind.KwIf;
                if (word.SequenceEqual("as"u8)) return TokenKind.KwAs;
                if (word.SequenceEqual("or"u8)) return TokenKind.KwOr;
                break;
            case 3:
                if (word.SequenceEqual("let"u8)) return TokenKind.KwLet;
                if (word.SequenceEqual("set"u8)) return TokenKind.KwSet;
                if (word.SequenceEqual("and"u8)) return TokenKind.KwAnd;
                if (word.SequenceEqual("not"u8)) return TokenKind.KwNot;
                break;
            case 4:
                if (word.SequenceEqual("kedo"u8)) return TokenKind.KwKedo;
                if (word.SequenceEqual("with"u8)) return TokenKind.KwWith;
                if (word.SequenceEqual("else"u8)) return TokenKind.KwElse;
                if (word.SequenceEqual("wait"u8)) return TokenKind.KwWait;
                if (word.SequenceEqual("emit"u8)) return TokenKind.KwEmit;
                if (word.SequenceEqual("stop"u8)) return TokenKind.KwStop;
                if (word.SequenceEqual("roll"u8)) return TokenKind.KwRoll;
                if (word.SequenceEqual("true"u8)) return TokenKind.KwTrue;
                break;
            case 5:
                if (word.SequenceEqual("mixin"u8)) return TokenKind.KwMixin;
                if (word.SequenceEqual("apply"u8)) return TokenKind.KwApply;
                if (word.SequenceEqual("false"u8)) return TokenKind.KwFalse;
                break;
            case 6:
                if (word.SequenceEqual("module"u8)) return TokenKind.KwModule;
                if (word.SequenceEqual("remove"u8)) return TokenKind.KwRemove;
                if (word.SequenceEqual("return"u8)) return TokenKind.KwReturn;
                if (word.SequenceEqual("signal"u8)) return TokenKind.KwSignal;
                if (word.SequenceEqual("option"u8)) return TokenKind.KwOption;
                break;
            case 7:
                if (word.SequenceEqual("extends"u8)) return TokenKind.KwExtends;
                break;
            case 8:
                if (word.SequenceEqual("manifest"u8)) return TokenKind.KwManifest;
                if (word.SequenceEqual("abstract"u8)) return TokenKind.KwAbstract;
                if (word.SequenceEqual("function"u8)) return TokenKind.KwFunction;
                if (word.SequenceEqual("decision"u8)) return TokenKind.KwDecision;
                break;
            case 9:
                if (word.SequenceEqual("component"u8)) return TokenKind.KwComponent;
                break;
        }

        return TokenKind.Identifier;
    }
}
