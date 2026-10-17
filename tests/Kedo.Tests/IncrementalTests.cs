using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

/// <summary>Incremental resolution (#15): after every edit the session must equal a full resolution of the same files.</summary>
public class IncrementalTests
{
    private const string SchemaText = """
        kedo 1
        module s
        kedo_enum mood { values = [calm, tense] }
        kedo_record stats { fields = [{ name = hp  kind = int  min = 0  max = 500 }, { name = armor  kind = int  default = 1 }] }
        kedo_type unit { fields = [
            { name = title  kind = text  required = true }
            { name = mood  kind = enum  enum = mood }
            { name = stats  kind = record  record = stats }
            { name = tags  kind = list  strategy = set  element = { kind = enum } }
            { name = ally  kind = reference  target = unit }
        ] }
        """;

    private static SchemaRegistry Schema()
    {
        var bag = new DiagnosticBag();
        var schema = Kedo.Compiler.Schema.SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(SchemaText, "s.kedo"), bag), bag) }, bag);
        Assert.False(bag.HasErrors);
        return schema;
    }

    /// <summary>Six files of eight units; units extend earlier ones across files, use mixins and reference each other.</summary>
    private static Dictionary<string, string> Corpus()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.kedo"] = "kedo 1\nmodule m\n\nmixin veteran { stats = { armor = 3 }  tags += [veteran] }\nmixin calm_one { mood = calm }\n\nabstract unit base { title = \"Base\"  stats = { hp = 10 } }\n",
        };
        for (var f = 0; f < 6; f++)
        {
            var sb = new System.Text.StringBuilder("kedo 1\nmodule m\n\n");
            for (var i = 0; i < 8; i++)
            {
                var n = f * 8 + i;
                var parent = n == 0 ? "base" : $"u{(n * 7 + 3) % n}";
                var with = n % 3 == 0 ? " with veteran" : n % 3 == 1 ? " with calm_one" : "";
                sb.Append($"unit u{n} extends {parent}{with} {{\n    title = \"Unit {n}\"\n    stats = {{ hp = {10 + n} }}\n    tags += [t{n % 5}]\n    ally = unit:u{(n + 5) % 48}\n}}\n\n");
            }

            files[$"f{f}.kedo"] = sb.ToString();
        }

        return files;
    }

    private static List<SourceText> Sources(Dictionary<string, string> files)
        => files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => SourceText.FromString(f.Value, f.Key)).ToList();

    private static (string Dump, string Hash, string[] Diagnostics, string[] Errors, string[] Order) Snapshot(ResolvedCatalog catalog, DiagnosticBag bag)
        => (ResolvedDump.Dump(catalog, withProvenance: true), catalog.Hash,
            bag.Items.Select(d => $"{d.Severity}|{d.Code}|{d.File}|{d.Span.Start}|{d.Message}").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            catalog.Objects.Where(o => o.HasErrors).Select(o => o.Key).ToArray(),
            catalog.InDeclarationOrder.Select(o => o.Key).ToArray());

    private static void AssertSame(IncrementalCatalog session, Dictionary<string, string> files, SchemaRegistry schema)
    {
        var sources = Sources(files);
        var fullBag = new DiagnosticBag();
        var decls = sources.Select(s => DeclarationBuilder.Build(Parser.Parse(s, fullBag), fullBag)).ToList();
        var full = Snapshot(Resolver.Resolve(decls, schema, fullBag), fullBag);
        var bag = new DiagnosticBag();
        var inc = Snapshot(session.Update(sources, bag), bag);
        Assert.Equal(full.Diagnostics, inc.Diagnostics);
        Assert.Equal(full.Errors, inc.Errors);
        Assert.Equal(full.Order, inc.Order);
        Assert.Equal(full.Hash, inc.Hash);
        Assert.Equal(full.Dump, inc.Dump);
    }

    [Fact]
    public void EveryEditMatchesAFullResolution()
    {
        var schema = Schema();
        var files = Corpus();
        var session = new IncrementalCatalog(schema);
        AssertSame(session, files, schema);
        Assert.Equal(7, session.LastParsedFiles);

        // A value in the last file: one file parsed, most objects reused.
        files["f5.kedo"] = files["f5.kedo"].Replace("title = \"Unit 47\"", "title = \"Last unit\"");
        AssertSame(session, files, schema);
        Assert.Equal(1, session.LastParsedFiles);
        Assert.True(session.LastReusedObjects > 30, $"reused {session.LastReusedObjects}");

        // Lines added above everything in a file: positions move, so its objects (and their descendants) resolve again.
        files["f2.kedo"] = files["f2.kedo"].Replace("kedo 1\nmodule m\n\n", "kedo 1\nmodule m\n\n// a note\n// another\n\n");
        AssertSame(session, files, schema);

        // A mixin change reaches every user.
        files["a.kedo"] = files["a.kedo"].Replace("stats = { armor = 3 }", "stats = { armor = 4 }");
        AssertSame(session, files, schema);

        // An out-of-range value: a stage-B error on the object and inherited by its descendants.
        files["f1.kedo"] = files["f1.kedo"].Replace("stats = { hp = 18 }", "stats = { hp = 999 }");
        AssertSame(session, files, schema);

        // A referenced object disappears: its referrers report K4005 although they did not change.
        files["f3.kedo"] = files["f3.kedo"].Replace("unit u30 extends", "unit u30_gone extends");
        AssertSame(session, files, schema);

        // ... and comes back.
        files["f3.kedo"] = files["f3.kedo"].Replace("unit u30_gone extends", "unit u30 extends");
        AssertSame(session, files, schema);

        // A syntax error, a duplicate id, a removed file and a new file.
        files["f4.kedo"] = files["f4.kedo"].Replace("title = \"Unit 33\"", "title = ");
        AssertSame(session, files, schema);
        files["dup.kedo"] = "kedo 1\nmodule m\nunit u1 extends base { title = \"Again\" }\n";
        AssertSame(session, files, schema);
        files.Remove("dup.kedo");
        files.Remove("f4.kedo");
        AssertSame(session, files, schema);
    }

    [Fact]
    public void VariantsResolveInFull()
    {
        var schema = new SchemaRegistry { Permissive = true };
        var files = new Dictionary<string, string>
        {
            ["a.kedo"] = "kedo 1\nmodule m\nunit u { hp = 1 }\n",
            ["b.kedo"] = "kedo 1\nmodule m\nvariant hard\noverride unit u { hp = 9 }\n",
        };
        var session = new IncrementalCatalog(schema);
        var catalog = session.Update(Sources(files), new DiagnosticBag());
        Assert.Equal("9", ((RInt)catalog.Variants["hard"].ByKey["unit:u"].Body.Fields.First(f => f.Key == "hp").Value).CanonicalText);
    }
}
