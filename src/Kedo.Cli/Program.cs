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
Kedo.Cli.CliOptions.Variant = valued.TryGetValue("--variant", out var variantName) ? variantName : null;
if (valued.TryGetValue("--text", out var textOverrides))
{
    foreach (var pair in textOverrides.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var split = pair.LastIndexOf('=');
        if (split > 0)
        {
            Kedo.Cli.CliOptions.TextOverrides[Path.GetFullPath(pair[..split])] = pair[(split + 1)..];
        }
    }
}

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
        case "catalog":
            return Catalog(positional.Skip(1).ToArray(), json);
        case "import-json":
            return ImportJson(positional.Skip(1).ToArray(), valued);
        case "build":
            return Build(positional.Skip(1).ToArray(), json, valued.TryGetValue("--out", out var outPath) ? outPath : null);
        case "fmt":
            return Fmt(positional.Skip(1).ToArray(), options.Contains("--check"), json);
        case "eval":
            return Kedo.Cli.Eval.Run(positional.Skip(1).ToArray(), valued, json, options.Contains("--reference"), options.Contains("--keep"), options.Contains("--mcp"));
        case "describe":
            return Describe(positional.Skip(1).ToArray(), json);
        case "edit":
            return Edit(positional.Skip(1).ToArray(), valued, json);
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
    Console.WriteLine("kedo <command> [files or directories...] [--json] [--schema=<paths>] [--variant=<name>]");
    Console.WriteLine("  schema <paths>     Load a KEDO-written schema (kedo_type, kedo_component, ...) and print it as a JSON Schema bundle.");
    Console.WriteLine("  version            Print the toolchain version.");
    Console.WriteLine("  parse <paths>      Parse and print the syntax tree and diagnostics.");
    Console.WriteLine("  check <paths>      Parse, resolve and validate; print diagnostics.");
    Console.WriteLine("  resolve <paths>    Like check, then print the resolved catalog (--provenance adds origins).");
    Console.WriteLine("  explain <paths> <type:id> <path>   Show where a resolved value came from.");
    Console.WriteLine("  catalog <paths>    List objects (type, id, file, line, chain, mixins, hash) and mixins; with --json for tools.");
    Console.WriteLine("  compile <paths>    Resolve and compile hooks and functions to IR; print the disassembly.");
    Console.WriteLine("  build <paths> --out=<file.kpk>   Resolve, compile and write the KEDO Pack binary.");
    Console.WriteLine("  import-json <file.json> --out=<dir> --module=<name> [--map=events:event,items:item] [--enums=f1,f2] [--pack-type=pack]   Draft .kedo files from existing JSON content.");
    Console.WriteLine("  mcp                Start the stdio MCP server (stateless; tools kedo_check, kedo_format, kedo_resolve, kedo_describe, kedo_explain, kedo_catalog, kedo_compile, kedo_schema).");
    Console.WriteLine("  fmt <paths>        Rewrite files in canonical form; with --check only report files that would change.");
    Console.WriteLine("  describe <paths> <type:id>   Fields of an object for forms: schema, resolved value as a literal and its layer (--json).");
    Console.WriteLine("  edit <file> set <type:id> <path> <value>|--value-file=<file> [--op==!|+=|-=] | unset <type:id> <path> | new <type> <id> [--extends=<parent>] | duplicate <type:id> <new id>");
    Console.WriteLine("                     Structural edit of one declaration, reprinted in canonical form; the file is rewritten only when the edit is valid.");
    Console.WriteLine("  eval <suite.json> --reference | --agent=\"<command with {prompt_file} {workspace}>\"   Run the agent evaluation suite (eval/tasks.json) and verify each task.");
    Console.WriteLine("  --text=<file>=<copy>   Read <copy> in place of <file> (unsaved editor text).");
    Console.WriteLine("Without a registered schema the resolver runs in permissive mode (any type and field; list strategies inferred).");
    Console.WriteLine("Files with `variant <name>` after `module` are resolved over the base content; check reports every variant, and --variant=<name> makes resolve, explain, catalog, compile and build use that variant.");
}

