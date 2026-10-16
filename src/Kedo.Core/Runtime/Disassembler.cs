using System.Text;

namespace Kedo.Runtime;

/// <summary>Readable listing of a program, for `kedo compile`, traces and golden tests.</summary>
public static class Disassembler
{
    /// <summary>Lists functions, hooks, await sites and every instruction.</summary>
    public static string Dump(BehaviorProgram p)
    {
        var sb = new StringBuilder();
        sb.Append("program ").Append(p.BehaviorId).Append(" v").Append(p.DefinitionVersion).Append('\n');
        foreach (var hook in p.Hooks.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            sb.Append("  hook ").Append(hook.Key).Append(" -> ").Append(p.Functions[hook.Value].Name).Append('\n');
        }

        foreach (var site in p.AwaitSites)
        {
            sb.Append("  await ").Append(site.Label).Append(" @").Append(site.Pc).Append('\n');
        }

        for (var f = 0; f < p.Functions.Count; f++)
        {
            var fn = p.Functions[f];
            var end = f + 1 < p.Functions.Count ? p.Functions[f + 1].Entry : p.Code.Length;
            sb.Append("  function ").Append(fn.Name).Append(" params=").Append(fn.ParameterCount).Append(" regs=").Append(fn.RegisterCount).Append('\n');
            for (var pc = fn.Entry; pc < end; pc++)
            {
                var w = p.Code[pc];
                var op = Instr.Op(w);
                sb.Append("    ").Append(pc.ToString().PadLeft(4)).Append("  ").Append(op.ToString().PadRight(12));
                switch (op)
                {
                    case OpCode.LoadConst:
                        sb.Append($"r{Instr.A(w)} = {p.Constants[Instr.Imm(w)]}");
                        break;
                    case OpCode.LoadSymbol or OpCode.LoadContext or OpCode.WaitSignal or OpCode.Trace:
                        sb.Append($"r{Instr.A(w)}, \"{p.Strings[Instr.Imm(w)]}\"");
                        break;
                    case OpCode.Move or OpCode.Neg or OpCode.Not:
                        sb.Append($"r{Instr.A(w)} = r{Instr.B(w)}");
                        break;
                    case OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div or OpCode.Mod or OpCode.Eq or OpCode.Ne or OpCode.Lt or OpCode.Le or OpCode.And or OpCode.Or:
                        sb.Append($"r{Instr.A(w)} = r{Instr.B(w)}, r{Instr.C(w)}");
                        break;
                    case OpCode.Jump:
                        sb.Append($"-> {pc + 1 + Instr.Imm(w)}");
                        break;
                    case OpCode.JumpIfFalse:
                        sb.Append($"r{Instr.A(w)} -> {pc + 1 + Instr.Imm(w)}");
                        break;
                    case OpCode.Call:
                    {
                        var arg = p.Code[pc + 1];
                        sb.Append($"r{Instr.A(w)} = {p.Functions[Instr.Imm(w)].Name}(r{Instr.A(arg)}.. x{Instr.B(arg)})");
                        pc++;
                        break;
                    }

                    case OpCode.Query or OpCode.WaitRoll:
                    {
                        var arg = p.Code[pc + 1];
                        sb.Append($"r{Instr.A(w)} = \"{p.Strings[Instr.Imm(w)]}\"(r{Instr.A(arg)}.. x{Instr.B(arg)})");
                        pc++;
                        break;
                    }

                    case OpCode.Apply or OpCode.Emit:
                    {
                        var arg = p.Code[pc + 1];
                        sb.Append($"\"{p.Strings[Instr.Imm(w)]}\"(r{Instr.A(arg)}.. x{Instr.B(arg)})");
                        pc++;
                        break;
                    }

                    case OpCode.WaitDecision:
                    {
                        var opt = p.Code[pc + 1];
                        var names = Enumerable.Range(Instr.Imm(opt), Instr.A(opt)).Select(i => p.Strings[i]);
                        sb.Append($"r{Instr.A(w)} = \"{p.Strings[Instr.Imm(w)]}\" [{string.Join(", ", names)}]");
                        pc++;
                        break;
                    }

                    case OpCode.WaitMinutes or OpCode.Return:
                        sb.Append($"r{Instr.A(w)}");
                        break;
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
