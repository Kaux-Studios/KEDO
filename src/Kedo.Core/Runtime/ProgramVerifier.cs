namespace Kedo.Runtime;

/// <summary>
/// Structural checks of a program before it runs (opcodes, jump targets, constant, string and function indices, two-word
/// instructions). Shared by <see cref="BehaviorVm"/>, which throws on a failure as in 1.0, and by <see cref="BehaviorBinder"/>,
/// which reports it as a diagnostic (ADR-0015).
/// </summary>
internal static class ProgramVerifier
{
    /// <summary>Returns null when the program is well formed, or the problem and its pc.</summary>
    public static string? Check(BehaviorProgram program, out int at)
    {
        var code = program.Code;
        for (var pc = 0; pc < code.Length; pc++)
        {
            at = pc;
            var w = code[pc];
            var op = Instr.Op(w);
            if (op > OpCode.Trace)
            {
                return $"invalid opcode {(int)op} at {pc}";
            }

            switch (op)
            {
                case OpCode.Jump or OpCode.JumpIfFalse:
                {
                    var target = pc + 1 + Instr.Imm(w);
                    if (target < 0 || target > code.Length)
                    {
                        return $"jump out of range at {pc}";
                    }

                    break;
                }

                case OpCode.LoadConst when Instr.Imm(w) < 0 || Instr.Imm(w) >= program.Constants.Length:
                    return $"constant index out of range at {pc}";
                case OpCode.LoadSymbol or OpCode.LoadContext or OpCode.Query or OpCode.Apply or OpCode.Emit or OpCode.WaitDecision or OpCode.WaitRoll or OpCode.WaitSignal or OpCode.Trace:
                    if (Instr.Imm(w) < 0 || Instr.Imm(w) >= program.Strings.Length)
                    {
                        return $"string index out of range at {pc}";
                    }

                    if (op is OpCode.Query or OpCode.Apply or OpCode.Emit or OpCode.WaitDecision or OpCode.WaitRoll)
                    {
                        pc++;   // argument word
                        if (pc >= code.Length)
                        {
                            return "truncated two-word instruction";
                        }
                    }

                    break;
                case OpCode.Call:
                    if (Instr.Imm(w) < 0 || Instr.Imm(w) >= program.Functions.Count)
                    {
                        return $"function index out of range at {pc}";
                    }

                    pc++;
                    break;
            }
        }

        at = -1;
        return null;
    }
}
