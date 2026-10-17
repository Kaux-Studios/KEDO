using BenchmarkDotNet.Attributes;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

namespace Kedo.Benchmarks;

/// <summary>
/// Incremental resolution (#15, ADR-0007 D4: at most 50 ms per edited file with 10k objects). The synthetic module is
/// split into 100 files; each operation edits one value of one file and updates the session. Editing the last file
/// touches objects nobody extends; editing the middle reaches every descendant of the edited objects, which in this
/// corpus (each actor extends one of the eight before it) is most of the second half.
/// </summary>
[MemoryDiagnoser]
public class IncrementalBenchmarks
{
    [Params(10_000)]
    public int Objects { get; set; }

    private readonly SchemaRegistry _schema = new() { Permissive = true };
    private List<(string Name, string Text)> _files = new();
    private SourceText[] _sources = Array.Empty<SourceText>();
    private IncrementalCatalog _session = null!;
    private int _edit;

    [GlobalSetup]
    public void Setup()
    {
        _files = SyntheticCorpus.GenerateFiles(Objects, 100);
        _sources = _files.Select(f => SourceText.FromString(f.Text, f.Name)).ToArray();
        _session = new IncrementalCatalog(_schema);
        _session.Update(_sources, new DiagnosticBag());
    }

    [Benchmark]
    public int UpdateUnchanged() => _session.Update(_sources, new DiagnosticBag()).Objects.Count;

    [Benchmark]
    public int EditLastFile() => Edit(_files.Count - 1);

    [Benchmark]
    public int EditMiddleFile() => Edit(_files.Count / 2);

    private int Edit(int file)
    {
        // A different value every time, written in place of the first `name_key` of the file: same length, same lines.
        var (name, text) = _files[file];
        var at = text.IndexOf("name_key = \"actor.", StringComparison.Ordinal);
        var value = (_edit++ % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var edited = text[..(at + 12)] + value + text[(at + 13)..];
        _sources[file] = SourceText.FromString(edited, name);
        return _session.Update(_sources, new DiagnosticBag()).Objects.Count;
    }
}
