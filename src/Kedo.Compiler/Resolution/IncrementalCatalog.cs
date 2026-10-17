using System.IO.Hashing;
using Kedo.Compiler.Model;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

namespace Kedo.Compiler.Resolution;

/// <summary>What an incremental run leaves for the next one (#15).</summary>
internal sealed class IncrementalState
{
    public IncrementalState(IReadOnlyDictionary<string, string> prints) => Prints = prints;

    public IReadOnlyDictionary<string, string> Prints { get; }

    public ResolvedCatalog Catalog { get; set; } = null!;

    public Dictionary<string, List<Diagnostic>> Resolution { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> Invalid { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<Diagnostic>> StageB { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, HashSet<string>> References { get; } = new(StringComparer.Ordinal);

    public HashSet<string> HasErrors { get; } = new(StringComparer.Ordinal);

    public int ResolvedCount { get; set; }

    public int ReusedCount { get; set; }
}

/// <summary>
/// Keeps a content set resolved across edits (#15, ADR-0007 D4): files whose bytes did not change are not parsed
/// again, and only the objects an edit can affect are resolved again: those whose declaration text or position
/// changed, their descendants and the users of a changed mixin. Stage B runs again for those and for the objects that
/// referenced a key that changed, appeared or vanished. Every update returns the same catalog, hashes and diagnostics
/// as a full <see cref="Resolver.Resolve"/>. Content with variants (ADR-0012) is resolved in full.
/// </summary>
public sealed class IncrementalCatalog
{
    private readonly SchemaRegistry _schema;
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.Ordinal);
    private IncrementalState? _state;

    private sealed record CachedFile(UInt128 Hash, FileDeclarations Declarations, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<string, string> Prints);

    /// <summary>Creates an empty session over a schema (a permissive registry accepts any type).</summary>
    public IncrementalCatalog(SchemaRegistry schema) => _schema = schema;

    /// <summary>Files parsed by the last update (the rest came from the cache).</summary>
    public int LastParsedFiles { get; private set; }

    /// <summary>Objects resolved by the last update.</summary>
    public int LastResolvedObjects { get; private set; }

    /// <summary>Objects reused by the last update.</summary>
    public int LastReusedObjects { get; private set; }

    /// <summary>Resolves the whole content set given as sources (every file, every time), reusing what did not change.</summary>
    public ResolvedCatalog Update(IReadOnlyList<SourceText> sources, DiagnosticBag diagnostics)
    {
        var parseBag = new DiagnosticBag();
        var files = new List<FileDeclarations>(sources.Count);
        var prints = new Dictionary<string, string>(StringComparer.Ordinal);
        var live = new HashSet<string>(StringComparer.Ordinal);
        LastParsedFiles = 0;
        foreach (var source in sources)
        {
            var path = source.Path ?? "";
            live.Add(path);
            var hash = XxHash128.HashToUInt128(source.Bytes);
            if (!_files.TryGetValue(path, out var cached) || cached.Hash != hash)
            {
                var bag = new DiagnosticBag();
                var tree = Parser.Parse(source, bag);
                var decls = DeclarationBuilder.Build(tree, bag);
                cached = new CachedFile(hash, decls, bag.Items.ToList(), Prints(tree, decls));
                _files[path] = cached;
                LastParsedFiles++;
            }

            foreach (var d in cached.Diagnostics)
            {
                parseBag.Add(d);
            }

            files.Add(cached.Declarations);
            foreach (var (key, print) in cached.Prints)
            {
                prints.TryAdd(key, print);
            }
        }

        foreach (var gone in _files.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _files.Remove(gone);
        }

        diagnostics.AddRange(parseBag);
        if (files.Any(f => f.Variant is not null))
        {
            // Variants resolve the module once per variant; keep it simple and exact.
            _state = null;
            var full = Resolver.Resolve(files, _schema, diagnostics);
            LastResolvedObjects = full.Objects.Count;
            LastReusedObjects = 0;
            return full;
        }

        var (catalog, state) = Resolver.ResolveIncremental(files, _schema, diagnostics, prints, _state);
        _state = state;
        LastResolvedObjects = state.ResolvedCount;
        LastReusedObjects = state.ReusedCount;
        return catalog;
    }

    /// <summary>
    /// A print per declaration: its text and its position. Position matters because provenance (file, line, span)
    /// is part of what an object resolves to; an edit above a declaration therefore resolves it again.
    /// </summary>
    private static Dictionary<string, string> Prints(SyntaxTree tree, FileDeclarations decls)
    {
        var prints = new Dictionary<string, string>(StringComparer.Ordinal);
        string Print(Origin origin) => $"{origin.File}|{origin.Span.Start}|{origin.Line}|{origin.Column}|{XxHash128.HashToUInt128(tree.Source.Slice(origin.Span))}";
        foreach (var obj in decls.Objects)
        {
            prints.TryAdd(obj.Key, Print(obj.Origin));
        }

        foreach (var mixin in decls.Mixins)
        {
            prints.TryAdd("mixin:" + mixin.Id, Print(mixin.Origin));
        }

        return prints;
    }
}
