using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

public class LexerTests
{
    private static (Token[] tokens, DiagnosticBag diagnostics) Lex(string text)
    {
        var bag = new DiagnosticBag();
        return (Lexer.Lex(SourceText.FromString(text), bag), bag);
    }

    private static TokenKind[] Significant(Token[] tokens)
        => tokens.Where(t => !t.IsTrivia).Select(t => t.Kind).ToArray();

    [Fact]
    public void LexesHeaderAndDeclaration()
    {
        var (tokens, bag) = Lex("kedo 1\nmodule patriark\n\nabstract actor sentinel_machine extends base with m1, m2 { name_key = \"x\" }\n");
        Assert.False(bag.HasErrors);
        Assert.Equal(new[]
        {
            TokenKind.KwKedo, TokenKind.Integer, TokenKind.KwModule, TokenKind.Identifier,
            TokenKind.KwAbstract, TokenKind.Identifier, TokenKind.Identifier, TokenKind.KwExtends, TokenKind.Identifier,
            TokenKind.KwWith, TokenKind.Identifier, TokenKind.Comma, TokenKind.Identifier,
            TokenKind.LBrace, TokenKind.Identifier, TokenKind.Equals, TokenKind.String, TokenKind.RBrace, TokenKind.EndOfFile,
        }, Significant(tokens));
    }

    [Fact]
    public void LexesReferencesDurationsAndOperators()
    {
        var (tokens, bag) = Lex("sheet = asset:sentinel_sheet  rope = item:core/rope  t = 30min  tags += [a]  x =! {}  y -= [b]  z = (a + b * 2 >= 3 and not c)");
        Assert.False(bag.HasErrors);
        var kinds = Significant(tokens);
        Assert.Contains(TokenKind.Reference, kinds);
        Assert.Contains(TokenKind.Duration, kinds);
        Assert.Contains(TokenKind.PlusEquals, kinds);
        Assert.Contains(TokenKind.EqualsBang, kinds);
        Assert.Contains(TokenKind.MinusEquals, kinds);
        Assert.Contains(TokenKind.GreaterEquals, kinds);
        Assert.Contains(TokenKind.KwAnd, kinds);
        Assert.Contains(TokenKind.KwNot, kinds);
        var refs = tokens.Where(t => t.Kind == TokenKind.Reference).ToArray();
        Assert.Equal(2, refs.Length);
    }

    [Fact]
    public void NewlineBeforeFlagMarksLineStarts()
    {
        var (tokens, _) = Lex("a = 1\n  b = 2 ; c = 3");
        var sig = tokens.Where(t => !t.IsTrivia).ToArray();
        Assert.True(sig[0].Flags.HasFlag(TokenFlags.NewlineBefore));   // a
        Assert.False(sig[1].Flags.HasFlag(TokenFlags.NewlineBefore));  // =
        Assert.True(sig[3].Flags.HasFlag(TokenFlags.NewlineBefore));   // b
        Assert.False(sig[7].Flags.HasFlag(TokenFlags.NewlineBefore));  // c (after ;)
    }

    [Fact]
    public void KeepsTriviaIncludingDocComments()
    {
        var (tokens, _) = Lex("/// doc\n// line\n/* block */ x = 1");
        Assert.Equal(TokenKind.DocComment, tokens[0].Kind);
        Assert.Equal(TokenKind.LineComment, tokens[2].Kind);
        Assert.Equal(TokenKind.BlockComment, tokens[4].Kind);
    }

    [Theory]
    [InlineData("Name = 1", "K1003")]
    [InlineData("x = \"open", "K1004")]
    [InlineData("x = \"bad \\q\"", "K1004")]
    [InlineData("x = 5kg", "K1002")]
    [InlineData("x = ! 1", "K1002")]
    [InlineData("x = ñ", "K1002")]
    public void ReportsLexicalErrorsWithoutThrowing(string text, string code)
    {
        var (tokens, bag) = Lex(text);
        Assert.True(bag.HasErrors);
        Assert.Contains(bag.Items, d => d.Code == code);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void TokensCoverTheWholeSource()
    {
        const string text = "kedo 1\nmodule m\n\nactor a { t = 10min  r = type:x.y/z  s = \"a\\n\" }\n";
        var (tokens, _) = Lex(text);
        var pos = 0;
        foreach (var t in tokens)
        {
            Assert.Equal(pos, t.Start);
            pos += t.Length;
        }

        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), pos);
    }
}
