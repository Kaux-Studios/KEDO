using System.Globalization;
using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;

/// <summary>
/// Golden cases of the declared host API and the fast entry (ADR-0015): tests/corpus/&lt;case&gt;/schema/*.kedo (game schema)
/// and content/*.kedo. Expected files: diagnostics.txt (schema, resolution, compilation and binding) and, when there are no
/// errors, trace.txt: every hook of every program run on <see cref="FastBehaviorVm"/> with a scripted host until it ends.
/// Optional unbound.txt lists `kind name` lines the host leaves unregistered. The same scenario runs on <see cref="BehaviorVm"/>
/// and must make the same host calls (parity).
/// </summary>
public class HostApiGoldenTests
{
    public static IEnumerable<object[]> Cases()
    {
        var root = Path.Combine(ParserTests.RepoRoot(), "tests", "corpus");
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (Directory.Exists(Path.Combine(dir, "schema")) && Directory.Exists(Path.Combine(dir, "content")))
            {
                yield return new object[] { Path.GetFileName(dir) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HostApiCaseMatchesGoldenFiles(string name)
    {
        var dir = Path.Combine(ParserTests.RepoRoot(), "tests", "corpus", name);
        var bag = new DiagnosticBag();
        var sources = new Dictionary<string, SourceText>();
        List<FileDeclarations> Load(string sub) => Directory.GetFiles(Path.Combine(dir, sub), "*.kedo").OrderBy(f => f, StringComparer.Ordinal).Select(f =>
        {
            var source = SourceText.FromFile(f);
            sources[f] = source;
            var tree = Parser.Parse(source, bag);
            Assert.True(Kedo.Compiler.Formatting.Formatter.Format(tree) == System.Text.Encoding.UTF8.GetString(source.Bytes), $"{Path.GetFileName(f)} of {name} is not formatted; run `kedo fmt`");
            return DeclarationBuilder.Build(tree, bag);
        }).ToList();

        var schema = SchemaLoader.Load(Load("schema"), bag);
        var decls = Load("content");
        var catalog = Resolver.Resolve(decls, schema, bag);
        var functions = decls.SelectMany(d => d.Functions).ToList();
        var programs = catalog.Concrete.Where(o => o.Hooks.Count > 0).OrderBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => BehaviorCompiler.Compile(o, functions, schema, bag)!).ToList();

        var unbound = File.Exists(Path.Combine(dir, "unbound.txt"))
            ? File.ReadAllLines(Path.Combine(dir, "unbound.txt")).Where(l => l.Trim().Length > 0).Select(l => l.Split(' ', 2)).Select(p => (Enum.Parse<BindingKind>(p[0], ignoreCase: true), p[1].Trim())).ToHashSet()
            : new HashSet<(BindingKind, string)>();
        var all = BindingTable.FromPrograms(programs);
        var bindings = new BindingTable();
        foreach (var kind in Enum.GetValues<BindingKind>())
        {
            foreach (var n in all.Names(kind).Where(n => !unbound.Contains((kind, n))))
            {
                all.TryGet(kind, n, out var id);
                bindings.Add(kind, n, id);
            }
        }

        var bound = programs.Select(p => BehaviorBinder.Bind(p, bindings, bag)).ToList();
        var diagnostics = string.Join("", bag.Items.Select(d =>
        {
            var pos = d.File is not null && sources.TryGetValue(d.File, out var s) ? s.GetLinePosition(d.Span.Start) : default;
            var where = d.File is null ? "-" : $"{Path.GetFileName(d.File)}:{pos.Line}:{pos.Column}";
            return $"{d.Severity.ToString().ToLowerInvariant()} {d.Code} {where} {d.Message}\n";
        }));
        GoldenTests.Check(dir, "diagnostics.txt", diagnostics);

        // Invariants and computed fields (ADR-0016): the resolved catalog shows the computed values and their provenance.
        if (name.StartsWith("invariants-", StringComparison.Ordinal) || name.StartsWith("computed-", StringComparison.Ordinal))
        {
            GoldenTests.Check(dir, "resolved.txt", ResolvedDump.Dump(catalog, withProvenance: true));
        }

        if (bag.Items.Any(d => d.IsError && d.Code != "K6001"))
        {
            Assert.False(File.Exists(Path.Combine(dir, "trace.txt")), "a case with errors has no trace");
            return;
        }

        var trace = new List<string>();
        foreach (var b in bound)
        {
            trace.Add($"program {b.Program.BehaviorId}{(b.Program.TimeUnit == DurationUnit.Minute ? "" : " time_unit=" + DurationUnits.Suffix(b.Program.TimeUnit))}");
            foreach (var hook in b.Program.Hooks.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var fast = RunFast(b, schema, hook);
                if (unbound.Count == 0)
                {
                    Assert.Equal(HostCalls(RunLegacy(b.Program, schema, hook)), HostCalls(fast));
                }

                trace.AddRange(fast.Select(l => "  " + l));
            }
        }

        GoldenTests.Check(dir, "trace.txt", string.Join("", trace.Select(l => l + "\n")));
    }

    private const long Instance = 7;

    // Host calls and statuses, without the VM-specific failure text and frame dumps.
    private static IEnumerable<string> HostCalls(IEnumerable<string> lines)
        => lines.Where(l => !l.StartsWith("  frame ", StringComparison.Ordinal))
            .Select(l => l.StartsWith("  failed", StringComparison.Ordinal) ? "  failed" : l.StartsWith("  completed", StringComparison.Ordinal) ? "  completed" : l);

    private static List<string> RunFast(BoundProgram bound, SchemaRegistry schema, string hook)
    {
        var lines = new List<string> { $"hook {hook} instance {Instance}" };
        var host = new ScriptedFastHost(bound.Bindings, schema, hook, lines);
        var vm = new FastBehaviorVm(bound, host, options: new VmOptions { Trace = true });
        var outcome = new RunOutcome();
        vm.Activate(bound.Hook(hook), Instance, ref outcome);
        for (var i = 0; i < 16 && outcome.Status == RunStatus.Suspended; i++)
        {
            var pending = vm.Pending(outcome.Frame);
            var value = Answer(pending.Kind, pending.Name, bound.Bindings.Symbols);
            lines.Add($"  suspended {pending.Kind} {pending.Label}{(pending.Kind == AwaitKind.Minutes ? $" due={pending.DueAt}{DurationUnits.Suffix(pending.Unit)}" : pending.Name is null ? "" : " " + pending.Name)}");
            lines.Add("  frame " + BehaviorVm.Save(vm.Save(outcome.Frame)));
            if (pending.Kind == AwaitKind.Minutes)
            {
                host.NowMs = DurationUnits.ConvertTruncating(pending.DueAt, pending.Unit, DurationUnit.Millisecond);
            }

            lines.Add($"  resume {pending.Kind} {value.Text}");
            vm.Resume(outcome.Frame, pending.Kind, value.Number, ref outcome);
        }

        lines.Add(outcome.Status == RunStatus.Failed ? $"  failed: {vm.Describe(outcome)}" : $"  {outcome.Status.ToString().ToLowerInvariant()} steps={outcome.Steps}");
        return lines;
    }

    private static List<string> RunLegacy(BehaviorProgram program, SchemaRegistry schema, string hook)
    {
        var lines = new List<string> { $"hook {hook} instance {Instance}" };
        var host = new ScriptedHost(schema, hook, lines);
        var result = new BehaviorVm(program, host).Activate(hook, Instance.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < 16 && result.Status == RunStatus.Suspended; i++)
        {
            var pending = result.Frame!.Pending!;
            var value = Answer(pending.Kind, pending.Id, host.Symbols);
            lines.Add($"  suspended {pending.Kind} {pending.Label}{(pending.Kind == AwaitKind.Minutes ? $" due={pending.DueAtMinutes}{DurationUnits.Suffix(pending.Unit)}" : pending.Id is null ? "" : " " + pending.Id)}");
            if (pending.Kind == AwaitKind.Minutes)
            {
                host.NowMs = DurationUnits.ConvertTruncating(pending.DueAtMinutes, pending.Unit, DurationUnit.Millisecond);
            }

            lines.Add($"  resume {pending.Kind} {value.Text}");
            result = new BehaviorVm(program, host).Resume(result.Frame, new Resumption(pending.Kind, value.Number));
        }

        lines.Add(result.Status == RunStatus.Failed ? "  failed" : $"  {result.Status.ToString().ToLowerInvariant()}");
        return lines;
    }

    // Decisions take the first option, rolls give 10, signals deliver their own name, time resumes at the due time.
    private static (long Number, string Text) Answer(AwaitKind kind, string? name, SymbolTable symbols) => kind switch
    {
        AwaitKind.Decision => (0, "0"),
        AwaitKind.Roll => (10, "10"),
        AwaitKind.Signal => (symbols.Intern(name!), name!),
        _ => (0, "0"),
    };

    /// <summary>Answers by the declared kinds: int and duration 50 (contexts 1), bool true, anything else a symbol `name:7`.</summary>
    internal static HostValue Scripted(FieldSchema? f, string name, bool context, SymbolTable symbols) => f?.Kind switch
    {
        ValueKind.Int or ValueKind.Duration => HostValue.Int(context ? 1 : 50),
        ValueKind.Bool => HostValue.Int(1),
        _ => HostValue.Symbol(symbols.Intern($"{name}:{Instance}")),
    };

    internal static string Render(ReadOnlySpan<HostValue> args, SymbolTable symbols)
    {
        var parts = new List<string>();
        foreach (var a in args)
        {
            parts.Add(Render(a, symbols));
        }

        return string.Join(",", parts);
    }

    internal static string Render(HostValue v, SymbolTable symbols) => v.IsSymbol ? symbols.Name(v.Number) ?? "none" : v.Number.ToString(CultureInfo.InvariantCulture);

    private sealed class ScriptedFastHost : IFastBehaviorHost
    {
        private readonly BindingTable _b;
        private readonly SchemaRegistry _schema;
        private readonly string _hook;
        private readonly List<string> _lines;

        public ScriptedFastHost(BindingTable bindings, SchemaRegistry schema, string hook, List<string> lines)
        {
            _b = bindings;
            _schema = schema;
            _hook = hook;
            _lines = lines;
        }

        public long NowMs { get; set; }

        public long Now(DurationUnit unit) => DurationUnits.ConvertTruncating(NowMs, DurationUnit.Millisecond, unit);

        public HostValue Context(long instance, int slot)
        {
            var name = _b.NameOf(BindingKind.Context, slot)!;
            var v = Scripted(_schema.FindHook(_hook)?.Context.Find(name), name, true, _b.Symbols);
            _lines.Add($"  context {name} -> {Render(v, _b.Symbols)}");
            return v;
        }

        public HostValue Query(int queryId, ReadOnlySpan<HostValue> args)
        {
            var name = _b.NameOf(BindingKind.Query, queryId)!;
            var v = Scripted(_schema.FindQuery(name)?.Returns, name, false, _b.Symbols);
            _lines.Add($"  query {name}({Render(args, _b.Symbols)}) -> {Render(v, _b.Symbols)}");
            return v;
        }

        public bool Apply(in FastEffectKey key, int effectId, ReadOnlySpan<HostValue> args)
        {
            _lines.Add($"  apply {key.Instance}/pc{key.Site}#{key.Sequence} {_b.NameOf(BindingKind.Effect, effectId)}({Render(args, _b.Symbols)})");
            return true;
        }

        public void Raise(int signalId, ReadOnlySpan<HostValue> args) => _lines.Add($"  raise {_b.NameOf(BindingKind.Signal, signalId)}({Render(args, _b.Symbols)})");
    }

    private sealed class ScriptedHost : IBehaviorHost, IBehaviorClock
    {
        private readonly SchemaRegistry _schema;
        private readonly string _hook;
        private readonly List<string> _lines;

        public ScriptedHost(SchemaRegistry schema, string hook, List<string> lines)
        {
            _schema = schema;
            _hook = hook;
            _lines = lines;
        }

        public long NowMs { get; set; }

        public long NowMinutes => NowMs / 60_000;

        public SymbolTable Symbols { get; } = new();

        public long Now(DurationUnit unit) => DurationUnits.ConvertTruncating(NowMs, DurationUnit.Millisecond, unit);

        public HostValue Context(string field)
        {
            var v = Scripted(_schema.FindHook(_hook)?.Context.Find(field), field, true, Symbols);
            _lines.Add($"  context {field} -> {Render(v, Symbols)}");
            return v;
        }

        public HostValue Query(string queryId, ReadOnlySpan<HostValue> args)
        {
            var v = Scripted(_schema.FindQuery(queryId)?.Returns, queryId, false, Symbols);
            _lines.Add($"  query {queryId}({Render(args, Symbols)}) -> {Render(v, Symbols)}");
            return v;
        }

        public bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args)
        {
            _lines.Add($"  apply {key} {effectId}({Render(args, Symbols)})");
            return true;
        }

        public long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args) => 10;

        public void Raise(string signalId, ReadOnlySpan<HostValue> args) => _lines.Add($"  raise {signalId}({Render(args, Symbols)})");

        public void Trace(string message) => _lines.Add("  trace " + message);
    }
}
