using System.Text.Json;

namespace Kedo.Runtime;

/// <summary>
/// Register VM (ADR-0007 D6): single dense switch dispatch, 64-bit integer registers over one array, no allocations per
/// instruction (the program counter lives in a local and is written back to the call stack only on calls, returns and
/// suspensions), step budget in the loop, suspension by returning a status. Frames serialise to JSON and resume by label.
/// </summary>
public sealed class BehaviorVm
{
    private readonly BehaviorProgram _program;
    private readonly IBehaviorHost _host;
    private readonly Budget _budget;
    private readonly List<string> _trace = new();

    // Execution state (one activation at a time).
    private long[] _regs = Array.Empty<long>();
    private bool[] _isSymbol = Array.Empty<bool>();
    private readonly List<CallFrame> _stack = new();
    private readonly List<string> _applied = new();
    private long _steps;
    private int _effectSequence;
    private string _instanceId = "";
    private string _hookId = "";

    /// <summary>Creates a VM for a program against a host.</summary>
    public BehaviorVm(BehaviorProgram program, IBehaviorHost host, Budget? budget = null)
    {
        _program = program;
        _host = host;
        _budget = budget ?? Budget.Default;
        Verify();
    }

    /// <summary>Starts a hook. Returns Completed, Suspended (with frame) or Failed.</summary>
    public RunResult Activate(string hookId, string instanceId)
    {
        if (!_program.Hooks.TryGetValue(hookId, out var fn))
        {
            return new RunResult(RunStatus.Failed, null, $"program `{_program.BehaviorId}` has no hook `{hookId}`", Array.Empty<string>());
        }

        Reset(instanceId, hookId);
        var info = _program.Functions[fn];
        EnsureRegisters(info.RegisterCount);
        _stack.Add(new CallFrame(fn, info.Name, 0, info.RegisterCount, -1, null, info.Entry));
        return Run();
    }

    /// <summary>Resumes a suspended frame with the awaited input.</summary>
    public RunResult Resume(BehaviorFrame frame, Resumption input, StalePolicy stalePolicy = StalePolicy.AbortSilently)
    {
        if (frame.FormatVersion != BehaviorFrame.CurrentFormat)
        {
            return new RunResult(RunStatus.Failed, null, $"frame format {frame.FormatVersion} is not supported", Array.Empty<string>());
        }

        if (frame.Pending is null)
        {
            return new RunResult(RunStatus.Failed, null, "frame is not suspended", Array.Empty<string>());
        }

        if (frame.Pending.Kind != input.Kind)
        {
            return new RunResult(RunStatus.Failed, null, $"frame waits for {frame.Pending.Kind} but got {input.Kind}", Array.Empty<string>());
        }

        var stale = frame.DefinitionHash != _program.DefinitionHash || frame.DefinitionVersion != _program.DefinitionVersion;
        Reset(frame.InstanceId, frame.HookId);
        _steps = frame.StepsUsed;
        _effectSequence = frame.EffectSequence;
        _applied.AddRange(frame.AppliedEffects);

        if (stale)
        {
            switch (stalePolicy)
            {
                case StalePolicy.RestartAtEntry:
                    _trace.Add($"stale frame: restarting hook `{frame.HookId}` at entry");
                    return Activate(frame.HookId, frame.InstanceId);
                case StalePolicy.AbortWithCompensation:
                    return new RunResult(RunStatus.Failed, null, "stale frame: definition changed; compensation requested", _trace);
                default:
                    return new RunResult(RunStatus.Failed, null, "stale frame: definition changed; aborted", _trace);
            }
        }

        // Restore registers, re-interning symbols by name.
        EnsureRegisters(frame.Registers.Length);
        for (var i = 0; i < frame.Registers.Length; i++)
        {
            if (frame.SymbolNames[i] is { } name)
            {
                _regs[i] = _host.Symbols.Intern(name);
                _isSymbol[i] = true;
            }
            else
            {
                _regs[i] = frame.Registers[i];
                _isSymbol[i] = false;
            }
        }

        foreach (var cf in frame.Stack)
        {
            var fnIndex = _program.FunctionIndex(cf.FunctionName);
            if (fnIndex < 0)
            {
                return new RunResult(RunStatus.Failed, null, $"function `{cf.FunctionName}` no longer exists", _trace);
            }

            var pc = cf.ResumeLabel is null ? cf.Pc : _program.PcOfLabel(cf.ResumeLabel);
            if (pc < 0)
            {
                return new RunResult(RunStatus.Failed, null, $"await site `{cf.ResumeLabel}` no longer exists", _trace);
            }

            _stack.Add(cf with { FunctionIndex = fnIndex, Pc = pc });
        }

        // Deliver the awaited value and continue after the await instruction.
        var top = _stack[^1];
        var result = frame.Pending.ResultRegister;
        if (result >= 0)
        {
            var abs = top.RegisterBase + result;
            _regs[abs] = input.Value;
            _isSymbol[abs] = input.Kind == AwaitKind.Signal;
        }

        var resumePc = top.Pc + (Instr.Op(_program.Code[top.Pc]) is OpCode.WaitDecision or OpCode.WaitRoll ? 2 : 1);
        _stack[^1] = top with { Pc = resumePc, ResumeLabel = null };
        return Run();
    }

