using System.Text;
using System.Text.Json;
using Kedo.Compiler.Formatting;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;

// KEDO command line. Every command accepts --json for machine-readable output (KEDO-00, anexo G).
// Exit codes: 0 ok, 1 diagnostics with errors (or `fmt --check` found changes), 2 usage error, 3 I/O error.

var options = new HashSet<string>(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)));
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var json = options.Contains("--json");

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
            return Parse(positional.Skip(1).ToArray(), json, dumpTree: false);
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
    Console.WriteLine("kedo <command> [files or directories...] [--json]");
    Console.WriteLine("  version            Print the toolchain version.");
    Console.WriteLine("  parse <paths>      Parse and print the syntax tree and diagnostics.");
    Console.WriteLine("  check <paths>      Parse and print diagnostics only.");
    Console.WriteLine("  fmt <paths>        Rewrite files in canonical form; with --check only report files that would change.");
}

static IEnumerable<string> ExpandFiles(string[] paths)
{
    foreach (var p in paths)
    {
        if (Directory.Exists(p))
        {
            foreach (var f in Directory.EnumerateFiles(p, "*.kedo", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                yield return f;
            }
        }
        else
        {
            yield return p;
        }
    }
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