static Kedo.Compiler.Resolution.ResolvedCatalog Load(string[] paths, DiagnosticBag bag, List<SyntaxTree> trees)
{
    var decls = new List<Kedo.Compiler.Model.FileDeclarations>();
    foreach (var file in ExpandFiles(paths))
    {
        var tree = Parser.Parse(ReadSource(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, bag, trees);
    return View(Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, bag));
}

// The catalog the command works on: the base, or the variant named with --variant (ADR-0012).
static Kedo.Compiler.Resolution.ResolvedCatalog View(Kedo.Compiler.Resolution.ResolvedCatalog catalog)
{
    if (Kedo.Cli.CliOptions.Variant is not { } variant)
    {
        return catalog;
    }

    if (catalog.Variants.TryGetValue(variant, out var view))
    {
        return view;
    }

    Console.Error.WriteLine($"no variant `{variant}` in the input{(catalog.Variants.Count == 0 ? "" : $" (variants: {string.Join(", ", catalog.Variants.Keys)})")}");
    Environment.Exit(2);
    return catalog;
}

// A file, or the copy given with --text for it (the same path for diagnostics).
static SourceText ReadSource(string file)
    => Kedo.Cli.CliOptions.TextOverrides.TryGetValue(Path.GetFullPath(file), out var copy)
        ? SourceText.FromBytes(File.ReadAllBytes(copy), file)
        : SourceText.FromFile(file);

static int Describe(string[] args, bool json)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: kedo describe <paths...> <type:id> --json [--schema=...]");
        return 2;
    }

    var bag = new DiagnosticBag();
    var trees = new List<SyntaxTree>();
    var catalog = Load(args[..^1], bag, trees);
    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, new DiagnosticBag(), new List<SyntaxTree>());
    using var stream = Console.OpenStandardOutput();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    if (!catalog.ByKey.TryGetValue(args[^1], out var obj))
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", false);
        writer.WriteString("error", $"no object `{args[^1]}`");
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
        return 1;
    }

    Kedo.Compiler.Editing.ObjectDescriber.Write(writer, obj, schema);
    writer.Flush();
    Console.WriteLine();
    return 0;
}

static int Edit(string[] args, Dictionary<string, string> valued, bool json)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: kedo edit <file> set <type:id> <path> <value> | unset <type:id> <path> | new <type> <id> | duplicate <type:id> <new id>");
        return 2;
    }

    var file = args[0];
    var source = File.ReadAllText(file);
    static (string Type, string Id) Split(string key)
    {
        var colon = key.IndexOf(':');
        return colon > 0 ? (key[..colon], key[(colon + 1)..]) : ("", key);
    }

    Kedo.Compiler.Editing.EditResult result;
    switch (args[1])
    {
        case "set" when args.Length >= 5 || (args.Length >= 4 && valued.ContainsKey("--value-file")):
        {
            // --value-file carries the literal untouched: command lines lose quotes on some platforms.
            var (type, id) = Split(args[2]);
            var value = valued.TryGetValue("--value-file", out var valueFile) ? File.ReadAllText(valueFile).Trim() : string.Join(" ", args[4..]);
            result = Kedo.Compiler.Editing.SourceEditor.SetField(source, type, id, args[3], value, valued.TryGetValue("--op", out var op) ? op : "=");
            break;
        }

        case "unset" when args.Length >= 4:
        {
            var (type, id) = Split(args[2]);
            result = Kedo.Compiler.Editing.SourceEditor.RemoveField(source, type, id, args[3]);
            break;
        }

        case "new" when args.Length >= 4:
            result = Kedo.Compiler.Editing.SourceEditor.AddObject(source, args[2], args[3], valued.TryGetValue("--extends", out var parent) ? parent : null);
            break;
        case "duplicate" when args.Length >= 4:
        {
            var (type, id) = Split(args[2]);
            result = Kedo.Compiler.Editing.SourceEditor.DuplicateObject(source, type, id, args[3]);
            break;
        }

        default:
            Console.Error.WriteLine($"unknown edit `{args[1]}` or missing arguments");
            return 2;
    }

    if (result.Ok)
    {
        // Atomic replace: write next to the file, then move over it.
        var temp = file + ".kedo-edit";
        File.WriteAllText(temp, result.Text, new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteBoolean("ok", result.Ok);
        if (result.Ok)
        {
            writer.WriteBoolean("changed", result.Text != source);
        }
        else
        {
            writer.WriteString("error", result.Error);
        }

        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine(result.Ok ? $"edited {file}" : $"not edited: {result.Error}");
    }

    return result.Ok ? 0 : 1;
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
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
        var tree = Parser.Parse(ReadSource(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, bag, trees);
    var catalog = View(Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, bag));
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
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
        var tree = Parser.Parse(ReadSource(file), bag);
        trees.Add(tree);
        decls.Add(Kedo.Compiler.Model.DeclarationBuilder.Build(tree, bag));
    }

    var schema = LoadSchema(Kedo.Cli.CliOptions.SchemaPaths, bag, trees);
    var catalog = View(Kedo.Compiler.Resolution.Resolver.Resolve(decls, schema, bag));
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
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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

