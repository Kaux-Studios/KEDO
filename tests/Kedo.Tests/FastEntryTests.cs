using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;

/// <summary>Fast host for tests: answers by id, records calls with the names of the binding table.</summary>
public sealed class FastFakeHost : IFastBehaviorHost
{
    private readonly BindingTable _bindings;

    public FastFakeHost(BindingTable bindings) => _bindings = bindings;

    public long NowMs { get; set; } = 60_000_000;
    public Dictionary<int, HostValue> ContextValues { get; } = new();
    public Dictionary<(long, int), HostValue> PerInstance { get; } = new();
    public Dictionary<int, HostValue> Queries { get; } = new();
    public List<string> Applied { get; } = new();
    public List<string> Raised { get; } = new();
    public bool Record { get; set; } = true;
    public long Applies { get; private set; }

    public long Now(DurationUnit unit) => DurationUnits.ConvertTruncating(NowMs, DurationUnit.Millisecond, unit);

    public HostValue Context(long instance, int slot)
        => PerInstance.TryGetValue((instance, slot), out var v) ? v : ContextValues.TryGetValue(slot, out v) ? v : HostValue.None;

    public HostValue Query(int queryId, ReadOnlySpan<HostValue> args) => Queries.TryGetValue(queryId, out var v) ? v : HostValue.Int(0);

    public bool Apply(in FastEffectKey key, int effectId, ReadOnlySpan<HostValue> args)
    {
        Applies++;
        if (Record)
        {
            Applied.Add($"{key.Instance}/pc{key.Site}#{key.Sequence} {_bindings.NameOf(BindingKind.Effect, effectId)}({Render(args)})");
        }

        return true;
    }

    public void Raise(int signalId, ReadOnlySpan<HostValue> args)
    {
        if (Record)
        {
            Raised.Add($"{_bindings.NameOf(BindingKind.Signal, signalId)}({Render(args)})");
        }
    }

    public HostValue Sym(string name) => HostValue.Symbol(_bindings.Symbols.Intern(name));

