namespace Kedo.Runtime;

/// <summary>
/// VM opcodes. Instructions are 32-bit: <c>op:8 | a:8 | b:8 | c:8</c> or <c>op:8 | a:8 | imm:16</c> (signed for jumps).
/// Registers are 64-bit integers; symbolic values (enum values, references, text) are handles into the session
/// <see cref="SymbolTable"/>. There are no floats (ADR-0007 D6, anexo E).
/// </summary>
public enum OpCode : byte
{
    /// <summary>No operation.</summary>
    Nop = 0,
    /// <summary><c>r[a] = constants[imm]</c> (integer constant).</summary>
    LoadConst,
    /// <summary><c>r[a] = symbol handle of strings[imm]</c>.</summary>
    LoadSymbol,
    /// <summary><c>r[a] = r[b]</c>.</summary>
    Move,
    /// <summary><c>r[a] = r[b] + r[c]</c>.</summary>
    Add,
    /// <summary><c>r[a] = r[b] - r[c]</c>.</summary>
    Sub,
    /// <summary><c>r[a] = r[b] * r[c]</c>.</summary>
    Mul,
    /// <summary><c>r[a] = r[b] / r[c]</c>; division by zero fails the behaviour.</summary>
    Div,
    /// <summary><c>r[a] = r[b] % r[c]</c>.</summary>
    Mod,
    /// <summary><c>r[a] = -r[b]</c>.</summary>
    Neg,
    /// <summary><c>r[a] = r[b] == r[c]</c>.</summary>
    Eq,
    /// <summary><c>r[a] = r[b] != r[c]</c>.</summary>
    Ne,
    /// <summary><c>r[a] = r[b] &lt; r[c]</c>.</summary>
    Lt,
    /// <summary><c>r[a] = r[b] &lt;= r[c]</c>.</summary>
    Le,
    /// <summary><c>r[a] = r[b] &amp;&amp; r[c]</c>.</summary>
    And,
    /// <summary><c>r[a] = r[b] || r[c]</c>.</summary>
    Or,
    /// <summary><c>r[a] = !r[b]</c>.</summary>
    Not,
    /// <summary><c>pc += imm</c>.</summary>
    Jump,
    /// <summary><c>if (r[a] == 0) pc += imm</c>.</summary>
    JumpIfFalse,
    /// <summary><c>r[a] = context field strings[imm]</c> (host provides the hook context).</summary>
    LoadContext,
    /// <summary>Call program function <c>functions[imm]</c> with arguments in <c>r[a..a+c)</c>; result in <c>r[a]</c>. Encoded as two words: this one and a trailing <see cref="Nop"/> carrying b=argBase, c=argCount in its a/b fields.</summary>
    Call,
    /// <summary>Return <c>r[a]</c> from the current function.</summary>
    Return,
    /// <summary><c>r[a] = host.Query(strings[b-word], args r[c..])</c>. Two words: op a imm(query symbol) ; Nop argBase argCount.</summary>
    Query,
    /// <summary><c>host.Apply(effect strings[imm], args)</c>. Two words.</summary>
    Apply,
    /// <summary><c>host.Raise(signal strings[imm], args)</c>. Two words.</summary>
    Emit,
    /// <summary>Suspend until <c>r[a]</c> minutes elapse. Resumes at the next instruction.</summary>
    WaitMinutes,
    /// <summary>Suspend for a player decision on prompt <c>strings[imm]</c>; resumes with the chosen option index in <c>r[a]</c>. Options table in the trailing <see cref="Nop"/> word (a=first option symbol index, b=count).</summary>
    WaitDecision,
    /// <summary>Suspend for a roll on stream <c>strings[imm]</c>; result in <c>r[a]</c>. Two words with args.</summary>
    WaitRoll,
    /// <summary>Suspend until signal <c>strings[imm]</c>; payload handle in <c>r[a]</c>.</summary>
    WaitSignal,
    /// <summary>End the behaviour successfully.</summary>
    Stop,
    /// <summary>Host trace point: <c>strings[imm]</c>.</summary>
    Trace,
}

/// <summary>Helpers to pack and unpack 32-bit instructions.</summary>
public static class Instr
{
    /// <summary>Packs <c>op a b c</c>.</summary>
    public static uint Abc(OpCode op, int a, int b, int c) => (uint)op | ((uint)(a & 0xFF) << 8) | ((uint)(b & 0xFF) << 16) | ((uint)(c & 0xFF) << 24);

    /// <summary>Packs <c>op a imm16</c> (imm is signed).</summary>
    public static uint AImm(OpCode op, int a, int imm) => (uint)op | ((uint)(a & 0xFF) << 8) | ((uint)(ushort)(short)imm << 16);

