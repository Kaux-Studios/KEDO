using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

public class ResolverTests
{
    private static (ResolvedCatalog catalog, DiagnosticBag bag) Resolve(SchemaRegistry schema, params (string name, string text)[] files)
    {
        var bag = new DiagnosticBag();
        var decls = files.Select(f => DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(f.text, f.name), bag), bag)).ToList();
        return (Resolver.Resolve(decls, schema, bag), bag);
    }

    private static SchemaRegistry Permissive() => new() { Permissive = true };

    private static string Errors(DiagnosticBag bag) => string.Join("\n", bag.Items.Select(d => $"{d.Code} {d.Message}"));

    /// <summary>KEDO-02 §10 worked example.</summary>
    private const string Worked = """
        kedo 1
        module m

        abstract unit creature {
            component stats { hp = 100 }
            component loot { table = [{ id = gold  weight = 10 }, { id = hide  weight = 3 }] }
            tags = [animal]
        }

        mixin elite {
            component stats { hp = 250  spd = 7 }
            component loot { table += [{ id = trophy  weight = 1 }]  table -= [hide] }
            tags = [elite]
            abilities = [ability:roar]
        }

        unit wolf extends creature {
            component stats { hp = 120 }
            abilities = [ability:bite]
            tags = [pack]
        }

        unit alpha_wolf extends wolf with elite {
            component stats { spd = 9 }
            component loot { table[gold] { weight = 25 } }
            remove component vision
        }

        ability roar {}
        ability bite {}
        """;

    [Fact]
    public void ResolvesTheWorkedExampleOfTheSpec()
    {
        var (catalog, bag) = Resolve(Permissive(), ("units.kedo", Worked));
        var alpha = catalog.ByKey["unit:alpha_wolf"];
        var stats = (RRecord)alpha.Components.First(c => c.Key == "stats").Value;
        Assert.Equal("250", stats.Fields.First(f => f.Key == "hp").Value.CanonicalText);
        Assert.Equal("9", stats.Fields.First(f => f.Key == "spd").Value.CanonicalText);
        var loot = (RRecord)alpha.Components.First(c => c.Key == "loot").Value;
        var table = (RList)loot.Fields.First(f => f.Key == "table").Value;
        Assert.Equal(ListStrategy.Keyed, table.Strategy);
        Assert.Equal(new[] { "gold", "trophy" }, table.Items.Select(i => RList.KeyOf(i, "id")));
        Assert.Equal("25", ((RRecord)table.Items[0]).Fields.First(f => f.Key == "weight").Value.CanonicalText);
        var tags = (RList)alpha.Body.Fields.First(f => f.Key == "tags").Value;
        Assert.Equal(new[] { "animal", "pack", "elite" }, tags.Items.Select(i => i.CanonicalText));
        var abilities = (RList)alpha.Body.Fields.First(f => f.Key == "abilities").Value;
        Assert.Equal(new[] { "ability:bite", "ability:roar" }, abilities.Items.Select(i => i.CanonicalText));
        Assert.Contains(bag.Items, d => d.Code == "K3004" && d.Message.Contains("vision"));
        Assert.Equal(new[] { "unit:creature", "unit:wolf", "unit:alpha_wolf" }, alpha.Chain);

        var explain = Resolver.Explain(catalog, "unit:alpha_wolf", "stats.hp");
        Assert.Contains("= 250 from mixin elite", explain);
        Assert.Contains("shadowed: 120 from own", explain);
        Assert.Contains("shadowed: 100 from own", explain);

        var wolf = catalog.ByKey["unit:wolf"];
        Assert.False(wolf.HasErrors, Errors(bag));
        Assert.NotEqual(wolf.Hash, alpha.Hash);
        Assert.Equal(32, wolf.Hash.Length);
    }

    [Fact]
    public void HashIgnoresProvenanceAndSetOrder()
    {
        var a = Resolve(Permissive(), ("a.kedo", "kedo 1\nmodule m\nitem x { tags = [b, a] }\n")).catalog.ByKey["item:x"].Hash;
        var b = Resolve(Permissive(), ("b.kedo", "kedo 1\nmodule m\n\n\nitem x {\n    tags = [a, b]\n}\n")).catalog.ByKey["item:x"].Hash;
        Assert.Equal(a, b);
        var c = Resolve(Permissive(), ("c.kedo", "kedo 1\nmodule m\nitem x { tags = [a, b, c] }\n")).catalog.ByKey["item:x"].Hash;
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ReportsCyclesDuplicatesAndMissingBases()
    {
        var (_, bag) = Resolve(Permissive(), ("c.kedo", """
            kedo 1
            module m
            actor a extends b {}
            actor b extends a {}
            actor a {}
            actor c extends nope {}
            place p {}
            actor d extends p {}
            actor e with ghost {}
            """));
        Assert.Contains(bag.Items, d => d.Code == "K3001" && d.Message.Contains("actor:a → actor:b → actor:a") || d.Code == "K3001" && d.Message.Contains("actor:b → actor:a → actor:b"));
        Assert.Contains(bag.Items, d => d.Code == "K2002");
        Assert.Contains(bag.Items, d => d.Code == "K2004" && d.Message.Contains("nope"));
        Assert.Contains(bag.Items, d => d.Code == "K2003");
        Assert.Contains(bag.Items, d => d.Code == "K2004" && d.Message.Contains("ghost"));
    }

    [Fact]
    public void AppliesSchemaDefaultsRangesEnumsAndUnknownFields()
    {
        var schema = new SchemaRegistry();
        schema.AddEnum(new EnumSchema("context", new[] { "travel", "node_action" }));
        schema.AddRecord(new RecordSchema("option").Add(new FieldSchema("id", ValueKind.Enum)).Add(new FieldSchema("duration", ValueKind.Duration) { Min = 0 }));
        schema.AddType(new TypeSchema("event", new RecordSchema("event")
            .Add(new FieldSchema("version", ValueKind.Int) { Required = true, Min = 1 })
            .Add(new FieldSchema("context", ValueKind.Enum) { EnumId = "context", Required = true })
            .Add(new FieldSchema("repeat", ValueKind.Enum) { EnumId = "context", DefaultText = "travel" })
            .Add(new FieldSchema("options", ValueKind.List) { ListStrategy = ListStrategy.Keyed, Element = new FieldSchema("item", ValueKind.Record) { RecordId = "option" } })));

        var (catalog, bag) = Resolve(schema, ("e.kedo", """
            kedo 1
            module m
            abstract event base { version = 1  options = [{ id = safe  duration = 30min }] }
            event ok extends base { context = travel  options[safe] { duration = 10min } }
            event bad extends base { context = flying  versoin = 2  options += [{ id = safe  duration = 1min }] }
            event missing { version = 0 }
            """));
        var ok = catalog.ByKey["event:ok"];
        Assert.False(ok.HasErrors, Errors(bag));
        Assert.Equal("travel", ok.Body.Fields.First(f => f.Key == "repeat").Value.CanonicalText);
        Assert.Equal("10min", ((RRecord)((RList)ok.Body.Fields.First(f => f.Key == "options").Value).Items[0]).Fields.First(f => f.Key == "duration").Value.CanonicalText);
        Assert.Contains(bag.Items, d => d.Code == "K4003" && d.Message.Contains("flying"));
        Assert.Contains(bag.Items, d => d.Code == "K2005" && d.Message.Contains("did you mean `version`"));
        Assert.Contains(bag.Items, d => d.Code == "K3006");
        Assert.Contains(bag.Items, d => d.Code == "K4002" && d.Message.Contains("context"));
        Assert.Contains(bag.Items, d => d.Code == "K4004" && d.Message.Contains("version"));
        Assert.True(catalog.ByKey["event:missing"].HasErrors);
    }

    [Fact]
    public void ListOperatorsFollowTheStrategy()
    {
        var (catalog, bag) = Resolve(Permissive(), ("l.kedo", """
            kedo 1
            module m
            abstract item base { tags = [a, b]  nums = [1, 2]  recs = [{ id = x  v = 1 }] }
            item t1 extends base { tags -= [a]  tags += [c] }
            item t2 extends base { tags =! [z] }
            item t3 extends base { tags -= [nope] }
            item t4 extends base { recs[x] { v = 2 }  recs += [{ id = y  v = 3 }] }
            item t5 extends base { recs += [{ id = x }] }
            item t6 extends base { nums = [9] }
            """));
        string Tags(string id) => ((RList)catalog.ByKey[$"item:{id}"].Body.Fields.First(f => f.Key == "tags").Value).CanonicalText;
        Assert.Equal("[b, c]", Tags("t1"));
        Assert.Equal("[z]", Tags("t2"));
        Assert.Contains(bag.Items, d => d.Code == "K3004" && d.Message.Contains("nope"));
        var recs = (RList)catalog.ByKey["item:t4"].Body.Fields.First(f => f.Key == "recs").Value;
        Assert.Equal("[{ id = x  v = 2 }, { id = y  v = 3 }]", recs.CanonicalText);
        Assert.Contains(bag.Items, d => d.Code == "K3006" && d.Message.Contains("recs"));
        Assert.Equal("[9]", ((RList)catalog.ByKey["item:t6"].Body.Fields.First(f => f.Key == "nums").Value).CanonicalText);
    }

    [Fact]
    public void ConstantExpressionsFoldAndNonConstantsFail()
    {
        var (catalog, bag) = Resolve(Permissive(), ("x.kedo", """
            kedo 1
            module m
            item a { v = (2 + 3 * 4)  d = (2h + 30min)  f = (not true or 1 < 2)  bad = (ctx.x + 1) }
            """));
        var body = catalog.ByKey["item:a"].Body;
        Assert.Equal("14", body.Fields.First(f => f.Key == "v").Value.CanonicalText);
        Assert.Equal("150min", body.Fields.First(f => f.Key == "d").Value.CanonicalText);
        Assert.Equal("true", body.Fields.First(f => f.Key == "f").Value.CanonicalText);
        Assert.Contains(bag.Items, d => d.Code == "K5001");
        Assert.False(body.Fields.TryGetValue("bad", out _));
    }

    [Fact]
    public void ReferencesAreValidatedAfterResolution()
    {
        var (catalog, bag) = Resolve(Permissive(), ("r.kedo", """
            kedo 1
            module m
            abstract actor base {}
            actor a { friend = actor:b  ghost = actor:nobody  parent = actor:base }
            actor b {}
            """));
        Assert.Contains(bag.Items, d => d.Code == "K4005" && d.Message.Contains("actor:nobody"));
        Assert.Contains(bag.Items, d => d.Code == "K4005" && d.Message.Contains("abstract"));
        Assert.True(catalog.ByKey["actor:a"].HasErrors);
        Assert.False(catalog.ByKey["actor:b"].HasErrors);
    }

    [Fact]
    public void MixinShadowingIsWarnedAndHooksAreInherited()
    {
        var (catalog, bag) = Resolve(Permissive(), ("h.kedo", """
            kedo 1
            module m
            mixin m1 { x = 1 }
            mixin m2 { x = 2 }
            actor base { on arrive(ctx) { stop } }
            actor child extends base with m1, m2 { on leave(ctx) { stop } }
            """));
        Assert.Contains(bag.Items, d => d.Code == "K3010" && d.Severity == Severity.Warning);
        var child = catalog.ByKey["actor:child"];
        Assert.Equal("2", child.Body.Fields.First(f => f.Key == "x").Value.CanonicalText);
        Assert.Equal(new[] { "leave", "arrive" }, child.Hooks.Select(h => h.HookId));
    }
}