    private string Render(ReadOnlySpan<HostValue> args)
        => string.Join(",", args.ToArray().Select(a => a.IsSymbol ? _bindings.Symbols.Name(a.Number) ?? "?" : a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}

public class FastEntryTests
{
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

    private const string Waits = """
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

    private const string Boss = """
        kedo 1
        module m
        manifest { base_unit = ms }
        function threshold(phase: int) -> int { return 100 - phase * 25 }
        actor boss {
            on damaged(ctx) {
                let hp = ctx.boss.hp_percent()
                if (hp <= threshold(ctx.phase)) {
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

    private static (BehaviorProgram Program, DiagnosticBag Bag) Compile(string text, string key, SchemaRegistry? schema = null)
    {
        var bag = new DiagnosticBag();
        var decls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "b.kedo"), bag), bag);
        schema ??= new SchemaRegistry { Permissive = true };
        var catalog = Resolver.Resolve(new[] { decls }, schema, bag);
        return (BehaviorCompiler.Compile(catalog.ByKey[key], decls.Functions, schema, bag)!, bag);
    }

    private static string Errors(IEnumerable<Diagnostic> items) => string.Join("\n", items.Select(d => $"{d.Code} {d.Message}"));

    private static (BoundProgram Bound, FastFakeHost Host) Bind(BehaviorProgram program)
    {
        var bindings = BindingTable.FromPrograms(new[] { program });
        var bound = BehaviorBinder.Bind(program, bindings, out var diagnostics);
        Assert.Empty(diagnostics);
        return (bound, new FastFakeHost(bindings));
    }

    private static int Id(BoundProgram bound, BindingKind kind, string name) => bound.Bindings.TryGet(kind, name, out var id) ? id : throw new KeyNotFoundException(name);

    [Fact]
    public void LegacyHostThroughTheAdapterGivesTheSameFrameAndEffects()
    {
        var (program, bag) = Compile(Sentinel, "actor:depot_sentinel");
        Assert.False(bag.HasErrors, Errors(bag.Items));

        static FakeHost Host()
        {
            var host = new FakeHost();
            host.ContextValues["character"] = host.Sym("character:mara");
            host.ContextValues["place"] = host.Sym("place:depot");
            host.ContextValues["quiet"] = HostValue.Int(0);
            host.Queries["detected_by"] = HostValue.Int(1);
            return host;
        }

        var legacyHost = Host();
        var legacy = new BehaviorVm(program, legacyHost).Activate("arrive", "42");

        var fastHost = Host();
        var bindings = BindingTable.FromPrograms(new[] { program }, fastHost.Symbols);
        var bound = BehaviorBinder.Bind(program, bindings, out var diagnostics);
        Assert.Empty(diagnostics);
        var vm = new FastBehaviorVm(bound, new BehaviorHostAdapter(fastHost, bindings));
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook("arrive"), 42, ref outcome);
        Assert.Equal(RunStatus.Suspended, outcome.Status);
        Assert.Equal(1, vm.SuspendedCount);
        Assert.Equal(BehaviorVm.Save(legacy.Frame!), BehaviorVm.Save(vm.Save(outcome.Frame)));
        var pending = vm.Pending(outcome.Frame);
        Assert.Equal(AwaitKind.Minutes, pending.Kind);
        Assert.Equal(1030, pending.DueAt);
        Assert.Equal("arrive/1", pending.Label);

        var frame = outcome.Frame;
        vm.Resume(frame, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Equal(0, vm.SuspendedCount);
        var legacyDone = new BehaviorVm(program, legacyHost).Resume(legacy.Frame!, new Resumption(AwaitKind.Minutes, 0));
        Assert.Equal(RunStatus.Completed, legacyDone.Status);
        Assert.Equal(legacyHost.Applied, fastHost.Applied);
        Assert.Single(fastHost.Applied);

        // The handle of a frame that ended is stale: nothing to resume, nothing to release.
        vm.Resume(frame, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunError.NotSuspended, outcome.Error);
        Assert.False(vm.Release(frame));
    }

    [Fact]
    public void SavedFramesResumeInEitherVmAndReceiptsAreKept()
    {
        var (program, _) = Compile(Sentinel, "actor:depot_sentinel");
        var (bound, host) = Bind(program);
        host.Queries[Id(bound, BindingKind.Query, "detected_by")] = HostValue.Int(1);
        host.ContextValues[Id(bound, BindingKind.Context, "place")] = host.Sym("place:depot");
        var vm = new FastBehaviorVm(bound, host);
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook("arrive"), 7, ref outcome);
        var json = BehaviorVm.Save(vm.Save(outcome.Frame));
        Assert.True(vm.Release(outcome.Frame));
        Assert.Equal(0, vm.SuspendedCount);

        // A legacy VM resumes the frame the fast VM saved.
        var legacyHost = new FakeHost();
        legacyHost.ContextValues["place"] = legacyHost.Sym("place:depot");
        var done = new BehaviorVm(program, legacyHost).Resume(BehaviorVm.Load(json)!, new Resumption(AwaitKind.Minutes, 0));
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Equal("7/pc", legacyHost.Applied[0][..4]);

        // The fast VM resumes it in a new session; with the receipt of that effect it does not apply again.
        var vm2 = new FastBehaviorVm(bound, host);
        vm2.Resume(BehaviorVm.Load(json)!, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Single(host.Applied);
        Assert.Equal(legacyHost.Applied[0].Split(' ')[0], host.Applied[0].Split(' ')[0]);
        var receipt = host.Applied[0].Split(' ')[0];
        vm2.Resume(BehaviorVm.Load(json)! with { AppliedEffects = new[] { receipt } }, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Single(host.Applied);

        // Load keeps it in the pool until resumed.
        var handle = vm2.Load(BehaviorVm.Load(json)!, ref outcome);
        Assert.True(handle.IsValid);
        Assert.Equal(RunStatus.Suspended, outcome.Status);
        Assert.Equal(1, vm2.SuspendedCount);
        vm2.Resume(handle, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunStatus.Completed, outcome.Status);
    }

    [Fact]
    public void StaleFramesFollowThePolicy()
    {
        var (program, _) = Compile(Sentinel, "actor:depot_sentinel");
        var (bound, host) = Bind(program);
        host.Queries[Id(bound, BindingKind.Query, "detected_by")] = HostValue.Int(1);
        var vm = new FastBehaviorVm(bound, host, options: new VmOptions { Trace = true });
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook("arrive"), 1, ref outcome);
        var stale = vm.Save(outcome.Frame) with { DefinitionHash = "other" };
        vm.Resume(stale, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunError.StaleFrame, outcome.Error);
        vm.Resume(stale, AwaitKind.Minutes, 0, ref outcome, StalePolicy.AbortWithCompensation);
        Assert.Equal(RunError.StaleFrameCompensation, outcome.Error);
        vm.Resume(stale, AwaitKind.Minutes, 0, ref outcome, StalePolicy.RestartAtEntry);
        Assert.Equal(RunStatus.Suspended, outcome.Status);
        Assert.Contains("stale frame: restarting hook `arrive` at entry", vm.Trace);
        Assert.False(vm.Load(stale, ref outcome).IsValid);
        Assert.Equal(RunError.StaleFrame, outcome.Error);
        Assert.False(vm.Load(stale with { DefinitionHash = program.DefinitionHash, InstanceId = "not-a-number" }, ref outcome).IsValid);
        Assert.Equal(RunError.UnsupportedFrame, outcome.Error);
    }

    [Fact]
    public void DecisionRollAndSignalWaitsExposeWhatTheyWaitFor()
    {
        var (program, _) = Compile(Waits, "actor:a");
        var (bound, host) = Bind(program);
        var vm = new FastBehaviorVm(bound, host);
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook("go"), 3, ref outcome);
        var frame = outcome.Frame;
        var decision = vm.Pending(frame);
        Assert.Equal(AwaitKind.Decision, decision.Kind);
        Assert.Equal("prompt:choose", decision.Name);
        Assert.Equal(new[] { "fight", "flee" }, decision.Options.ToArray());

        vm.Resume(frame, AwaitKind.Roll, 1, ref outcome);
        Assert.Equal(RunError.WrongResumption, outcome.Error);
        vm.Resume(frame, AwaitKind.Decision, 1, ref outcome);
        Assert.Equal(RunStatus.Suspended, outcome.Status);
        Assert.Equal(frame.IsValid, outcome.Frame.IsValid);
        var roll = vm.Pending(outcome.Frame);
        Assert.Equal(AwaitKind.Roll, roll.Kind);
        Assert.Equal(Id(bound, BindingKind.Stream, "resolution"), roll.Id);
        vm.Resume(outcome.Frame, AwaitKind.Roll, 17, ref outcome);
        var signal = vm.Pending(outcome.Frame);
        Assert.Equal(AwaitKind.Signal, signal.Kind);
        Assert.Equal(Id(bound, BindingKind.Signal, "signal:dawn"), signal.Id);
        Assert.Equal(new[] { "signal:won(17)" }, host.Raised);
        vm.Resume(outcome.Frame, AwaitKind.Signal, bound.Bindings.Symbols.Intern("signal:dawn"), ref outcome);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.EndsWith("effect:flee()", host.Applied[0]);
        Assert.EndsWith("effect:done(signal:dawn)", host.Applied[1]);
        Assert.Equal(0, vm.SuspendedCount);
    }

    [Fact]
    public void FunctionsBatchesAndContextColumns()
    {
        var (program, bag) = Compile(Boss, "actor:boss");
        Assert.False(bag.HasErrors, Errors(bag.Items));
        Assert.Equal(DurationUnit.Millisecond, program.TimeUnit);
        var (bound, host) = Bind(program);
        var boss = Id(bound, BindingKind.Context, "boss");
        var phase = Id(bound, BindingKind.Context, "phase");
        host.Queries[Id(bound, BindingKind.Query, "hp_percent")] = HostValue.Int(70);
        var vm = new FastBehaviorVm(bound, host);

        // Context in columns: slot-major, one value per instance. Phase 0 → threshold 100, 1 → 75, 2 → 50.
        long[] instances = { 10, 11, 12 };
        var columns = new HostValue[Math.Max(boss, phase) * 3 + 3];
        for (var i = 0; i < 3; i++)
        {
            columns[boss * 3 + i] = host.Sym("enemy:boss_" + i);
            columns[phase * 3 + i] = HostValue.Int(i);
        }

        var outcomes = new RunOutcome[3];
        vm.ActivateBatch(bound.Hook("damaged"), instances, columns, outcomes);
        Assert.All(outcomes, o => Assert.Equal(RunStatus.Completed, o.Status));
        Assert.Equal(2, host.Applied.Count);
        Assert.StartsWith("10/", host.Applied[0]);
        Assert.EndsWith("effect:set_phase(enemy:boss_0,1)", host.Applied[0]);
        Assert.EndsWith("effect:set_phase(enemy:boss_1,2)", host.Applied[1]);
        Assert.Equal(new[] { "signal:phase_changed(enemy:boss_0)", "signal:phase_changed(enemy:boss_1)" }, host.Raised);

        // Without columns the host answers per instance.
        host.Applied.Clear();
        host.PerInstance[(20, boss)] = host.Sym("enemy:solo");
        host.PerInstance[(20, phase)] = HostValue.Int(1);
        vm.ActivateBatch(bound.Hook("damaged"), new long[] { 20 }, outcomes);
        Assert.EndsWith("effect:set_phase(enemy:solo,2)", host.Applied.Single());

        // A wait in milliseconds through the host clock.
        var one = new RunOutcome();
        vm.Activate(bound.Hook("enrage"), 30, ref one);
        Assert.Equal(host.NowMs + 350, vm.Pending(one.Frame).DueAt);
        Assert.Equal(DurationUnit.Millisecond, vm.Pending(one.Frame).Unit);
        Assert.Equal(DurationUnit.Millisecond, vm.Save(one.Frame).Pending!.Unit);
    }

    [Fact]
    public void UnboundNamesAreDiagnosedAndFailOnlyWhereTheyAreUsed()
    {
        var (program, _) = Compile(Boss, "actor:boss");
        var bindings = new BindingTable()
            .Context("boss", 0).Context("phase", 1)
            .Query("hp_percnt", 0)
            .Effect("effect:spawn_wave", 0);
        var bound = BehaviorBinder.Bind(program, bindings, out var diagnostics);
        Assert.True(bound.IsValid);
        Assert.Equal(new[] { "K6001", "K6001", "K6001" }, diagnostics.Select(d => d.Code).ToArray());
        Assert.Contains("query `hp_percent` used by `actor:boss` (b.kedo:", diagnostics[0].Message);
        Assert.Contains("did you mean `hp_percnt`?", diagnostics[0].Message);
        Assert.Contains("effect `effect:set_phase`", diagnostics[1].Message);
        Assert.Contains("register it in the binding table (`BindingTable.Effect`)", diagnostics[1].Message);
        Assert.Contains("signal `signal:phase_changed`", diagnostics[2].Message);

        var host = new FastFakeHost(bindings);
        var vm = new FastBehaviorVm(bound, host, options: new VmOptions { Trace = true });
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook("damaged"), 1, ref outcome);
        Assert.Equal(RunError.Unbound, outcome.Error);
        Assert.StartsWith("query `hp_percent` is not bound by the host (K6001) (b.kedo:", vm.Describe(outcome));
        Assert.StartsWith("fail at b.kedo:", vm.Trace.Single());
        vm.Activate(bound.Hook("enrage"), 1, ref outcome);
        Assert.Equal(RunStatus.Suspended, outcome.Status);

        Assert.Throws<ArgumentException>(() => bindings.Query("hp_percnt", 5));
        Assert.Throws<ArgumentException>(() => bindings.Query("other", 0));
    }

