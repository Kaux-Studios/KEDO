using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Kedo.Compiler.Behaviors;
using Kedo.Compiler.Formatting;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Runtime;
using Kedo.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Kedo.Cli;

/// <summary>`kedo mcp`: stateless stdio MCP server over the same core as the CLI (anexo G §7). Logs go to stderr; stdout is JSON-RPC only.</summary>
internal static class McpServer
{
    public const string Instructions =
        "KEDO content tools: validate (check), format, resolve inherited objects, explain provenance, list the catalog, compile hooks to IR, export the JSON Schema. " +
        "Use kedo_check after writing or editing .kedo files and fix every error it reports; use kedo_resolve to see the effective object after `extends`/`with`; " +
        "use kedo_explain for 'where does this value come from'; use kedo_catalog to learn valid types, fields, enums and existing object ids before inventing names. " +
        "Inputs are workspace paths (files or directories) plus optional schema_paths (a KEDO-written schema: kedo_type, kedo_enum, ...). Nothing is cached between calls.";

    public static async Task<int> RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "kedo", Version = Kedo.KedoVersion.Current };
                o.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<KedoTools>();
        await builder.Build().RunAsync();
        return 0;
    }
}

/// <summary>Loaded workspace: sources, declarations, schema and resolved catalog. Built per call (stateless server).</summary>
internal sealed class Workspace
{
    public DiagnosticBag Bag { get; } = new();
    public List<SyntaxTree> Trees { get; } = new();
    public List<FileDeclarations> Declarations { get; } = new();
    public SchemaRegistry Schema { get; private set; } = new() { Permissive = true };
    public ResolvedCatalog Catalog { get; private set; } = null!;

    public static Workspace Load(string[] paths, string[]? schemaPaths, IReadOnlyList<(string path, string text)>? overrides = null)
    {
        var ws = new Workspace();
        var schemaDirs = (schemaPaths ?? Array.Empty<string>()).Select(Path.GetFullPath).ToArray();
        if (schemaDirs.Length > 0)
        {
            var schemaDecls = new List<FileDeclarations>();
            foreach (var file in Expand(schemaPaths!, Array.Empty<string>()))
            {
                var tree = Parser.Parse(SourceText.FromFile(file), ws.Bag);
                ws.Trees.Add(tree);
                schemaDecls.Add(DeclarationBuilder.Build(tree, ws.Bag));
            }

            ws.Schema = SchemaLoader.Load(schemaDecls, ws.Bag);
        }

        var overridden = new HashSet<string>((overrides ?? Array.Empty<(string, string)>()).Select(o => Path.GetFullPath(o.path)), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Expand(paths, schemaDirs))
        {
            if (overridden.Contains(Path.GetFullPath(file)))
            {
                continue;
            }

            var tree = Parser.Parse(SourceText.FromFile(file), ws.Bag);
            ws.Trees.Add(tree);
            ws.Declarations.Add(DeclarationBuilder.Build(tree, ws.Bag));
        }

        foreach (var (path, text) in overrides ?? Array.Empty<(string, string)>())
        {
            var tree = Parser.Parse(SourceText.FromString(text, path), ws.Bag);
            ws.Trees.Add(tree);
            ws.Declarations.Add(DeclarationBuilder.Build(tree, ws.Bag));
        }

        ws.Catalog = Resolver.Resolve(ws.Declarations, ws.Schema, ws.Bag);
        return ws;
    }

    public static IEnumerable<string> Expand(IEnumerable<string> paths, string[] excludeDirs)
    {
        bool Excluded(string f) => excludeDirs.Any(d => Path.GetFullPath(f).StartsWith(d, StringComparison.OrdinalIgnoreCase));
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*.kedo", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (!Excluded(f))
                    {
                        yield return f;
                    }
                }
            }
            else if (File.Exists(p) && !Excluded(p))
            {
                yield return p;
            }
        }
    }

    public List<DiagnosticDto> Diagnostics(int max)
        => Bag.Items.Take(max).Select(d =>
        {
            var tree = Trees.FirstOrDefault(t => t.Source.Path == d.File);
            var pos = tree?.Source.GetLinePosition(d.Span.Start);
            return new DiagnosticDto(d.Code, d.Severity.ToString().ToLowerInvariant(), d.Message, d.File, pos?.Line ?? 0, pos?.Column ?? 0,
                d.Labels.Select(l => l.Message).ToArray());
        }).ToList();
}

/// <summary>Diagnostic as returned to agents.</summary>
internal sealed record DiagnosticDto(string Code, string Severity, string Message, string? File, int Line, int Column, string[] Related);

