using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

public class ParserTests
{
    private static (SyntaxTree tree, DiagnosticBag bag) Parse(string text)
    {
        var bag = new DiagnosticBag();
        return (Parser.Parse(SourceText.FromString(text), bag), bag);
    }

    private static string Errors(DiagnosticBag bag) => string.Join("\n", bag.Items.Select(d => $"{d.Code} {d.Span} {d.Message}"));

    [Fact]
    public void ParsesTheSentinelExampleShape()
    {
        var (tree, bag) = Parse("""
            kedo 1
            module patriark

            /// Base.
            abstract actor sentinel_machine {
                name_key = "actor.sentinel"
                tags = [machine, hostile]
                component health { max = 40  regen = 0 }
            }

            actor depot_sentinel extends sentinel_machine with a, b {
                tags += [depot]
                component health { max = 60 }
                remove component patrol
                component sprite { sheet = asset:sentinel_sheet }
                options[risk] { check = { difficulty = 14 } }
                on arrive(ctx) {
                    if (ctx.character.detected_by(actor:depot_sentinel) and not ctx.quiet) {
                        wait 30min
                        apply effect:alarm_raised(ctx.place)
                    } else {
                        stop
                    }
                }
            }
            """);
        Assert.True(!bag.HasErrors, Errors(bag));
        var dump = SyntaxDump.Dump(tree);
        Assert.Contains("ObjectDecl `abstract`", dump);
        Assert.Contains("ExtendsClause `extends`", dump);
        Assert.Contains("WithClause `with` `,`", dump);
        Assert.Contains("Removal `remove` `component` `patrol`", dump);
        Assert.Contains("KeyedEntry `options` `[` `]`", dump);
        Assert.Contains("Hook `on` `arrive` `(` `ctx` `)`", dump);
        Assert.Contains("BinaryExpr `and`", dump);
        Assert.Contains("UnaryExpr `not`", dump);
        Assert.Contains("CallExpr", dump);
        Assert.Contains("MemberExpr `.` `place`", dump);
        Assert.Contains("ElseClause `else`", dump);
    }

    [Fact]
    public void PrattPrecedenceGroupsMultiplicationBeforeAddition()
    {
        var (tree, bag) = Parse("kedo 1\nmodule m\nfunction f(a: int) -> int {\n return a + 2 * 3 - 1\n}\n");
        Assert.False(bag.HasErrors, Errors(bag));
        var dump = SyntaxDump.Dump(tree);
        // (a + (2 * 3)) - 1
        var expected = """
            ReturnStmt `return`
              BinaryExpr `-`
                BinaryExpr `+`
                  NameExpr `a`
                  BinaryExpr `*`
                    LiteralExpr `2`
                    LiteralExpr `3`
                LiteralExpr `1`
            """.Replace("\r\n", "\n");
        var indented = string.Join("\n", expected.Split('\n').Select(l => l.Length == 0 ? l : new string(' ', 6) + l));
        Assert.Contains(indented, dump);
    }

    [Fact]
    public void ListsAcceptCommasOrNewlines()
    {
        var (_, bag) = Parse("kedo 1\nmodule m\nactor a {\n xs = [\n { id = a }\n { id = b },\n { id = c }\n ]\n ys = [1, 2, 3,]\n}\n");
        Assert.False(bag.HasErrors, Errors(bag));
        var (_, bad) = Parse("kedo 1\nmodule m\nactor a { xs = [1 2] }\n");
        Assert.Contains(bad.Items, d => d.Message.Contains("expected `,` or a newline"));
    }

    [Fact]
    public void StatementsNeedNewlineOrSemicolon()
    {
        var (_, ok) = Parse("kedo 1\nmodule m\nactor a { on x(ctx) { stop; stop\n stop } }\n");
        Assert.False(ok.HasErrors, Errors(ok));
        var (_, bad) = Parse("kedo 1\nmodule m\nactor a { on x(ctx) { stop stop } }\n");
        Assert.Contains(bad.Items, d => d.Message.Contains("expected a newline or `;`"));
    }

    [Theory]
    [InlineData("module m\nactor a {}\n", "K1002")]
    [InlineData("kedo 2\nmodule m\n", "K1001")]
    [InlineData("kedo 1\nmodule m\nactor a { x 1 }\n", "K1002")]
    [InlineData("kedo 1\nmodule m\nactor a { x = -y }\n", "K1005")]
    [InlineData("kedo 1\nmodule m\nactor signal {}\n", "K1002")]
    public void ReportsSyntaxErrorsAndKeepsParsing(string text, string code)
    {
        var (tree, bag) = Parse(text);
        Assert.Contains(bag.Items, d => d.Code == code);
        Assert.Equal(NodeKind.File, tree[tree.Root].Kind);
    }

    [Fact]
    public void RecoversAtTheNextDeclaration()
    {
        var (tree, bag) = Parse("kedo 1\nmodule m\nactor a { x = }\n??? garbage here\nactor b { y = 1 }\n");
        Assert.True(bag.HasErrors);
        var dump = SyntaxDump.Dump(tree);
        Assert.Contains("Error", dump);
        Assert.Contains("Name `b`", dump);
        Assert.Contains("Field `y` `=`", dump);
    }

    [Fact]
    public void WaitFormsParse()
    {
        var (tree, bag) = Parse("""
            kedo 1
            module m
            actor a {
                on x(ctx) {
                    wait 2d
                    wait decision prompt:choose { option yes { stop } option no { emit signal:refused(ctx.place) } }
                    wait roll resolution(20) as r
                    wait signal signal:structure_repaired(ctx.structure) as s
                    let n = r + 1
                    set ctx.counter = n
                }
            }
            """);
        Assert.False(bag.HasErrors, Errors(bag));
        var dump = SyntaxDump.Dump(tree);
        Assert.Contains("WaitStmt `wait`", dump);
        Assert.Contains("WaitDecisionStmt", dump);
        Assert.Contains("OptionClause `option` `yes`", dump);
        Assert.Contains("WaitRollStmt", dump);
        Assert.Contains("WaitSignalStmt", dump);
        Assert.Contains("LetStmt `let` `n` `=`", dump);
        Assert.Contains("SetStmt `set` `=`", dump);
    }

    [Fact]
    public void EveryNodeCoversItsChildrenAndTokensAreMonotonic()
    {
        var (tree, _) = Parse(File.ReadAllText(Path.Combine(RepoRoot(), "spec", "ejemplos", "campaign.kedo")));
        for (var i = 0; i < tree.NodeCount; i++)
        {
            var n = tree[i];
            foreach (var c in tree.Children(i))
            {
                Assert.True(c < i);
                Assert.True(tree[c].FirstToken >= n.FirstToken && tree[c].LastToken <= n.LastToken, $"child {tree[c].Kind} outside parent {n.Kind}");
            }
        }
    }

    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Kedo.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Kedo.sln not found above " + AppContext.BaseDirectory);
    }
}
