using System.Text.Json;
using Kedo.Compiler.Editing;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

/// <summary>Structural edits and object descriptions used by the addon's forms (#14).</summary>
public class EditingTests
{
    private const string Source = """
        kedo 1
        module m

        /// The reactor.
        room reactor extends base {
            title = "Reactor"  // keep this comment
            stats = { hp = 3 }
            tags += [hot]
        }

        room small { }
        """;

    [Fact]
    public void SetReplacesAnExistingEntryAndKeepsComments()
    {
        var result = SourceEditor.SetField(Source, "room", "reactor", "title", "\"Main reactor\"");
        Assert.True(result.Ok, result.Error);
        Assert.Contains("title = \"Main reactor\"  // keep this comment", result.Text);
        Assert.Contains("/// The reactor.", result.Text);
    }

    [Fact]
    public void SetAddsEntriesAndNestedRecords()
    {
        var added = SourceEditor.SetField(Source, "room", "reactor", "capacity", "4");
        Assert.True(added.Ok, added.Error);
        Assert.Contains("    capacity = 4\n}", added.Text);

        var nested = SourceEditor.SetField(Source, "room", "reactor", "stats.armor", "2");
        Assert.True(nested.Ok, nested.Error);
        Assert.Contains("stats = { hp = 3  armor = 2 }", nested.Text);

        var deep = SourceEditor.SetField(Source, "room", "small", "power.grid.load", "7");
        Assert.True(deep.Ok, deep.Error);
        Assert.Contains("room small { power = { grid = { load = 7 } } }", deep.Text);
    }

    [Fact]
    public void SetRefusesWhatIsNotOneValue()
    {
        Assert.False(SourceEditor.SetField(Source, "room", "reactor", "title", "a = 1").Ok);
        Assert.False(SourceEditor.SetField(Source, "room", "reactor", "title", "1 2").Ok);
        Assert.False(SourceEditor.SetField(Source, "room", "reactor", "title", "").Ok);
        Assert.False(SourceEditor.SetField(Source, "room", "missing", "title", "1").Ok);
        Assert.False(SourceEditor.SetField(Source, "room", "reactor", "title", "1", "+:").Ok);
    }

    [Fact]
    public void UnsetNewAndDuplicate()
    {
        var unset = SourceEditor.RemoveField(Source, "room", "reactor", "title");
        Assert.True(unset.Ok, unset.Error);
        Assert.DoesNotContain("title", unset.Text);
        Assert.False(SourceEditor.RemoveField(Source, "room", "reactor", "nothing").Ok);

        var created = SourceEditor.AddObject(Source, "room", "spare", "base");
        Assert.True(created.Ok, created.Error);
        Assert.EndsWith("room spare extends base { }\n", created.Text);
        Assert.False(SourceEditor.AddObject(Source, "room", "reactor").Ok);
        Assert.False(SourceEditor.AddObject(Source, "room", "bad id").Ok);

        var copy = SourceEditor.DuplicateObject(Source, "room", "reactor", "reactor_b");
        Assert.True(copy.Ok, copy.Error);
        Assert.Contains("room reactor_b extends base {", copy.Text);
        Assert.Equal(2, copy.Text!.Split("title = \"Reactor\"").Length - 1);
    }

    [Fact]
    public void EditedTextIsCanonical()
    {
        var result = SourceEditor.SetField(Source, "room", "reactor", "capacity", "4");
        var tree = Parser.Parse(SourceText.FromString(result.Text!), new DiagnosticBag());
        Assert.Equal(result.Text, Kedo.Compiler.Formatting.Formatter.Format(tree));
    }

    [Fact]
    public void DescribeListsSchemaFieldsWithLayers()
    {
        var bag = new DiagnosticBag();
        var schemaText = """
            kedo 1
            module s
            kedo_enum mood { values = [calm, tense] }
            kedo_record stats { fields = [{ name = hp  kind = int  min = 0 }, { name = armor  kind = int  default = 1 }] }
            kedo_type room { fields = [{ name = title  kind = text  required = true  doc = "Shown name" }, { name = mood  kind = enum  enum = mood }, { name = stats  kind = record  record = stats }, { name = next  kind = reference  target = room }] }
            """;
        var schema = Kedo.Compiler.Schema.SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(schemaText, "s.kedo"), bag), bag) }, bag);
        var content = """
            kedo 1
            module m
            room base { title = "Base"  mood = calm }
            room reactor extends base { stats = { hp = 3 } }
            """;
        var catalog = Resolver.Resolve(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(content, "c.kedo"), bag), bag) }, schema, bag);
        Assert.False(bag.HasErrors, string.Join("; ", bag.Items.Select(d => d.Message)));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            ObjectDescriber.Write(writer, catalog.ByKey["room:reactor"], schema);
        }

        using var json = JsonDocument.Parse(stream.ToArray());
        var root = json.RootElement;
        Assert.Equal("base", root.GetProperty("parent").GetString());
        var fields = root.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        Assert.Equal(new[] { "title", "mood", "stats", "next" }, fields.Keys);
        Assert.Equal("parent", fields["title"].GetProperty("layer").GetString());
        Assert.Equal("Shown name", fields["title"].GetProperty("doc").GetString());
        Assert.Equal(new[] { "calm", "tense" }, fields["mood"].GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("absent", fields["next"].GetProperty("layer").GetString());
        Assert.Equal("room", fields["next"].GetProperty("target").GetString());
        var stats = fields["stats"].GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        Assert.Equal("own", stats["hp"].GetProperty("layer").GetString());
        Assert.Equal("default", stats["armor"].GetProperty("layer").GetString());
    }
}