    /// <summary>Opcode of a word.</summary>
    public static OpCode Op(uint w) => (OpCode)(w & 0xFF);

    /// <summary>Field a.</summary>
    public static int A(uint w) => (int)((w >> 8) & 0xFF);

    /// <summary>Field b.</summary>
    public static int B(uint w) => (int)((w >> 16) & 0xFF);

    /// <summary>Field c.</summary>
    public static int C(uint w) => (int)((w >> 24) & 0xFF);

    /// <summary>Signed 16-bit immediate.</summary>
    public static int Imm(uint w) => (short)(ushort)(w >> 16);
}

/// <summary>A compiled function or hook body inside a program.</summary>
public sealed record FunctionInfo(string Name, int Entry, int RegisterCount, int ParameterCount);

/// <summary>Stable label of a suspension site: resumption is by label, never by instruction offset (KEDO-02, anexo E).</summary>
public sealed record AwaitSite(string Label, int Pc);

/// <summary>
/// A compiled behaviour program: code, constants, strings, functions, hook entry points, await labels and a source map.
/// Produced by the compiler, consumed by the VM. Immutable.
/// </summary>
public sealed class BehaviorProgram
{
    /// <summary>Creates a program.</summary>
    public BehaviorProgram(string behaviorId, int definitionVersion, string definitionHash, uint[] code, long[] constants, string[] strings,
        IReadOnlyList<FunctionInfo> functions, IReadOnlyDictionary<string, int> hooks, IReadOnlyList<AwaitSite> awaitSites, IReadOnlyList<string> sourceMap)
    {
        BehaviorId = behaviorId;
        DefinitionVersion = definitionVersion;
        DefinitionHash = definitionHash;
        Code = code;
        Constants = constants;
        Strings = strings;
        Functions = functions;
        Hooks = hooks;
        AwaitSites = awaitSites;
        SourceMap = sourceMap;
    }

    /// <summary>Owner object key (<c>type:id</c>) or module function group.</summary>
    public string BehaviorId { get; }

    /// <summary>Version of the owning definition.</summary>
    public int DefinitionVersion { get; }

    /// <summary>Content hash of the owning definition.</summary>
    public string DefinitionHash { get; }

    /// <summary>Instruction words.</summary>
    public uint[] Code { get; }

    /// <summary>Integer constants.</summary>
    public long[] Constants { get; }

    /// <summary>Strings: symbol names, effect/signal/query/prompt ids, trace messages, context field names.</summary>
    public string[] Strings { get; }

    /// <summary>Functions by index (hooks are functions too).</summary>
    public IReadOnlyList<FunctionInfo> Functions { get; }

    /// <summary>Hook id → function index.</summary>
    public IReadOnlyDictionary<string, int> Hooks { get; }

    /// <summary>Await sites with stable labels.</summary>
    public IReadOnlyList<AwaitSite> AwaitSites { get; }

    /// <summary>Source location text per instruction (same length as <see cref="Code"/>), for traces and diagnostics.</summary>
    public IReadOnlyList<string> SourceMap { get; }

    /// <summary>Finds the pc of an await label, or -1 if the label no longer exists.</summary>
    public int PcOfLabel(string label)
    {
        foreach (var site in AwaitSites)
        {
            if (site.Label == label)
            {
                return site.Pc;
            }
        }

        return -1;
    }

    /// <summary>Finds the label of an await site at a pc, or null.</summary>
    public string? LabelAt(int pc)
    {
        foreach (var site in AwaitSites)
        {
            if (site.Pc == pc)
            {
                return site.Label;
            }
        }

        return null;
    }

    /// <summary>Function index of a function name, or -1.</summary>
    public int FunctionIndex(string name)
    {
        for (var i = 0; i < Functions.Count; i++)
        {
            if (Functions[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Session-wide interning of symbolic values so programs, host and frames agree on handles.</summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _names = new();

    /// <summary>Handle of a name, interning it on first use. Handles start at 1; 0 is "none".</summary>
    public int Intern(string name)
    {
        if (_ids.TryGetValue(name, out var id))
        {
            return id;
        }

        _names.Add(name);
        id = _names.Count;
        _ids.Add(name, id);
        return id;
    }

    /// <summary>Name of a handle, or null for 0 and unknown handles.</summary>
    public string? Name(long handle) => handle >= 1 && handle <= _names.Count ? _names[(int)handle - 1] : null;

    /// <summary>Number of interned symbols.</summary>
    public int Count => _names.Count;
}
