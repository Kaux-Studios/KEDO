using System.Globalization;
using Kedo.Schema;

namespace Kedo.Runtime;

/// <summary>
/// Fast entry (ADR-0015): the register VM of <see cref="BehaviorVm"/> over a <see cref="BoundProgram"/> and an
/// <see cref="IFastBehaviorHost"/>. Hooks are activated by handle, names were resolved at bind time, registers, call stack
/// and argument buffer are reused, the result is a struct passed by reference, and a frame is taken from a pool only when a
/// hook suspends: after warm-up an activation allocates nothing. Same instruction semantics, effect keys, budgets and frame
/// format as <see cref="BehaviorVm"/>. One instance runs one activation at a time; use one per worker thread.
/// </summary>
public sealed class FastBehaviorVm
{
    // Slack after the highest register a frame declares, so every 8-bit operand stays inside the array.
    private const int OperandSlack = 256;

    private readonly BoundProgram _bound;
    private readonly BehaviorProgram _program;
    private readonly IFastBehaviorHost _host;
    private readonly BehaviorHostAdapter? _adapter;
    private readonly Budget _budget;
    private readonly bool _tracing;
    private readonly List<string> _trace = new();

    // Execution state of the current activation.
    private long[] _regs = new long[64 + OperandSlack];
    private bool[] _sym = new bool[64 + OperandSlack];
    private CallRecord[] _stack = new CallRecord[8];
    private int _depth;
    private Receipt[] _receipts = new Receipt[8];
    private int _receiptCount;
    private int _restoredReceipts;
    private readonly HostValue[] _args = new HostValue[256];
    private long _steps;
    private int _effectSequence;
    private long _instance;
    private int _hook;

    // Frame pool.
    private readonly List<Suspension> _frames = new();
    private readonly Stack<int> _free = new();
    private int _suspended;

    /// <summary>Creates a VM for a bound program against a host.</summary>
    public FastBehaviorVm(BoundProgram program, IFastBehaviorHost host, Budget? budget = null, VmOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(host);
        _bound = program;
        _program = program.Program;
        _host = host;
        _adapter = host as BehaviorHostAdapter;
        _budget = budget ?? Budget.Default;
        _tracing = (options ?? VmOptions.Default).Trace;
    }

    /// <summary>The bound program.</summary>
    public BoundProgram Program => _bound;

    /// <summary>Frames suspended in the pool (not yet resumed to the end or released).</summary>
    public int SuspendedCount => _suspended;

    /// <summary>Trace points and failure messages of the last call, when <see cref="VmOptions.Trace"/> is on; empty otherwise.</summary>
    public IReadOnlyList<string> Trace => _trace;

    /// <summary>Starts a hook for an instance. <paramref name="outcome"/> receives the status, the frame if it suspended, or the error.</summary>
    public void Activate(HookHandle hook, long instance, ref RunOutcome outcome)
    {
        if (_tracing)
        {
            _trace.Clear();
        }

        Start(hook, instance, ref outcome, default, 0, 0);
    }

    /// <summary>Starts a hook for each instance; context fields come from the host (<see cref="IFastBehaviorHost.Context"/>).</summary>
    public void ActivateBatch(HookHandle hook, ReadOnlySpan<long> instances, Span<RunOutcome> outcomes)
        => ActivateBatch(hook, instances, default, outcomes);

    /// <summary>
    /// Starts a hook for each instance, reading context fields from columns: the value of slot <c>s</c> for instance <c>i</c>
    /// is <c>context[s * instances.Length + i]</c>. A slot past the end of <paramref name="context"/> is asked to the host.
    /// </summary>
    public void ActivateBatch(HookHandle hook, ReadOnlySpan<long> instances, ReadOnlySpan<HostValue> context, Span<RunOutcome> outcomes)
    {
        if (outcomes.Length < instances.Length)
        {
            throw new ArgumentException("there must be one outcome per instance", nameof(outcomes));
        }

        if (_tracing)
        {
            _trace.Clear();
        }

        for (var i = 0; i < instances.Length; i++)
        {
            Start(hook, instances[i], ref outcomes[i], context, instances.Length, i);
        }
    }

