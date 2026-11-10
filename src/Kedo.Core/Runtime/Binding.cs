using System.Threading;
using Kedo.Diagnostics;

namespace Kedo.Runtime;

/// <summary>Vocabulary a program uses to talk to its host; each kind has its own names and ids (ADR-0015).</summary>
public enum BindingKind
{
    /// <summary>A context field read as <c>ctx.field</c>.</summary>
    Context,
    /// <summary>A host query: <c>name(args)</c>, <c>receiver.name(args)</c> or <c>receiver.name</c>.</summary>
    Query,
    /// <summary>An effect, by its reference as written (<c>effect:alarm_raised</c>).</summary>
    Effect,
    /// <summary>A signal raised with <c>emit</c> or awaited with <c>wait signal</c>, by its reference (<c>signal:phase_two</c>).</summary>
    Signal,
    /// <summary>A roll stream awaited with <c>wait roll</c>.</summary>
    Stream,
}

/// <summary>
/// Names the host exposes to behaviours, each mapped to an integer id the host chooses (its own enum or table index).
/// Built once at load and shared by every program of the session; <see cref="BehaviorBinder"/> turns the names of a program
/// into these ids. Registering the same name or id twice in one kind throws: bindings are code, not content.
/// </summary>
public sealed class BindingTable
{
    private const int KindCount = 5;
    private readonly Dictionary<string, int>[] _ids = new Dictionary<string, int>[KindCount];
    private readonly Dictionary<int, string>[] _names = new Dictionary<int, string>[KindCount];

    /// <summary>Creates an empty table whose symbol handles live in <paramref name="symbols"/> (a new table when null).</summary>
    public BindingTable(SymbolTable? symbols = null)
    {
        Symbols = symbols ?? new SymbolTable();
        for (var i = 0; i < KindCount; i++)
        {
            _ids[i] = new Dictionary<string, int>(StringComparer.Ordinal);
            _names[i] = new Dictionary<int, string>();
        }
    }

    /// <summary>Symbol table of the session: the handles of <see cref="HostValue.Symbol"/> values the bound programs produce.</summary>
    public SymbolTable Symbols { get; }

    /// <summary>Registers a context field.</summary>
    public BindingTable Context(string field, int slot) => Add(BindingKind.Context, field, slot);

    /// <summary>Registers a query.</summary>
    public BindingTable Query(string queryId, int id) => Add(BindingKind.Query, queryId, id);

    /// <summary>Registers an effect by its reference as written (<c>effect:alarm_raised</c>).</summary>
    public BindingTable Effect(string effectId, int id) => Add(BindingKind.Effect, effectId, id);

    /// <summary>Registers a signal by its reference as written (<c>signal:phase_two</c>).</summary>
    public BindingTable Signal(string signalId, int id) => Add(BindingKind.Signal, signalId, id);

    /// <summary>Registers a roll stream.</summary>
    public BindingTable Stream(string streamId, int id) => Add(BindingKind.Stream, streamId, id);

    /// <summary>Registers a name of any kind. Ids are non-negative; a name or id already registered in the kind throws.</summary>
    public BindingTable Add(BindingKind kind, string name, int id)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        var k = (int)kind;
        if (_ids[k].ContainsKey(name))
        {
            throw new ArgumentException($"{Describe(kind)} `{name}` is already bound", nameof(name));
        }

        if (_names[k].ContainsKey(id))
        {
            throw new ArgumentException($"{Describe(kind)} id {id} is already bound to `{_names[k][id]}`", nameof(id));
        }

        _ids[k].Add(name, id);
        _names[k].Add(id, name);
        return this;
    }

    /// <summary>Id of a name, or false when the host did not register it.</summary>
    public bool TryGet(BindingKind kind, string name, out int id) => _ids[(int)kind].TryGetValue(name, out id);

    /// <summary>Name bound to an id, or null.</summary>
    public string? NameOf(BindingKind kind, int id) => _names[(int)kind].TryGetValue(id, out var name) ? name : null;

    /// <summary>Registered names of a kind (any order).</summary>
    public IEnumerable<string> Names(BindingKind kind) => _ids[(int)kind].Keys;

    /// <summary>
    /// A table with every name the programs use, ids in order of first appearance per kind. For tools, tests and
    /// <see cref="BehaviorHostAdapter"/>; a game usually registers its own ids.
    /// </summary>
    public static BindingTable FromPrograms(IEnumerable<BehaviorProgram> programs, SymbolTable? symbols = null)
    {
        var table = new BindingTable(symbols);
        foreach (var program in programs)
        {
            foreach (var (kind, name, _) in BehaviorBinder.Uses(program))
            {
                if (!table.TryGet(kind, name, out _))
                {
                    table.Add(kind, name, table._ids[(int)kind].Count);
                }
            }
        }

        return table;
    }

    internal static string Describe(BindingKind kind) => kind switch
    {
        BindingKind.Context => "context field",
        BindingKind.Query => "query",
        BindingKind.Effect => "effect",
        BindingKind.Signal => "signal",
        _ => "roll stream",
    };
}

