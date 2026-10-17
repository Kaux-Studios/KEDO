using System.Text.Json;
using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Formatting;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;

namespace Kedo.Compiler.Evaluation;

/// <summary>A change to a workspace file: whole content, text appended, or the first occurrence of a text replaced.</summary>
public sealed record EvalStep(string File, string? Content, string? Append, string? Find, string? Replace);

/// <summary>
/// One assertion over the workspace after the agent ran (see docs/eval/README.md for every kind): `clean`, `formatted`,
/// `value`, `present`, `layer`, `exists`, `absent`, `parent`, `mixin`, `keys`, `no_component`, `compiles`, `effect`, `no_effect`.
/// </summary>
public sealed record EvalCheck(string Kind, string? Key, string? Path, string? Expected, string? Variant, string? Hook,
    IReadOnlyDictionary<string, string> Context, IReadOnlyDictionary<string, long> Queries);

/// <summary>An authoring task: what the agent is asked, how its workspace starts, a reference solution and the checks.</summary>
public sealed record EvalTask(string Id, string Prompt, IReadOnlyList<EvalStep> Setup, IReadOnlyList<EvalStep> Solution, IReadOnlyList<EvalCheck> Checks);

/// <summary>Result of verifying one task.</summary>
public sealed record EvalOutcome(string Id, bool Passed, IReadOnlyList<string> Failures);

/// <summary>
/// Agent evaluation (#17, anexo G §7.1): authoring tasks whose result is checked mechanically against the resolved
/// catalog and the VM, so the same suite measures an agent with or without skills and with or without the MCP server.
/// The suite file names a base content directory (content plus a `schema` folder) copied into each workspace.
/// </summary>
public static class EvalSuite
{
    /// <summary>Base directory of the suite (relative to the suite file) and its tasks.</summary>
    public static (string Base, IReadOnlyList<EvalTask> Tasks) Load(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var tasks = new List<EvalTask>();
        foreach (var t in root.GetProperty("tasks").EnumerateArray())
        {
            tasks.Add(new EvalTask(
                t.GetProperty("id").GetString()!,
                t.GetProperty("prompt").GetString()!,
                Steps(t, "setup"),
                Steps(t, "solution"),
                t.GetProperty("checks").EnumerateArray().Select(Check).ToList()));
        }

        return (root.GetProperty("base").GetString()!, tasks);
    }

    /// <summary>Copies the base content into <paramref name="workspace"/> and applies the task's setup.</summary>
    public static void Prepare(EvalTask task, string baseDirectory, string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }

