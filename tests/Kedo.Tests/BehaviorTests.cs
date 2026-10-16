using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;

/// <summary>Scripted host for tests: records calls, answers queries from a table, provides context fields.</summary>
public sealed class FakeHost : IBehaviorHost
{
    public SymbolTable Symbols { get; } = new();
    public long NowMinutes { get; set; } = 1000;
    public Dictionary<string, HostValue> ContextValues { get; } = new();
    public Dictionary<string, HostValue> Queries { get; } = new();
    public List<string> Applied { get; } = new();
    public List<string> Raised { get; } = new();
    public List<string> Traces { get; } = new();

    public HostValue Context(string field) => ContextValues.TryGetValue(field, out var v) ? v : HostValue.None;

    public HostValue Query(string queryId, ReadOnlySpan<HostValue> args)
        => Queries.TryGetValue(queryId, out var v) ? v : HostValue.Int(0);

    public bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args)
    {
        var rendered = string.Join(",", args.ToArray().Select(a => a.IsSymbol ? Symbols.Name(a.Number) ?? "?" : a.Number.ToString()));
        Applied.Add($"{key} {effectId}({rendered})");
        return true;
    }

    public long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args) => 17;

    public void Raise(string signalId, ReadOnlySpan<HostValue> args) => Raised.Add(signalId);

    public void Trace(string message) => Traces.Add(message);

    public HostValue Sym(string name) => HostValue.Symbol(Symbols.Intern(name));
}

