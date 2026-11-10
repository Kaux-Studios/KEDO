using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

/// <summary>Invariants and computed fields of the schema (ADR-0016): language, evaluation, pass order and incrementality.</summary>
public class InvariantTests
{
    private const string SchemaText = """
        kedo 1
        module s

        kedo_record share { fields = [{ name = id  kind = enum  required = true }, { name = part  kind = decimal  scale = 2  required = true }] }

        kedo_type ruleset {
            fields = [
                { name = max_points  kind = int  required = true }
                { name = budget  kind = int  computed = (max_points - 3) }
            ]
        }

        kedo_type card {
            fields = [
                { name = weight  kind = int  min = 0  required = true }
                { name = ruleset  kind = reference  target = ruleset }
            ]
            invariants = [{ id = within_budget  check = (not has(ruleset) or weight <= ruleset.budget)  message = "weight {weight} is over the budget {ruleset.budget}" }]
        }

        kedo_type deck {
            fields = [
                { name = shares  kind = list  strategy = keyed  element = { kind = record  record = share }  required = true }
                { name = window  kind = duration }
                { name = total  kind = decimal  scale = 2  computed = (sum(shares, part)) }
            ]
            invariants = [
                { id = whole  check = (total == 1) }
                { id = every_card_weighted  check = (forall(card, weight > 0))  message = "some card has weight 0" }
                { id = window_band  check = (not has(window) or (window >= 1500ms and window <= 2s)) }
            ]
        }

        kedo_invariant cards_light { check = (forall(card, weight < 100))  severity = warning }
        """;

