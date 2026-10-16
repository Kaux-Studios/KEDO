using System.Text;
using System.Text.Json;
using Kedo.Compiler.Formatting;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

// KEDO command line. Every command accepts --json for machine-readable output (KEDO-00, anexo G).
// Exit codes: 0 ok, 1 diagnostics with errors (or `fmt --check` found changes), 2 usage error, 3 I/O error.

var options = new HashSet<string>(args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && !a.Contains('=')));
var valued = args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=')).ToDictionary(a => a[..a.IndexOf('=')], a => a[(a.IndexOf('=') + 1)..]);
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var json = options.Contains("--json");
Kedo.Cli.CliOptions.SchemaPaths = valued.TryGetValue("--schema", out var schemaPath) ? schemaPath.Split(';', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

if (positional.Length == 0 || options.Contains("--help"))
{
    PrintUsage();
    return positional.Length == 0 ? 2 : 0;
}

try
{
    switch (positional[0])
    {
        case "version":
            Console.WriteLine(json
                ? $"{{\"toolchain\":\"{Kedo.KedoVersion.Current}\",\"language\":{Kedo.KedoVersion.LanguageMajor}}}"
                : Kedo.KedoVersion.Current);
            return 0;
        case "parse":
            return Parse(positional.Skip(1).ToArray(), json, dumpTree: true);
        case "check":
            return Check(positional.Skip(1).ToArray(), json, dump: false, options.Contains("--provenance"));
        case "resolve":
            return Check(positional.Skip(1).ToArray(), json, dump: true, options.Contains("--provenance"));
        case "explain":
            return Explain(positional.Skip(1).ToArray());
        case "schema":
            return Schema(positional.Skip(1).ToArray(), json);
        case "compile":
            return Compile(positional.Skip(1).ToArray(), json);
        case "mcp":
            return await Kedo.Cli.McpServer.RunAsync();
        case "build":
            return Build(positional.Skip(1).ToArray(), json, valued.TryGetValue("--out", out var outPath) ? outPath : null);
        case "fmt":
            return Fmt(positional.Skip(1).ToArray(), options.Contains("--check"), json);
        default:
            Console.Error.WriteLine($"unknown command `{positional[0]}`");
            PrintUsage();
            return 2;
    }
}
catch (IOException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

static void PrintUsage()
{
    Console.WriteLine("kedo <command> [files or directories...] [--json] [--schema=<paths>]");
    Console.WriteLine("  schema <paths>     Load a KEDO-written schema (kedo_type, kedo_component, ...) and print it as a JSON Schema bundle.");
    Console.WriteLine("  version            Print the toolchain version.");
    Console.WriteLine("  parse <paths>      Parse and print the syntax tree and diagnostics.");
    Console.WriteLine("  check <paths>      Parse, resolve and validate; print diagnostics.");
    Console.WriteLine("  resolve <paths>    Like check, then print the resolved catalog (--provenance adds origins).");
    Console.WriteLine("  explain <paths> <type:id> <path>   Show where a resolved value came from.");
    Console.WriteLine("  compile <paths>    Resolve and compile hooks and functions to IR; print the disassembly.");
    Console.WriteLine("  build <paths> --out=<file.kpk>   Resolve, compile and write the KEDO Pack binary.");
    Console.WriteLine("  mcp                Start the stdio MCP server (stateless; tools kedo_check, kedo_format, kedo_resolve, kedo_explain, kedo_catalog, kedo_compile, kedo_schema).");
    Console.WriteLine("  fmt <paths>        Rewrite files in canonical form; with --check only report files that would change.");
    Console.WriteLine("Without a registered schema the resolver runs in permissive mode (any type and field; list strategies inferred).");
}

static Kedo.Compiler.Resolution.ResolvedCatalog Load(string[] paths, DiagnosticBag bag, List<SyntaxTree> trees)
{
    var decls = new List<Kedo.Compiler.Model.FileDeclarations>();
    foreach (var file in ExpandFiles(paths))
    {
        var tree = Parser.Parse(SourceText.FromFile(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, bag, trees);
    return Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, bag);
}

static int Check(string[] paths, bool json, bool dump, bool provenance)
{
    if (paths.Length == 0)
    {
        Console.Error.WriteLine("expected at least one file or directory");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    var catalog = Load(paths, bag, trees);
    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("catalog_hash", catalog.Hash);
        writer.WriteNumber("objects", catalog.Concrete.Count());
        if (dump)
        {
            writer.WriteString("resolved", Kedo.Compiler.Resolution.ResolvedDump.Dump(catalog, provenance));
        }

        WriteDiagnosticsJson(writer, bag, trees);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
    else
    {
        if (dump)
        {
            Console.Write(Kedo.Compiler.Resolution.ResolvedDump.Dump(catalog, provenance));
        }

        WriteDiagnosticsText(bag, trees);
        if (!dump)
        {
            Console.WriteLine($"{catalog.Concrete.Count()} object(s), catalog {catalog.Hash}, {bag.Items.Count(d => d.IsError)} error(s), {bag.Items.Count(d => d.Severity == Severity.Warning)} warning(s)");
        }
    }

    return bag.HasErrors ? 1 : 0;
}

static int Compile(string[] paths, bool json)
{
    if (paths.Length == 0)
    {
        Console.Error.WriteLine("expected at least one file or directory");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    var decls = new List<Kedo.Compiler.Model.FileDeclarations>();
    foreach (var file in ExpandFiles(paths))
    {
        var tree = Parser.Parse(SourceText.FromFile(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, bag, trees);
    var catalog = Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, bag);
    var moduleFunctions = decls.SelectMany(d => d.Functions).ToList();
    var programs = new List<Kedo.Runtime.BehaviorProgram>();
    foreach (var obj in catalog.Objects)
    {
        var program = Kedo.Compiler.Behaviors.BehaviorCompiler.Compile(obj, moduleFunctions, schema, bag);
        if (program is not null)
        {
            programs.Add(program);
        }
    }

    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("programs");
        foreach (var p in programs)
        {
            writer.WriteStartObject();
            writer.WriteString("behavior", p.BehaviorId);
            writer.WriteNumber("instructions", p.Code.Length);
            writer.WriteString("listing", Kedo.Runtime.Disassembler.Dump(p));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteDiagnosticsJson(writer, bag, trees);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
    else
    {
        foreach (var p in programs)
        {
            Console.Write(Kedo.Runtime.Disassembler.Dump(p));
        }

        WriteDiagnosticsText(bag, trees);
    }

    return bag.HasErrors ? 1 : 0;
}

static int Build(string[] paths, bool json, string? outPath)
{
    if (paths.Length == 0 || outPath is null)
    {
        Console.Error.WriteLine("usage: kedo build <paths...> --out=<file.kpk> [--schema=<paths>]");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    var decls = new List<Kedo.Compiler.Model.FileDeclarations>();
    foreach (var file in ExpandFiles(paths))
    {
        var tree = Parser.Parse(SourceText.FromFile(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, bag, trees);
    var catalog = Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, bag);
    var moduleFunctions = decls.SelectMany(d => d.Functions).ToList();
    var programs = new List<Kedo.Runtime.BehaviorProgram>();
    foreach (var obj in catalog.Objects)
    {
        var program = Kedo.Compiler.Behaviors.BehaviorCompiler.Compile(obj, moduleFunctions, schema, bag);
        if (program is not null)
        {
            programs.Add(program);
        }
    }

    var written = false;
    var size = 0;
    if (!bag.HasErrors)
    {
        var bytes = Kedo.Compiler.Pack.PackWriter.Write(catalog, programs);
        File.WriteAllBytes(outPath, bytes);
        written = true;
        size = bytes.Length;
    }

    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteBoolean("written", written);
        writer.WriteString("out", outPath);
        writer.WriteNumber("bytes", size);
        writer.WriteNumber("objects", catalog.Concrete.Count());
        writer.WriteNumber("programs", programs.Count);
        writer.WriteString("catalog_hash", catalog.Hash);
        WriteDiagnosticsJson(writer, bag, trees);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
    else
    {
        WriteDiagnosticsText(bag, trees);
        Console.WriteLine(written
            ? $"wrote {outPath}: {size} bytes, {catalog.Concrete.Count()} object(s), {programs.Count} program(s), catalog {catalog.Hash}"
            : $"not written: {bag.Items.Count(d => d.IsError)} error(s)");
    }

    return bag.HasErrors ? 1 : 0;
}

static int Explain(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: kedo explain <paths...> <type:id> <field.path>");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    var catalog = Load(args[..^2], bag, trees);
    Console.WriteLine(Kedo.Compiler.Resolution.Resolver.Explain(catalog, args[^2], args[^1]));
    WriteDiagnosticsText(bag, trees);
    return 0;
}

static IEnumerable<string> ExpandFiles(string[] paths, bool excludeSchema = true)
{
    var schemaDirs = excludeSchema ? Kedo.Cli.CliOptions.SchemaPaths.Select(Path.GetFullPath).ToArray() : Array.Empty<string>();
    bool IsSchema(string f) => schemaDirs.Any(d => Path.GetFullPath(f).StartsWith(d, StringComparison.OrdinalIgnoreCase));
    foreach (var p in paths)
    {
        if (Directory.Exists(p))
        {
            foreach (var f in Directory.EnumerateFiles(p, "*.kedo", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!IsSchema(f))
                {
                    yield return f;
                }
            }
        }
        else if (!IsSchema(p))
        {
            yield return p;
        }
    }
}

static Kedo.Schema.SchemaRegistry LoadSchema(string[] paths, DiagnosticBag bag, List<SyntaxTree> trees)
{
    if (paths.Length == 0)
    {
        return new Kedo.Schema.SchemaRegistry { Permissive = true };
    }

    var decls = new List<Kedo.Compiler.Model.FileDeclarations>();
    foreach (var file in ExpandFiles(paths, excludeSchema: false))
    {
        var tree = Parser.Parse(SourceText.FromFile(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    return Kedo.Compiler.Schema.SchemaLoader.Load(decls, bag);
}

static int Schema(string[] paths, bool json)
{
    if (paths.Length == 0)
    {
        Console.Error.WriteLine("expected the schema files or directory");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    var registry = LoadSchema(paths, bag, trees);
    if (bag.HasErrors)
    {
        WriteDiagnosticsText(bag, trees);
        return 1;
    }

    using var stream = Console.OpenStandardOutput();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    Kedo.Compiler.Schema.JsonSchemaWriter.Write(registry, writer);
    writer.Flush();
    Console.WriteLine();
    if (!json)
    {
        WriteDiagnosticsText(bag, trees);
    }

    return 0;
}

static int Parse(string[] paths, bool json, bool dumpTree)
{
    if (paths.Length == 0)
    {
        Console.Error.WriteLine("expected at least one file or directory");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    foreach (var file in ExpandFiles(paths))
    {
        trees.Add(Parser.Parse(SourceText.FromFile(file), bag));
    }

    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        if (dumpTree)
        {
            writer.WriteStartArray("files");
            foreach (var tree in trees)
            {
                writer.WriteStartObject();
                writer.WriteString("path", tree.Source.Path);
                writer.WriteString("tree", SyntaxDump.Dump(tree));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        WriteDiagnosticsJson(writer, bag, trees);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
    else
    {
        if (dumpTree)
        {
            foreach (var tree in trees)
            {
                Console.WriteLine($"== {tree.Source.Path}");
                Console.Write(SyntaxDump.Dump(tree));
            }
        }

        WriteDiagnosticsText(bag, trees);
    }

    return bag.HasErrors ? 1 : 0;
}

static int Fmt(string[] paths, bool checkOnly, bool json)
{
    if (paths.Length == 0)
    {
        Console.Error.WriteLine("expected at least one file or directory");
        return 2;
    }

    var changed = new List<string>();
    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    foreach (var file in ExpandFiles(paths))
    {
        var source = SourceText.FromFile(file);
        var tree = Parser.Parse(source, bag);
        trees.Add(tree);
        var formatted = Formatter.Format(tree);
        if (formatted != Encoding.UTF8.GetString(source.Bytes))
        {
            changed.Add(file);
            if (!checkOnly)
            {
                File.WriteAllText(file, formatted, new UTF8Encoding(false));
            }
        }
    }

    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("changed");
        foreach (var f in changed)
        {
            writer.WriteStringValue(f);
        }

        writer.WriteEndArray();
        WriteDiagnosticsJson(writer, bag, trees);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
    else
    {
        foreach (var f in changed)
        {
            Console.WriteLine(checkOnly ? $"would reformat {f}" : $"reformatted {f}");
        }

        WriteDiagnosticsText(bag, trees);
    }

    if (bag.HasErrors)
    {
        return 1;
    }

    return checkOnly && changed.Count > 0 ? 1 : 0;
}

static void WriteDiagnosticsText(DiagnosticBag bag, List<SyntaxTree> trees)
{
    foreach (var d in bag.Items)
    {
        var tree = trees.FirstOrDefault(t => t.Source.Path == d.File);
        var pos = tree?.Source.GetLinePosition(d.Span.Start);
        Console.Error.WriteLine($"{d.File}:{pos?.Line}:{pos?.Column}: {d.Severity.ToString().ToLowerInvariant()} {d.Code}: {d.Message}");
    }
}

static void WriteDiagnosticsJson(Utf8JsonWriter writer, DiagnosticBag bag, List<SyntaxTree> trees)
{
    writer.WriteBoolean("ok", !bag.HasErrors);
    writer.WriteStartArray("diagnostics");
    foreach (var d in bag.Items)
    {
        var tree = trees.FirstOrDefault(t => t.Source.Path == d.File);
        writer.WriteStartObject();
        writer.WriteString("code", d.Code);
        writer.WriteString("severity", d.Severity.ToString().ToLowerInvariant());
        writer.WriteString("message", d.Message);
        writer.WriteString("file", d.File);
        writer.WriteNumber("start", d.Span.Start);
        writer.WriteNumber("length", d.Span.Length);
        if (tree is not null)
        {
            var pos = tree.Source.GetLinePosition(d.Span.Start);
            writer.WriteNumber("line", pos.Line);
            writer.WriteNumber("column", pos.Column);
        }

        writer.WriteEndObject();
    }

    writer.WriteEndArray();
}