    /// <summary>Serialises a frame to JSON (source-generated).</summary>
    public static string Save(BehaviorFrame frame) => JsonSerializer.Serialize(frame, BehaviorJsonContext.Default.BehaviorFrame);

    /// <summary>Deserialises a frame from JSON.</summary>
    public static BehaviorFrame? Load(string json) => JsonSerializer.Deserialize(json, BehaviorJsonContext.Default.BehaviorFrame);

    // ----- internals -----

    private void Reset(string instanceId, string hookId)
    {
        _instanceId = instanceId;
        _hookId = hookId;
        _stack.Clear();
        _applied.Clear();
        _trace.Clear();
        _steps = 0;
        _effectSequence = 0;
    }

    private void EnsureRegisters(int count)
    {
        if (count > _budget.MaxRegisters)
        {
            throw new InvalidOperationException($"program needs {count} registers; the budget allows {_budget.MaxRegisters}");
        }

        if (_regs.Length < count)
        {
            Array.Resize(ref _regs, Math.Max(count, 64));
            Array.Resize(ref _isSymbol, _regs.Length);
        }
    }

    private void Verify()
    {
        var code = _program.Code;
        for (var pc = 0; pc < code.Length; pc++)
        {
            var w = code[pc];
            var op = Instr.Op(w);
            if (op > OpCode.Trace)
            {
                throw new InvalidOperationException($"invalid opcode {(int)op} at {pc}");
            }

            switch (op)
            {
                case OpCode.Jump or OpCode.JumpIfFalse:
                {
                    var target = pc + 1 + Instr.Imm(w);
                    if (target < 0 || target > code.Length)
                    {
                        throw new InvalidOperationException($"jump out of range at {pc}");
                    }

                    break;
                }

                case OpCode.LoadConst when Instr.Imm(w) < 0 || Instr.Imm(w) >= _program.Constants.Length:
                    throw new InvalidOperationException($"constant index out of range at {pc}");
                case OpCode.LoadSymbol or OpCode.LoadContext or OpCode.Query or OpCode.Apply or OpCode.Emit or OpCode.WaitDecision or OpCode.WaitRoll or OpCode.WaitSignal or OpCode.Trace:
                    if (Instr.Imm(w) < 0 || Instr.Imm(w) >= _program.Strings.Length)
                    {
                        throw new InvalidOperationException($"string index out of range at {pc}");
                    }

                    if (op is OpCode.Query or OpCode.Apply or OpCode.Emit or OpCode.WaitDecision or OpCode.WaitRoll)
                    {
                        pc++;   // argument word
                        if (pc >= code.Length)
                        {
                            throw new InvalidOperationException("truncated two-word instruction");
                        }
                    }

                    break;
                case OpCode.Call:
                    if (Instr.Imm(w) < 0 || Instr.Imm(w) >= _program.Functions.Count)
                    {
                        throw new InvalidOperationException($"function index out of range at {pc}");
                    }

                    pc++;
                    break;
            }
        }
    }

