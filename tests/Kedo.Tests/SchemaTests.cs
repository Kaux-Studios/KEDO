using System.Text.Json;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

public class SchemaTests
{
    private static List<FileDeclarations> Decls(DiagnosticBag bag, params string[] files)
        => files.Select(f => DeclarationBuilder.Build(Parser.Parse(SourceText.FromFile(f), bag), bag)).ToList();

    private static string Errors(DiagnosticBag bag) => string.Join("\n", bag.Items.Select(d => $"{d.Code} {d.File}:{d.Span} {d.Message}"));

    private static string Examples(string name) => Path.Combine(ParserTests.RepoRoot(), "spec", "ejemplos", name);

    [Fact]
    public void TheExampleSchemaLoadsAndValidatesTheExamplesWithoutErrors()
    {
        var bag = new DiagnosticBag();
        var registry = SchemaLoader.Load(Decls(bag, Examples(Path.Combine("schema", "patriark.kedo"))), bag);
        Assert.False(bag.HasErrors, Errors(bag));
        Assert.False(registry.Permissive);
        Assert.NotNull(registry.FindType("event"));
        Assert.Equal(ListStrategy.Keyed, registry.FindType("event")!.Body.Find("options")!.ListStrategy);
        Assert.Equal("travel", registry.FindEnum("context")!.Values[0]);
        Assert.NotNull(registry.FindHook("arrive"));

        var content = Decls(bag, Examples("sentinel.kedo"), Examples("bridge.kedo"), Examples("campaign.kedo"));
        var catalog = Resolver.Resolve(content, registry, bag);
        Assert.False(bag.HasErrors, Errors(bag));
        Assert.Equal(42, catalog.Concrete.Count());
        // Schema defaults reach objects that did not write the field.
        var inspect = catalog.ByKey["event:bridge.inspect_damage"];
        var options = (RList)inspect.Body.Fields.First(f => f.Key == "options").Value;
        var leave = (RRecord)options.Items.First(i => RList.KeyOf(i, "id") == "leave");
        Assert.Equal("deterministic", leave.Fields.First(f => f.Key == "resolution").Value.CanonicalText);
    }

    [Fact]
    public void ClosedSchemaRejectsUnknownFieldsComponentsAndHooks()
    {
        var bag = new DiagnosticBag();
        var registry = SchemaLoader.Load(Decls(bag, Examples(Path.Combine("schema", "patriark.kedo"))), bag);
        var text = """
            kedo 1
            module patriark
            actor a { name_key = "x"  nam_key = "y"  component wings { span = 2 }  on vanish(ctx) { stop } }
            event e { version = 1  family = f  context = travel  text_key = "t"  options = [{ id = go  label_key = "l"  exit = arrive  duration = 5 }] }
            """;
        var content = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "c.kedo"), bag), bag);
        Resolver.Resolve(new[] { content }, registry, bag);
        Assert.Contains(bag.Items, d => d.Code == "K2005" && d.Message.Contains("did you mean `name_key`"));
        Assert.Contains(bag.Items, d => d.Code == "K2001" && d.Message.Contains("wings"));
        Assert.Contains(bag.Items, d => d.Code == "K2006" && d.Message.Contains("vanish"));
        Assert.True(bag.Items.All(d => d.Code != "K4001"), Errors(bag)); // an integer is accepted for a duration field (base unit)
    }

    [Fact]
    public void SchemaLoaderReportsDanglingReferencesInsideTheSchema()
    {
        var bag = new DiagnosticBag();
        var text = """
            kedo 1
            module m
            kedo_type thing { fields = [{ name = kind  kind = enum  enum = nope }, { name = parts  kind = list  strategy = keyed  element = { kind = int } }]  components = [ghost] }
            """;
        SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "s.kedo"), bag), bag) }, bag);
        Assert.Contains(bag.Items, d => d.Message.Contains("unknown enum `nope`"));
        Assert.Contains(bag.Items, d => d.Message.Contains("needs record elements"));
        Assert.Contains(bag.Items, d => d.Message.Contains("unknown component `ghost`"));
    }

    [Fact]
    public void JsonSchemaBundleIsValidJsonWithTypesAndDefs()
    {
        var bag = new DiagnosticBag();
        var registry = SchemaLoader.Load(Decls(bag, Examples(Path.Combine("schema", "patriark.kedo"))), bag);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            JsonSchemaWriter.Write(registry, writer);
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        var root = doc.RootElement;
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        var eventType = root.GetProperty("types").GetProperty("event");
        Assert.Equal("array", eventType.GetProperty("properties").GetProperty("options").GetProperty("type").GetString());
        Assert.Equal("keyed", eventType.GetProperty("properties").GetProperty("options").GetProperty("x-kedo").GetProperty("strategy").GetString());
        Assert.Equal("#/$defs/enum:context", eventType.GetProperty("properties").GetProperty("context").GetProperty("$ref").GetString());
        Assert.True(root.GetProperty("$defs").TryGetProperty("record:event_option", out _));
        Assert.True(root.GetProperty("components").TryGetProperty("health", out _));
        Assert.True(root.GetProperty("hooks").TryGetProperty("arrive", out _));
    }

    [Fact]
    public void EmptyDefaultsFillListsAndMaps()
    {
        var bag = new DiagnosticBag();
        var schemaText = """
            kedo 1
            module s
            kedo_record step { fields = [{ name = amount  kind = int  default = 0  required = true }, { name = next  kind = record  record = step  default = { } }] }
            kedo_type service { fields = [
                { name = costs  kind = map  element = { kind = int }  default = { }  required = true }
                { name = effects  kind = list  element = { kind = record  record = step }  default = []  required = true }
                { name = bad  kind = list  element = { kind = int }  default = [1] }
                { name = worse  kind = int  default = [] }
            ] }
            """;
        var schema = Kedo.Compiler.Schema.SchemaLoader.Load(new[] { Kedo.Compiler.Model.DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(schemaText, "s.kedo"), bag), bag) }, bag);
        Assert.Equal(3, bag.Items.Count(d => d.Message.Contains("a list default can only be `[]`")));
        var field = schema.FindType("service")!.Body.Find("effects")!;
        Assert.True(field.DefaultEmpty);

        var content = new DiagnosticBag();
        var source = """
            kedo 1
            module m
            service s { }
            service t { costs = { water = 2 }  effects = [{ amount = 1 }] }
            """;
        var decls = new[] { Kedo.Compiler.Model.DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(source, "c.kedo"), content), content) };
        var catalog = Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, content);
        Assert.False(content.HasErrors, string.Join("; ", content.Items.Select(d => d.Code + " " + d.Message)));
        var s = catalog.ByKey["service:s"].Body.Fields;
        Assert.Equal("{  }", s.First(f => f.Key == "costs").Value.CanonicalText);
        Assert.Equal("[]", s.First(f => f.Key == "effects").Value.CanonicalText);
        Assert.DoesNotContain(content.Items, d => d.Code is "K3011" or "K3014" or "K3015");
        var effects = (RList)catalog.ByKey["service:t"].Body.Fields.First(f => f.Key == "effects").Value;
        Assert.Equal("1", ((RRecord)Assert.Single(effects.Items)).Fields.First(f => f.Key == "amount").Value.CanonicalText);
        Assert.Equal("{ water = 2 }", catalog.ByKey["service:t"].Body.Fields.First(f => f.Key == "costs").Value.CanonicalText);
    }
}