    /// <summary>Resumes a pooled frame with the awaited input (decision index, roll value, signal payload; ignored for time).</summary>
    public void Resume(FrameHandle frame, AwaitKind kind, long value, ref RunOutcome outcome)
    {
        if (_tracing)
        {
            _trace.Clear();
        }

        if (Find(frame) is not { } s)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.NotSuspended, -1);
            return;
        }

        if (s.Kind != kind)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.WrongResumption, s.AwaitPc);
            return;
        }

        // Restore the activation and deliver the value after the await instruction.
        _instance = s.Instance;
        _hook = s.Hook;
        _steps = s.Steps;
        _effectSequence = s.EffectSequence;
        if (!EnsureRegisters(s.RegisterCount))
        {
            Free(frame.Index);
            Finish(ref outcome, RunStatus.Failed, RunError.RegisterBudget, s.AwaitPc, s.Steps);
            return;
        }

        Array.Copy(s.Registers, _regs, s.RegisterCount);
        Array.Copy(s.Symbols, _sym, s.RegisterCount);
        EnsureStack(s.Depth);
        Array.Copy(s.Stack, _stack, s.Depth);
        _depth = s.Depth;
        EnsureReceipts(s.ReceiptCount);
        Array.Copy(s.Receipts, _receipts, s.ReceiptCount);
        _receiptCount = s.ReceiptCount;
        _restoredReceipts = s.ReceiptCount;
        ref var top = ref _stack[_depth - 1];
        if (s.ResultRegister >= 0)
        {
            var abs = top.RegisterBase + s.ResultRegister;
            _regs[abs] = value;
            _sym[abs] = kind == AwaitKind.Signal;
        }

        top.Pc = s.AwaitPc + (Instr.Op(_program.Code[s.AwaitPc]) is OpCode.WaitDecision or OpCode.WaitRoll ? 2 : 1);
        Run(ref outcome, default, 0, 0, frame.Index);
    }

    /// <summary>
    /// Restores a saved frame and resumes it, like <see cref="BehaviorVm.Resume"/>: a frame whose definition changed follows
    /// <paramref name="stalePolicy"/>. The instance id of the frame must be an integer (frames saved by this VM are).
    /// </summary>
    public void Resume(BehaviorFrame frame, AwaitKind kind, long value, ref RunOutcome outcome, StalePolicy stalePolicy = StalePolicy.AbortSilently)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_tracing)
        {
            _trace.Clear();
        }

        var handle = Restore(frame, ref outcome, out var stale);
        if (stale)
        {
            switch (stalePolicy)
            {
                case StalePolicy.RestartAtEntry when long.TryParse(frame.InstanceId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var instance):
                    Note($"stale frame: restarting hook `{frame.HookId}` at entry");
                    Start(_bound.Hook(frame.HookId), instance, ref outcome, default, 0, 0);
                    return;
                case StalePolicy.AbortWithCompensation:
                    Finish(ref outcome, RunStatus.Failed, RunError.StaleFrameCompensation, -1);
                    return;
                default:
                    Finish(ref outcome, RunStatus.Failed, RunError.StaleFrame, -1);
                    return;
            }
        }

        if (handle.IsValid)
        {
            Resume(handle, kind, value, ref outcome);
        }
    }

    /// <summary>
    /// Restores a saved frame into the pool without running it. Returns its handle, or an invalid handle with the reason in
    /// <paramref name="outcome"/> (<see cref="RunError.StaleFrame"/> when its definition changed, <see cref="RunError.UnsupportedFrame"/>).
    /// </summary>
    public FrameHandle Load(BehaviorFrame frame, ref RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var handle = Restore(frame, ref outcome, out var stale);
        if (stale)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.StaleFrame, -1);
        }

        return handle;
    }

    /// <summary>What a pooled frame waits for. An invalid or released handle returns the default value.</summary>
    public PendingAwait Pending(FrameHandle frame)
    {
        if (Find(frame) is not { } s)
        {
            return default;
        }

        var code = _program.Code;
        var w = code[s.AwaitPc];
        var label = _bound.Labels[s.AwaitPc] ?? "pc" + s.AwaitPc.ToString(CultureInfo.InvariantCulture);
        switch (s.Kind)
        {
            case AwaitKind.Minutes:
                return new PendingAwait(s.Kind, label, s.DueAt, _program.TimeUnit, -1, null, default);
            case AwaitKind.Decision:
            {
                var options = code[s.AwaitPc + 1];
                return new PendingAwait(s.Kind, label, 0, _program.TimeUnit, -1, _program.Strings[Instr.Imm(w)],
                    new ReadOnlyMemory<string>(_program.Strings, Instr.Imm(options), Instr.A(options)));
            }

            default:
                return new PendingAwait(s.Kind, label, 0, _program.TimeUnit, _bound.Sites[s.AwaitPc], _program.Strings[Instr.Imm(w)], default);
        }
    }

    /// <summary>Discards a pooled frame (its instance is gone). Returns false for an invalid or already released handle.</summary>
    public bool Release(FrameHandle frame)
    {
        if (Find(frame) is null)
        {
            return false;
        }

        Free(frame.Index);
        return true;
    }

    /// <summary>The serializable frame of a pooled one, in the format of <see cref="BehaviorVm"/> (allocates; for saves).</summary>
    public BehaviorFrame Save(FrameHandle frame)
    {
        var s = Find(frame) ?? throw new ArgumentException("the frame is not suspended in this VM", nameof(frame));
        var stack = new List<CallFrame>(s.Depth);
        for (var i = 0; i < s.Depth; i++)
        {
            var r = s.Stack[i];
            var top = i == s.Depth - 1;
            stack.Add(new CallFrame(r.Function, _program.Functions[r.Function].Name, r.RegisterBase, r.RegisterCount, r.ReturnRegister,
                top ? _bound.Labels[s.AwaitPc] ?? "pc" + s.AwaitPc.ToString(CultureInfo.InvariantCulture) : null, top ? s.AwaitPc : r.Pc));
        }

        var used = 0;
        for (var i = 0; i < s.Depth; i++)
        {
            used = Math.Max(used, s.Stack[i].RegisterBase + s.Stack[i].RegisterCount);
        }

        var regs = new long[used];
        var names = new string?[used];
        for (var i = 0; i < used; i++)
        {
            regs[i] = s.Registers[i];
            names[i] = s.Symbols[i] ? _bound.Bindings.Symbols.Name(s.Registers[i]) : null;
        }

        var instance = s.Instance.ToString(CultureInfo.InvariantCulture);
        var applied = new string[s.ReceiptCount];
        for (var i = 0; i < s.ReceiptCount; i++)
        {
            applied[i] = $"{instance}/{_bound.SiteLabel(s.Receipts[i].Site)}#{s.Receipts[i].Sequence.ToString(CultureInfo.InvariantCulture)}";
        }

        return new BehaviorFrame(BehaviorFrame.CurrentFormat, instance, _program.BehaviorId, _program.DefinitionVersion, _program.DefinitionHash,
            _bound.HookIds[s.Hook] ?? "", stack, regs, names, Request(s), applied, s.Steps, s.EffectSequence);
    }

    /// <summary>A failure as text, with its source location: what <see cref="RunResult.Error"/> would say (allocates).</summary>
    public string Describe(in RunOutcome outcome)
    {
        var message = Message(outcome);
        return (uint)outcome.Pc < (uint)_program.SourceMap.Count ? $"{message} ({_program.SourceMap[outcome.Pc]})" : message;
    }

    private string Message(in RunOutcome outcome)
        => outcome.Error switch
        {
            RunError.None => outcome.Status.ToString(),
            RunError.NoSuchHook => $"program `{_program.BehaviorId}` has no such hook",
            RunError.InvalidProgram => $"program `{_program.BehaviorId}` did not pass verification (K6002)",
            RunError.Unbound => Unbound(outcome.Pc),
            RunError.StepBudget => $"step budget of {_budget.MaxSteps} exhausted",
            RunError.CallDepth => $"call depth of {_budget.MaxCallDepth} exceeded",
            RunError.RegisterBudget => $"the program needs more registers than the budget allows ({_budget.MaxRegisters})",
            RunError.DivisionByZero => "division by zero",
            RunError.FellOffEnd => "fell off the end of the code",
            RunError.NotSuspended => "frame is not suspended",
            RunError.WrongResumption => "the resumption is not of the kind the frame waits for",
            RunError.StaleFrame => "stale frame: definition changed; aborted",
            RunError.StaleFrameCompensation => "stale frame: definition changed; compensation requested",
            _ => "the frame cannot be restored by this program",
        };

    // ----- activation -----

    private void Start(HookHandle hook, long instance, ref RunOutcome outcome, ReadOnlySpan<HostValue> context, int stride, int row)
    {
        if (hook.Program != _bound.Token || (uint)hook.Function >= (uint)_program.Functions.Count)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.NoSuchHook, -1);
            return;
        }

        if (!_bound.IsValid)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.InvalidProgram, -1);
            return;
        }

        _instance = instance;
        _hook = hook.Function;
        _steps = 0;
        _effectSequence = 0;
        _receiptCount = 0;
        _restoredReceipts = 0;
        var info = _program.Functions[hook.Function];
        if (!EnsureRegisters(info.RegisterCount))
        {
            Finish(ref outcome, RunStatus.Failed, RunError.RegisterBudget, info.Entry);
            return;
        }

        _stack[0] = new CallRecord { Function = hook.Function, RegisterBase = 0, RegisterCount = info.RegisterCount, ReturnRegister = -1, Pc = info.Entry };
        _depth = 1;
        Run(ref outcome, context, stride, row, -1);
    }

    private void Run(ref RunOutcome outcome, ReadOnlySpan<HostValue> context, int stride, int row, int resumed)
    {
        var code = _program.Code;
        var constants = _program.Constants;
        var sites = _bound.Sites;
        var symbols = _bound.SymbolHandles;
        var regs = _regs;
        var sym = _sym;
        var maxSteps = _budget.MaxSteps;
        var steps = _steps;
        ref var frame = ref _stack[_depth - 1];
        var pc = frame.Pc;
        var rb = frame.RegisterBase;
        RunStatus status;
        RunError error;

        while (true)
        {
            if ((uint)pc >= (uint)code.Length)
            {
                (status, error) = (RunStatus.Failed, RunError.FellOffEnd);
                break;
            }

            if (++steps > maxSteps)
            {
                (status, error) = (RunStatus.Failed, RunError.StepBudget);
                break;
            }

            var w = code[pc];
            var a = rb + Instr.A(w);
            var b = rb + Instr.B(w);
            var c = rb + Instr.C(w);
            var next = pc + 1;
            switch (Instr.Op(w))
            {
                case OpCode.Nop:
                    break;
                case OpCode.LoadConst:
                    regs[a] = constants[Instr.Imm(w)];
                    sym[a] = false;
                    break;
                case OpCode.LoadSymbol:
                    regs[a] = symbols[Instr.Imm(w)];
                    sym[a] = true;
                    break;
                case OpCode.Move:
                    regs[a] = regs[b];
                    sym[a] = sym[b];
                    break;
                case OpCode.Add: regs[a] = regs[b] + regs[c]; sym[a] = false; break;
                case OpCode.Sub: regs[a] = regs[b] - regs[c]; sym[a] = false; break;
                case OpCode.Mul: regs[a] = regs[b] * regs[c]; sym[a] = false; break;
                case OpCode.Div:
                    if (regs[c] == 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.DivisionByZero);
                        goto done;
                    }

                    regs[a] = regs[b] / regs[c];
                    sym[a] = false;
                    break;
                case OpCode.Mod:
                    if (regs[c] == 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.DivisionByZero);
                        goto done;
                    }

                    regs[a] = regs[b] % regs[c];
                    sym[a] = false;
                    break;
                case OpCode.Neg: regs[a] = -regs[b]; sym[a] = false; break;
                case OpCode.Eq: regs[a] = regs[b] == regs[c] ? 1 : 0; sym[a] = false; break;
                case OpCode.Ne: regs[a] = regs[b] != regs[c] ? 1 : 0; sym[a] = false; break;
                case OpCode.Lt: regs[a] = regs[b] < regs[c] ? 1 : 0; sym[a] = false; break;
                case OpCode.Le: regs[a] = regs[b] <= regs[c] ? 1 : 0; sym[a] = false; break;
                case OpCode.And: regs[a] = regs[b] != 0 && regs[c] != 0 ? 1 : 0; sym[a] = false; break;
                case OpCode.Or: regs[a] = regs[b] != 0 || regs[c] != 0 ? 1 : 0; sym[a] = false; break;
                case OpCode.Not: regs[a] = regs[b] == 0 ? 1 : 0; sym[a] = false; break;
                case OpCode.Jump:
                    next = pc + 1 + Instr.Imm(w);
                    break;
                case OpCode.JumpIfFalse:
                    if (regs[a] == 0)
                    {
                        next = pc + 1 + Instr.Imm(w);
                    }

                    break;
                case OpCode.LoadContext:
                {
                    var slot = sites[pc];
                    if (slot < 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.Unbound);
                        goto done;
                    }

                    var index = (long)slot * stride + row;
                    var v = stride > 0 && (ulong)index < (ulong)context.Length ? context[(int)index] : _host.Context(_instance, slot);
                    regs[a] = v.Number;
                    sym[a] = v.IsSymbol;
                    break;
                }

                case OpCode.Call:
                {
                    var fnIndex = Instr.Imm(w);
                    var argWord = code[pc + 1];
                    var argBase = rb + Instr.A(argWord);
                    var argCount = Instr.B(argWord);
                    var info = _program.Functions[fnIndex];
                    if (_depth >= _budget.MaxCallDepth)
                    {
                        (status, error) = (RunStatus.Failed, RunError.CallDepth);
                        goto done;
                    }

                    var newBase = rb + frame.RegisterCount;
                    if (!EnsureRegisters(newBase + info.RegisterCount))
                    {
                        (status, error) = (RunStatus.Failed, RunError.RegisterBudget);
                        goto done;
                    }

                    regs = _regs;
                    sym = _sym;
                    for (var i = 0; i < argCount && i < info.ParameterCount; i++)
                    {
                        regs[newBase + i] = regs[argBase + i];
                        sym[newBase + i] = sym[argBase + i];
                    }

                    frame.Pc = pc + 2;
                    EnsureStack(_depth + 1);
                    _stack[_depth] = new CallRecord { Function = fnIndex, RegisterBase = newBase, RegisterCount = info.RegisterCount, ReturnRegister = a, Pc = info.Entry };
                    frame = ref _stack[_depth];
                    _depth++;
                    pc = info.Entry;
                    rb = newBase;
                    continue;
                }

                case OpCode.Return:
                {
                    var value = regs[a];
                    var isSym = sym[a];
                    var returnRegister = frame.ReturnRegister;
                    _depth--;
                    if (_depth == 0)
                    {
                        (status, error) = (RunStatus.Completed, RunError.None);
                        goto done;
                    }

                    frame = ref _stack[_depth - 1];
                    if (returnRegister >= 0)
                    {
                        regs[returnRegister] = value;
                        sym[returnRegister] = isSym;
                    }

                    pc = frame.Pc;      // already points after the call
                    rb = frame.RegisterBase;
                    continue;
                }

                case OpCode.Query:
                {
                    var id = sites[pc];
                    if (id < 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.Unbound);
                        goto done;
                    }

                    var argWord = code[pc + 1];
                    var v = _host.Query(id, Args(rb + Instr.A(argWord), Instr.B(argWord)));
                    regs[a] = v.Number;
                    sym[a] = v.IsSymbol;
                    next = pc + 2;
                    break;
                }

                case OpCode.Apply:
                {
                    var id = sites[pc];
                    if (id < 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.Unbound);
                        goto done;
                    }

                    var argWord = code[pc + 1];
                    var sequence = _effectSequence++;
                    if (!Applied(pc, sequence))
                    {
                        var key = new FastEffectKey(_instance, pc, sequence);
                        _host.Apply(in key, id, Args(rb + Instr.A(argWord), Instr.B(argWord)));
                        AddReceipt(pc, sequence);
                    }

                    next = pc + 2;
                    break;
                }

                case OpCode.Emit:
                {
                    var id = sites[pc];
                    if (id < 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.Unbound);
                        goto done;
                    }

                    var argWord = code[pc + 1];
                    _host.Raise(id, Args(rb + Instr.A(argWord), Instr.B(argWord)));
                    next = pc + 2;
                    break;
                }

                case OpCode.WaitMinutes:
                {
                    var now = _host.Now(_program.TimeUnit);
                    var wait = regs[a];
                    var due = wait > 0 && now > long.MaxValue - wait ? long.MaxValue : now + wait;   // saturate, as BehaviorVm
                    _steps = steps;
                    Suspend(ref outcome, pc, AwaitKind.Minutes, due, -1, resumed);
                    return;
                }

                case OpCode.WaitDecision:
                    _steps = steps;
                    Suspend(ref outcome, pc, AwaitKind.Decision, 0, Instr.A(w), resumed);
                    return;
                case OpCode.WaitRoll or OpCode.WaitSignal:
                    if (sites[pc] < 0)
                    {
                        (status, error) = (RunStatus.Failed, RunError.Unbound);
                        goto done;
                    }

                    _steps = steps;
                    Suspend(ref outcome, pc, Instr.Op(w) == OpCode.WaitRoll ? AwaitKind.Roll : AwaitKind.Signal, 0, Instr.A(w), resumed);
                    return;
                case OpCode.Stop:
                    (status, error) = (RunStatus.Completed, RunError.None);
                    goto done;
                case OpCode.Trace:
                    if (_tracing)
                    {
                        var message = _program.Strings[Instr.Imm(w)];
                        _trace.Add(message);
                        _adapter?.ForwardTrace(message);
                    }

                    break;
                default:
                    (status, error) = (RunStatus.Failed, RunError.InvalidProgram);
                    goto done;
            }

            pc = next;
        }

    done:
        _steps = steps;
        if (resumed >= 0)
        {
            Free(resumed);   // a resumed frame that ends leaves the pool
        }

        Finish(ref outcome, status, error, error == RunError.None ? -1 : pc, steps);
    }

    private ReadOnlySpan<HostValue> Args(int baseReg, int count)
    {
        var args = _args;
        var regs = _regs;
        var sym = _sym;
        for (var i = 0; i < count; i++)
        {
            args[i] = new HostValue(regs[baseReg + i], sym[baseReg + i]);
        }

        return new ReadOnlySpan<HostValue>(args, 0, count);
    }

    // A key can only repeat one restored from a frame: in one run the sequence only grows.
    private bool Applied(int site, int sequence)
    {
        for (var i = 0; i < _restoredReceipts; i++)
        {
            if (_receipts[i].Site == site && _receipts[i].Sequence == sequence)
            {
                return true;
            }
        }

        return false;
    }

    private void AddReceipt(int site, int sequence)
    {
        EnsureReceipts(_receiptCount + 1);
        _receipts[_receiptCount++] = new Receipt(site, sequence);
    }

    private void Finish(ref RunOutcome outcome, RunStatus status, RunError error, int pc, long steps = 0)
    {
        outcome.Status = status;
        outcome.Error = error;
        outcome.Frame = default;
        outcome.Pc = pc;
        outcome.Steps = steps;
        if (_tracing && error != RunError.None)
        {
            _trace.Add($"fail at {((uint)pc < (uint)_program.SourceMap.Count ? _program.SourceMap[pc] : "?")}: {Message(outcome)}");
        }
    }

    private void Note(string line)
    {
        if (_tracing)
        {
            _trace.Add(line);
        }
    }

    // ----- frames -----

    private void Suspend(ref RunOutcome outcome, int pc, AwaitKind kind, long dueAt, int resultRegister, int reuse)
    {
        int index;
        if (reuse >= 0)
        {
            index = reuse;
        }
        else if (_free.Count > 0)
        {
            index = _free.Pop();
            _suspended++;
        }
        else
        {
            index = _frames.Count;
            _frames.Add(new Suspension());
            _suspended++;
        }

        var s = _frames[index];
        if (reuse < 0)
        {
            s.Generation = s.Generation == int.MaxValue ? 1 : s.Generation + 1;
            s.InUse = true;
        }

        var used = 0;
        for (var i = 0; i < _depth; i++)
        {
            used = Math.Max(used, _stack[i].RegisterBase + _stack[i].RegisterCount);
        }

        s.Instance = _instance;
        s.Hook = _hook;
        s.Steps = _steps;
        s.EffectSequence = _effectSequence;
        s.Kind = kind;
        s.AwaitPc = pc;
        s.DueAt = dueAt;
        s.ResultRegister = resultRegister;
        s.RegisterCount = used;
        if (s.Registers.Length < used)
        {
            s.Registers = new long[used];
            s.Symbols = new bool[used];
        }

        Array.Copy(_regs, s.Registers, used);
        Array.Copy(_sym, s.Symbols, used);
        _stack[_depth - 1].Pc = pc;
        if (s.Stack.Length < _depth)
        {
            s.Stack = new CallRecord[_depth];
        }

        Array.Copy(_stack, s.Stack, _depth);
        s.Depth = _depth;
        if (s.Receipts.Length < _receiptCount)
        {
            s.Receipts = new Receipt[_receiptCount];
        }

        Array.Copy(_receipts, s.Receipts, _receiptCount);
        s.ReceiptCount = _receiptCount;

        outcome.Status = RunStatus.Suspended;
        outcome.Error = RunError.None;
        outcome.Frame = new FrameHandle(index, s.Generation);
        outcome.Pc = pc;
        outcome.Steps = _steps;
    }

    private Suspension? Find(FrameHandle frame)
    {
        if (!frame.IsValid || (uint)frame.Index >= (uint)_frames.Count)
        {
            return null;
        }

        var s = _frames[frame.Index];
        return s.InUse && s.Generation == frame.Generation ? s : null;
    }

    private void Free(int index)
    {
        var s = _frames[index];
        if (!s.InUse)
        {
            return;
        }

        s.InUse = false;
        s.Generation = s.Generation == int.MaxValue ? 1 : s.Generation + 1;
        _free.Push(index);
        _suspended--;
    }

    private AwaitRequest Request(Suspension s)
    {
        var label = _bound.Labels[s.AwaitPc] ?? "pc" + s.AwaitPc.ToString(CultureInfo.InvariantCulture);
        var w = _program.Code[s.AwaitPc];
        switch (s.Kind)
        {
            case AwaitKind.Minutes:
                return new AwaitRequest(AwaitKind.Minutes, label, s.DueAt, null, Array.Empty<string>(), -1) { Unit = _program.TimeUnit };
            case AwaitKind.Decision:
            {
                var optWord = _program.Code[s.AwaitPc + 1];
                var options = new string[Instr.A(optWord)];
                Array.Copy(_program.Strings, Instr.Imm(optWord), options, 0, options.Length);
                return new AwaitRequest(AwaitKind.Decision, label, 0, _program.Strings[Instr.Imm(w)], options, s.ResultRegister);
            }

            default:
                return new AwaitRequest(s.Kind, label, 0, _program.Strings[Instr.Imm(w)], Array.Empty<string>(), s.ResultRegister);
        }
    }

    // Restores a saved frame into a pooled one, mirroring BehaviorVm.Resume. Sets `stale` instead of applying a policy.
    private FrameHandle Restore(BehaviorFrame frame, ref RunOutcome outcome, out bool stale)
    {
        stale = false;
        if (frame.FormatVersion != BehaviorFrame.CurrentFormat || frame.Pending is null || frame.Stack.Count == 0
            || frame.Registers.Length != frame.SymbolNames.Length)
        {
            Finish(ref outcome, RunStatus.Failed, frame.Pending is null && frame.FormatVersion == BehaviorFrame.CurrentFormat ? RunError.NotSuspended : RunError.UnsupportedFrame, -1);
            return default;
        }

        if (frame.DefinitionHash != _program.DefinitionHash || frame.DefinitionVersion != _program.DefinitionVersion
            || (frame.Pending.Kind == AwaitKind.Minutes && frame.Pending.Unit != _program.TimeUnit))
        {
            stale = true;
            return default;
        }

        if (!_bound.IsValid)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.InvalidProgram, -1);
            return default;
        }

        if (!long.TryParse(frame.InstanceId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var instance)
            || !_program.Hooks.TryGetValue(frame.HookId, out var hook))
        {
            Finish(ref outcome, RunStatus.Failed, RunError.UnsupportedFrame, -1);
            return default;
        }

        var stack = new CallRecord[frame.Stack.Count];
        var used = 0;
        for (var i = 0; i < frame.Stack.Count; i++)
        {
            var cf = frame.Stack[i];
            var fn = _program.FunctionIndex(cf.FunctionName);
            var pc = cf.ResumeLabel is null ? cf.Pc : _program.PcOfLabel(cf.ResumeLabel);
            if (fn < 0 || pc < 0 || pc >= _program.Code.Length || cf.RegisterBase < 0 || cf.RegisterCount < 0)
            {
                Finish(ref outcome, RunStatus.Failed, RunError.UnsupportedFrame, -1);
                return default;
            }

            stack[i] = new CallRecord { Function = fn, RegisterBase = cf.RegisterBase, RegisterCount = cf.RegisterCount, ReturnRegister = cf.ReturnRegister, Pc = pc };
            used = Math.Max(used, cf.RegisterBase + cf.RegisterCount);
        }

        var awaitPc = stack[^1].Pc;
        var op = Instr.Op(_program.Code[awaitPc]);
        var expected = frame.Pending.Kind switch
        {
            AwaitKind.Minutes => OpCode.WaitMinutes,
            AwaitKind.Decision => OpCode.WaitDecision,
            AwaitKind.Roll => OpCode.WaitRoll,
            _ => OpCode.WaitSignal,
        };
        var size = Math.Max(used, frame.Registers.Length);
        if (op != expected || size > _budget.MaxRegisters)
        {
            Finish(ref outcome, RunStatus.Failed, RunError.UnsupportedFrame, -1);
            return default;
        }

        var regs = new long[size + OperandSlack];
        var symbols = new bool[size + OperandSlack];
        for (var i = 0; i < frame.Registers.Length; i++)
        {
            if (frame.SymbolNames[i] is { } name)
            {
                regs[i] = _bound.Bindings.Symbols.Intern(name);
                symbols[i] = true;
            }
            else
            {
                regs[i] = frame.Registers[i];
            }
        }

        var receipts = new List<Receipt>(frame.AppliedEffects.Count);
        var prefix = frame.InstanceId + "/pc";
        foreach (var text in frame.AppliedEffects)
        {
            // `{instance}/pc{site}#{sequence}`: the only receipts either VM writes; anything else can never match a key.
            var hash = text.LastIndexOf('#');
            if (text.StartsWith(prefix, StringComparison.Ordinal) && hash > prefix.Length
                && int.TryParse(text.AsSpan(prefix.Length, hash - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var site)
                && int.TryParse(text.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            {
                receipts.Add(new Receipt(site, sequence));
            }
        }

        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            index = _frames.Count;
            _frames.Add(new Suspension());
        }

        _suspended++;
        var s = _frames[index];
        s.Generation = s.Generation == int.MaxValue ? 1 : s.Generation + 1;
        s.InUse = true;
        s.Instance = instance;
        s.Hook = hook;
        s.Steps = frame.StepsUsed;
        s.EffectSequence = frame.EffectSequence;
        s.Kind = frame.Pending.Kind;
        s.AwaitPc = awaitPc;
        s.DueAt = frame.Pending.DueAtMinutes;
        s.ResultRegister = frame.Pending.ResultRegister;
        s.Registers = regs;
        s.Symbols = symbols;
        s.RegisterCount = size;
        s.Stack = stack;
        s.Depth = stack.Length;
        s.Receipts = receipts.ToArray();
        s.ReceiptCount = s.Receipts.Length;
        var handle = new FrameHandle(index, s.Generation);
        outcome.Status = RunStatus.Suspended;
        outcome.Error = RunError.None;
        outcome.Frame = handle;
        outcome.Pc = awaitPc;
        outcome.Steps = s.Steps;
        return handle;
    }

    // ----- buffers -----

    private bool EnsureRegisters(int count)
    {
        if (count > _budget.MaxRegisters)
        {
            return false;
        }

        if (_regs.Length < count + OperandSlack)
        {
            var size = Math.Max(count + OperandSlack, _regs.Length * 2);
            Array.Resize(ref _regs, size);
            Array.Resize(ref _sym, size);
        }

        return true;
    }

    private void EnsureStack(int depth)
    {
        if (_stack.Length < depth)
        {
            Array.Resize(ref _stack, Math.Max(depth, _stack.Length * 2));
        }
    }

    private void EnsureReceipts(int count)
    {
        if (_receipts.Length < count)
        {
            Array.Resize(ref _receipts, Math.Max(count, _receipts.Length * 2));
        }
    }

    private string Unbound(int pc)
    {
        if ((uint)pc >= (uint)_program.Code.Length)
        {
            return "unbound name";
        }

        var w = _program.Code[pc];
        var kind = Instr.Op(w) switch
        {
            OpCode.LoadContext => BindingKind.Context,
            OpCode.Query => BindingKind.Query,
            OpCode.Apply => BindingKind.Effect,
            OpCode.WaitRoll => BindingKind.Stream,
            _ => BindingKind.Signal,
        };
        return $"{BindingTable.Describe(kind)} `{_program.Strings[Instr.Imm(w)]}` is not bound by the host (K6001)";
    }

    private struct CallRecord
    {
        public int Function;
        public int RegisterBase;
        public int RegisterCount;
        public int ReturnRegister;
        public int Pc;
    }

    private readonly record struct Receipt(int Site, int Sequence);

    private sealed class Suspension
    {
        public int Generation;
        public bool InUse;
        public long Instance;
        public int Hook;
        public long Steps;
        public int EffectSequence;
        public AwaitKind Kind;
        public int AwaitPc;
        public long DueAt;
        public int ResultRegister;
        public long[] Registers = Array.Empty<long>();
        public bool[] Symbols = Array.Empty<bool>();
        public int RegisterCount;
        public CallRecord[] Stack = Array.Empty<CallRecord>();
        public int Depth;
        public Receipt[] Receipts = Array.Empty<Receipt>();
        public int ReceiptCount;
    }
}