internal sealed record CheckResult(bool Ok, string Summary, int Objects, string CatalogHash, List<DiagnosticDto> Diagnostics, bool Truncated);

internal sealed record FormatResult(bool Changed, string FormattedText, List<DiagnosticDto> Diagnostics);

internal sealed record ResolveResult(string Key, bool Found, bool IsAbstract, string Hash, string[] Chain, string[] Mixins, string Resolved, List<DiagnosticDto> Diagnostics);

internal sealed record ExplainResult(string Key, string Path, string Explanation);

internal sealed record CatalogItem(string Kind, string Name, string Summary);

internal sealed record CatalogResult(string Kind, List<CatalogItem> Items, bool Truncated);

internal sealed record CompileResult(List<string> Listings, List<DiagnosticDto> Diagnostics);

/// <summary>MCP tools. Names are stable (never renamed; new shapes get `_v2`). Every tool is a pure function of its inputs.</summary>
[McpServerToolType]
internal sealed class KedoTools
{
    [McpServerTool(Name = "kedo_check", ReadOnly = true, Idempotent = true), Description("Validate KEDO content: parse, resolve inheritance and mixins, check against the schema. Returns diagnostics with codes, positions and hints. Call it after every edit; fix all errors before moving on.")]
    public static CheckResult Check(
        [Description("Files or directories with .kedo content")] string[] paths,
        [Description("Optional files or directories with the game's KEDO-written schema (kedo_type, kedo_enum, ...). Without it any type and field is accepted.")] string[]? schema_paths = null,
        [Description("Optional unsaved texts to check instead of the file on disk: [{path, text}]")] TextOverride[]? texts = null,
        [Description("Maximum diagnostics to return (default 50)")] int max_diagnostics = 50)
    {
        var ws = Workspace.Load(paths, schema_paths, texts?.Select(t => (t.Path, t.Text)).ToList());
        var errors = ws.Bag.Items.Count(d => d.IsError);
        var warnings = ws.Bag.Items.Count(d => d.Severity == Severity.Warning);
        return new CheckResult(!ws.Bag.HasErrors, $"{ws.Catalog.Concrete.Count()} object(s), {errors} error(s), {warnings} warning(s)", ws.Catalog.Concrete.Count(), ws.Catalog.Hash,
            ws.Diagnostics(max_diagnostics), ws.Bag.Items.Count > max_diagnostics);
    }

    [McpServerTool(Name = "kedo_format", ReadOnly = true, Idempotent = true), Description("Return the canonical formatting of a .kedo file or text. Never writes; the caller writes the result. Formatting changes only whitespace.")]
    public static FormatResult Format(
        [Description("Path of the file to format (used for diagnostics)")] string path,
        [Description("Text to format; when omitted the file is read from disk")] string? text = null)
    {
        var bag = new DiagnosticBag();
        var source = text is null ? SourceText.FromFile(path) : SourceText.FromString(text, path);
        var tree = Parser.Parse(source, bag);
        var formatted = Formatter.Format(tree);
        var original = Encoding.UTF8.GetString(source.Bytes);
        var ws = new Workspace();
        ws.Trees.Add(tree);
        foreach (var d in bag.Items) ws.Bag.Add(d);
        return new FormatResult(formatted != original, formatted, ws.Diagnostics(50));
    }

    [McpServerTool(Name = "kedo_resolve", ReadOnly = true, Idempotent = true), Description("Show the effective object after applying schema defaults, `extends`, mixins and its own entries: all fields, components, hooks, inheritance chain and hash. Use it to verify what the game will see.")]
    public static ResolveResult Resolve(
        [Description("Files or directories with .kedo content")] string[] paths,
        [Description("Object key as type:id, e.g. actor:depot_sentinel")] string key,
        [Description("Optional schema files or directories")] string[]? schema_paths = null)
    {
        var ws = Workspace.Load(paths, schema_paths);
        if (!ws.Catalog.ByKey.TryGetValue(key, out var obj))
        {
            return new ResolveResult(key, false, false, "", Array.Empty<string>(), Array.Empty<string>(), $"no object `{key}`; use kedo_catalog with kind=objects to list ids", ws.Diagnostics(20));
        }

        return new ResolveResult(key, true, obj.IsAbstract, obj.Hash, obj.Chain.ToArray(), obj.Mixins.ToArray(), ResolvedDump.DumpObject(obj, withProvenance: true), ws.Diagnostics(20));
    }