    [Fact]
    public void BadHandlesAndInvalidProgramsFailWithoutThrowing()
    {
        var (program, _) = Compile(Sentinel, "actor:depot_sentinel");
        var (bound, host) = Bind(program);
        var (other, _) = Bind(program);
        var vm = new FastBehaviorVm(bound, host);
        var outcome = new RunOutcome();
        vm.Activate(default, 1, ref outcome);
        Assert.Equal(RunError.NoSuchHook, outcome.Error);
        Assert.False(bound.Hook("nope").IsValid);
        vm.Activate(other.Hook("arrive"), 1, ref outcome);
        Assert.Equal(RunError.NoSuchHook, outcome.Error);
        vm.Resume(default, AwaitKind.Minutes, 0, ref outcome);
        Assert.Equal(RunError.NotSuspended, outcome.Error);
        Assert.Equal(default, vm.Pending(default).Kind);

        var broken = new BehaviorProgram("broken", 1, "h", new[] { Instr.AImm(OpCode.Jump, 0, 100) }, Array.Empty<long>(), Array.Empty<string>(),
            new[] { new FunctionInfo("on go", 0, 1, 0) }, new Dictionary<string, int> { ["go"] = 0 }, Array.Empty<AwaitSite>(), new[] { "x.kedo:1:1" });
        var badBound = BehaviorBinder.Bind(broken, new BindingTable(), out var diagnostics);
        Assert.False(badBound.IsValid);
        Assert.Equal("K6002", diagnostics.Single().Code);
        Assert.Contains("jump out of range at 0 (x.kedo:1:1)", diagnostics[0].Message);
        new FastBehaviorVm(badBound, host).Activate(badBound.Hook("go"), 1, ref outcome);
        Assert.Equal(RunError.InvalidProgram, outcome.Error);
    }