/// <summary>
/// Handle of a hook of one <see cref="BoundProgram"/>, from <see cref="BoundProgram.Hook"/>. The default value is invalid;
/// activating an invalid handle, or one of another program, fails with <see cref="RunError.NoSuchHook"/>.
/// </summary>
public readonly struct HookHandle
{
    internal HookHandle(int program, int function)
    {
        Program = program;
        Function = function;
    }

    internal int Program { get; }

    internal int Function { get; }

    /// <summary>True for a handle returned by <see cref="BoundProgram.Hook"/> for an existing hook.</summary>
    public bool IsValid => Program != 0;
}

/// <summary>
/// A program whose names are resolved to the host's ids and whose symbols are interned, once, at load (ADR-0015).
/// Immutable; one bound program serves any number of <see cref="FastBehaviorVm"/> instances (one per worker thread).
/// </summary>
public sealed class BoundProgram
{
    private static int s_nextToken;

    internal BoundProgram(BehaviorProgram program, BindingTable bindings, bool isValid, int[] sites, int[] symbols, string?[] labels, string?[] hookIds)
    {
        Program = program;
        Bindings = bindings;
        IsValid = isValid;
        Sites = sites;
        SymbolHandles = symbols;
        Labels = labels;
        HookIds = hookIds;
        Token = Interlocked.Increment(ref s_nextToken);
    }

    /// <summary>The compiled program.</summary>
    public BehaviorProgram Program { get; }

    /// <summary>The table its names were bound against.</summary>
    public BindingTable Bindings { get; }

    /// <summary>False when the code did not pass verification (K6002): every activation then fails with <see cref="RunError.InvalidProgram"/>.</summary>
    public bool IsValid { get; }

    /// <summary>Bound id of the name used by the instruction at each pc, or -1 (not a host instruction, or unbound: K6001).</summary>
    internal int[] Sites { get; }

    /// <summary>Symbol handle of each string, interned at bind time for <c>LoadSymbol</c> (0 for strings no instruction loads).</summary>
    internal int[] SymbolHandles { get; }

    /// <summary>Await label at each pc, or null.</summary>
    internal string?[] Labels { get; }

    /// <summary>Hook id of each function, or null for plain functions.</summary>
    internal string?[] HookIds { get; }

    internal int Token { get; }

    /// <summary>Handle of a hook; an invalid handle when the program has no such hook.</summary>
    public HookHandle Hook(string hookId) => TryGetHook(hookId, out var hook) ? hook : default;

    /// <summary>Handle of a hook, or false when the program has no such hook.</summary>
    public bool TryGetHook(string hookId, out HookHandle hook)
    {
        if (Program.Hooks.TryGetValue(hookId, out var fn))
        {
            hook = new HookHandle(Token, fn);
            return true;
        }

        hook = default;
        return false;
    }

    /// <summary>Label of an effect site, as in the receipts of a saved frame (<c>pc17</c>).</summary>
    internal string SiteLabel(int pc) => (uint)pc < (uint)Labels.Length && Labels[pc] is { } label ? label : "pc" + pc.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Binds a program to the host's ids (ADR-0015). Never throws on content: a name the host did not register is K6001 and
/// fails only the activations that reach it; a program that does not verify is K6002.
/// </summary>
public static class BehaviorBinder
{
    /// <summary>Binds a program; <paramref name="diagnostics"/> lists the problems (empty when the host covers every name).</summary>
    public static BoundProgram Bind(BehaviorProgram program, BindingTable bindings, out IReadOnlyList<Diagnostic> diagnostics)
    {
        var bag = new DiagnosticBag();
        var bound = Bind(program, bindings, bag);
        diagnostics = bag.Items;
        return bound;
    }

