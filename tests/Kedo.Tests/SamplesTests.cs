using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Pack;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Pack;
using Kedo.Runtime;

/// <summary>The sample game under samples/ must validate with its own closed schema, compile and pack: it guards against Patriark assumptions leaking into the core.</summary>
public class SamplesTests
{
    private static string Sample(string name) => Path.Combine(ParserTests.RepoRoot(), "samples", name);

    private static string Errors(DiagnosticBag bag) => string.Join("\n", bag.Items.Select(d => $"{d.Code} {Path.GetFileName(d.File)}:{d.Span} {d.Message}"));

    [Fact]
    public void OutpostValidatesCompilesAndPacks()
    {
        var root = Sample("outpost");
        var bag = new DiagnosticBag();
        var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromFile(Path.Combine(root, "schema", "outpost.kedo")), bag), bag) }, bag);
        Assert.False(bag.HasErrors, Errors(bag));

        var decls = Directory.GetFiles(root, "*.kedo").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => DeclarationBuilder.Build(Parser.Parse(SourceText.FromFile(f), bag), bag)).ToList();
        var catalog = Resolver.Resolve(decls, schema, bag);
        Assert.False(bag.HasErrors, Errors(bag));

        var reactor = catalog.ByKey["room:reactor"];
        Assert.False(reactor.Components.TryGetValue("soundscape", out _));
        Assert.Equal("40", ((RRecord)reactor.Components.First(c => c.Key == "life_support").Value).Fields.First(f => f.Key == "power").Value.CanonicalText);
        var minor = catalog.ByKey["crisis:coolant_leak_minor"];
        var choices = (RList)minor.Body.Fields.First(f => f.Key == "choices").Value;
        Assert.Equal(new[] { "patch", "wait" }, choices.Items.Select(i => RList.KeyOf(i, "id")));

        var functions = decls.SelectMany(d => d.Functions).ToList();
        var programs = catalog.Objects.Select(o => BehaviorCompiler.Compile(o, functions, schema, bag)).Where(p => p is not null).Select(p => p!).ToList();
        Assert.False(bag.HasErrors, Errors(bag));
        Assert.Contains(programs, p => p.BehaviorId == "crisis:coolant_leak" && p.AwaitSites.Count >= 3);

        var pack = KedoPack.Load(PackWriter.Write(catalog, programs));
        var leak = pack.Find("crisis:coolant_leak")!;
        var program = pack.ProgramOf(leak)!;
        var host = new FakeHost();
        host.ContextValues["choice_id"] = host.Sym("patch");
        host.ContextValues["room"] = host.Sym("room:reactor");
        host.Queries["integrity"] = HostValue.Int(55);
        var result = new BehaviorVm(program, host).Activate("resolved", "leak-1");
        Assert.Equal(RunStatus.Suspended, result.Status);
        Assert.Contains("effect:consume(resource.parts,4)", host.Applied[0]);
        var done = new BehaviorVm(program, host).Resume(result.Frame!, new Resumption(AwaitKind.Minutes, 0));
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Contains("effect:restore(room:reactor,20)", host.Applied[1]);
    }
}
