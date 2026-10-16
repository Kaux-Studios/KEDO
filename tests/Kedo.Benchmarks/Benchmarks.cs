using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Formatting;
using Kedo.Compiler.Model;
using Kedo.Compiler.Pack;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Pack;
using Kedo.Runtime;
using Kedo.Schema;

namespace Kedo.Benchmarks;

/// <summary>Toolchain benchmarks over a synthetic module (ADR-0007 D10). Budgets: see spec/adr/0007 D10.</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class ToolchainBenchmarks
{
    [Params(1_000, 10_000)]
    public int Objects { get; set; }

    private SourceText _source = null!;
    private byte[] _bytes = Array.Empty<byte>();
    private SyntaxTree _tree = null!;
    private FileDeclarations _decls = null!;
    private ResolvedCatalog _catalog = null!;
    private byte[] _pack = Array.Empty<byte>();
    private readonly SchemaRegistry _schema = new() { Permissive = true };

    [GlobalSetup]
    public void Setup()
    {
        _source = SourceText.FromString(SyntheticCorpus.Generate(Objects), "bench.kedo");
        _bytes = _source.Bytes;
        var bag = new DiagnosticBag();
        _tree = Parser.Parse(_source, bag);
        _decls = DeclarationBuilder.Build(_tree, bag);
        _catalog = Resolver.Resolve(new[] { _decls }, _schema, bag);
        var programs = _catalog.Objects.Select(o => BehaviorCompiler.Compile(o, _decls.Functions, _schema, bag)).Where(p => p is not null).Select(p => p!).ToList();
        _pack = PackWriter.Write(_catalog, programs);
        if (bag.HasErrors)
        {
            throw new InvalidOperationException(string.Join("\n", bag.Items.Select(d => d.Message)));
        }
    }

    /// <summary>Source bytes processed, for MB/s.</summary>
    public long SourceBytes => _bytes.Length;

    [Benchmark]
    public int Lex() => Lexer.Lex(_source, new DiagnosticBag()).Length;

    [Benchmark]
    public int Parse() => Parser.Parse(_source, new DiagnosticBag()).NodeCount;

    [Benchmark]
    public int ParseAndBuildDeclarations()
    {
        var bag = new DiagnosticBag();
        return DeclarationBuilder.Build(Parser.Parse(_source, bag), bag).Objects.Count;
    }

    [Benchmark]
    public int Resolve() => Resolver.Resolve(new[] { _decls }, _schema, new DiagnosticBag()).Objects.Count;

    [Benchmark]
    public int Format() => Formatter.Format(_tree).Length;

    [Benchmark]
    public int WritePack()
    {
        var bag = new DiagnosticBag();
        var programs = _catalog.Objects.Select(o => BehaviorCompiler.Compile(o, _decls.Functions, _schema, bag)).Where(p => p is not null).Select(p => p!).ToList();
        return PackWriter.Write(_catalog, programs).Length;
    }

    [Benchmark]
    public int LoadPack() => KedoPack.Load(_pack).Objects.Count;
}

/// <summary>VM throughput on a synthetic straight-line program (no allocations per instruction expected).</summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class VmBenchmarks
{
    private const int Instructions = 100_000;
    private BehaviorProgram _program = null!;
    private NullHost _host = null!;

    [GlobalSetup]
    public void Setup()
    {
        // r0 = 1; then alternate r1 = r0 + r0 ; r0 = r1 - r0 ... Instructions times; Stop.
        var code = new List<uint> { Instr.AImm(OpCode.LoadConst, 0, 0), Instr.AImm(OpCode.LoadConst, 1, 1) };
        for (var i = 0; i < Instructions; i++)
        {
            code.Add(i % 3 == 0 ? Instr.Abc(OpCode.Add, 2, 0, 1) : i % 3 == 1 ? Instr.Abc(OpCode.Sub, 0, 2, 1) : Instr.Abc(OpCode.Lt, 3, 0, 2));
        }

        code.Add(Instr.Abc(OpCode.Stop, 0, 0, 0));
        var fn = new FunctionInfo("on tick", 0, 4, 0);
        _program = new BehaviorProgram("bench", 1, "h", code.ToArray(), new long[] { 1, 2 }, Array.Empty<string>(), new[] { fn },
            new Dictionary<string, int> { ["tick"] = 0 }, Array.Empty<AwaitSite>(), Enumerable.Repeat("bench", code.Count).ToList());
        _host = new NullHost();
    }

    [Benchmark(OperationsPerInvoke = Instructions)]
    public RunStatus Run100kInstructions() => new BehaviorVm(_program, _host, new Budget(MaxSteps: Instructions + 10)).Activate("tick", "i").Status;

    private sealed class NullHost : IBehaviorHost
    {
        public long NowMinutes => 0;
        public SymbolTable Symbols { get; } = new();
        public HostValue Context(string field) => HostValue.None;
        public HostValue Query(string queryId, ReadOnlySpan<HostValue> args) => HostValue.Int(0);
        public bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args) => true;
        public long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args) => 0;
        public void Raise(string signalId, ReadOnlySpan<HostValue> args) { }
        public void Trace(string message) { }
    }
}
