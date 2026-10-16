using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Pack;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Pack;
using Kedo.Runtime;
using Kedo.Schema;

public class PackTests
{
    private static (byte[] bytes, ResolvedCatalog catalog, DiagnosticBag bag) BuildExamples()
    {
        var root = Path.Combine(ParserTests.RepoRoot(), "spec", "ejemplos");
        var bag = new DiagnosticBag();
        var schemaDecls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromFile(Path.Combine(root, "schema", "patriark.kedo")), bag), bag);
        var schema = SchemaLoader.Load(new[] { schemaDecls }, bag);
        var decls = new[] { "sentinel.kedo", "bridge.kedo", "campaign.kedo" }
            .Select(f => DeclarationBuilder.Build(Parser.Parse(SourceText.FromFile(Path.Combine(root, f)), bag), bag)).ToList();
        var catalog = Resolver.Resolve(decls, schema, bag);
        var moduleFunctions = decls.SelectMany(d => d.Functions).ToList();
        var programs = catalog.Objects.Select(o => BehaviorCompiler.Compile(o, moduleFunctions, schema, bag)).Where(p => p is not null).Select(p => p!).ToList();
        return (PackWriter.Write(catalog, programs), catalog, bag);
    }

    [Fact]
    public void PackRoundTripsObjectsValuesAndPrograms()
    {
        var (bytes, catalog, bag) = BuildExamples();
        Assert.False(bag.HasErrors, string.Join("\n", bag.Items.Select(d => d.Message)));
        var pack = KedoPack.Load(bytes);
        Assert.Equal(catalog.Hash, pack.CatalogHash);
        Assert.Equal(catalog.Concrete.Count(), pack.Objects.Count);

        var depot = pack.Find("event", "road.sentinel.depot")!;
        Assert.Equal(catalog.ByKey["event:road.sentinel.depot"].Hash, depot.Hash);
        Assert.Equal(2, depot.Body.Int("version"));
        Assert.Equal("travel", depot.Body.Symbol("context"));
        var options = depot.Body.List("options")!;
        Assert.Equal(4, options.Items.Count);
        Assert.Equal(14, options.ByKey("risk")!.Record("check")!.Int("difficulty"));
        Assert.Equal(15, options.ByKey("distract")!.Int("duration"));

        var sentinel = pack.Find("actor:depot_sentinel")!;
        Assert.Equal(60, sentinel.Components["health"].Int("max"));
        Assert.False(sentinel.Components.ContainsKey("patrol"));
        Assert.Equal("asset:sentinel_sheet", sentinel.Components["sprite"].Symbol("sheet"));
        Assert.Equal(new[] { "arrive" }, sentinel.Hooks);

        // The program travels with the pack and runs from it.
        var program = pack.ProgramOf(sentinel)!;
        var host = new FakeHost();
        host.Queries["detected_by"] = HostValue.Int(1);
        host.ContextValues["place"] = host.Sym("place:depot");
        var result = new BehaviorVm(program, host).Activate("arrive", "i");
        Assert.Equal(RunStatus.Suspended, result.Status);
        Assert.Equal("arrive/1", result.Frame!.Pending!.Label);
    }

    [Fact]
    public void CorruptionAndWrongFormatAreRejected()
    {
        var (bytes, _, _) = BuildExamples();
        var flipped = (byte[])bytes.Clone();
        flipped[bytes.Length / 2] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => KedoPack.Load(flipped));
        var wrongFormat = (byte[])bytes.Clone();
        wrongFormat[4] = 9;
        Assert.Throws<InvalidDataException>(() => KedoPack.Load(wrongFormat));
        Assert.Throws<InvalidDataException>(() => KedoPack.Load(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void PackBytesAreDeterministic()
    {
        var a = BuildExamples().bytes;
        var b = BuildExamples().bytes;
        Assert.Equal(a, b);
    }
}