    private static SchemaRegistry Schema(string text, DiagnosticBag? bag = null)
    {
        bag ??= new DiagnosticBag();
        var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "schema.kedo"), bag), bag) }, bag);
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items.Select(d => $"{d.Code} {d.Message}")));
        return schema;
    }

    private static ResolvedCatalog Resolve(SchemaRegistry schema, DiagnosticBag bag, params string[] files)
    {
        var decls = files.Select((f, i) => DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(f, $"f{i}.kedo"), bag), bag)).ToList();
        return Resolver.Resolve(decls, schema, bag);
    }

    private static string[] Codes(DiagnosticBag bag) => bag.Items.Select(d => $"{d.Code} {d.Message}").ToArray();

    [Fact]
    public void DecimalsAddExactlyAcrossScales()
    {
        var bag = new DiagnosticBag();
        var catalog = Resolve(Schema(SchemaText), bag, "kedo 1\nmodule m\ndeck d { shares = [{ id = a  part = 0.1 }, { id = b  part = 0.65 }, { id = c  part = 0.25 }] }\n");
        Assert.Empty(Codes(bag));
        Assert.Equal("1.00", catalog.ByKey["deck:d"].Body.Fields.TryGetValue("total", out var t) ? t.CanonicalText : null);
    }

    [Fact]
    public void DurationsCompareInMillisecondsWhateverTheBaseUnit()
    {
        var schema = Schema(SchemaText);
        var ok = new DiagnosticBag();
        Resolve(schema, ok, "kedo 1\nmodule m\nmanifest { base_unit = ms }\ndeck d { shares = [{ id = a  part = 1 }]  window = 1750ms }\n");
        Assert.Empty(Codes(ok));

        var bad = new DiagnosticBag();
        Resolve(schema, bad, "kedo 1\nmodule m\nmanifest { base_unit = s }\ndeck d { shares = [{ id = a  part = 1 }]  window = 3s }\n");
        Assert.Contains(bad.Items, d => d.Code == "K4008" && d.Message.Contains("window_band"));
    }

    [Fact]
    public void ForallReportsOnTheAnchorAndCatalogInvariantsOnEachObject()
    {
        var bag = new DiagnosticBag();
        var catalog = Resolve(Schema(SchemaText), bag, """
            kedo 1
            module m
            deck d { shares = [{ id = a  part = 1 }] }
            card zero { weight = 0 }
            card heavy { weight = 150 }
            card fine { weight = 5 }
            """);
        Assert.Contains(bag.Items, d => d.Code == "K4008" && d.IsError && d.Message == "invariant `every_card_weighted` fails: some card has weight 0");
        var warning = Assert.Single(bag.Items, d => d.Code == "K4008" && d.Severity == Severity.Warning);
        Assert.Equal("invariant `cards_light` fails: `weight < 100` is false", warning.Message);
        Assert.True(catalog.ByKey["deck:d"].HasErrors);
        Assert.False(catalog.ByKey["card:heavy"].HasErrors);
    }

    [Fact]
    public void InheritedFailuresAreReportedOnceWithALabel()
    {
        var bag = new DiagnosticBag();
        Resolve(Schema(SchemaText), bag, """
            kedo 1
            module m
            ruleset r { max_points = 10 }
            abstract card base { weight = 9  ruleset = ruleset:r }
            card a extends base { }
            card b extends base { }
            """);
        var d = Assert.Single(bag.Items, x => x.Code == "K4008");
        Assert.Equal("invariant `within_budget` fails: weight 9 is over the budget 7", d.Message);
        Assert.Contains(d.Labels, l => l.Message.StartsWith("inherited by `card:a`", StringComparison.Ordinal));
        Assert.Contains(d.Labels, l => l.Message == "`weight` = 9");
    }

    [Fact]
    public void ARegistryBuiltInCSharpCompilesTheExpressionText()
    {
        var registry = new SchemaRegistry();
        var body = new RecordSchema("unit")
            .Add(new FieldSchema("hp", ValueKind.Int) { Required = true })
            .Add(new FieldSchema("level", ValueKind.Int) { Required = true })
            .Add(new FieldSchema("max_hp", ValueKind.Int) { Computed = "hp * level" })
            .AddInvariant(new InvariantSchema("sturdy", "max_hp >= 100") { Message = "max_hp is {max_hp}" });
        registry.AddType(new TypeSchema("unit", body));
        var bag = new DiagnosticBag();
        var catalog = Resolve(registry, bag, "kedo 1\nmodule m\nunit a { hp = 30  level = 4 }\nunit b { hp = 20  level = 2 }\n");
        Assert.Equal("120", catalog.ByKey["unit:a"].Body.Fields.TryGetValue("max_hp", out var v) ? v.CanonicalText : null);
        Assert.Equal(new[] { "K4008 invariant `sturdy` fails: max_hp is 40" }, Codes(bag));

        var broken = new SchemaRegistry();
        broken.AddType(new TypeSchema("unit", new RecordSchema("unit").Add(new FieldSchema("hp", ValueKind.Int)).AddInvariant(new InvariantSchema("x", "hq > 0"))));
        var bag2 = new DiagnosticBag();
        Resolve(broken, bag2, "kedo 1\nmodule m\nunit a { hp = 1 }\n");
        var k2013 = Assert.Single(bag2.Items, d => d.Code == "K2013");
        Assert.Equal("`hq` is not a field of `unit`; did you mean `hp`?", k2013.Message);
        Assert.Null(k2013.File);
    }

    [Fact]
    public void ExpressionsAreKeptAsCanonicalText()
    {
        var schema = Schema("kedo 1\nmodule s\nkedo_type t {\n    fields = [{ name = xs  kind = list  element = { kind = int } }, { name = n  kind = int  computed = (count( xs ,it>-1 ) // all of them\n    ) }]\n    invariants = [{ id = small  check = (n<=3) }]\n}\n");
        var t = schema.FindType("t")!.Body;
        Assert.Equal("count(xs, it > -1)", t.Find("n")!.Computed);
        Assert.Equal("n <= 3", Assert.Single(t.Invariants).Check);
    }

    [Fact]
    public void ToolsShowComputedFieldsAndInvariants()
    {
        var schema = Schema(SchemaText);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            JsonSchemaWriter.Write(schema, w);
        }

        var json = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"computed\":\"sum(shares, part)\"", json);
        Assert.Contains("\"readOnly\":true", json);
        Assert.Contains("\"check\":\"total == 1\"", json);

        var bag = new DiagnosticBag();
        var catalog = Resolve(schema, bag, "kedo 1\nmodule m\ndeck d { shares = [{ id = a  part = 1 }] }\n");
        var described = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new System.Text.Json.Utf8JsonWriter(described))
        {
            Kedo.Compiler.Editing.ObjectDescriber.Write(w, catalog.ByKey["deck:d"], schema);
        }

        var text = System.Text.Encoding.UTF8.GetString(described.WrittenSpan);
        Assert.Contains("\"computed\":true,\"expression\":\"sum(shares, part)\"", text);
        Assert.Contains("computed (sum(shares, part))", Resolver.Explain(catalog, "deck:d", "total"));
    }

    [Fact]
    public void ContentThatWritesAComputedFieldIsK4015()
    {
        var bag = new DiagnosticBag();
        Resolve(Schema(SchemaText), bag, "kedo 1\nmodule m\ndeck d { shares = [{ id = a  part = 1 }]  total = 2 }\n");
        Assert.Equal(new[] { "K4015 `total` is computed by the schema (`sum(shares, part)`); remove it from the content" }, Codes(bag));
    }

    [Fact]
    public void OverflowIsADiagnosticNotAnException()
    {
        var registry = new SchemaRegistry();
        registry.AddType(new TypeSchema("t", new RecordSchema("t").Add(new FieldSchema("x", ValueKind.Int) { Required = true }).AddInvariant(new InvariantSchema("big", "x * x > 0"))));
        var bag = new DiagnosticBag();
        Resolve(registry, bag, "kedo 1\nmodule m\nt a { x = 9_000_000_000 }\n");
        Assert.Equal(new[] { "K4016 invariant `big` cannot be checked: the arithmetic overflows 64 bits" }, Codes(bag));
    }

    [Fact]
    public void EachVariantViewIsCheckedOnItsOwnValues()
    {
        var bag = new DiagnosticBag();
        var catalog = Resolve(Schema(SchemaText), bag,
            "kedo 1\nmodule m\ndeck d { shares = [{ id = a  part = 0.5 }, { id = b  part = 0.5 }] }\n",
            "kedo 1\nmodule m\nvariant hard\noverride deck d { shares[b] { part = 0.75 } }\n");
        Assert.Equal("1.00", catalog.ByKey["deck:d"].Body.Fields.TryGetValue("total", out var t) ? t.CanonicalText : null);
        Assert.Equal("1.25", catalog.Variants["hard"].ByKey["deck:d"].Body.Fields.TryGetValue("total", out var h) ? h.CanonicalText : null);
        Assert.Single(bag.Items, d => d.Code == "K4008" && d.Message.Contains("whole"));
    }

    [Fact]
    public void ContentWithoutRulesKeepsItsHashes()
    {
        // The same content against a schema with and without rules on another type: same hashes, byte for byte.
        const string plain = "kedo 1\nmodule s\nkedo_type unit { fields = [{ name = hp  kind = int }] }\n";
        var content = "kedo 1\nmodule m\nunit a { hp = 3 }\n";
        var a = Resolve(Schema(plain), new DiagnosticBag(), content);
        var b = Resolve(Schema(plain + "kedo_type other { fields = [{ name = n  kind = int  computed = (1 + 1) }]  invariants = [{ id = x  check = (n > 0) }] }\n"), new DiagnosticBag(), content);
        Assert.Equal(a.ByKey["unit:a"].Hash, b.ByKey["unit:a"].Hash);
        Assert.Equal(ResolvedDump.Dump(a), ResolvedDump.Dump(b));
    }

    [Fact]
    public void IncrementalUpdatesEqualAFullResolution()
    {
        var schema = Schema(SchemaText);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.kedo"] = "kedo 1\nmodule m\nruleset r { max_points = 20 }\n",
            ["b.kedo"] = "kedo 1\nmodule m\ncard c1 { weight = 10  ruleset = ruleset:r }\ncard c2 { weight = 15  ruleset = ruleset:r }\n",
            ["c.kedo"] = "kedo 1\nmodule m\ndeck d { shares = [{ id = a  part = 0.5 }, { id = b  part = 0.5 }] }\n",
        };
        var session = new IncrementalCatalog(schema);
        IncrementalTests.AssertSame(session, files, schema);

        // The ruleset's computed budget drops under c2's weight: c2 is checked again though its file did not change.
        files["a.kedo"] = "kedo 1\nmodule m\nruleset r { max_points = 15 }\n";
        IncrementalTests.AssertSame(session, files, schema);

        // A new card with weight 0 breaks the deck's `forall`; the deck's file did not change.
        files["d.kedo"] = "kedo 1\nmodule m\ncard zero { weight = 0 }\n";
        IncrementalTests.AssertSame(session, files, schema);

        files.Remove("d.kedo");
        IncrementalTests.AssertSame(session, files, schema);

        files["c.kedo"] = "kedo 1\nmodule m\ndeck d { shares = [{ id = a  part = 0.5 }, { id = b  part = 0.25 }] }\n";
        IncrementalTests.AssertSame(session, files, schema);

        files["a.kedo"] = "kedo 1\nmodule m\nruleset r { max_points = 40 }\n";
        IncrementalTests.AssertSame(session, files, schema);
    }
}