    [McpServerTool(Name = "kedo_explain", ReadOnly = true, Idempotent = true), Description("Explain where a resolved value came from: the winning layer (own, mixin, parent, schema default) with file and line, and every shadowed candidate. Path syntax: field.sub, component.field, list[key].field.")]
    public static ExplainResult Explain(
        [Description("Files or directories with .kedo content")] string[] paths,
        [Description("Object key as type:id")] string key,
        [Description("Field path, e.g. stats.hp or options[risk].check.difficulty")] string field_path,
        [Description("Optional schema files or directories")] string[]? schema_paths = null)
    {
        var ws = Workspace.Load(paths, schema_paths);
        return new ExplainResult(key, field_path, Resolver.Explain(ws.Catalog, key, field_path));
    }

    [McpServerTool(Name = "kedo_catalog", ReadOnly = true, Idempotent = true), Description("List what exists: kind=objects (type:id with chain), types (registered types and their fields), components, enums (values), hooks (context fields), mixins. Filter with query. Use before inventing a type, field, enum value or id.")]
    public static CatalogResult Catalog(
        [Description("Files or directories with .kedo content")] string[] paths,
        [Description("objects | types | components | enums | hooks | mixins")] string kind,
        [Description("Optional schema files or directories")] string[]? schema_paths = null,
        [Description("Optional substring filter on names")] string? query = null,
        [Description("Maximum items (default 100)")] int limit = 100)
    {
        var ws = Workspace.Load(paths, schema_paths);
        IEnumerable<CatalogItem> items = kind switch
        {
            "objects" => ws.Catalog.Objects.Select(o => new CatalogItem(o.Type, o.Key, (o.IsAbstract ? "abstract; " : "") + (o.Chain.Count > 1 ? "extends " + o.Chain[^2] : "root") + (o.Mixins.Count > 0 ? "; with " + string.Join(", ", o.Mixins) : ""))),
            "types" => ws.Schema.Types.Values.Select(t => new CatalogItem("type", t.Id, string.Join(", ", t.Body.Fields.Select(f => f.Name + ":" + f.Kind.ToString().ToLowerInvariant() + (f.Required ? "!" : ""))))),
            "components" => ws.Schema.Components.Values.Select(c => new CatalogItem("component", c.Id, string.Join(", ", c.Fields.Select(f => f.Name + ":" + f.Kind.ToString().ToLowerInvariant())))),
            "enums" => ws.Schema.Enums.Values.Select(e => new CatalogItem("enum", e.Id, string.Join(", ", e.Values))),
            "hooks" => ws.Schema.Hooks.Values.Select(h => new CatalogItem("hook", h.Id, "ctx: " + string.Join(", ", h.Context.Fields.Select(f => f.Name)) + (h.AllowsAwait ? "" : "; no wait") + (h.AllowsEffects ? "" : "; no apply"))),
            "mixins" => ws.Catalog.Mixins.Values.Select(m => new CatalogItem("mixin", m.Id, m.Doc ?? "")),
            _ => new[] { new CatalogItem("error", kind, "unknown kind; use objects, types, components, enums, hooks or mixins") },
        };
        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        return new CatalogResult(kind, list.Take(limit).ToList(), list.Count > limit);
    }

    [McpServerTool(Name = "kedo_compile", ReadOnly = true, Idempotent = true), Description("Compile the hooks and functions of one object (or all) to KEDO IR and return the disassembly, with diagnostics about context fields, recursion and permissions.")]
    public static CompileResult Compile(
        [Description("Files or directories with .kedo content")] string[] paths,
        [Description("Optional object key type:id; omit for all objects with hooks")] string? key = null,
        [Description("Optional schema files or directories")] string[]? schema_paths = null)
    {
        var ws = Workspace.Load(paths, schema_paths);
        var functions = ws.Declarations.SelectMany(d => d.Functions).ToList();
        var listings = new List<string>();
        foreach (var obj in ws.Catalog.Objects.Where(o => key is null || o.Key == key))
        {
            var program = BehaviorCompiler.Compile(obj, functions, ws.Schema, ws.Bag);
            if (program is not null)
            {
                listings.Add(Disassembler.Dump(program));
            }
        }

        return new CompileResult(listings, ws.Diagnostics(50));
    }

    [McpServerTool(Name = "kedo_schema", ReadOnly = true, Idempotent = true), Description("Export the game's schema as a JSON Schema 2020-12 bundle (types, components, hooks, enums, records) for form generation or to learn the exact field names and kinds.")]
    public static string Schema([Description("Files or directories with the KEDO-written schema")] string[] schema_paths)
    {
        var ws = Workspace.Load(Array.Empty<string>(), schema_paths);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            JsonSchemaWriter.Write(ws.Schema, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>Unsaved text for a path.</summary>
internal sealed record TextOverride(string Path, string Text);