    private RunResult Fail(string message, int pc)
    {
        var where = pc >= 0 && pc < _program.SourceMap.Count ? _program.SourceMap[pc] : "?";
        _trace.Add($"fail at {where}: {message}");
        return new RunResult(RunStatus.Failed, null, $"{message} ({where})", _trace.ToArray());
    }

    private BehaviorFrame Snapshot(AwaitRequest pending)
    {
        var used = 0;
        foreach (var f in _stack)
        {
            used = Math.Max(used, f.RegisterBase + f.RegisterCount);
        }

        var regs = new long[used];
        var names = new string?[used];
        for (var i = 0; i < used; i++)
        {
            regs[i] = _regs[i];
            names[i] = _isSymbol[i] ? _host.Symbols.Name(_regs[i]) : null;
        }

        var stack = _stack.Select((f, i) => i == _stack.Count - 1 ? f with { ResumeLabel = pending.Label } : f).ToList();
        return new BehaviorFrame(BehaviorFrame.CurrentFormat, _instanceId, _program.BehaviorId, _program.DefinitionVersion, _program.DefinitionHash,
            _hookId, stack, regs, names, pending, _applied.ToArray(), _steps, _effectSequence);
    }

    private EffectKey NextKey(int pc)
    {
        var label = _program.LabelAt(pc) ?? $"pc{pc}";
        return new EffectKey(_instanceId, label, _effectSequence++);
    }

    private HostValue[] Args(int baseReg, int count)
    {
        var args = new HostValue[count];
        for (var i = 0; i < count; i++)
        {
            args[i] = new HostValue(_regs[baseReg + i], _isSymbol[baseReg + i]);
        }

        return args;
    }

    private RunResult Suspend(CallFrame frame, int pc, AwaitRequest pending)
    {
        _stack[^1] = frame with { Pc = pc };
        return new RunResult(RunStatus.Suspended, Snapshot(pending), null, _trace.ToArray());
    }

