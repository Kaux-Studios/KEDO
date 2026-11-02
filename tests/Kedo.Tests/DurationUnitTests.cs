using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;

/// <summary>Duration base unit (ADR-0014).</summary>
public class DurationUnitTests
{
    private static (ResolvedCatalog Catalog, DiagnosticBag Bag) Resolve(string content, string? baseUnit, SchemaRegistry? schema = null)
    {
        var bag = new DiagnosticBag();
        var decls = Build(content, baseUnit, bag);
        return (Resolver.Resolve(new[] { decls }, schema ?? new SchemaRegistry { Permissive = true }, bag), bag);
    }

    private static FileDeclarations Build(string content, string? baseUnit, DiagnosticBag bag)
    {
        var text = "kedo 1\nmodule m\n" + (baseUnit is null ? "" : $"manifest {{ base_unit = {baseUnit} }}\n") + content;
        return DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "c.kedo"), bag), bag);
    }

    private static ResolvedValue Field(ResolvedCatalog catalog, string key, string field) => catalog.ByKey[key].Body.Fields.TryGetValue(field, out var v) ? v : throw new KeyNotFoundException(field);

    private static BehaviorProgram Program(string content, string? baseUnit, string key)
    {
        var bag = new DiagnosticBag();
        var schema = new SchemaRegistry { Permissive = true };
        var decls = Build(content, baseUnit, bag);
        var catalog = Resolver.Resolve(new[] { decls }, schema, bag);
        var program = BehaviorCompiler.Compile(catalog.ByKey[key], decls.Functions, schema, bag);
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items.Select(d => d.Message)));
        return program!;
    }

    [Fact]
    public void TheManifestSetsTheUnitAndMinutesAreTheDefault()
    {
        Assert.Equal(DurationUnit.Minute, Resolve("thing t { a = 1min }", baseUnit: null).Catalog.TimeUnit);
        var (catalog, bag) = Resolve("thing t { a = 2s }", baseUnit: "ms");
        Assert.False(bag.HasErrors);
        Assert.Equal(DurationUnit.Millisecond, catalog.TimeUnit);
        Assert.Equal(DurationUnit.Millisecond, catalog.ByKey["thing:t"].TimeUnit);
    }

    [Fact]
    public void AnUnknownUnitWarnsAndKeepsMinutes()
    {
        var (catalog, bag) = Resolve("thing t { a = 1min }", baseUnit: "minutes");
        Assert.Equal(DurationUnit.Minute, catalog.TimeUnit);
        Assert.Contains(bag.Items, d => d.Code == "K2011" && d.Severity == Severity.Warning && d.Message.Contains("`base_unit = minutes`"));
        Assert.False(bag.HasErrors);
    }

    [Fact]
    public void AVariantCannotChangeTheUnit()
    {
        var bag = new DiagnosticBag();
        var files = new[]
        {
            "kedo 1\nmodule m\nmanifest { base_unit = ms }\nthing t { a = 2s }\n",
            "kedo 1\nmodule m\nvariant fast\nmanifest { base_unit = s }\noverride thing t { a = 1s }\n",
        }.Select((text, i) => DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, $"f{i}.kedo"), bag), bag)).ToList();
        var catalog = Resolver.Resolve(files, new SchemaRegistry { Permissive = true }, bag);
        Assert.Contains(bag.Items, d => d.Code == "K2012" && d.IsError && d.File == "f1.kedo");
        Assert.Equal(DurationUnit.Minute, catalog.TimeUnit);
        Assert.Equal(DurationUnit.Minute, catalog.Variants["fast"].TimeUnit);
        Assert.DoesNotContain(bag.Items, d => d.Code == "K4014");
    }

    [Fact]
    public void LegacyMinuteContentKeepsItsValues()
    {
        var (catalog, bag) = Resolve("thing t { a = 30s  b = 90s  c = (90s + 30s)  d = 1h  e = 2d  f = -30s  g = 15 }", baseUnit: null);
        Assert.False(bag.HasErrors);
        Assert.Equal("0min", Field(catalog, "thing:t", "a").CanonicalText);
        Assert.Equal("1min", Field(catalog, "thing:t", "b").CanonicalText);
        Assert.Equal("1min", Field(catalog, "thing:t", "c").CanonicalText);
        Assert.Equal("60min", Field(catalog, "thing:t", "d").CanonicalText);
        Assert.Equal("2880min", Field(catalog, "thing:t", "e").CanonicalText);
        Assert.Equal("0min", Field(catalog, "thing:t", "f").CanonicalText);
    }

    [Fact]
    public void LegacyWaitInSecondsLoadsWholeMinutes()
    {
        var program = Program("actor z { on go(ctx) { wait 90s } }", baseUnit: null, "actor:z");
        Assert.Contains(1L, program.Constants);
        Assert.DoesNotContain(90L, program.Constants);
    }

    [Fact]
    public void ExamplePackAndFramesArePinned()
    {
        var (bytes, catalog, bag) = PackTests.BuildExamples();
        Assert.False(bag.HasErrors);
        Assert.Equal("6BA542D72CE9F06CC01B9C9BE4AA7F011266A779F2DF03497DBA6143774365ED", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.Equal("79c0b6b66ad0d605f9ef70e823668efe", catalog.Hash);
        var program = Program("actor s { on arrive(ctx) { wait 30min\n apply effect:alarm(ctx.place) } }", baseUnit: null, "actor:s");
        var host = new FakeHost();
        host.ContextValues["place"] = host.Sym("place:depot");
        var frame = new BehaviorVm(program, host).Activate("arrive", "inst-1").Frame!;
        Assert.Equal("""{"format_version":1,"instance_id":"inst-1","behavior_id":"actor:s","definition_version":1,"definition_hash":"d56816832e5b3548b74e93aa7ec50015","hook_id":"arrive","stack":[{"function_index":0,"function_name":"on arrive","register_base":0,"register_count":1,"return_register":-1,"resume_label":"arrive/1","pc":1}],"registers":[30],"symbol_names":[null],"pending":{"kind":0,"label":"arrive/1","due_at_minutes":1030,"id":null,"options":[],"result_register":-1},"applied_effects":[],"steps_used":2,"effect_sequence":0}""", BehaviorVm.Save(frame));
    }

    [Fact]
    public void UnitsConvertExactlyOrSayWhy()
    {
        Assert.True(DurationUnits.TryConvert(2, DurationUnit.Second, DurationUnit.Millisecond, out var ms));
        Assert.Equal(2000, ms);
        Assert.False(DurationUnits.TryConvert(90, DurationUnit.Minute, DurationUnit.Hour, out _));
        Assert.True(DurationUnits.TryConvert(120, DurationUnit.Minute, DurationUnit.Hour, out var h));
        Assert.Equal(2, h);
        Assert.False(DurationUnits.TryConvert(250, DurationUnit.Millisecond, DurationUnit.Second, out _));
        Assert.False(DurationUnits.TryConvert(106_751_991_167_301, DurationUnit.Day, DurationUnit.Millisecond, out _));
        Assert.Equal(0, DurationUnits.ConvertTruncating(30, DurationUnit.Second, DurationUnit.Minute));
        Assert.Equal(1, DurationUnits.ConvertTruncating(90, DurationUnit.Second, DurationUnit.Minute));
        Assert.Equal(0, DurationUnits.ConvertTruncating(-30, DurationUnit.Second, DurationUnit.Minute));
        Assert.Equal(long.MaxValue, DurationUnits.ConvertTruncating(long.MaxValue / 2, DurationUnit.Day, DurationUnit.Millisecond));
        Assert.True(DurationUnits.TryParseSuffix("ms", out var u) && u == DurationUnit.Millisecond);
        Assert.False(DurationUnits.TryParseSuffix("minutes", out _));
        Assert.Equal("350ms", DurationUnits.Format(350, DurationUnit.Millisecond));
        Assert.Equal(DurationUnit.Minute, default(DurationUnit));
    }

    [Fact]
    public void MillisecondLiteralsAreOneTokenAndDecimalsStayRejected()
    {
        var bag = new DiagnosticBag();
        var tokens = Lexer.Lex(SourceText.FromString("a = 350ms\nb = 1.5ms\nc = 5mss\n", "l.kedo"), bag);
        Assert.Equal(1, tokens.Count(t => t.Kind == TokenKind.Duration));
        Assert.Contains(bag.Items, d => d.Code == "K1002" && d.Message.Contains("whole numbers"));
        Assert.Contains(bag.Items, d => d.Code == "K1002" && d.Message == "invalid number suffix `mss`; durations use `s`, `min`, `h` or `d`");
        Assert.Equal(0, new DurationValue(default, 1500, "ms").Minutes);
    }

    [Fact]
    public void MillisecondLiteralsInHooksCompileToTruncatedMinutes()
    {
        var program = Program("actor z { on go(ctx) { wait 350ms; wait 120000ms } }", baseUnit: null, "actor:z");
        Assert.Contains(0L, program.Constants);
        Assert.Contains(2L, program.Constants);
        Assert.DoesNotContain(350L, program.Constants);
        Assert.DoesNotContain(120000L, program.Constants);
    }

    [Fact]
    public void FormatterPreservesMillisecondLiterals()
    {
        const string text = "kedo 1\nmodule m\n\nthing t {\n    a = 350ms\n    b = 1_500ms\n}\n";
        var bag = new DiagnosticBag();
        var formatted = Kedo.Compiler.Formatting.Formatter.Format(Parser.Parse(SourceText.FromString(text, "f.kedo"), bag));
        Assert.False(bag.HasErrors);
        Assert.Contains("350ms", formatted);
        Assert.Contains("1_500ms", formatted);
        var again = Kedo.Compiler.Formatting.Formatter.Format(Parser.Parse(SourceText.FromString(formatted, "f.kedo"), new DiagnosticBag()));
        Assert.Equal(formatted, again);
    }
}