    /// <summary>Binds a program, appending its problems to <paramref name="diagnostics"/> (to bind a whole pack into one bag).</summary>
    public static BoundProgram Bind(BehaviorProgram program, BindingTable bindings, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var code = program.Code;
        var sites = new int[code.Length];
        Array.Fill(sites, -1);
        var symbols = new int[program.Strings.Length];
        var labels = new string?[code.Length];
        foreach (var site in program.AwaitSites)
        {
            if ((uint)site.Pc < (uint)labels.Length)
            {
                labels[site.Pc] ??= site.Label;   // the first site wins, as BehaviorProgram.LabelAt
            }
        }

        var hookIds = new string?[program.Functions.Count];
        foreach (var (id, fn) in program.Hooks)
        {
            if ((uint)fn < (uint)hookIds.Length)
            {
                hookIds[fn] = id;
            }
        }

        var error = ProgramVerifier.Check(program, out var at) ?? CheckOperands(program, out at);
        if (error is not null)
        {
            diagnostics.Error("K6002", $"program `{program.BehaviorId}` is not valid: {error} ({Where(program, at)}); rebuild it with `kedo build`", null, default);
            return new BoundProgram(program, bindings, false, sites, symbols, labels, hookIds);
        }

        var missing = new List<(BindingKind Kind, string Name, int Pc)>();
        var seen = new HashSet<(BindingKind, string)>();
        foreach (var (kind, name, pc) in Uses(program))
        {
            if (bindings.TryGet(kind, name, out var id))
            {
                sites[pc] = id;
            }
            else if (seen.Add((kind, name)))
            {
                missing.Add((kind, name, pc));
            }
        }

        for (var pc = 0; pc < code.Length; pc++)
        {
            var op = Instr.Op(code[pc]);
            if (op == OpCode.LoadSymbol)
            {
                var s = Instr.Imm(code[pc]);
                symbols[s] = bindings.Symbols.Intern(program.Strings[s]);
            }

            if (IsTwoWord(op))
            {
                pc++;
            }
        }

        foreach (var (kind, name, pc) in missing)
        {
            var what = BindingTable.Describe(kind);
            var near = Suggest(name, bindings.Names(kind));
            diagnostics.Error("K6001",
                $"{what} `{name}` used by `{program.BehaviorId}` ({Where(program, pc)}) is not bound by the host; "
                + (near is null ? $"register it in the binding table (`BindingTable.{kind}`)" : $"did you mean `{near}`?"),
                null, default);
        }

        return new BoundProgram(program, bindings, true, sites, symbols, labels, hookIds);
    }

    /// <summary>Every host name a program uses, with its kind and the pc of the instruction.</summary>
    internal static IEnumerable<(BindingKind Kind, string Name, int Pc)> Uses(BehaviorProgram program)
    {
        var code = program.Code;
        for (var pc = 0; pc < code.Length; pc++)
        {
            var op = Instr.Op(code[pc]);
            BindingKind? kind = op switch
            {
                OpCode.LoadContext => BindingKind.Context,
                OpCode.Query => BindingKind.Query,
                OpCode.Apply => BindingKind.Effect,
                OpCode.Emit or OpCode.WaitSignal => BindingKind.Signal,
                OpCode.WaitRoll => BindingKind.Stream,
                _ => null,
            };
            var s = Instr.Imm(code[pc]);
            if (kind is { } k && (uint)s < (uint)program.Strings.Length)
            {
                yield return (k, program.Strings[s], pc);
            }

            if (IsTwoWord(op))
            {
                pc++;
            }
        }
    }

    internal static bool IsTwoWord(OpCode op) => op is OpCode.Query or OpCode.Apply or OpCode.Emit or OpCode.WaitDecision or OpCode.WaitRoll or OpCode.Call;

    // Checks the fast VM relies on beyond ProgramVerifier: the argument word of a call and the option strings of a decision.
    private static string? CheckOperands(BehaviorProgram program, out int at)
    {
        var code = program.Code;
        for (var pc = 0; pc < code.Length; pc++)
        {
            at = pc;
            var op = Instr.Op(code[pc]);
            if (!IsTwoWord(op))
            {
                continue;
            }

            if (pc + 1 >= code.Length)
            {
                return "truncated two-word instruction";
            }

            if (op == OpCode.WaitDecision)
            {
                var options = code[pc + 1];
                var first = Instr.Imm(options);
                if (first < 0 || first + Instr.A(options) > program.Strings.Length)
                {
                    return $"decision options out of range at {pc}";
                }
            }

            if (op == OpCode.Call && program.Functions[Instr.Imm(code[pc])].Entry is var entry && (entry < 0 || entry >= code.Length))
            {
                return $"call to a function without code at {pc}";
            }

            pc++;
        }

        at = -1;
        return null;
    }

    private static string Where(BehaviorProgram program, int pc) => (uint)pc < (uint)program.SourceMap.Count ? program.SourceMap[pc] : "?";

    private static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = 3;
        foreach (var c in candidates.OrderBy(c => c, StringComparer.Ordinal))
        {
            var d = Levenshtein(name, c);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