    private RunResult Run()
    {
        var code = _program.Code;
        var regs = _regs;
        var sym = _isSymbol;
        var frame = _stack[^1];
        var pc = frame.Pc;
        var rb = frame.RegisterBase;

        while (true)
        {
            if (pc < 0 || pc >= code.Length)
            {
                return Fail("fell off the end of the code", pc);
            }

            if (++_steps > _budget.MaxSteps)
            {
                return Fail($"step budget of {_budget.MaxSteps} exhausted", pc);
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
                    regs[a] = _program.Constants[Instr.Imm(w)];
                    sym[a] = false;
                    break;
                case OpCode.LoadSymbol:
                    regs[a] = _host.Symbols.Intern(_program.Strings[Instr.Imm(w)]);
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
                        return Fail("division by zero", pc);
                    }

                    regs[a] = regs[b] / regs[c];
                    sym[a] = false;
                    break;
                case OpCode.Mod:
                    if (regs[c] == 0)
                    {
                        return Fail("division by zero", pc);
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
                    var v = _host.Context(_program.Strings[Instr.Imm(w)]);
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
                    if (_stack.Count >= _budget.MaxCallDepth)
                    {
                        return Fail($"call depth of {_budget.MaxCallDepth} exceeded", pc);
                    }

                    var newBase = rb + frame.RegisterCount;
                    EnsureRegisters(newBase + info.RegisterCount);
                    regs = _regs;
                    sym = _isSymbol;
                    for (var i = 0; i < argCount && i < info.ParameterCount; i++)
                    {
                        regs[newBase + i] = regs[argBase + i];
                        sym[newBase + i] = sym[argBase + i];
                    }

                    _stack[^1] = frame with { Pc = pc + 2 };
                    frame = new CallFrame(fnIndex, info.Name, newBase, info.RegisterCount, a, null, info.Entry);
                    _stack.Add(frame);
                    pc = info.Entry;
                    rb = newBase;
                    continue;
                }

                case OpCode.Return:
                {
                    var value = regs[a];
                    var isSym = sym[a];
                    _stack.RemoveAt(_stack.Count - 1);
                    if (_stack.Count == 0)
                    {
                        return new RunResult(RunStatus.Completed, null, null, _trace.ToArray());
                    }

                    if (frame.ReturnRegister >= 0)
                    {
                        regs[frame.ReturnRegister] = value;
                        sym[frame.ReturnRegister] = isSym;
                    }

                    frame = _stack[^1];
                    pc = frame.Pc;      // already points after the call
                    rb = frame.RegisterBase;
                    continue;
                }

                case OpCode.Query:
                {
                    var argWord = code[pc + 1];
                    var args = Args(rb + Instr.A(argWord), Instr.B(argWord));
                    var v = _host.Query(_program.Strings[Instr.Imm(w)], args);
                    regs[a] = v.Number;
                    sym[a] = v.IsSymbol;
                    next = pc + 2;
                    break;
                }

                case OpCode.Apply:
                {
                    var argWord = code[pc + 1];
                    var args = Args(rb + Instr.A(argWord), Instr.B(argWord));
                    var key = NextKey(pc);
                    var keyText = key.ToString();
                    if (!_applied.Contains(keyText))
                    {
                        _host.Apply(key, _program.Strings[Instr.Imm(w)], args);
                        _applied.Add(keyText);
                    }

                    next = pc + 2;
                    break;
                }

                case OpCode.Emit:
                {
                    var argWord = code[pc + 1];
                    var args = Args(rb + Instr.A(argWord), Instr.B(argWord));
                    _host.Raise(_program.Strings[Instr.Imm(w)], args);
                    next = pc + 2;
                    break;
                }

                case OpCode.WaitMinutes:
                {
                    var label = _program.LabelAt(pc) ?? $"pc{pc}";
                    return Suspend(frame, pc, new AwaitRequest(AwaitKind.Minutes, label, _host.NowMinutes + regs[a], null, Array.Empty<string>(), -1));
                }

                case OpCode.WaitDecision:
                {
                    var label = _program.LabelAt(pc) ?? $"pc{pc}";
                    var optWord = code[pc + 1];   // Nop word: a = option count, imm = index of the first option string
                    var first = Instr.Imm(optWord);
                    var count = Instr.A(optWord);
                    var options = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        options[i] = _program.Strings[first + i];
                    }

                    return Suspend(frame, pc, new AwaitRequest(AwaitKind.Decision, label, 0, _program.Strings[Instr.Imm(w)], options, Instr.A(w)));
                }

                case OpCode.WaitRoll:
                {
                    var label = _program.LabelAt(pc) ?? $"pc{pc}";
                    return Suspend(frame, pc, new AwaitRequest(AwaitKind.Roll, label, 0, _program.Strings[Instr.Imm(w)], Array.Empty<string>(), Instr.A(w)));
                }

                case OpCode.WaitSignal:
                {
                    var label = _program.LabelAt(pc) ?? $"pc{pc}";
                    return Suspend(frame, pc, new AwaitRequest(AwaitKind.Signal, label, 0, _program.Strings[Instr.Imm(w)], Array.Empty<string>(), Instr.A(w)));
                }

                case OpCode.Stop:
                    return new RunResult(RunStatus.Completed, null, null, _trace.ToArray());
                case OpCode.Trace:
                    _trace.Add(_program.Strings[Instr.Imm(w)]);
                    _host.Trace(_program.Strings[Instr.Imm(w)]);
                    break;
                default:
                    return Fail($"unknown opcode {Instr.Op(w)}", pc);
            }

            pc = next;
        }
    }
}
