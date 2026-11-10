using BenchmarkDotNet.Attributes;
using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;

namespace Kedo.Benchmarks;

/// <summary>
/// Fast entry (ADR-0015): cost of one activation of a typical hook (two context reads, a query, a function call, an effect
/// and a signal; 19 steps) on <see cref="FastBehaviorVm"/>, alone and in a batch, against <see cref="BehaviorVm"/>; and a
/// suspension with its resumption (pooled frame). Gates in baseline.json: 0 B per activation, ns per activation.
/// </summary>
[MemoryDiagnoser]
public class FastEntryBenchmarks
{
    private const int Activations = 1_000;
    private const int Batch = 1_024;

    private const string Source = """
        kedo 1
        module bench
        manifest { base_unit = ms }
        function threshold(phase: int) -> int { return 100 - phase * 25 }
        enemy warden {
            on damaged(ctx) {
                if (ctx.boss.hp_percent() <= threshold(ctx.phase)) {
                    apply effect:set_phase(ctx.boss, ctx.phase + 1)
                    emit signal:phase_changed(ctx.boss)
                }
            }
            on enrage(ctx) {
                wait 350ms
                apply effect:spawn_wave(3)
            }
        }
        """;

    private BehaviorProgram _program = null!;
    private FastBehaviorVm _vm = null!;
    private HookHandle _damaged;
    private HookHandle _enrage;
    private long[] _instances = null!;
    private HostValue[] _columns = null!;
    private RunOutcome[] _outcomes = null!;
    private FrameHandle[] _frames = null!;
    private LegacyHost _legacyHost = null!;

    [GlobalSetup]
    public void Setup()
    {
        var bag = new DiagnosticBag();
        var decls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(Source, "bench.kedo"), bag), bag);
        var schema = new SchemaRegistry { Permissive = true };
        var catalog = Resolver.Resolve(new[] { decls }, schema, bag);
        _program = BehaviorCompiler.Compile(catalog.ByKey["enemy:warden"], decls.Functions, schema, bag)!;
        if (bag.HasErrors)
        {
            throw new InvalidOperationException(string.Join("\n", bag.Items.Select(d => d.Message)));
        }

        var bindings = new BindingTable()
            .Context("boss", 0).Context("phase", 1)
            .Query("hp_percent", 0)
            .Effect("effect:set_phase", 0).Effect("effect:spawn_wave", 1)
            .Signal("signal:phase_changed", 0);
        var bound = BehaviorBinder.Bind(_program, bindings, out var diagnostics);
        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException(diagnostics[0].Message);
        }

        var boss = HostValue.Symbol(bindings.Symbols.Intern("enemy:boss"));
        _vm = new FastBehaviorVm(bound, new FastHost(boss), new Budget(MaxSteps: 1_000));
        _damaged = bound.Hook("damaged");
        _enrage = bound.Hook("enrage");
        _instances = Enumerable.Range(0, Batch).Select(i => (long)i).ToArray();
        _columns = new HostValue[2 * Batch];
        for (var i = 0; i < Batch; i++)
        {
            _columns[i] = boss;
            _columns[Batch + i] = HostValue.Int(1);
        }

        _outcomes = new RunOutcome[Batch];
        _frames = new FrameHandle[Activations];
        _legacyHost = new LegacyHost();
        _legacyHost.BossHandle = HostValue.Symbol(_legacyHost.Symbols.Intern("enemy:boss"));
    }

    /// <summary>One activation of `damaged` by handle (mean per activation).</summary>
    [Benchmark(OperationsPerInvoke = Activations)]
    public long FastActivate()
    {
        var outcome = new RunOutcome();
        long steps = 0;
        for (var i = 0; i < Activations; i++)
        {
            _vm.Activate(_damaged, i, ref outcome);
            steps += outcome.Steps;
        }

        return steps;
    }

    /// <summary>A batch of 1024 activations of `damaged` with the context in columns (mean per activation).</summary>
    [Benchmark(OperationsPerInvoke = Batch)]
    public RunStatus FastActivateBatch1024()
    {
        _vm.ActivateBatch(_damaged, _instances, _columns, _outcomes);
        return _outcomes[Batch - 1].Status;
    }

    /// <summary>`enrage`: activation that suspends on `wait 350ms` (pooled frame) and its resumption (mean per pair).</summary>
    [Benchmark(OperationsPerInvoke = Activations)]
    public RunStatus FastSuspendResume()
    {
        var outcome = new RunOutcome();
        for (var i = 0; i < Activations; i++)
        {
            _vm.Activate(_enrage, i, ref outcome);
            _frames[i] = outcome.Frame;
        }

        for (var i = 0; i < Activations; i++)
        {
            _vm.Resume(_frames[i], AwaitKind.Minutes, 0, ref outcome);
        }

        return outcome.Status;
    }

    /// <summary>The same activation on <see cref="BehaviorVm"/> by name (reference, 1.0 contract).</summary>
    [Benchmark(OperationsPerInvoke = Activations)]
    public int LegacyActivate()
    {
        var vm = new BehaviorVm(_program, _legacyHost, new Budget(MaxSteps: 1_000));
        var count = 0;
        for (var i = 0; i < Activations; i++)
        {
            count += vm.Activate("damaged", "i").Trace.Count;
        }

        return count;
    }

    private sealed class FastHost : IFastBehaviorHost
    {
        private readonly HostValue _boss;

        public FastHost(HostValue boss) => _boss = boss;

        public long Now(DurationUnit unit) => 0;

        public HostValue Context(long instance, int slot) => slot == 0 ? _boss : HostValue.Int(1);

        public HostValue Query(int queryId, ReadOnlySpan<HostValue> args) => HostValue.Int(50);

        public bool Apply(in FastEffectKey key, int effectId, ReadOnlySpan<HostValue> args) => true;

        public void Raise(int signalId, ReadOnlySpan<HostValue> args) { }
    }

    private sealed class LegacyHost : IBehaviorHost
    {
        public HostValue BossHandle { get; set; }
        public long NowMinutes => 0;
        public SymbolTable Symbols { get; } = new();
        public HostValue Context(string field) => field == "boss" ? BossHandle : HostValue.Int(1);
        public HostValue Query(string queryId, ReadOnlySpan<HostValue> args) => HostValue.Int(50);
        public bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args) => true;
        public long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args) => 0;
        public void Raise(string signalId, ReadOnlySpan<HostValue> args) { }
        public void Trace(string message) { }
    }
}
