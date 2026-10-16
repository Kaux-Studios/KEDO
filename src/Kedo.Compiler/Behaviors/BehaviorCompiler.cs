using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;

namespace Kedo.Compiler.Behaviors;

/// <summary>
/// Compiles the hooks and functions of a resolved object (plus the module's free functions) into a
/// <see cref="BehaviorProgram"/>. Register allocation is linear per function; await sites get stable labels
/// <c>hook/ordinal</c>; recursion is rejected (K5002); waits are only allowed in hooks that allow them.
/// </summary>
public sealed class BehaviorCompiler
{
    private readonly SchemaRegistry _schema;
    private readonly DiagnosticBag _d;
    private readonly List<uint> _code = new();
    private readonly List<string> _sourceMap = new();
    private readonly List<long> _constants = new();
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
    private readonly List<FunctionInfo> _functions = new();
    private readonly Dictionary<string, int> _hooks = new(StringComparer.Ordinal);
    private readonly List<AwaitSite> _awaits = new();
    private readonly Dictionary<string, FunctionEntry> _declaredFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _functionIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _callGraph = new(StringComparer.Ordinal);

    // Per-function state.
    private SyntaxTree _tree = null!;
    private string? _file;
    private Dictionary<string, int> _locals = new(StringComparer.Ordinal);
    private int _nextRegister;
    private int _maxRegister;
    private string? _contextName;
    private HookSchema? _hookSchema;
    private string _currentFunction = "";
    private int _awaitOrdinal;
    private bool _inHook;

    private BehaviorCompiler(SchemaRegistry schema, DiagnosticBag diagnostics)
    {
        _schema = schema;
        _d = diagnostics;
    }

    /// <summary>Compiles one object's behaviours. Returns null when the object has no hooks and no functions.</summary>
    public static BehaviorProgram? Compile(ResolvedObject obj, IEnumerable<FunctionEntry> moduleFunctions, SchemaRegistry schema, DiagnosticBag diagnostics)
    {
        var functions = moduleFunctions.Concat(obj.Functions).ToList();
        if (obj.Hooks.Count == 0 && obj.Functions.Count == 0)
        {
            return null;
        }

        var c = new BehaviorCompiler(schema, diagnostics);
        foreach (var f in functions)
        {
            c._declaredFunctions[f.Name] = f;
        }

        // Functions first (so calls can be resolved to indices), then hooks.
        foreach (var f in functions)
        {
            c._functionIndex[f.Name] = c._functions.Count;
            c._functions.Add(new FunctionInfo(f.Name, -1, 0, f.Parameters.Count));
        }

        foreach (var h in obj.Hooks)
        {
            c._hooks[h.HookId] = c._functions.Count;
            c._functions.Add(new FunctionInfo("on " + h.HookId, -1, 0, 0));
        }

        var fi = 0;
        foreach (var f in functions)
        {
            c.CompileFunction(fi++, f);
        }

        foreach (var h in obj.Hooks)
        {
            c.CompileHook(fi++, h);
        }

        c.CheckRecursion();
        var version = obj.Body.Fields.TryGetValue("version", out var v) && v is RInt vi ? (int)vi.Number : 1;
        return new BehaviorProgram(obj.Key, version, obj.Hash, c._code.ToArray(), c._constants.ToArray(), c._strings.ToArray(), c._functions, c._hooks, c._awaits, c._sourceMap);
    }

    // ----- functions and hooks -----

    private void BeginFunction(SyntaxTree tree, string name, bool inHook)
    {
        _tree = tree;
        _file = tree.Source.Path;
        _locals = new Dictionary<string, int>(StringComparer.Ordinal);
        _nextRegister = 0;
        _maxRegister = 0;
        _localsHighWater = 0;
        _currentFunction = name;
        _awaitOrdinal = 0;
        _inHook = inHook;
        _callGraph[name] = new HashSet<string>(StringComparer.Ordinal);
    }

