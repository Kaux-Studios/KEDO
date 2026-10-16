using Kedo.Diagnostics;
using Kedo.Text;

namespace Kedo.Compiler.Syntax;

/// <summary>
/// Hand-written recursive-descent parser with a Pratt expression parser (ADR-0007 D2). One token of lookahead
/// (two in a few places). Resilient: always produces a tree; malformed regions become <see cref="NodeKind.Error"/>
/// nodes and diagnostics. Newlines matter only as statement terminators inside blocks and as list separators.
/// </summary>
public sealed class Parser
{
    private readonly SourceText _source;
    private readonly Token[] _tokens;
    private readonly int[] _sig;          // indices of significant tokens; last is EndOfFile
    private readonly DiagnosticBag _diagnostics;
    private readonly TreeBuilder _builder = new();
    private int _pos;

    private Parser(SourceText source, Token[] tokens, DiagnosticBag diagnostics)
    {
        _source = source;
        _tokens = tokens;
        _diagnostics = diagnostics;
        var sig = new List<int>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].IsTrivia)
            {
                sig.Add(i);
            }
        }

        _sig = sig.ToArray();
    }

    /// <summary>Lexes and parses a source.</summary>
    public static SyntaxTree Parse(SourceText source, DiagnosticBag diagnostics)
    {
        var tokens = Lexer.Lex(source, diagnostics);
        var parser = new Parser(source, tokens, diagnostics);
        parser.ParseFile();
        return parser._builder.Finish(source, tokens);
    }

    // ----- token helpers -----

    private TokenKind Peek(int k = 0)
    {
        var i = Math.Min(_pos + k, _sig.Length - 1);
        return _tokens[_sig[i]].Kind;
    }

    private Token Current => _tokens[_sig[Math.Min(_pos, _sig.Length - 1)]];

    private int CurrentIndex => _sig[Math.Min(_pos, _sig.Length - 1)];

    private bool At(TokenKind kind) => Peek() == kind;

    private bool AtEof => Peek() == TokenKind.EndOfFile;

    private bool AtLineStart => (Current.Flags & TokenFlags.NewlineBefore) != 0;

    private void Advance()
    {
        if (AtEof)
        {
            return;
        }

        _builder.Token(_sig[_pos]);
        _pos++;
    }

    private void Open(NodeKind kind) => _builder.Open(kind, CurrentIndex);

    private int Close(NodeKind? kind = null) => _builder.Close(kind);

    private string Describe(Token t) => t.Kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.Newline => "newline",
        TokenKind.Identifier => $"identifier `{_source.GetText(t.Span)}`",
        TokenKind.String => "string",
        TokenKind.Integer => "number",
        TokenKind.Duration => "duration",
        TokenKind.Reference => $"reference `{_source.GetText(t.Span)}`",
        _ => $"`{_source.GetText(t.Span)}`",
    };

    private void Error(string code, string message, TextSpan span)
        => _diagnostics.Error(code, message, _source.Path, span);

    private void ErrorAtCurrent(string message)
        => Error("K1002", $"{message}, found {Describe(Current)}", Current.Span);

    private bool Expect(TokenKind kind, string what)
    {
        if (At(kind))
        {
            Advance();
            return true;
        }

        ErrorAtCurrent($"expected {what}");
        return false;
    }

    /// <summary>Consumes tokens into an Error node until <paramref name="stop"/> holds or EOF; always consumes at least one token unless at EOF.</summary>
    private void Recover(Func<bool> stop)
    {
        Open(NodeKind.Error);
        var consumed = 0;
        while (!AtEof && (consumed == 0 || !stop()))
        {
            Advance();
            consumed++;
        }

        Close();
    }

    private bool AtDeclarationStart()
    {
        if (!AtLineStart)
        {
            return false;
        }

        return Peek() switch
        {
            TokenKind.KwAbstract or TokenKind.KwMixin or TokenKind.KwFunction or TokenKind.KwManifest or TokenKind.At => true,
            TokenKind.Identifier => Peek(1) == TokenKind.Identifier,
            _ => false,
        };
    }

    // ----- file -----

    private void ParseFile()
    {
        Open(NodeKind.File);
        ParseHeader();
        while (!AtEof)
        {
            var before = _pos;
            ParseDeclaration();
            if (_pos == before)
            {
                Recover(AtDeclarationStart);
            }
        }

        Close();
    }

    private void ParseHeader()
    {
        Open(NodeKind.Header);
        if (At(TokenKind.KwKedo))
        {
            Advance();
            if (At(TokenKind.Integer))
            {
                var major = _source.GetText(Current.Span).Replace("_", "");
                if (major != "1")
                {
                    Error("K1001", $"language major `{major}` is not supported by this toolchain (language 1); run `kedo migrate`", Current.Span);
                }

                Advance();
            }
            else
            {
                ErrorAtCurrent("expected the language major after `kedo`");
            }
        }
        else
        {
            Error("K1002", $"expected `kedo 1` at the start of the file, found {Describe(Current)}", Current.Span);
        }

        if (At(TokenKind.KwModule))
        {
            Advance();
            Expect(TokenKind.Identifier, "the module name after `module`");
        }
        else
        {
            Error("K1002", $"expected `module <name>` after the `kedo` line, found {Describe(Current)}", Current.Span);
        }

        Close();
    }

    private void ParseDeclaration()
    {
        while (At(TokenKind.At))
        {
            ParseAnnotation();
        }

        switch (Peek())
        {
            case TokenKind.KwAbstract:
                Open(NodeKind.ObjectDecl);
                Advance();
                ParseObjectRest();
                break;
            case TokenKind.Identifier when Peek(1) == TokenKind.Identifier:
                Open(NodeKind.ObjectDecl);
                ParseObjectRest();
                break;
            case TokenKind.KwMixin:
                Open(NodeKind.MixinDecl);
                Advance();
                ParseName();
                if (At(TokenKind.KwWith))
                {
                    ParseWith();
                }

                ParseBody();
                Close();
                break;
            case TokenKind.KwManifest:
                Open(NodeKind.ManifestDecl);
                Advance();
                ParseBody();
                Close();
                break;
            case TokenKind.KwFunction:
                ParseFunction();
                break;
            default:
                ErrorAtCurrent("expected a declaration (`<type> <name> { ... }`, `abstract`, `mixin`, `function` or `manifest`)");
                Recover(AtDeclarationStart);
                break;
        }
    }

    private void ParseObjectRest()
    {
        Open(NodeKind.TypeName);
        Expect(TokenKind.Identifier, "an object type");
        Close();
        ParseName();
        if (At(TokenKind.KwExtends))
        {
            Open(NodeKind.ExtendsClause);
            Advance();
            ParseName();
            Close();
        }

        if (At(TokenKind.KwWith))
        {
            ParseWith();
        }

        ParseBody();
        Close();
    }

    private void ParseWith()
    {
        Open(NodeKind.WithClause);
        Advance();
        ParseName();
        while (At(TokenKind.Comma))
        {
            Advance();
            ParseName();
        }

        Close();
    }

    private void ParseName()
    {
        Open(NodeKind.Name);
        if (At(TokenKind.Identifier))
        {
            Advance();
            while (At(TokenKind.Dot) && Peek(1) == TokenKind.Identifier)
            {
                Advance();
                Advance();
            }
        }
        else if (Current.IsKeyword)
        {
            Error("K1002", $"`{_source.GetText(Current.Span)}` is a reserved word and cannot be used as a name", Current.Span);
            Advance();
        }
        else
        {
            ErrorAtCurrent("expected a name");
        }

        Close();
    }

    private void ParseAnnotation()
    {
        Open(NodeKind.Annotation);
        Advance();
        Expect(TokenKind.Identifier, "an annotation name after `@`");
        if (At(TokenKind.LParen))
        {
            Open(NodeKind.ArgList);
            Advance();
            while (!At(TokenKind.RParen) && !AtEof)
            {
                var before = _pos;
                if (At(TokenKind.Identifier) && Peek(1) == TokenKind.Equals)
                {
                    Open(NodeKind.Field);
                    Advance();
                    Advance();
                    ParseValue();
                    Close();
                }
                else
                {
                    ParseValue();
                }

                if (At(TokenKind.Comma))
                {
                    Advance();
                }

                if (_pos == before)
                {
                    Recover(() => At(TokenKind.RParen) || AtLineStart);
                }
            }

            Expect(TokenKind.RParen, "`)` to close the annotation");
            Close();
        }

        Close();
    }

    // ----- bodies -----

    private void ParseBody()
    {
        Open(NodeKind.Body);
        if (!Expect(TokenKind.LBrace, "`{`"))
        {
            Close();
            return;
        }

        while (!At(TokenKind.RBrace) && !AtEof)
        {
            var before = _pos;
            ParseEntry();
            if (_pos == before)
            {
                Recover(() => At(TokenKind.RBrace) || AtLineStart);
            }
        }

        Expect(TokenKind.RBrace, "`}` to close the block");
        Close();
    }

    private void ParseEntry()
    {
        switch (Peek())
        {
            case TokenKind.Semicolon:
                Advance();
                return;
            case TokenKind.KwComponent:
                Open(NodeKind.Component);
                Advance();
                Expect(TokenKind.Identifier, "a component type after `component`");
                ParseBody();
                Close();
                return;
            case TokenKind.KwRemove:
                Open(NodeKind.Removal);
                Advance();
                if (At(TokenKind.KwComponent))
                {
                    Advance();
                    Expect(TokenKind.Identifier, "a component type after `remove component`");
                }
                else
                {
                    ParseName();
                }

                Close();
                return;
            case TokenKind.KwOn:
                ParseHook();
                return;
            case TokenKind.KwFunction:
                ParseFunction();
                return;
            case TokenKind.Identifier when Peek(1) == TokenKind.LBracket:
                Open(NodeKind.KeyedEntry);
                Advance();
                Advance();
                if (At(TokenKind.Identifier))
                {
                    ParseName();
                }
                else if (At(TokenKind.Integer) || At(TokenKind.String) || At(TokenKind.Reference))
                {
                    Open(NodeKind.LiteralExpr);
                    Advance();
                    Close();
                }
                else
                {
                    ErrorAtCurrent("expected a key inside `[...]`");
                }

                Expect(TokenKind.RBracket, "`]` after the key");
                ParseBody();
                Close();
                return;
            case TokenKind.Identifier:
                Open(NodeKind.Field);
                Advance();
                if (At(TokenKind.Equals) || At(TokenKind.EqualsBang) || At(TokenKind.PlusEquals) || At(TokenKind.MinusEquals))
                {
                    Advance();
                    ParseValue();
                }
                else
                {
                    ErrorAtCurrent("expected `=`, `=!`, `+=` or `-=` after the field name");
                }

                Close();
                return;
            default:
                ErrorAtCurrent("expected a field, `component`, `remove`, `on` or `function`");
                return;
        }
    }

    // ----- values -----

    private void ParseValue()
    {
        switch (Peek())
        {
            case TokenKind.Integer or TokenKind.Duration or TokenKind.String or TokenKind.KwTrue or TokenKind.KwFalse:
                Open(NodeKind.LiteralExpr);
                Advance();
                Close();
                return;
            case TokenKind.Identifier:
                Open(NodeKind.NameExpr);
                Advance();
                Close();
                return;
            case TokenKind.Reference:
                Open(NodeKind.RefExpr);
                Advance();
                Close();
                return;
            case TokenKind.LBracket:
                ParseList();
                return;
            case TokenKind.LBrace:
                ParseBody();
                return;
            case TokenKind.LParen:
                Open(NodeKind.ParenExpr);
                Advance();
                ParseExpression(0);
                Expect(TokenKind.RParen, "`)` to close the expression");
                Close();
                return;
            case TokenKind.Minus when Peek(1) is TokenKind.Integer or TokenKind.Duration:
                // Negative literal: `-` immediately followed by a number is one LiteralExpr (`mood_shift = -1`).
                Open(NodeKind.LiteralExpr);
                Advance();
                Advance();
                Close();
                return;
            case TokenKind.Minus:
                Error("K1005", "a compound expression must be enclosed in parentheses: `(-x)`", Current.Span);
                Open(NodeKind.ParenExpr);
                ParseExpression(0);
                Close();
                return;
            default:
                if (Current.IsKeyword)
                {
                    // A reserved word in value position is an enumeration value (`strategy = set`, `kind = option`):
                    // no value can start with a keyword, so this is unambiguous (KEDO-01 §6).
                    Open(NodeKind.NameExpr);
                    Advance();
                    Close();
                    return;
                }

                ErrorAtCurrent("expected a value");
                return;
        }
    }

    private void ParseList()
    {
        Open(NodeKind.ListExpr);
        Advance();
        while (!At(TokenKind.RBracket) && !AtEof)
        {
            if (At(TokenKind.RBrace) || At(TokenKind.RParen))
            {
                break;   // unclosed list: let the enclosing construct report its own closer
            }

            var before = _pos;
            ParseValue();
            if (At(TokenKind.Comma))
            {
                Advance();
            }
            else if (!At(TokenKind.RBracket) && !AtLineStart && !AtEof)
            {
                ErrorAtCurrent("expected `,` or a newline between list items");
            }

            if (_pos == before)
            {
                Recover(() => At(TokenKind.RBracket) || At(TokenKind.Comma) || AtLineStart);
            }
        }

        Expect(TokenKind.RBracket, "`]` to close the list");
        Close();
    }

    // ----- expressions (Pratt) -----

    private static (int left, int right) InfixPower(TokenKind kind) => kind switch
    {
        TokenKind.KwOr => (1, 2),
        TokenKind.KwAnd => (3, 4),
        TokenKind.EqualsEquals or TokenKind.BangEquals or TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals => (5, 6),
        TokenKind.Plus or TokenKind.Minus => (7, 8),
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => (9, 10),
        _ => (-1, -1),
    };

    private const int PrefixNotPower = 5;
    private const int PrefixMinusPower = 11;

    private void ParseExpression(int minPower)
    {
        ParseUnary();
        while (true)
        {
            var (left, right) = InfixPower(Peek());
            if (left < 0 || left < minPower)
            {
                return;
            }

            _builder.OpenBefore(NodeKind.BinaryExpr);
            Advance();
            ParseExpression(right);
            Close();
        }
    }

    private void ParseUnary()
    {
        if (At(TokenKind.KwNot))
        {
            Open(NodeKind.UnaryExpr);
            Advance();
            ParseExpression(PrefixNotPower);
            Close();
            return;
        }

        if (At(TokenKind.Minus))
        {
            Open(NodeKind.UnaryExpr);
            Advance();
            ParseExpression(PrefixMinusPower);
            Close();
            return;
        }

        ParsePostfix();
    }

    private void ParsePostfix()
    {
        ParsePrimary();
        while (true)
        {
            if (At(TokenKind.Dot) && Peek(1) == TokenKind.Identifier)
            {
                _builder.OpenBefore(NodeKind.MemberExpr);
                Advance();
                Advance();
                Close();
            }
            else if (At(TokenKind.LParen))
            {
                _builder.OpenBefore(NodeKind.CallExpr);
                ParseArgList();
                Close();
            }
            else
            {
                return;
            }
        }
    }

    private void ParsePrimary()
    {
        switch (Peek())
        {
            case TokenKind.Integer or TokenKind.Duration or TokenKind.String or TokenKind.KwTrue or TokenKind.KwFalse:
                Open(NodeKind.LiteralExpr);
                Advance();
                Close();
                return;
            case TokenKind.Identifier:
                Open(NodeKind.NameExpr);
                Advance();
                Close();
                return;
            case TokenKind.Reference:
                Open(NodeKind.RefExpr);
                Advance();
                Close();
                return;
            case TokenKind.LParen:
                Open(NodeKind.ParenExpr);
                Advance();
                ParseExpression(0);
                Expect(TokenKind.RParen, "`)` to close the expression");
                Close();
                return;
            case TokenKind.LBracket:
                ParseList();
                return;
            case TokenKind.LBrace:
                ParseBody();
                return;
            default:
                ErrorAtCurrent("expected an expression");
                Open(NodeKind.Error);
                Close();
                return;
        }
    }

    private void ParseArgList()
    {
        Open(NodeKind.ArgList);
        Expect(TokenKind.LParen, "`(`");
        while (!At(TokenKind.RParen) && !AtEof)
        {
            if (At(TokenKind.RBrace) || At(TokenKind.RBracket))
            {
                break;   // unclosed argument list: let the enclosing block report its own closer
            }

            var before = _pos;
            ParseExpression(0);
            if (At(TokenKind.Comma))
            {
                Advance();
            }
            else if (!At(TokenKind.RParen))
            {
                ErrorAtCurrent("expected `,` or `)` after the argument");
            }

            if (_pos == before)
            {
                Recover(() => At(TokenKind.RParen) || At(TokenKind.Comma) || AtLineStart);
            }
        }

        Expect(TokenKind.RParen, "`)` to close the arguments");
        Close();
    }

    // ----- hooks, functions, statements -----

    private void ParseHook()
    {
        Open(NodeKind.Hook);
        Advance();
        Expect(TokenKind.Identifier, "a hook name after `on`");
        Expect(TokenKind.LParen, "`(` after the hook name");
        Expect(TokenKind.Identifier, "the context parameter name");
        Expect(TokenKind.RParen, "`)` after the context parameter");
        ParseBlock();
        Close();
    }

    private void ParseFunction()
    {
        Open(NodeKind.FunctionDecl);
        Advance();
        ParseName();
        Open(NodeKind.ParamList);
        Expect(TokenKind.LParen, "`(` after the function name");
        while (!At(TokenKind.RParen) && !AtEof)
        {
            var before = _pos;
            Open(NodeKind.Param);
            Expect(TokenKind.Identifier, "a parameter name");
            Expect(TokenKind.Colon, "`:` and the parameter type");
            Expect(TokenKind.Identifier, "the parameter type");
            Close();
            if (At(TokenKind.Comma))
            {
                Advance();
            }

            if (_pos == before)
            {
                Recover(() => At(TokenKind.RParen) || At(TokenKind.Comma) || AtLineStart);
            }
        }

        Expect(TokenKind.RParen, "`)` to close the parameters");
        Close();
        if (At(TokenKind.Arrow))
        {
            Open(NodeKind.ReturnType);
            Advance();
            Expect(TokenKind.Identifier, "the return type after `->`");
            Close();
        }

        ParseBlock();
        Close();
    }

    private void ParseBlock()
    {
        Open(NodeKind.Block);
        if (!Expect(TokenKind.LBrace, "`{`"))
        {
            Close();
            return;
        }

        while (!At(TokenKind.RBrace) && !AtEof)
        {
            var before = _pos;
            ParseStatement();
            if (_pos == before)
            {
                Recover(() => At(TokenKind.RBrace) || AtLineStart);
                continue;
            }

            if (!At(TokenKind.RBrace) && !AtEof && !AtLineStart)
            {
                if (At(TokenKind.Semicolon))
                {
                    Advance();
                }
                else
                {
                    ErrorAtCurrent("expected a newline or `;` after the statement");
                    Recover(() => At(TokenKind.RBrace) || AtLineStart);
                }
            }
        }

        Expect(TokenKind.RBrace, "`}` to close the block");
        Close();
    }

    private void ParseStatement()
    {
        switch (Peek())
        {
            case TokenKind.Semicolon:
                Advance();
                return;
            case TokenKind.KwLet:
                Open(NodeKind.LetStmt);
                Advance();
                Expect(TokenKind.Identifier, "a variable name after `let`");
                Expect(TokenKind.Equals, "`=`");
                ParseExpression(0);
                Close();
                return;
            case TokenKind.KwSet:
                Open(NodeKind.SetStmt);
                Advance();
                ParseName();
                Expect(TokenKind.Equals, "`=`");
                ParseExpression(0);
                Close();
                return;
            case TokenKind.KwWait:
                ParseWait();
                return;
            case TokenKind.KwApply:
                Open(NodeKind.ApplyStmt);
                Advance();
                Expect(TokenKind.Reference, "an `effect:` reference after `apply`");
                ParseArgList();
                Close();
                return;
            case TokenKind.KwEmit:
                Open(NodeKind.EmitStmt);
                Advance();
                Expect(TokenKind.Reference, "a `signal:` reference after `emit`");
                ParseArgList();
                Close();
                return;
            case TokenKind.KwIf:
                ParseIf();
                return;
            case TokenKind.KwStop:
                Open(NodeKind.StopStmt);
                Advance();
                Close();
                return;
            case TokenKind.KwReturn:
                Open(NodeKind.ReturnStmt);
                Advance();
                ParseExpression(0);
                Close();
                return;
            case TokenKind.Identifier or TokenKind.Reference:
                Open(NodeKind.ExprStmt);
                ParseExpression(0);
                Close();
                return;
            default:
                ErrorAtCurrent("expected a statement (`let`, `set`, `wait`, `apply`, `emit`, `if`, `stop`, `return` or a call)");
                return;
        }
    }

    private void ParseWait()
    {
        switch (Peek(1))
        {
            case TokenKind.KwDecision:
                Open(NodeKind.WaitDecisionStmt);
                Advance();
                Advance();
                Expect(TokenKind.Reference, "a prompt reference after `wait decision`");
                Expect(TokenKind.LBrace, "`{` with the options");
                while (At(TokenKind.KwOption))
                {
                    Open(NodeKind.OptionClause);
                    Advance();
                    Expect(TokenKind.Identifier, "an option name");
                    ParseBlock();
                    Close();
                }

                Expect(TokenKind.RBrace, "`}` after the options");
                Close();
                return;
            case TokenKind.KwRoll:
                Open(NodeKind.WaitRollStmt);
                Advance();
                Advance();
                Expect(TokenKind.Identifier, "the roll stream name");
                ParseArgList();
                Expect(TokenKind.KwAs, "`as <name>` to bind the roll result");
                Expect(TokenKind.Identifier, "a name for the roll result");
                Close();
                return;
            case TokenKind.KwSignal:
                Open(NodeKind.WaitSignalStmt);
                Advance();
                Advance();
                Expect(TokenKind.Reference, "a `signal:` reference");
                if (At(TokenKind.LParen))
                {
                    ParseArgList();
                }

                if (At(TokenKind.KwAs))
                {
                    Advance();
                    Expect(TokenKind.Identifier, "a name for the signal payload");
                }

                Close();
                return;
            default:
                Open(NodeKind.WaitStmt);
                Advance();
                ParseExpression(0);
                Close();
                return;
        }
    }

    private void ParseIf()
    {
        Open(NodeKind.IfStmt);
        Advance();
        ParseExpression(0);
        ParseBlock();
        if (At(TokenKind.KwElse))
        {
            Open(NodeKind.ElseClause);
            Advance();
            if (At(TokenKind.KwIf))
            {
                ParseIf();
            }
            else
            {
                ParseBlock();
            }

            Close();
        }

        Close();
    }
}