static int Catalog(string[] paths, bool json)
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
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteString("catalog_hash", catalog.Hash);
        writer.WriteStartArray("objects");
        foreach (var o in catalog.Objects)
        {
            writer.WriteStartObject();
            writer.WriteString("type", o.Type);
            writer.WriteString("id", o.Id);
            writer.WriteString("key", o.Key);
            writer.WriteBoolean("abstract", o.IsAbstract);
            writer.WriteString("file", o.Declaration.Origin.File);
            writer.WriteNumber("line", o.Declaration.Origin.Line);
            writer.WriteString("doc", o.Declaration.Doc);
            writer.WriteString("parent", o.Chain.Count > 1 ? o.Chain[^2] : null);
            writer.WriteStartArray("mixins");
            foreach (var m in o.Mixins) writer.WriteStringValue(m);
            writer.WriteEndArray();
            writer.WriteStartArray("hooks");
            foreach (var h in o.Hooks) writer.WriteStringValue(h.HookId);
            writer.WriteEndArray();
            writer.WriteString("hash", o.Hash);
            writer.WriteBoolean("errors", o.HasErrors);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("mixins");
        foreach (var m in catalog.Mixins.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", m.Id);
            writer.WriteString("file", m.Origin.File);
            writer.WriteNumber("line", m.Origin.Line);
            writer.WriteString("doc", m.Doc);
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
        foreach (var o in catalog.Objects)
        {
            Console.WriteLine($"{(o.IsAbstract ? "abstract " : "")}{o.Key}  {Path.GetFileName(o.Declaration.Origin.File)}:{o.Declaration.Origin.Line}{(o.Chain.Count > 1 ? "  extends " + o.Chain[^2] : "")}{(o.Mixins.Count > 0 ? "  with " + string.Join(", ", o.Mixins) : "")}");
        }

        WriteDiagnosticsText(bag, trees);
    }

    return bag.HasErrors ? 1 : 0;
}

static int ImportJson(string[] args, Dictionary<string, string> valued)
{
    if (args.Length != 1 || !valued.TryGetValue("--out", out var outDir))
    {
        Console.Error.WriteLine("usage: kedo import-json <file.json> --out=<dir> --module=<name> [--map=coll:type,...] [--enums=field,...] [--pack-type=pack] [--pack-id=id] [--id-field=id]");
        return 2;
    }

    var options = new Kedo.Compiler.Import.JsonImportOptions
    {
        Module = valued.TryGetValue("--module", out var m) ? m : "imported",
        PackType = valued.TryGetValue("--pack-type", out var pt) ? pt : "pack",
        PackId = valued.TryGetValue("--pack-id", out var pid) ? pid : null,
        IdField = valued.TryGetValue("--id-field", out var idField) ? idField : "id",
    };
    if (valued.TryGetValue("--map", out var map))
    {
        foreach (var pair in map.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            options.Collections[kv[0]] = kv.Length > 1 ? kv[1] : kv[0];
        }
    }

    if (valued.TryGetValue("--enums", out var enums))
    {
        foreach (var f in enums.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            options.EnumFields.Add(f);
        }
    }

    var files = Kedo.Compiler.Import.JsonImporter.Import(File.ReadAllText(args[0]), options);
    Directory.CreateDirectory(outDir);
    foreach (var (name, text) in files)
    {
        var path = Path.Combine(outDir, name);
        var bag = new DiagnosticBag();
        var tree = Parser.Parse(SourceText.FromString(text, path), bag);
        File.WriteAllText(path, bag.HasErrors ? text : Formatter.Format(tree), new UTF8Encoding(false));
        Console.WriteLine($"wrote {path}{(bag.HasErrors ? " (with syntax errors; review)" : "")}");
    }

    // Strings that repeat and look like identifiers are probably enumeration values (P5); --enums writes them bare.
    var suggestions = Kedo.Compiler.Import.JsonImporter.SuggestEnumFields(File.ReadAllText(args[0]), options);
    if (suggestions.Count > 0)
    {
        Console.WriteLine($"suggestion: these string fields look like enumerations; to write them bare, add --enums={string.Join(",", options.EnumFields.Concat(suggestions.Select(s => s.Field)))}");
        foreach (var s in suggestions)
        {
            Console.WriteLine($"  {s.Field}: {s.Values.Count} value(s) in {s.Uses} use(s): {string.Join(", ", s.Values.Take(8))}{(s.Values.Count > 8 ? ", ..." : "")}");
        }
    }

    return 0;
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
        var tree = Parser.Parse(ReadSource(file), bag);
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
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
        trees.Add(Parser.Parse(ReadSource(file), bag));
    }

    if (json)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
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