        foreach (var file in Directory.GetFiles(baseDirectory, "*.kedo", SearchOption.AllDirectories))
        {
            var target = Path.Combine(workspace, Path.GetRelativePath(baseDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Apply(task.Setup, workspace);
    }

    /// <summary>Applies the reference solution (to prove a task can pass).</summary>
    public static void ApplySolution(EvalTask task, string workspace) => Apply(task.Solution, workspace);

    /// <summary>Runs the task's checks over the workspace: content is every .kedo outside `schema/`, the schema is `schema/`.</summary>
    public static EvalOutcome Verify(EvalTask task, string workspace)
    {
        var failures = new List<string>();
        var schemaDir = Path.Combine(workspace, "schema");
        var bag = new DiagnosticBag();
        var schemaDecls = Directory.Exists(schemaDir)
            ? Directory.GetFiles(schemaDir, "*.kedo", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => DeclarationBuilder.Build(Parser.Parse(SourceText.FromFile(f), bag), bag)).ToList()
            : new List<FileDeclarations>();
        var schema = schemaDecls.Count > 0 ? SchemaLoader.Load(schemaDecls, bag) : new Kedo.Schema.SchemaRegistry { Permissive = true };
        var trees = new List<SyntaxTree>();
        var decls = new List<FileDeclarations>();
        foreach (var file in Directory.GetFiles(workspace, "*.kedo", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFullPath(file).StartsWith(Path.GetFullPath(schemaDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tree = Parser.Parse(SourceText.FromFile(file), bag);
            trees.Add(tree);
            decls.Add(DeclarationBuilder.Build(tree, bag));
        }

        var catalog = Resolver.Resolve(decls, schema, bag);
        var functions = decls.SelectMany(d => d.Functions).ToList();
        var programs = new Dictionary<string, BehaviorProgram?>(StringComparer.Ordinal);
        foreach (var obj in catalog.Concrete.Where(o => o.Hooks.Count > 0 || o.Functions.Count > 0))
        {
            programs[obj.Key] = BehaviorCompiler.Compile(obj, functions, schema, bag);
        }

        foreach (var check in task.Checks)
        {
            var view = check.Variant is null ? catalog : catalog.Variants.GetValueOrDefault(check.Variant);
            if (view is null)
            {
                failures.Add($"{check.Kind}: no variant `{check.Variant}`");
                continue;
            }

            var problem = Run(check, view, bag, trees, programs, functions, schema);
            if (problem is not null)
            {
                failures.Add(problem);
            }
        }

        return new EvalOutcome(task.Id, failures.Count == 0, failures);
    }

    private static string? Run(EvalCheck c, ResolvedCatalog catalog, DiagnosticBag bag, List<SyntaxTree> trees,
        Dictionary<string, BehaviorProgram?> programs, List<FunctionEntry> functions, Kedo.Schema.SchemaRegistry schema)
    {
        switch (c.Kind)
        {
            case "clean":
            {
                var errors = bag.Items.Where(d => d.IsError).ToList();
                return errors.Count == 0 ? null : $"clean: {errors.Count} error(s), first {errors[0].Code} {errors[0].Message}";
            }

            case "formatted":
            {
                var changed = trees.Where(t => Formatter.Format(t) != System.Text.Encoding.UTF8.GetString(t.Source.Bytes)).Select(t => Path.GetFileName(t.Source.Path)).ToList();
                return changed.Count == 0 ? null : $"formatted: not canonical: {string.Join(", ", changed)}";
            }

            case "exists":
                return catalog.ByKey.ContainsKey(c.Key!) ? null : $"exists: no `{c.Key}`";
            case "absent":
                return catalog.ByKey.ContainsKey(c.Key!) ? $"absent: `{c.Key}` exists" : null;
        }

        if (!catalog.ByKey.TryGetValue(c.Key ?? "", out var obj))
        {
            return $"{c.Kind}: no object `{c.Key}`";
        }

        switch (c.Kind)
        {
            case "parent":
                return obj.Declaration.Parent == c.Expected ? null : $"parent: `{c.Key}` extends `{obj.Declaration.Parent ?? "(nothing)"}`, expected `{c.Expected}`";
            case "mixin":
                return obj.Mixins.Contains(c.Expected!) ? null : $"mixin: `{c.Key}` does not use `{c.Expected}` ({string.Join(", ", obj.Mixins)})";
            case "no_component":
                return obj.Components.TryGetValue(c.Expected!, out _) ? $"no_component: `{c.Key}` still has `{c.Expected}`" : null;
            case "compiles":
                return programs.TryGetValue(obj.Key, out var p) && p is not null && !bag.Items.Any(d => d.IsError && d.Code.StartsWith("K5", StringComparison.Ordinal))
                    ? null : $"compiles: `{c.Key}` has no compiled behaviour or K5 errors";
            case "effect" or "no_effect":
            {
                var program = programs.GetValueOrDefault(obj.Key) ?? BehaviorCompiler.Compile(obj, functions, schema, new DiagnosticBag());
                if (program is null)
                {
                    return $"{c.Kind}: `{c.Key}` has no behaviour";
                }

                var host = new TraceHost();
                foreach (var (name, text) in c.Context)
                {
                    host.Context[name] = long.TryParse(text, out var n) ? HostValue.Int(n) : host.Symbol(text);
                }

                foreach (var (name, answer) in c.Queries)
                {
                    host.Queries[name] = HostValue.Int(answer);
                }

                var trace = host.Run(program, c.Hook!);
                var hit = host.Applied.Any(a => a.Contains(c.Expected!, StringComparison.Ordinal));
                return (c.Kind == "effect") == hit ? null
                    : $"{c.Kind}: `{c.Key}`.{c.Hook} applied [{string.Join("; ", host.Applied)}] ({trace}); expected {(c.Kind == "effect" ? "" : "no ")}`{c.Expected}`";
            }
        }

        var value = Navigate(obj, c.Path ?? "");
        switch (c.Kind)
        {
            case "value":
                return value?.CanonicalText == c.Expected ? null : $"value: `{c.Key}.{c.Path}` is {value?.CanonicalText ?? "(missing)"}, expected {c.Expected}";
            case "present":
                return value is not null ? null : $"present: `{c.Key}.{c.Path}` is missing";
            case "layer":
            {
                if (value is null)
                {
                    return $"layer: `{c.Key}.{c.Path}` is missing";
                }

                var p = value.Provenance;
                var layer = p.Layer switch
                {
                    LayerKind.Default => "default",
                    LayerKind.Mixin => "mixin",
                    LayerKind.Parent => "parent",
                    LayerKind.Variant => "variant",
                    _ => p.LayerName.Length > 0 && p.LayerName != obj.Key ? "parent" : "own",
                };
                return layer == c.Expected ? null : $"layer: `{c.Key}.{c.Path}` comes from {layer}, expected {c.Expected}";
            }

            case "keys":
            {
                var keys = value is RList l ? string.Join(",", l.Items.Select(i => RList.KeyOf(i, l.KeyField) ?? i.CanonicalText)) : null;
                return keys == c.Expected ? null : $"keys: `{c.Key}.{c.Path}` is [{keys ?? "(not a list)"}], expected [{c.Expected}]";
            }

            default:
                return $"unknown check kind `{c.Kind}`";
        }
    }

    /// <summary>`field.sub`, `component.field`, `list[key].field` over a resolved object.</summary>
    public static ResolvedValue? Navigate(ResolvedObject obj, string path)
    {
        ResolvedValue? value = obj.Body;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            string? itemKey = null;
            var bracket = segment.IndexOf('[');
            if (bracket >= 0 && segment.EndsWith(']'))
            {
                itemKey = segment[(bracket + 1)..^1];
                segment = segment[..bracket];
            }

            if (i == 0 && obj.Components.TryGetValue(segment, out var component) && !(obj.Body.Fields.TryGetValue(segment, out _)))
            {
                value = component;
            }
            else if (value is RRecord r && r.Fields.TryGetValue(segment, out var next))
            {
                value = next;
            }
            else
            {
                return null;
            }

            if (itemKey is not null)
            {
                value = value is RList list ? list.Items.FirstOrDefault(item => RList.KeyOf(item, list.KeyField) == itemKey) : null;
                if (value is null)
                {
                    return null;
                }
            }
        }

        return value;
    }

    private static void Apply(IReadOnlyList<EvalStep> steps, string workspace)
    {
        foreach (var step in steps)
        {
            var path = Path.Combine(workspace, step.File);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            if (step.Content is not null)
            {
                text = step.Content;
            }

            if (step.Find is not null)
            {
                var at = text.IndexOf(step.Find, StringComparison.Ordinal);
                if (at < 0)
                {
                    throw new InvalidDataException($"step on {step.File}: `{step.Find}` not found");
                }

                text = text[..at] + (step.Replace ?? "") + text[(at + step.Find.Length)..];
            }

            if (step.Append is not null)
            {
                text = text.TrimEnd('\n') + "\n\n" + step.Append.TrimEnd('\n') + "\n";
            }

            File.WriteAllText(path, text);
        }
    }

    private static List<EvalStep> Steps(JsonElement task, string name)
        => task.TryGetProperty(name, out var steps)
            ? steps.EnumerateArray().Select(s => new EvalStep(s.GetProperty("file").GetString()!, Text(s, "content"), Text(s, "append"), Text(s, "find"), Text(s, "replace"))).ToList()
            : new List<EvalStep>();

    private static EvalCheck Check(JsonElement c)
    {
        var context = new Dictionary<string, string>(StringComparer.Ordinal);
        if (c.TryGetProperty("context", out var ctx))
        {
            foreach (var p in ctx.EnumerateObject())
            {
                context[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : p.Value.GetString()!;
            }
        }

        var queries = new Dictionary<string, long>(StringComparer.Ordinal);
        if (c.TryGetProperty("queries", out var q))
        {
            foreach (var p in q.EnumerateObject())
            {
                queries[p.Name] = p.Value.GetInt64();
            }
        }

        return new EvalCheck(c.GetProperty("kind").GetString()!, Text(c, "key"), Text(c, "path"), Text(c, "expected"), Text(c, "variant"), Text(c, "hook"), context, queries);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
}

/// <summary>
/// A scripted host for tools and tests (#17): context values and query answers come from tables, effects and signals
/// are recorded as text, rolls return 10, and waits resume at once, so a hook runs to its end.
/// </summary>
public sealed class TraceHost : IBehaviorHost
{
    /// <inheritdoc />
    public SymbolTable Symbols { get; } = new();

    /// <inheritdoc />
    public long NowMinutes { get; set; } = 0;

    /// <summary>Context fields of the activation.</summary>
    public Dictionary<string, HostValue> Context { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers to queries by query id (missing ones answer 0).</summary>
    public Dictionary<string, HostValue> Queries { get; } = new(StringComparer.Ordinal);

    /// <summary>Applied effects as `effect(args)`.</summary>
    public List<string> Applied { get; } = new();

    /// <summary>Raised signals as `signal(args)`.</summary>
    public List<string> Raised { get; } = new();

    /// <summary>Trace lines of the VM and of this host.</summary>
    public List<string> Lines { get; } = new();

    /// <summary>A symbol value (`room:reactor`, `mood.calm`).</summary>
    public HostValue Symbol(string name) => HostValue.Symbol(Symbols.Intern(name));

    HostValue IBehaviorHost.Context(string field) => Context.TryGetValue(field, out var v) ? v : HostValue.None;

    /// <inheritdoc />
    public HostValue Query(string queryId, ReadOnlySpan<HostValue> args) => Queries.TryGetValue(queryId, out var v) ? v : HostValue.Int(0);

    /// <inheritdoc />
    public bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args)
    {
        Applied.Add($"{effectId}({Render(args)})");
        return true;
    }

    /// <inheritdoc />
    public long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args) => 10;

    /// <inheritdoc />
    public void Raise(string signalId, ReadOnlySpan<HostValue> args) => Raised.Add($"{signalId}({Render(args)})");

    /// <inheritdoc />
    public void Trace(string message) => Lines.Add(message);

    /// <summary>Activates a hook and resumes every wait (first option for decisions) until it ends; returns the final status.</summary>
    public string Run(BehaviorProgram program, string hook, int maxResumes = 16)
    {
        var vm = new BehaviorVm(program, this);
        var result = vm.Activate(hook, "trace");
        for (var i = 0; i < maxResumes && result.Status == RunStatus.Suspended && result.Frame?.Pending is { } pending; i++)
        {
            Lines.AddRange(result.Trace);
            Lines.Add($"suspended: {pending.Kind} {pending.Label}");
            NowMinutes = Math.Max(NowMinutes, pending.DueAtMinutes);
            result = new BehaviorVm(program, this).Resume(result.Frame, new Resumption(pending.Kind, 0));
        }

        Lines.AddRange(result.Trace);
        return result.Error is null ? result.Status.ToString() : $"{result.Status}: {result.Error}";
    }

    private string Render(ReadOnlySpan<HostValue> args)
    {
        var parts = new List<string>(args.Length);
        foreach (var a in args)
        {
            parts.Add(a.IsSymbol ? Symbols.Name(a.Number) ?? "?" : a.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(",", parts);
    }
}