    [Fact]
    public void BudgetsAndArithmeticFailures()
    {
        var (program, _) = Compile("""
            kedo 1
            module m
            function inner(x: int) -> int { return 10 / x }
            actor a { on go(ctx) { let v = inner(ctx.divisor); apply effect:got(v) } }
            """, "actor:a");
        var (bound, host) = Bind(program);
        var divisor = Id(bound, BindingKind.Context, "divisor");
        host.ContextValues[divisor] = HostValue.Int(0);
        var vm = new FastBehaviorVm(bound, host);
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook("go"), 1, ref outcome);
        Assert.Equal(RunError.DivisionByZero, outcome.Error);
        Assert.StartsWith("division by zero (b.kedo:3:", vm.Describe(outcome));
        host.ContextValues[divisor] = HostValue.Int(5);
        vm.Activate(bound.Hook("go"), 1, ref outcome);
        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.EndsWith("effect:got(2)", host.Applied.Single());

        new FastBehaviorVm(bound, host, new Budget(MaxSteps: 2)).Activate(bound.Hook("go"), 1, ref outcome);
        Assert.Equal(RunError.StepBudget, outcome.Error);
        new FastBehaviorVm(bound, host, new Budget(MaxCallDepth: 1)).Activate(bound.Hook("go"), 1, ref outcome);
        Assert.Equal(RunError.CallDepth, outcome.Error);
        new FastBehaviorVm(bound, host, new Budget(MaxRegisters: 1)).Activate(bound.Hook("go"), 1, ref outcome);
        Assert.Equal(RunError.RegisterBudget, outcome.Error);
    }

    [Fact]
    public void ActivationsAllocateNothingAfterWarmUp()
    {
        var (program, _) = Compile(Boss, "actor:boss");
        var (bound, host) = Bind(program);
        host.Record = false;
        host.Queries[Id(bound, BindingKind.Query, "hp_percent")] = HostValue.Int(10);
        host.ContextValues[Id(bound, BindingKind.Context, "boss")] = host.Sym("enemy:boss");
        host.ContextValues[Id(bound, BindingKind.Context, "phase")] = HostValue.Int(1);
        var vm = new FastBehaviorVm(bound, host);
        var damaged = bound.Hook("damaged");
        var enrage = bound.Hook("enrage");
        var outcome = new RunOutcome();
        long[] instances = Enumerable.Range(0, 64).Select(i => (long)i).ToArray();
        var outcomes = new RunOutcome[instances.Length];
        var frames = new FrameHandle[16];

        void Cycle()
        {
            for (var i = 0; i < 100; i++)
            {
                vm.Activate(damaged, i, ref outcome);
            }

            vm.ActivateBatch(damaged, instances, outcomes);
            for (var i = 0; i < frames.Length; i++)
            {
                vm.Activate(enrage, i, ref outcome);
                frames[i] = outcome.Frame;
            }

            for (var i = 0; i < frames.Length; i++)
            {
                vm.Resume(frames[i], AwaitKind.Minutes, 0, ref outcome);
            }
        }

        Cycle();   // warm-up: registers, stack, receipts and frame pool reach their size
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            Cycle();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Equal(0, vm.SuspendedCount);
        Assert.Equal(11 * (100 + 64 + 16), host.Applies);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FromProgramsRegistersEveryNameOnce()
    {
        var (a, _) = Compile(Sentinel, "actor:depot_sentinel");
        var (b, _) = Compile(Waits, "actor:a");
        var table = BindingTable.FromPrograms(new[] { a, b });
        Assert.Equal(new[] { "character", "quiet", "place" }, table.Names(BindingKind.Context).ToArray());
        Assert.Equal("effect:alarm_raised", table.NameOf(BindingKind.Effect, 0));
        Assert.Equal("effect:fight", table.NameOf(BindingKind.Effect, 1));
        Assert.True(table.TryGet(BindingKind.Signal, "signal:dawn", out _));
        Assert.True(table.TryGet(BindingKind.Stream, "resolution", out _));
    }

    // ----- compile-time checks of the declared host API -----

    private const string HostApiSchema = """
        kedo 1
        module game
        kedo_enum mood { values = [calm, angry] }
        kedo_type enemy { fields = [ { name = hp  kind = int } ] }
        kedo_type place { fields = [] }
        kedo_hook damaged {
            context = [
                { name = boss  kind = reference  target = enemy }
                { name = phase  kind = int }
                { name = label  kind = text }
            ]
        }
        kedo_query hp_percent { params = [ { name = target  kind = reference  target = enemy } ]  returns = { kind = int } }
        kedo_query distance { params = [ { name = from  kind = reference }, { name = to  kind = reference  target = place } ]  returns = { kind = int } }
        kedo_query is_mood { params = [ { name = target  kind = reference }, { name = mood  kind = enum  enum = mood } ]  returns = { kind = bool } }
        kedo_effect set_phase { params = [ { name = target  kind = reference  target = enemy }, { name = phase  kind = int } ] }
        kedo_effect announce { params = [ { name = text  kind = text } ] }
        """;

    private static (SchemaRegistry Schema, DiagnosticBag Bag) HostSchema(string text = HostApiSchema)
    {
        var bag = new DiagnosticBag();
        var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "schema.kedo"), bag), bag) }, bag);
        return (schema, bag);
    }

    private static DiagnosticBag CompileWith(SchemaRegistry schema, string hookBody)
    {
        var (_, bag) = Compile("kedo 1\nmodule m\nenemy boss {\n    hp = 10\n    on damaged(ctx) {\n" + hookBody + "\n    }\n}\n", "enemy:boss", schema);
        return bag;
    }

    [Fact]
    public void DeclaredQueriesAndEffectsLoadFromTheSchema()
    {
        var (schema, bag) = HostSchema();
        Assert.False(bag.HasErrors, Errors(bag.Items));
        Assert.Equal(new[] { "distance", "hp_percent", "is_mood" }, schema.Queries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "from", "to" }, schema.FindQuery("distance")!.Parameters.Select(p => p.Name).ToArray());
        Assert.Equal(ValueKind.Bool, schema.FindQuery("is_mood")!.Returns!.Kind);
        Assert.Equal("enemy", schema.FindEffect("set_phase")!.Parameters[0].TargetType);

        var (_, bad) = HostSchema("""
            kedo 1
            module game
            kedo_query tags { params = [ { name = of  kind = list  element = { kind = int } } ]  returns = { kind = enum  enum = nope } }
            """);
        Assert.Equal(2, bad.Items.Count(d => d.Code == "K2001"));
        Assert.Contains(bad.Items, d => d.Message.Contains("parameter `of` is of kind `list`"));
        Assert.Contains(bad.Items, d => d.Message.Contains("unknown enum `nope`"));
    }

    [Fact]
    public void CorrectCallsCompileClean()
    {
        var (schema, _) = HostSchema();
        var bag = CompileWith(schema, """
                    let hp = ctx.boss.hp_percent()
                    let near = distance(ctx.boss, place:gate) < 5
                    if (hp < 50 and near and is_mood(ctx.boss, mood.angry)) {
                        apply effect:set_phase(ctx.boss, ctx.phase + 1)
                        apply effect:announce(ctx.label)
                    }
            """);
        Assert.False(bag.HasErrors, Errors(bag.Items));
    }

    [Fact]
    public void UndeclaredNamesWrongArityAndWrongKindsAreReported()
    {
        var (schema, _) = HostSchema();
        var bag = CompileWith(schema, """
                    let a = ctx.boss.hp_procent()
                    let b = distance(ctx.boss)
                    let c = distance(ctx.boss, enemy:other)
                    let d = is_mood(ctx.boss, "angry")
                    let e = ctx.boss.hp_percent(1)
                    apply effect:set_phase(ctx.boss, ctx.label)
                    apply effect:set_phasee(ctx.boss, 1)
                    apply other:announce("x")
                    apply effect:announce(hp_percent(ctx.boss))
            """);
        var lines = bag.Items.Select(d => $"{d.Code} {d.Message}").ToArray();
        Assert.Equal(new[]
        {
            "K5008 query `hp_procent` is not declared in the schema; did you mean `hp_percent`?",
            "K5003 query `distance` takes 2 argument(s), got 1",
            "K5003 argument 2 (`to`) of query `distance` must be a reference to `place`, got a reference to `enemy`",
            "K5003 argument 2 (`mood`) of query `is_mood` must be an enum value of `mood`, got a text",
            "K5003 query `hp_percent` takes 1 argument(s), got 2 (the receiver of a member call is the first)",
            "K5003 argument 2 (`phase`) of effect `effect:set_phase` must be an int, got a text",
            "K5008 effect `effect:set_phasee` is not declared in the schema; did you mean `effect:set_phase`?",
            "K5008 effect `other:announce` is not declared in the schema; declare it with `kedo_effect` and write it `effect:<id>`",
            "K5003 argument 1 (`text`) of effect `effect:announce` must be a text, got an int",
        }, lines);
    }

    [Fact]
    public void WithoutDeclarationsAnyQueryAndEffectCompileAsBefore()
    {
        var (schema, _) = HostSchema("""
            kedo 1
            module game
            kedo_type enemy { fields = [ { name = hp  kind = int } ] }
            kedo_hook damaged { context = [ { name = boss  kind = reference } ] }
            """);
        var bag = CompileWith(schema, """
                    let a = ctx.boss.anything(1, 2, 3)
                    apply effect:whatever(a, "x")
                    apply other:thing()
            """);
        Assert.False(bag.HasErrors, Errors(bag.Items));
    }
}
