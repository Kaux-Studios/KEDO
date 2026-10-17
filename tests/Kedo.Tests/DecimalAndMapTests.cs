using System.Text;
using System.Text.Json;
using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Import;
using Kedo.Compiler.Model;
using Kedo.Compiler.Pack;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Pack;
using Kedo.Schema;

/// <summary>Fixed-point decimals (ADR-0008), maps and schema constraints (ADR-0009).</summary>
public class DecimalAndMapTests
{
    private const string Schema = """
        kedo 1
        module s

        kedo_enum biome { values = [plains, mountain, swamp] }

        kedo_type terrain {
            fields = [
                { name = cost  kind = decimal  scale = 2  min = 0  max = 9 }
                { name = weight  kind = decimal  scale = 2  min = 0.01 }
                { name = die  kind = int  one_of = [4, 6, 20] }
                { name = by_biome  kind = map  keys = biome  element = { kind = decimal  scale = 2 } }
                { name = scenes  kind = map  element = { kind = enum }  min_items = 1 }
                { name = tags  kind = list  strategy = set  element = { kind = enum }  max_items = 2 }
                { name = rules  kind = text  one_of = ["refuge-1", "refuge-2"] }
                { name = title  kind = text  min_length = 2  max_length = 5 }
            ]
        }
        """;