public class BehaviorTests
{
    private static (BehaviorProgram? program, ResolvedCatalog catalog, DiagnosticBag bag) Compile(string text, string key, SchemaRegistry? schema = null)
    {
        var bag = new DiagnosticBag();
        var decls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "b.kedo"), bag), bag);
        var catalog = Resolver.Resolve(new[] { decls }, schema ?? new SchemaRegistry { Permissive = true }, bag);
        var program = BehaviorCompiler.Compile(catalog.ByKey[key], decls.Functions, schema ?? new SchemaRegistry { Permissive = true }, bag);
        return (program, catalog, bag);
    }

    private static string Errors(DiagnosticBag bag) => string.Join("\n", bag.Items.Select(d => $"{d.Code} {d.Message}"));

    private const string Sentinel = """
        kedo 1
        module m
        actor depot_sentinel {
            on arrive(ctx) {
                if (ctx.character.detected_by(actor:depot_sentinel) and not ctx.quiet) {
                    wait 30min
                    apply effect:alarm_raised(ctx.place)
                } else {
                    stop
                }
            }
        }
        """;

    [Fact]
    public void HookSuspendsOnWaitAndResumesIdempotently()
    {
        var (program, _, bag) = Compile(Sentinel, "actor:depot_sentinel");
        Assert.False(bag.HasErrors, Errors(bag));
        Assert.NotNull(program);
        var host = new FakeHost();
        host.ContextValues["character"] = host.Sym("character:mara");
        host.ContextValues["place"] = host.Sym("place:depot");
        host.ContextValues["quiet"] = HostValue.Int(0);
        host.Queries["detected_by"] = HostValue.Int(1);

        var vm = new BehaviorVm(program!, host);
        var first = vm.Activate("arrive", "inst-1");
        Assert.Equal(RunStatus.Suspended, first.Status);
        Assert.Equal(AwaitKind.Minutes, first.Frame!.Pending!.Kind);
        Assert.Equal(1030, first.Frame.Pending.DueAtMinutes);
        Assert.Equal("arrive/1", first.Frame.Pending.Label);
        Assert.Empty(host.Applied);

        // Round-trip through JSON and a new session (new symbol table) before resuming.
        var json = BehaviorVm.Save(first.Frame);
        var loaded = BehaviorVm.Load(json)!;
        var host2 = new FakeHost();
        host2.ContextValues["place"] = host2.Sym("place:depot");
        var vm2 = new BehaviorVm(program!, host2);
        var second = vm2.Resume(loaded, new Resumption(AwaitKind.Minutes, 0));
        Assert.Equal(RunStatus.Completed, second.Status);
        Assert.Single(host2.Applied);
        Assert.Contains("alarm_raised(place:depot)", host2.Applied[0]);
        Assert.StartsWith("inst-1/", host2.Applied[0]);

        // Resuming the same frame again must not re-apply (receipts travel with the frame).
        var vm3 = new BehaviorVm(program!, host2);
        var third = vm3.Resume(loaded with { AppliedEffects = new[] { host2.Applied[0].Split(' ')[0] } }, new Resumption(AwaitKind.Minutes, 0));
        Assert.Equal(RunStatus.Completed, third.Status);
        Assert.Single(host2.Applied);
    }

    [Fact]
    public void ElseBranchStopsWithoutEffects()
    {
        var (program, _, _) = Compile(Sentinel, "actor:depot_sentinel");
        var host = new FakeHost();
        host.Queries["detected_by"] = HostValue.Int(0);
        var result = new BehaviorVm(program!, host).Activate("arrive", "i");
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Empty(host.Applied);
    }

    [Fact]
    public void FunctionsCallEachOtherAndRecursionIsRejected()
    {
        var text = """
            kedo 1
            module m
            function cost(stage: int) -> int {
                if (stage <= 1) { return 4 }
                return 4 + stage * 2
            }
            function twice(stage: int) -> int { return cost(stage) * 2 }
            structure bridge {
                on repaired(ctx) {
                    let c = twice(3)
                    apply effect:consume(ctx.structure, c)
                }
            }
            """;
        var (program, _, bag) = Compile(text, "structure:bridge");
        Assert.False(bag.HasErrors, Errors(bag));
        var host = new FakeHost();
        host.ContextValues["structure"] = host.Sym("structure:bridge");
        var result = new BehaviorVm(program!, host).Activate("repaired", "i");
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Contains("effect:consume(structure:bridge,20)", host.Applied[0]);

        var (_, _, bad) = Compile("""
            kedo 1
            module m
            function a(x: int) -> int { return b(x) }
            function b(x: int) -> int { return a(x) }
            actor z { on go(ctx) { let v = a(1) } }
            """, "actor:z");
        Assert.Contains(bad.Items, d => d.Code == "K5002");
    }

    [Fact]
    public void DecisionRollAndSignalWaitsResumeWithValues()
    {
        var text = """
            kedo 1
            module m
            actor a {
                on go(ctx) {
                    wait decision prompt:choose { option fight { apply effect:fight() } option flee { apply effect:flee() } }
                    wait roll resolution(20) as r
                    if (r >= 15) { emit signal:won(r) }
                    wait signal signal:dawn as s
                    apply effect:done(s)
                }
            }
            """;
        var (program, _, bag) = Compile(text, "actor:a");
        Assert.False(bag.HasErrors, Errors(bag));
        var host = new FakeHost();
        var vm = new BehaviorVm(program!, host);
        var r1 = vm.Activate("go", "i");
        Assert.Equal(AwaitKind.Decision, r1.Frame!.Pending!.Kind);
        Assert.Equal(new[] { "fight", "flee" }, r1.Frame.Pending.Options);
        var r2 = vm.Resume(r1.Frame, new Resumption(AwaitKind.Decision, 1));
        Assert.Equal(AwaitKind.Roll, r2.Frame!.Pending!.Kind);
        Assert.Contains("effect:flee()", host.Applied[0]);
        var r3 = vm.Resume(r2.Frame, new Resumption(AwaitKind.Roll, 17));
        Assert.Equal(AwaitKind.Signal, r3.Frame!.Pending!.Kind);
        Assert.Contains("signal:won", host.Raised);
        var r4 = vm.Resume(r3.Frame, new Resumption(AwaitKind.Signal, host.Symbols.Intern("signal:dawn")));
        Assert.Equal(RunStatus.Completed, r4.Status);
        Assert.Contains("effect:done(signal:dawn)", host.Applied[1]);
    }

    [Fact]
    public void StaleFramesFollowThePolicyAndBudgetsFail()
    {
        var (program, _, _) = Compile(Sentinel, "actor:depot_sentinel");
        var host = new FakeHost();
        host.Queries["detected_by"] = HostValue.Int(1);
        var frame = new BehaviorVm(program!, host).Activate("arrive", "i").Frame!;
        var stale = frame with { DefinitionHash = "other" };
        Assert.Equal(RunStatus.Failed, new BehaviorVm(program!, host).Resume(stale, new Resumption(AwaitKind.Minutes, 0)).Status);
        var restarted = new BehaviorVm(program!, host).Resume(stale, new Resumption(AwaitKind.Minutes, 0), StalePolicy.RestartAtEntry);
        Assert.Equal(RunStatus.Suspended, restarted.Status);

        var tiny = new BehaviorVm(program!, host, new Budget(MaxSteps: 2));
        var failed = tiny.Activate("arrive", "i");
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Contains("step budget", failed.Error);
    }

    [Fact]
    public void SchemaRestrictsContextFieldsWaitsAndEffects()
    {
        var schema = new SchemaRegistry();
        schema.AddHook(new HookSchema("tick", new RecordSchema("tick.context").Add(new FieldSchema("place", ValueKind.Reference))) { AllowsAwait = false, AllowsEffects = false });
        schema.AddType(new TypeSchema("actor", new RecordSchema("actor")));
        var (_, _, bag) = Compile("""
            kedo 1
            module m
            actor a { on tick(ctx) { let p = ctx.plaec; wait 5min; apply effect:x(p) } }
            """, "actor:a", schema);
        Assert.True(bag.Items.Any(d => d.Code == "K5001" && d.Message.Contains("plaec")), Errors(bag));
        Assert.True(bag.Items.Any(d => d.Code == "K5004"), Errors(bag));
        Assert.True(bag.Items.Any(d => d.Code == "K5005"), Errors(bag));
    }

    [Fact]
    public void DisassemblyListsHooksAndAwaitSites()
    {
        var (program, _, _) = Compile(Sentinel, "actor:depot_sentinel");
        var text = Disassembler.Dump(program!);
        Assert.Contains("hook arrive -> on arrive", text);
        Assert.Contains("await arrive/1", text);
        Assert.Contains("WaitMinutes", text);
        Assert.Contains("\"effect:alarm_raised\"", text);
    }
}
