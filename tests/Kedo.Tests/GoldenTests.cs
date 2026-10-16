using Kedo.Compiler.Formatting;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

/// <summary>
/// Golden corpus (tests/corpus/&lt;case&gt;/input.kedo). Expected files: tree.txt, diagnostics.txt, formatted.kedo.
/// Missing expected files are generated on first run; set KEDO_UPDATE_GOLDEN=1 to regenerate all of them.
/// </summary>
public class GoldenTests
{
    public static IEnumerable<object[]> Cases()
    {
        var root = Path.Combine(ParserTests.RepoRoot(), "tests", "corpus");
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, "input.kedo")))
            {
                yield return new object[] { Path.GetFileName(dir) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesGoldenFiles(string name)
    {
        var dir = Path.Combine(ParserTests.RepoRoot(), "tests", "corpus", name);
        var source = SourceText.FromFile(Path.Combine(dir, "input.kedo"));
        var bag = new DiagnosticBag();
        var tree = Parser.Parse(source, bag);

        var actualTree = SyntaxDump.Dump(tree);
        var actualDiagnostics = string.Join("", bag.Items.Select(d =>
        {
            var pos = source.GetLinePosition(d.Span.Start);
            return $"{d.Severity.ToString().ToLowerInvariant()} {d.Code} {pos.Line}:{pos.Column} {d.Message}\n";
        }));
        var actualFormatted = Formatter.Format(tree);

        Check(dir, "tree.txt", actualTree);
        Check(dir, "diagnostics.txt", actualDiagnostics);
        Check(dir, "formatted.kedo", actualFormatted);

        // Formatter invariants: idempotent, and the formatted source parses to the same tree shape with the same diagnostics.
        var bag2 = new DiagnosticBag();
        var tree2 = Parser.Parse(SourceText.FromString(actualFormatted), bag2);
        Assert.Equal(actualFormatted, Formatter.Format(tree2));
        Assert.Equal(StripTokens(actualTree), StripTokens(SyntaxDump.Dump(tree2)));
        Assert.Equal(bag.Items.Select(d => d.Code), bag2.Items.Select(d => d.Code));
    }

    private static string StripTokens(string dump)
        => string.Join("\n", dump.Split('\n').Select(l => l.Split(' ', 2)[0] + (l.Contains('`') ? " " + l[l.IndexOf('`')..] : string.Empty)));

    private static void Check(string dir, string file, string actual)
    {
        var path = Path.Combine(dir, file);
        actual = actual.Replace("\r\n", "\n");
        var update = Environment.GetEnvironmentVariable("KEDO_UPDATE_GOLDEN") == "1";
        if (!File.Exists(path) || update)
        {
            File.WriteAllText(path, actual, new System.Text.UTF8Encoding(false));
            return;
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(expected == actual, $"{file} differs for case {Path.GetFileName(dir)}.\n--- expected\n{expected}\n--- actual\n{actual}");
    }
}