    private static SchemaRegistry LoadSchema(DiagnosticBag bag)
        => SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(Schema, "schema.kedo"), bag), bag) }, bag);

    private static (ResolvedCatalog Catalog, DiagnosticBag Bag) Resolve(string content, bool permissive = false)
    {
        var bag = new DiagnosticBag();
        var schema = permissive ? new SchemaRegistry { Permissive = true } : LoadSchema(bag);
        var decls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString("kedo 1\nmodule m\n" + content, "c.kedo"), bag), bag);
        return (Resolver.Resolve(new[] { decls }, schema, bag), bag);
    }

    private static string Errors(DiagnosticBag bag) => string.Join("\n", bag.Items.Select(d => $"{d.Code} {d.Message}"));

    private static ResolvedValue Field(ResolvedCatalog catalog, string key, string field)
        => catalog.ByKey[key].Body.Fields.First(f => f.Key == field).Value;

    [Fact]
    public void DecimalLiteralsAreOneTokenButNotInsideQualifiedIds()
    {
        var bag = new DiagnosticBag();
        var tokens = Lexer.Lex(SourceText.FromString("x = 1.25\nid = place.1.5\nd = 1.5h\nlong = 0.1234567891\n", "l.kedo"), bag);
        var kinds = tokens.Where(t => t.Kind is TokenKind.Decimal or TokenKind.Integer).Select(t => t.Kind).ToList();
        Assert.Equal(new[] { TokenKind.Decimal, TokenKind.Integer, TokenKind.Integer, TokenKind.Decimal }, kinds);
        Assert.Contains(bag.Items, d => d.Code == "K1002" && d.Message.Contains("whole numbers"));
        Assert.Contains(bag.Items, d => d.Code == "K1006");
    }

    [Fact]
    public void DecimalsTakeTheScaleOfTheSchemaAndNeverRound()
    {
        var (catalog, bag) = Resolve("terrain a { cost = 1.5 }\nterrain b { cost = 2 }\nterrain c { cost = -0.25 }\nterrain d { cost = 1.255 }");
        Assert.Equal("1.50", Field(catalog, "terrain:a", "cost").CanonicalText);
        var cost = Assert.IsType<RDecimal>(Field(catalog, "terrain:a", "cost"));
        Assert.Equal((150L, 2), (cost.Units, cost.Scale));
        Assert.Equal("2.00", Field(catalog, "terrain:b", "cost").CanonicalText);
        Assert.Contains(bag.Items, d => d.Code == "K4004" && d.Message.Contains("-0.25"));
        Assert.Contains(bag.Items, d => d.Code == "K4009" && d.Message.Contains("1.255"));
        Assert.DoesNotContain(bag.Items, d => d.Code == "K4002");
    }

    [Fact]
    public void WithoutSchemaADecimalKeepsTheScaleItWasWrittenWith()
    {
        var (catalog, bag) = Resolve("thing t { a = 1.0  b = 0.50  c = (2) }", permissive: true);
        Assert.False(bag.HasErrors, Errors(bag));
        Assert.Equal("1.0", Field(catalog, "thing:t", "a").CanonicalText);
        Assert.Equal("0.50", Field(catalog, "thing:t", "b").CanonicalText);
    }

    [Fact]
    public void DecimalsAreDataNotExpressions()
    {
        var (_, bag) = Resolve("thing t { a = (1.5 + 1) }", permissive: true);
        Assert.Contains(bag.Items, d => d.Code == "K5007");

        var hookBag = new DiagnosticBag();
        var decls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString("kedo 1\nmodule m\nactor z { on go(ctx) { let v = 1.5 } }", "h.kedo"), hookBag), hookBag);
        var permissive = new SchemaRegistry { Permissive = true };
        var catalog = Resolver.Resolve(new[] { decls }, permissive, hookBag);
        BehaviorCompiler.Compile(catalog.ByKey["actor:z"], decls.Functions, permissive, hookBag);
        Assert.Contains(hookBag.Items, d => d.Code == "K5007");
    }

    [Fact]
    public void MapsTakeQuotedKeysTypedValuesAndEnumKeys()
    {
        var (catalog, bag) = Resolve("""
            terrain a {
                by_biome = { plains = 1.0  mountain = 2.25 }
                scenes = { "role:bridge_west" = riverbed  "biome:swamp" = marsh }
            }
            terrain b { by_biome = { desert = 1 }  scenes = { x = "quoted" } }
            terrain c { scenes = { } }
            """);
        Assert.Equal("{ plains = 1.00  mountain = 2.25 }", Field(catalog, "terrain:a", "by_biome").CanonicalText);
        Assert.Equal("{ \"role:bridge_west\" = riverbed  \"biome:swamp\" = marsh }", Field(catalog, "terrain:a", "scenes").CanonicalText);
        Assert.Contains(bag.Items, d => d.Code == "K4003" && d.Message.Contains("`desert`"));
        Assert.Contains(bag.Items, d => d.Code == "K4001" && d.Message.Contains("expects enum"));
        Assert.Contains(bag.Items, d => d.Code == "K4010" && d.Message.Contains("terrain:c.scenes"));
        Assert.DoesNotContain(bag.Items, d => d.Message.Contains("terrain:a"));
    }

    [Fact]
    public void MapsMergeKeyByKeyAndReplaceWithBang()
    {
        var (catalog, bag) = Resolve("""
            terrain base { by_biome = { plains = 1  swamp = 3 } }
            terrain more extends base { by_biome = { mountain = 2 } }
            terrain only extends base { by_biome =! { mountain = 2 } }
            """);
        Assert.False(bag.HasErrors, Errors(bag));
        Assert.Equal("{ plains = 1.00  swamp = 3.00  mountain = 2.00 }", Field(catalog, "terrain:more", "by_biome").CanonicalText);
        Assert.Equal("{ mountain = 2.00 }", Field(catalog, "terrain:only", "by_biome").CanonicalText);
    }

    [Fact]
    public void DecimalBoundsAndIntOneOfAreChecked()
    {
        var (_, bag) = Resolve("terrain a { weight = 0  die = 5 }\nterrain b { weight = 0.01  die = 20 }");
        Assert.Contains(bag.Items, d => d.Code == "K4004" && d.Message.Contains("terrain:a.weight") && d.Message.Contains("[0.01,"));
        Assert.Contains(bag.Items, d => d.Code == "K4012" && d.Message.Contains("terrain:a.die") && d.Message.Contains("4, 6, 20"));
        Assert.DoesNotContain(bag.Items, d => d.Message.Contains("terrain:b"));

        var loader = new DiagnosticBag();
        var text = "kedo 1\nmodule s\nkedo_type t { fields = [{ name = n  kind = int  min = 0.5 }, { name = f  kind = bool  one_of = [1] }] }";
        SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "s.kedo"), loader), loader) }, loader);
        Assert.Contains(loader.Items, d => d.Message.Contains("decimal bounds are only for kind = decimal"));
        Assert.Contains(loader.Items, d => d.Message.Contains("applies to kind = text or int"));
    }

    [Fact]
    public void ListAndTextConstraintsAreChecked()
    {
        var (_, bag) = Resolve("""terrain a { tags = [x, y, z]  rules = "refuge-3"  title = "a" }""");
        Assert.Contains(bag.Items, d => d.Code == "K4010" && d.Message.Contains("3 item(s)"));
        Assert.Contains(bag.Items, d => d.Code == "K4012" && d.Message.Contains("\"refuge-1\""));
        Assert.Contains(bag.Items, d => d.Code == "K4011" && d.Message.Contains("1 character(s)"));
        var (_, ok) = Resolve("""terrain a { tags = [x]  rules = "refuge-2"  title = "ñandú" }""");
        Assert.False(ok.HasErrors, Errors(ok));
    }

    [Fact]
    public void DecimalsTravelThroughThePackAndTheHash()
    {
        var (catalog, bag) = Resolve("terrain a { cost = 1.5  by_biome = { plains = 0.75 } }");
        Assert.False(bag.HasErrors, Errors(bag));
        var pack = KedoPack.Load(PackWriter.Write(catalog, Array.Empty<Kedo.Runtime.BehaviorProgram>()));
        var body = pack.Find("terrain:a")!.Body;
        Assert.Equal(new PackDecimal(150, 2), body.Fields["cost"]);
        Assert.Equal(new PackDecimal(75, 2), body.Record("by_biome")!.Fields["plains"]);

        var (other, _) = Resolve("terrain a { cost = 1.6  by_biome = { plains = 0.75 } }");
        Assert.NotEqual(catalog.ByKey["terrain:a"].Hash, other.ByKey["terrain:a"].Hash);
    }

    [Fact]
    public void JsonSchemaDescribesDecimalsMapsAndConstraints()
    {
        var bag = new DiagnosticBag();
        var registry = LoadSchema(bag);
        Assert.False(bag.HasErrors, Errors(bag));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            JsonSchemaWriter.Write(registry, writer);
        }

        var fields = JsonDocument.Parse(stream.ToArray()).RootElement.GetProperty("types").GetProperty("terrain").GetProperty("properties");
        Assert.Equal("number", fields.GetProperty("cost").GetProperty("type").GetString());
        Assert.Equal(0.01m, fields.GetProperty("cost").GetProperty("multipleOf").GetDecimal());
        Assert.Equal("object", fields.GetProperty("by_biome").GetProperty("type").GetString());
        Assert.Equal("#/$defs/enum:biome", fields.GetProperty("by_biome").GetProperty("propertyNames").GetProperty("$ref").GetString());
        Assert.Equal(1, fields.GetProperty("scenes").GetProperty("minProperties").GetInt32());
        Assert.Equal(2, fields.GetProperty("tags").GetProperty("maxItems").GetInt32());
        Assert.Equal("refuge-2", fields.GetProperty("rules").GetProperty("enum")[1].GetString());
        Assert.Equal(5, fields.GetProperty("title").GetProperty("maxLength").GetInt32());
        Assert.Equal(0.01m, fields.GetProperty("weight").GetProperty("minimum").GetDecimal());
        Assert.Equal(6, fields.GetProperty("die").GetProperty("enum")[1].GetInt32());
    }

    [Fact]
    public void ALoaderErrorNamesAMapWithoutValues()
    {
        var bag = new DiagnosticBag();
        var text = "kedo 1\nmodule s\nkedo_type t { fields = [{ name = m  kind = map  keys = nope }] }";
        SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "s.kedo"), bag), bag) }, bag);
        Assert.Contains(bag.Items, d => d.Message.Contains("needs an `element` descriptor for its values"));
        Assert.Contains(bag.Items, d => d.Message.Contains("unknown enum `nope`"));
    }

    [Fact]
    public void TheImporterKeepsDecimalsKeysAndMarksNulls()
    {
        var json = """{ "version": "v1", "weights": { "role:city_b": 1.25, "plain": 2.0 }, "caliber": null, "tiny": 0.0000000001 }""";
        var files = JsonImporter.Import(json, new JsonImportOptions { Module = "m" });
        var text = string.Concat(files.Values);
        Assert.Contains("\"role:city_b\" = 1.25", text);
        Assert.Contains("plain = 2.0", text);
        Assert.Contains("caliber = null in the JSON", text);
        Assert.Contains("more than 9 decimal places", text);

        var bag = new DiagnosticBag();
        var decls = files.Select(f => DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(f.Value, f.Key), bag), bag)).ToList();
        var catalog = Resolver.Resolve(decls, new SchemaRegistry { Permissive = true }, bag);
        Assert.False(bag.HasErrors, Errors(bag) + "\n" + text);
        Assert.Contains("\"role:city_b\" = 1.25", ResolvedDump.Dump(catalog));
    }

    [Fact]
    public void TheImporterSuggestsRepeatedIdentifierStringsAsEnums()
    {
        var json = """
            { "events": [
                { "id": "a", "exit": "arrive", "title": "Hello there", "kind": "x" },
                { "id": "b", "exit": "retreat", "title": "Bye", "kind": "y" },
                { "id": "c", "exit": "arrive", "title": "Again", "kind": "z" },
                { "id": "d", "exit": "arrive", "title": "More", "kind": "w", "tags": ["night", "road", "night"] } ] }
            """;
        var options = new JsonImportOptions { Module = "m", Collections = { ["events"] = "event" } };
        var suggestions = JsonImporter.SuggestEnumFields(json, options);
        Assert.Equal(new[] { "exit" }, suggestions.Select(s => s.Field));
        Assert.Equal(new[] { "arrive", "retreat" }, suggestions[0].Values);
        Assert.Equal(4, suggestions[0].Uses);

        options.EnumFields.Add("exit");
        Assert.Empty(JsonImporter.SuggestEnumFields(json, options));
    }
}