    private void CompileFunction(int index, FunctionEntry f)
    {
        BeginFunction(f.Tree, f.Name, inHook: false);
        _contextName = null;
        _hookSchema = null;
        var entry = _code.Count;
        foreach (var (pname, _) in f.Parameters)
        {
            Bind(pname, Alloc());
        }

        CompileBlock(f.BlockNode);
        // Implicit return of 0 when the body falls through.
        var zero = Alloc();
        Emit(Instr.AImm(OpCode.LoadConst, zero, Const(0)), f.BlockNode);
        Emit(Instr.Abc(OpCode.Return, zero, 0, 0), f.BlockNode);
        _functions[index] = new FunctionInfo(f.Name, entry, _maxRegister, f.Parameters.Count);
    }

    private void CompileHook(int index, HookEntry h)
    {
        BeginFunction(h.Tree, "on " + h.HookId, inHook: true);
        _contextName = h.ContextName;
        _hookSchema = _schema.FindHook(h.HookId);
        var entry = _code.Count;
        CompileBlock(h.BlockNode);
        Emit(Instr.Abc(OpCode.Stop, 0, 0, 0), h.BlockNode);
        _functions[index] = new FunctionInfo("on " + h.HookId, entry, _maxRegister, 0);
    }

    private void CheckRecursion()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fn in _callGraph.Keys)
        {
            Visit(fn, new List<string>());
        }

        void Visit(string fn, List<string> path)
        {
            if (state.TryGetValue(fn, out var s))
            {
                if (s == 1)
                {
                    var cycle = string.Join(" → ", path.Skip(path.IndexOf(fn)).Append(fn));
                    var decl = _declaredFunctions.TryGetValue(fn, out var fe) ? fe.Origin : default;
                    _d.Error("K5002", $"recursive call: {cycle}", decl.File, decl.Span);
                }

                return;
            }

            state[fn] = 1;
            path.Add(fn);
            foreach (var callee in _callGraph.TryGetValue(fn, out var set) ? set : Enumerable.Empty<string>())
            {
                Visit(callee, path);
            }

            path.RemoveAt(path.Count - 1);
            state[fn] = 2;
        }
    }

    // ----- emission helpers -----

    private int Alloc()
    {
        var r = _nextRegister++;
        if (_nextRegister > 255)
        {
            _d.Error("K5006", $"`{_currentFunction}` needs more than 255 registers; split it", _file, default);
            _nextRegister = 255;
        }

        _maxRegister = Math.Max(_maxRegister, _nextRegister);
        return r;
    }

    private int _localsHighWater;

    /// <summary>Frees temporaries down to <paramref name="mark"/> but never below registers held by locals.</summary>
    private void Release(int mark) => _nextRegister = Math.Max(mark, _localsHighWater);

    private void Bind(string name, int register)
    {
        _locals[name] = register;
        _localsHighWater = Math.Max(_localsHighWater, register + 1);
    }

    private int Const(long value)
    {
        var i = _constants.IndexOf(value);
        if (i < 0)
        {
            _constants.Add(value);
            i = _constants.Count - 1;
        }

        return i;
    }

    private int Str(string s)
    {
        if (!_stringIndex.TryGetValue(s, out var i))
        {
            _strings.Add(s);
            i = _strings.Count - 1;
            _stringIndex[s] = i;
        }

        return i;
    }

    private int Emit(uint word, int node)
    {
        _code.Add(word);
        var span = _tree.SpanOf(node);
        var pos = _tree.Source.GetLinePosition(span.Start);
        _sourceMap.Add($"{(_file is null ? "<memory>" : Path.GetFileName(_file))}:{pos.Line}:{pos.Column}");
        return _code.Count - 1;
    }

    private void Patch(int at, int target)
    {
        var w = _code[at];
        var offset = target - (at + 1);
        if (offset is < short.MinValue or > short.MaxValue)
        {
            _d.Error("K5006", $"`{_currentFunction}` is too long for a 16-bit jump; split it", _file, default);
        }

        _code[at] = Instr.AImm(Instr.Op(w), Instr.A(w), offset);
    }

    private void Error(string code, string message, int node) => _d.Error(code, message, _file, _tree.SpanOf(node));

    private string Text(int token) => _tree.TextOf(token);

    private string NodeText(int node) => _tree.TextOfNode(node);

    // ----- statements -----

    private void CompileBlock(int block)
    {
        foreach (var stmt in _tree.Children(block))
        {
            var mark = _nextRegister;
            CompileStatement(stmt);
            Release(mark);
        }
    }

    private void CompileStatement(int node)
    {
        var n = _tree[node];
        var own = _tree.OwnTokens(node);
        var children = _tree.Children(node);
        switch (n.Kind)
        {
            case NodeKind.LetStmt:
            {
                var name = Text(own[1]);
                var reg = Alloc();
                var value = CompileExpression(children[0], reg);
                if (value != reg)
                {
                    Emit(Instr.Abc(OpCode.Move, reg, value, 0), node);
                }

                Bind(name, reg);
                break;
            }

            case NodeKind.SetStmt:
            {
                var target = children[0];
                var targetText = NodeText(target);
                if (_contextName is not null && (targetText == _contextName || targetText.StartsWith(_contextName + ".", StringComparison.Ordinal)))
                {
                    Error("K5001", "the hook context is read-only; use `apply` to change the game", target);
                    return;
                }

                if (!_locals.TryGetValue(targetText, out var reg))
                {
                    Error("K5001", $"`{targetText}` is not a variable declared with `let`", target);
                    return;
                }

                var value = CompileExpression(children[1], reg);
                if (value != reg)
                {
                    Emit(Instr.Abc(OpCode.Move, reg, value, 0), node);
                }

                break;
            }

            case NodeKind.WaitStmt:
            {
                if (!CheckAwaitAllowed(node))
                {
                    return;
                }

                var reg = CompileExpression(children[0], Alloc());
                var label = NextLabel();
                _awaits.Add(new AwaitSite(label, _code.Count));
                Emit(Instr.Abc(OpCode.WaitMinutes, reg, 0, 0), node);
                break;
            }

            case NodeKind.WaitDecisionStmt:
            {
                if (!CheckAwaitAllowed(node))
                {
                    return;
                }

                var prompt = own.Select(t => _tree.Tokens[t]).First(t => t.Kind == TokenKind.Reference);
                var promptText = _tree.Source.GetText(prompt.Span);
                var options = children.ToArray().Where(c => _tree[c].Kind == NodeKind.OptionClause).ToArray();
                var names = options.Select(o => Text(_tree.OwnTokens(o)[1])).ToArray();
                var first = _strings.Count;
                foreach (var name in names)
                {
                    _strings.Add(name);   // consecutive, not deduplicated on purpose
                }

                var reg = Alloc();
                var label = NextLabel();
                _awaits.Add(new AwaitSite(label, _code.Count));
                Emit(Instr.AImm(OpCode.WaitDecision, reg, Str(promptText)), node);
                Emit(Instr.AImm(OpCode.Nop, names.Length, first), node);
                var ends = new List<int>();
                for (var i = 0; i < options.Length; i++)
                {
                    var tmp = Alloc();
                    var idx = Alloc();
                    Emit(Instr.AImm(OpCode.LoadConst, idx, Const(i)), options[i]);
                    Emit(Instr.Abc(OpCode.Eq, tmp, reg, idx), options[i]);
                    var skip = Emit(Instr.AImm(OpCode.JumpIfFalse, tmp, 0), options[i]);
                    var block = _tree.FirstChildOfKind(options[i], NodeKind.Block);
                    CompileBlock(block);
                    ends.Add(Emit(Instr.AImm(OpCode.Jump, 0, 0), options[i]));
                    Patch(skip, _code.Count);
                }

                foreach (var e in ends)
                {
                    Patch(e, _code.Count);
                }

                break;
            }

            case NodeKind.WaitRollStmt:
            {
                if (!CheckAwaitAllowed(node))
                {
                    return;
                }

                var idents = own.Where(t => _tree.Tokens[t].Kind == TokenKind.Identifier).Select(Text).ToArray();
                var stream = idents[0];
                var resultName = idents[^1];
                var argList = _tree.FirstChildOfKind(node, NodeKind.ArgList);
                var (argBase, argCount) = CompileArgs(argList, null);
                var reg = Alloc();
                var label = NextLabel();
                _awaits.Add(new AwaitSite(label, _code.Count));
                Emit(Instr.AImm(OpCode.WaitRoll, reg, Str(stream)), node);
                Emit(Instr.Abc(OpCode.Nop, argBase, argCount, 0), node);
                Bind(resultName, reg);
                break;
            }

            case NodeKind.WaitSignalStmt:
            {
                if (!CheckAwaitAllowed(node))
                {
                    return;
                }

                var signal = own.Select(t => _tree.Tokens[t]).First(t => t.Kind == TokenKind.Reference);
                var reg = Alloc();
                var label = NextLabel();
                _awaits.Add(new AwaitSite(label, _code.Count));
                Emit(Instr.AImm(OpCode.WaitSignal, reg, Str(_tree.Source.GetText(signal.Span))), node);
                var asName = own.Where(t => _tree.Tokens[t].Kind == TokenKind.Identifier).Select(Text).LastOrDefault();
                if (asName is not null)
                {
                    Bind(asName, reg);
                }

                break;
            }

            case NodeKind.ApplyStmt or NodeKind.EmitStmt:
            {
                var isApply = n.Kind == NodeKind.ApplyStmt;
                if (isApply && _hookSchema is { AllowsEffects: false })
                {
                    Error("K5005", $"hook `{_hookSchema.Id}` does not allow effects", node);
                    return;
                }

                var reference = own.Select(t => _tree.Tokens[t]).FirstOrDefault(t => t.Kind == TokenKind.Reference);
                if (reference.Kind != TokenKind.Reference)
                {
                    return;
                }

                var argList = _tree.FirstChildOfKind(node, NodeKind.ArgList);
                var (argBase, argCount) = CompileArgs(argList, null);
                Emit(Instr.AImm(isApply ? OpCode.Apply : OpCode.Emit, 0, Str(_tree.Source.GetText(reference.Span))), node);
                Emit(Instr.Abc(OpCode.Nop, argBase, argCount, 0), node);
                break;
            }

            case NodeKind.IfStmt:
                CompileIf(node);
                break;
            case NodeKind.StopStmt:
                if (!_inHook)
                {
                    Error("K5003", "`stop` is only allowed in hooks; use `return` in functions", node);
                    return;
                }

                Emit(Instr.Abc(OpCode.Stop, 0, 0, 0), node);
                break;
            case NodeKind.ReturnStmt:
            {
                if (_inHook)
                {
                    Error("K5003", "`return` is only allowed in functions; use `stop` in hooks", node);
                    return;
                }

                var reg = CompileExpression(children[0], Alloc());
                Emit(Instr.Abc(OpCode.Return, reg, 0, 0), node);
                break;
            }

            case NodeKind.ExprStmt:
                CompileExpression(children[0], Alloc());
                break;
            case NodeKind.Error:
                break;
            default:
                Error("K5003", $"unsupported statement {n.Kind}", node);
                break;
        }
    }

    private void CompileIf(int node)
    {
        var children = _tree.Children(node);
        var cond = CompileExpression(children[0], Alloc());
        var jumpElse = Emit(Instr.AImm(OpCode.JumpIfFalse, cond, 0), node);
        CompileBlock(children[1]);
        var elseClause = _tree.FirstChildOfKind(node, NodeKind.ElseClause);
        if (elseClause < 0)
        {
            Patch(jumpElse, _code.Count);
            return;
        }

        var jumpEnd = Emit(Instr.AImm(OpCode.Jump, 0, 0), node);
        Patch(jumpElse, _code.Count);
        var inner = _tree.Children(elseClause)[0];
        if (_tree[inner].Kind == NodeKind.IfStmt)
        {
            CompileIf(inner);
        }
        else
        {
            CompileBlock(inner);
        }

        Patch(jumpEnd, _code.Count);
    }

    private bool CheckAwaitAllowed(int node)
    {
        if (!_inHook)
        {
            Error("K5004", "`wait` is only allowed in hooks", node);
            return false;
        }

        if (_hookSchema is { AllowsAwait: false })
        {
            Error("K5004", $"hook `{_hookSchema.Id}` does not allow `wait`", node);
            return false;
        }

        return true;
    }

    private string NextLabel() => $"{_currentFunction.Replace("on ", "", StringComparison.Ordinal)}/{++_awaitOrdinal}";

    // ----- expressions -----

    /// <summary>Compiles an expression; returns the register holding the result (may be <paramref name="target"/> or a variable's register).</summary>
    private int CompileExpression(int node, int target)
    {
        var n = _tree[node];
        var own = _tree.OwnTokens(node);
        var children = _tree.Children(node);
        switch (n.Kind)
        {
            case NodeKind.LiteralExpr:
            {
                var tk = _tree.Tokens[own[0]];
                var text = Text(own[0]);
                switch (tk.Kind)
                {
                    case TokenKind.Decimal:
                        // Decimals are data in 1.x (ADR-0008): the VM works with integers only.
                        _d.Error("K5007", $"decimal `{text}` cannot be used in a hook or function; decimals are data in KEDO 1.x", _file, tk.Span);
                        return target;
                    case TokenKind.Integer:
                        Emit(Instr.AImm(OpCode.LoadConst, target, Const(long.Parse(text.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture))), node);
                        return target;
                    case TokenKind.Duration:
                    {
                        var i = 0;
                        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '_')) i++;
                        var amount = long.Parse(text[..i].Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture);
                        var minutes = text[i..] switch { "s" => amount / 60, "h" => amount * 60, "d" => amount * 1440, _ => amount };
                        Emit(Instr.AImm(OpCode.LoadConst, target, Const(minutes)), node);
                        return target;
                    }

                    case TokenKind.String:
                        Emit(Instr.AImm(OpCode.LoadSymbol, target, Str(text.Length >= 2 ? text[1..^1] : text)), node);
                        return target;
                    case TokenKind.KwTrue:
                        Emit(Instr.AImm(OpCode.LoadConst, target, Const(1)), node);
                        return target;
                    case TokenKind.KwFalse:
                        Emit(Instr.AImm(OpCode.LoadConst, target, Const(0)), node);
                        return target;
                }

                break;
            }

            case NodeKind.RefExpr:
                Emit(Instr.AImm(OpCode.LoadSymbol, target, Str(Text(own[0]))), node);
                return target;
            case NodeKind.NameExpr:
            {
                var name = Text(own[0]);
                if (_locals.TryGetValue(name, out var reg))
                {
                    return reg;
                }

                if (name == _contextName)
                {
                    Error("K5001", "the context itself is not a value; read one of its fields (`ctx.field`)", node);
                    return target;
                }

                Error("K5001", $"`{name}` is not a variable; declare it with `let` or qualify an enum value as `type.value`", node);
                return target;
            }

            case NodeKind.ParenExpr:
                return CompileExpression(children[0], target);
            case NodeKind.UnaryExpr:
            {
                var opKind = _tree.Tokens[own[0]].Kind;
                var operand = CompileExpression(children[0], target);
                Emit(Instr.Abc(opKind == TokenKind.Minus ? OpCode.Neg : OpCode.Not, target, operand, 0), node);
                return target;
            }

            case NodeKind.BinaryExpr:
                return CompileBinary(node, target);
            case NodeKind.MemberExpr:
                return CompileMember(node, target);
            case NodeKind.CallExpr:
                return CompileCall(node, target);
            case NodeKind.ListExpr or NodeKind.Body:
                Error("K5003", "lists and records are not values in hooks; pass ids and let the host look them up", node);
                return target;
            case NodeKind.Error:
                return target;
        }

        Error("K5003", $"unsupported expression {n.Kind}", node);
        return target;
    }

    private int CompileBinary(int node, int target)
    {
        var own = _tree.OwnTokens(node);
        var children = _tree.Children(node);
        var opKind = _tree.Tokens[own[0]].Kind;
        if (opKind is TokenKind.KwAnd or TokenKind.KwOr)
        {
            // Short-circuit: evaluate lhs into target; jump past rhs when the result is already known.
            var lhs = CompileExpression(children[0], target);
            if (lhs != target)
            {
                Emit(Instr.Abc(OpCode.Move, target, lhs, 0), node);
            }

            int jump;
            if (opKind == TokenKind.KwAnd)
            {
                jump = Emit(Instr.AImm(OpCode.JumpIfFalse, target, 0), node);
            }
            else
            {
                var notTmp = Alloc();
                Emit(Instr.Abc(OpCode.Not, notTmp, target, 0), node);
                jump = Emit(Instr.AImm(OpCode.JumpIfFalse, notTmp, 0), node);
            }

            var rhs = CompileExpression(children[1], target);
            if (rhs != target)
            {
                Emit(Instr.Abc(OpCode.Move, target, rhs, 0), node);
            }

            Patch(jump, _code.Count);
            return target;
        }

        var mark = _nextRegister;
        var l = CompileExpression(children[0], Alloc());
        var r = CompileExpression(children[1], Alloc());
        switch (opKind)
        {
            case TokenKind.Plus: Emit(Instr.Abc(OpCode.Add, target, l, r), node); break;
            case TokenKind.Minus: Emit(Instr.Abc(OpCode.Sub, target, l, r), node); break;
            case TokenKind.Star: Emit(Instr.Abc(OpCode.Mul, target, l, r), node); break;
            case TokenKind.Slash: Emit(Instr.Abc(OpCode.Div, target, l, r), node); break;
            case TokenKind.Percent: Emit(Instr.Abc(OpCode.Mod, target, l, r), node); break;
            case TokenKind.EqualsEquals: Emit(Instr.Abc(OpCode.Eq, target, l, r), node); break;
            case TokenKind.BangEquals: Emit(Instr.Abc(OpCode.Ne, target, l, r), node); break;
            case TokenKind.Less: Emit(Instr.Abc(OpCode.Lt, target, l, r), node); break;
            case TokenKind.LessEquals: Emit(Instr.Abc(OpCode.Le, target, l, r), node); break;
            case TokenKind.Greater: Emit(Instr.Abc(OpCode.Lt, target, r, l), node); break;
            case TokenKind.GreaterEquals: Emit(Instr.Abc(OpCode.Le, target, r, l), node); break;
            default: Error("K5003", $"unsupported operator `{Text(own[0])}`", node); break;
        }

        Release(mark);
        return target;
    }

    /// <summary>Dotted path of a member chain rooted at a NameExpr, or null if the root is not a bare name.</summary>
    private List<string>? PathOf(int node)
    {
        var n = _tree[node];
        if (n.Kind == NodeKind.NameExpr)
        {
            return new List<string> { Text(_tree.OwnTokens(node)[0]) };
        }

        if (n.Kind == NodeKind.MemberExpr)
        {
            var inner = PathOf(_tree.Children(node)[0]);
            if (inner is null)
            {
                return null;
            }

            inner.Add(Text(_tree.OwnTokens(node)[^1]));
            return inner;
        }

        return null;
    }

    private int CompileMember(int node, int target)
    {
        var path = PathOf(node);
        var member = Text(_tree.OwnTokens(node)[^1]);
        if (path is not null)
        {
            // ctx.field
            if (_contextName is not null && path[0] == _contextName && path.Count == 2)
            {
                if (_hookSchema is not null && _hookSchema.Context.Find(member) is null && !_schema.Permissive)
                {
                    Error("K5001", $"hook `{_hookSchema.Id}` has no context field `{member}` ({string.Join(", ", _hookSchema.Context.Fields.Select(f => f.Name))})", node);
                }

                Emit(Instr.AImm(OpCode.LoadContext, target, Str(member)), node);
                return target;
            }

            // enum.value (root is not a variable nor the context)
            if (path.Count == 2 && !_locals.ContainsKey(path[0]) && path[0] != _contextName)
            {
                Emit(Instr.AImm(OpCode.LoadSymbol, target, Str(path[0] + "." + path[1])), node);
                return target;
            }
        }

        // receiver.member → host query "member" with the receiver as first argument
        var mark = _nextRegister;
        var receiver = CompileExpression(_tree.Children(node)[0], Alloc());
        var argBase = Alloc();
        Emit(Instr.Abc(OpCode.Move, argBase, receiver, 0), node);
        Emit(Instr.AImm(OpCode.Query, target, Str(member)), node);
        Emit(Instr.Abc(OpCode.Nop, argBase, 1, 0), node);
        Release(mark);
        return target;
    }

    private int CompileCall(int node, int target)
    {
        var children = _tree.Children(node);
        var callee = children[0];
        var argList = _tree.FirstChildOfKind(node, NodeKind.ArgList);
        var calleeKind = _tree[callee].Kind;
        if (calleeKind == NodeKind.NameExpr)
        {
            var name = Text(_tree.OwnTokens(callee)[0]);
            if (_functionIndex.TryGetValue(name, out var fi))
            {
                _callGraph[_currentFunction].Add(name);
                var expected = _declaredFunctions[name].Parameters.Count;
                var (argBase, argCount) = CompileArgs(argList, null);
                if (argCount != expected)
                {
                    Error("K5003", $"`{name}` takes {expected} argument(s), got {argCount}", node);
                }

                Emit(Instr.AImm(OpCode.Call, target, fi), node);
                Emit(Instr.Abc(OpCode.Nop, argBase, argCount, 0), node);
                return target;
            }

            // Host query by name.
            var (qb, qc) = CompileArgs(argList, null);
            Emit(Instr.AImm(OpCode.Query, target, Str(name)), node);
            Emit(Instr.Abc(OpCode.Nop, qb, qc, 0), node);
            return target;
        }

        if (calleeKind == NodeKind.MemberExpr)
        {
            // receiver.method(args) → query "method" with the receiver first.
            var member = Text(_tree.OwnTokens(callee)[^1]);
            var receiverNode = _tree.Children(callee)[0];
            var (argBase, argCount) = CompileArgs(argList, receiverNode);
            Emit(Instr.AImm(OpCode.Query, target, Str(member)), node);
            Emit(Instr.Abc(OpCode.Nop, argBase, argCount, 0), node);
            return target;
        }

        Error("K5003", "only names and members can be called", node);
        return target;
    }

    /// <summary>Compiles arguments into consecutive registers; returns (base, count). An optional receiver goes first.</summary>
    private (int, int) CompileArgs(int argList, int? receiverNode)
    {
        var args = new List<int>();
        if (receiverNode is not null)
        {
            args.Add(receiverNode.Value);
        }

        if (argList >= 0)
        {
            args.AddRange(_tree.Children(argList).ToArray());
        }

        var argBase = _nextRegister;
        var regs = new int[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            regs[i] = Alloc();
        }

        for (var i = 0; i < args.Count; i++)
        {
            var mark = _nextRegister;
            var r = CompileExpression(args[i], regs[i]);
            if (r != regs[i])
            {
                Emit(Instr.Abc(OpCode.Move, regs[i], r, 0), args[i]);
            }

            Release(mark);
        }

        return (argBase, args.Count);
    }
}
