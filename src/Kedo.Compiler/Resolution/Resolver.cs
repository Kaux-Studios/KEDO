using Kedo.Compiler.Model;
using Kedo.Diagnostics;
using Kedo.Schema;
using Kedo.Text;

namespace Kedo.Compiler.Resolution;

/// <summary>Result of resolving a set of files: objects in deterministic order plus diagnostics and the catalog hash.</summary>
public sealed class ResolvedCatalog
{
    internal ResolvedCatalog(IReadOnlyList<ResolvedObject> objects, IReadOnlyDictionary<string, ResolvedObject> byKey, IReadOnlyDictionary<string, MixinDeclaration> mixins, DiagnosticBag diagnostics)
    {
        Objects = objects;
        ByKey = byKey;
        Mixins = mixins;
        Diagnostics = diagnostics;
    }

    /// <summary>All resolved objects (abstract ones included) ordered by type then id.</summary>
    public IReadOnlyList<ResolvedObject> Objects { get; }

    /// <summary>Lookup by <c>type:id</c>.</summary>
    public IReadOnlyDictionary<string, ResolvedObject> ByKey { get; }

    /// <summary>Mixin declarations by id.</summary>
    public IReadOnlyDictionary<string, MixinDeclaration> Mixins { get; }

    /// <summary>Diagnostics of all stages.</summary>
    public DiagnosticBag Diagnostics { get; }

    /// <summary>Hash (hex) over the concrete objects; set by the hasher.</summary>
    public string Hash { get; internal set; } = "";

    /// <summary>Concrete (non-abstract) objects.</summary>
    public IEnumerable<ResolvedObject> Concrete => Objects.Where(o => !o.IsAbstract);
}

/// <summary>
/// Layered resolution (KEDO-02): schema defaults → resolved parent → flattened mixins → own entries; merge rules by value
/// kind; explicit deletion; provenance on every value; cycles and limits as errors; two-stage validation.
/// </summary>
public sealed class Resolver
{
    private const int MaxDepth = 32;
    private const int MaxMixins = 64;
    private const int MaxNesting = 16;

    private readonly SchemaRegistry _schema;
    private readonly DiagnosticBag _d;
    private readonly Dictionary<string, ObjectDeclaration> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MixinDeclaration> _mixins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedObject> _resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    private Resolver(SchemaRegistry schema, DiagnosticBag diagnostics)
    {
        _schema = schema;
        _d = diagnostics;
    }

    /// <summary>Resolves the declarations of several files against a schema.</summary>
    public static ResolvedCatalog Resolve(IEnumerable<FileDeclarations> files, SchemaRegistry schema, DiagnosticBag diagnostics)
    {
        var r = new Resolver(schema, diagnostics);
        r.Index(files);
        r.CheckReferencesToBases();
        foreach (var key in r.TopologicalOrder())
        {
            r.ResolveObject(key);
        }

        var objects = r._resolved.Values.OrderBy(o => o.Type, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        var catalog = new ResolvedCatalog(objects, r._resolved, r._mixins, diagnostics);
        r.ValidateStageB(catalog);
        CatalogHasher.Hash(catalog);
        return catalog;
    }

    // ----- indexing -----

    private void Index(IEnumerable<FileDeclarations> files)
    {
        foreach (var file in files)
        {
            foreach (var obj in file.Objects)
            {
                if (_objects.TryGetValue(obj.Key, out var existing))
                {
                    _d.Add(new Diagnostic("K2002", Severity.Error, $"duplicate id `{obj.Key}`", obj.Origin.File, obj.Origin.Span,
                        new[] { new DiagnosticLabel(existing.Origin.File, existing.Origin.Span, "first declared here") }, Array.Empty<string>()));
                    continue;
                }

                if (!_schema.Permissive && _schema.FindType(obj.Type) is null)
                {
                    _d.Error("K2001", $"type `{obj.Type}` is not registered", obj.Origin.File, obj.Origin.Span);
                    _failed.Add(obj.Key);
                }

                _objects.Add(obj.Key, obj);
            }

            foreach (var mixin in file.Mixins)
            {
                if (_mixins.TryGetValue(mixin.Id, out var existing))
                {
                    _d.Add(new Diagnostic("K2002", Severity.Error, $"duplicate mixin `{mixin.Id}`", mixin.Origin.File, mixin.Origin.Span,
                        new[] { new DiagnosticLabel(existing.Origin.File, existing.Origin.Span, "first declared here") }, Array.Empty<string>()));
                    continue;
                }

                _mixins.Add(mixin.Id, mixin);
            }
        }
    }

    private void CheckReferencesToBases()
    {
        foreach (var obj in _objects.Values)
        {
            if (obj.Parent is not null)
            {
                var parentKey = $"{obj.Type}:{obj.Parent}";
                if (!_objects.ContainsKey(parentKey))
                {
                    var other = _objects.Values.FirstOrDefault(o => o.Id == obj.Parent);
                    if (other is not null)
                    {
                        _d.Error("K2003", $"`{obj.Key}` extends `{other.Key}`, which is a `{other.Type}`, not a `{obj.Type}`", obj.Origin.File, obj.Origin.Span);
                    }
                    else
                    {
                        _d.Error("K2004", $"parent `{obj.Parent}` of `{obj.Key}` does not exist", obj.Origin.File, obj.Origin.Span);
                    }

                    _failed.Add(obj.Key);
                }
            }

            foreach (var m in obj.Mixins)
            {
                if (!_mixins.ContainsKey(m))
                {
                    _d.Error("K2004", $"mixin `{m}` used by `{obj.Key}` does not exist", obj.Origin.File, obj.Origin.Span);
                    _failed.Add(obj.Key);
                }
            }
        }

        foreach (var mixin in _mixins.Values)
        {
            foreach (var m in mixin.Mixins)
            {
                if (!_mixins.ContainsKey(m))
                {
                    _d.Error("K2004", $"mixin `{m}` used by mixin `{mixin.Id}` does not exist", mixin.Origin.File, mixin.Origin.Span);
                }
            }
        }
    }

    // ----- ordering -----

    private List<string> TopologicalOrder()
    {
        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done
        var stack = new List<string>();
        foreach (var key in _objects.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            Visit(key);
        }

        return order;

        void Visit(string key)
        {
            if (state.TryGetValue(key, out var s))
            {
                if (s == 1)
                {
                    var start = stack.IndexOf(key);
                    var path = string.Join(" → ", stack.Skip(start).Append(key));
                    var obj = _objects[key];
                    _d.Error("K3001", $"inheritance cycle: {path}", obj.Origin.File, obj.Origin.Span);
                    foreach (var member in stack.Skip(start))
                    {
                        _failed.Add(member);
                    }
                }

                return;
            }

            state[key] = 1;
            stack.Add(key);
            var decl = _objects[key];
            if (decl.Parent is not null)
            {
                var parentKey = $"{decl.Type}:{decl.Parent}";
                if (_objects.ContainsKey(parentKey))
                {
                    Visit(parentKey);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[key] = 2;
            order.Add(key);
        }
    }

    private List<string> FlattenMixins(IReadOnlyList<string> mixins, Origin where, string owner)
    {
        var result = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in mixins)
        {
            Add(m);
        }

        if (result.Count > MaxMixins)
        {
            _d.Error("K3009", $"`{owner}` includes {result.Count} mixins after flattening; the limit is {MaxMixins}", where.File, where.Span);
        }

        return result;

        void Add(string id)
        {
            if (!_mixins.TryGetValue(id, out var mixin))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                _d.Error("K3001", $"mixin cycle through `{id}` in `{owner}`", where.File, where.Span);
                return;
            }

            foreach (var sub in mixin.Mixins)
            {
                Add(sub);
            }

            if (!result.Contains(id))
            {
                result.Add(id);
            }

            visiting.Remove(id);
        }
    }

    // ----- resolution -----

    private void ResolveObject(string key)
    {
        var decl = _objects[key];
        if (_failed.Contains(key))
        {
            return;
        }

        ResolvedObject? parent = null;
        var chain = new List<string>();
        if (decl.Parent is not null)
        {
            var parentKey = $"{decl.Type}:{decl.Parent}";
            if (!_resolved.TryGetValue(parentKey, out parent))
            {
                _d.Error("K3002", $"`{key}` cannot be resolved because its base `{parentKey}` failed", decl.Origin.File, decl.Origin.Span);
                _failed.Add(key);
                return;
            }

            chain.AddRange(parent.Chain);
        }

        chain.Add(key);
        if (chain.Count > MaxDepth)
        {
            _d.Error("K3008", $"`{key}` has an inheritance depth of {chain.Count}; the limit is {MaxDepth}", decl.Origin.File, decl.Origin.Span);
            _failed.Add(key);
            return;
        }

        var typeSchema = _schema.FindType(decl.Type);
        var bodySchema = typeSchema?.Body;
        var ownProv = new Provenance(decl.Origin, LayerKind.Own, key);

        // Layer 0: schema defaults. Layer 1: parent (values keep their provenance; layer kind becomes Parent for explain).
        RRecord body;
        OrderedFields components;
        if (parent is not null)
        {
            body = parent.Body;
            components = parent.Components;
        }
        else
        {
            body = bodySchema is null ? RRecord.Empty(new Provenance(decl.Origin, LayerKind.Default, "")) : Defaults(bodySchema, decl.Origin);
            components = OrderedFields.Empty;
        }

        var hooks = new List<HookEntry>();
        var functions = new List<FunctionEntry>();

        // Layer 2: mixins.
        var mixins = FlattenMixins(decl.Mixins, decl.Origin, key);
        var written = new Dictionary<string, string>(StringComparer.Ordinal); // scalar path -> mixin that wrote it (K3010)
        foreach (var mixinId in mixins)
        {
            var mixin = _mixins[mixinId];
            var prov = new Provenance(mixin.Origin, LayerKind.Mixin, mixinId);
            ApplyEntries(mixin.Entries, ref body, ref components, hooks, functions, typeSchema, bodySchema, prov, key, written, mixinId);
        }

        // Layer 3: own.
        ApplyEntries(decl.Entries, ref body, ref components, hooks, functions, typeSchema, bodySchema, ownProv, key, null, null);

        if (parent is not null)
        {
            hooks.AddRange(parent.Hooks.Where(ph => hooks.All(h => h.HookId != ph.HookId)));
        }

        _resolved[key] = new ResolvedObject(decl, body, components, hooks, functions, chain, mixins);
    }

    private static RRecord Defaults(RecordSchema schema, Origin origin)
    {
        var fields = OrderedFields.Empty;
        var prov = new Provenance(origin, LayerKind.Default, schema.Id);
        foreach (var f in schema.Fields)
        {
            ResolvedValue? v = f.Kind switch
            {
                ValueKind.Int when f.DefaultInt is not null => new RInt(prov, f.DefaultInt.Value),
                ValueKind.Duration when f.DefaultInt is not null => new RDuration(prov, f.DefaultInt.Value),
                ValueKind.Bool when f.DefaultBool is not null => new RBool(prov, f.DefaultBool.Value),
                ValueKind.Text when f.DefaultText is not null => new RText(prov, f.DefaultText),
                ValueKind.Enum when f.DefaultText is not null => new REnum(prov, f.DefaultText),
                _ => null,
            };
            if (v is not null)
            {
                fields = fields.With(f.Name, v);
            }
        }

        return new RRecord(prov, fields);
    }

    private void ApplyEntries(IReadOnlyList<Entry> entries, ref RRecord body, ref OrderedFields components, List<HookEntry> hooks, List<FunctionEntry> functions,
        TypeSchema? typeSchema, RecordSchema? bodySchema, Provenance prov, string owner, Dictionary<string, string>? written, string? mixinId)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ComponentEntry c:
                {
                    var compSchema = _schema.FindComponent(c.ComponentType);
                    if (compSchema is null && !_schema.Permissive)
                    {
                        _d.Error("K2001", $"component `{c.ComponentType}` is not registered", c.Origin.File, c.Origin.Span);
                        continue;
                    }

                    if (typeSchema?.AllowedComponents is not null && !typeSchema.AllowedComponents.Contains(c.ComponentType))
                    {
                        _d.Error("K2005", $"type `{typeSchema.Id}` does not allow component `{c.ComponentType}`", c.Origin.File, c.Origin.Span);
                        continue;
                    }

                    var existing = components.TryGetValue(c.ComponentType, out var ev) ? (RRecord)ev : (compSchema is null ? RRecord.Empty(prov) : Defaults(compSchema, c.Origin));
                    var merged = MergeRecord(existing, c.Body, compSchema, prov with { Origin = c.Origin }, c.ComponentType, 1, written, mixinId);
                    components = components.With(c.ComponentType, merged);
                    break;
                }

                case RemovalEntry r when r.ComponentType is not null:
                    if (!components.TryGetValue(r.ComponentType, out _))
                    {
                        _d.Error("K3004", $"cannot remove component `{r.ComponentType}` from `{owner}`: no earlier layer provides it", r.Origin.File, r.Origin.Span);
                    }
                    else
                    {
                        components = components.Without(r.ComponentType);
                    }

                    break;
                case RemovalEntry r:
                {
                    var path = r.Path!;
                    var removed = RemovePath(body, path, 0, r.Origin, owner, bodySchema);
                    if (removed is not null)
                    {
                        body = removed;
                    }

                    break;
                }

                case HookEntry h:
                    if (!_schema.Permissive && _schema.FindHook(h.HookId) is null)
                    {
                        _d.Error("K2006", $"hook `{h.HookId}` is not registered", h.Origin.File, h.Origin.Span);
                    }
                    else if (typeSchema?.AllowedHooks is not null && !typeSchema.AllowedHooks.Contains(h.HookId))
                    {
                        _d.Error("K2006", $"type `{typeSchema.Id}` does not allow hook `{h.HookId}`", h.Origin.File, h.Origin.Span);
                    }
                    else if (hooks.Any(x => x.HookId == h.HookId && x.Origin.File == h.Origin.File))
                    {
                        _d.Error("K2007", $"hook `{h.HookId}` is declared twice in `{owner}`", h.Origin.File, h.Origin.Span);
                    }
                    else
                    {
                        hooks.RemoveAll(x => x.HookId == h.HookId); // a later layer overrides a mixin's hook
                        hooks.Add(h);
                    }

                    break;
                case FunctionEntry f:
                    functions.Add(f);
                    break;
                default:
                    body = MergeRecord(body, new RecordValue(entry.Origin, new[] { entry }), bodySchema, prov, "", 1, written, mixinId);
                    break;
            }
        }
    }

    private RRecord? RemovePath(RRecord record, IReadOnlyList<string> path, int index, Origin origin, string owner, RecordSchema? schema)
    {
        var name = path[index];
        if (!record.Fields.TryGetValue(name, out var existing))
        {
            _d.Error("K3004", $"cannot remove `{string.Join(".", path)}` from `{owner}`: no earlier layer provides it", origin.File, origin.Span);
            return null;
        }

        if (index == path.Count - 1)
        {
            var fs = schema?.Find(name);
            if (fs is { Required: true })
            {
                _d.Error("K3007", $"cannot remove required field `{string.Join(".", path)}` from `{owner}`", origin.File, origin.Span);
                return null;
            }

            return record with { Fields = record.Fields.Without(name) };
        }

        if (existing is not RRecord nested)
        {
            _d.Error("K3004", $"`{string.Join(".", path.Take(index + 1))}` is not a record", origin.File, origin.Span);
            return null;
        }

        var nestedSchema = schema?.Find(name) is { RecordId: { } rid } ? _schema.FindRecord(rid) : null;
        var updated = RemovePath(nested, path, index + 1, origin, owner, nestedSchema);
        return updated is null ? null : record with { Fields = record.Fields.With(name, updated) };
    }

    // ----- merging -----

    private RRecord MergeRecord(RRecord existing, RecordValue authored, RecordSchema? schema, Provenance prov, string pathPrefix, int nesting, Dictionary<string, string>? written, string? mixinId)
    {
        if (nesting > MaxNesting)
        {
            _d.Error("K3013", $"records nested deeper than {MaxNesting}", authored.Origin.File, authored.Origin.Span);
            return existing;
        }

        var fields = existing.Fields;
        foreach (var entry in authored.Entries)
        {
            switch (entry)
            {
                case FieldEntry fe:
                {
                    var fs = schema?.Find(fe.Name);
                    if (fs is null && schema is { Closed: true } && !_schema.Permissive)
                    {
                        var suggestion = Suggest(fe.Name, schema.Fields.Select(f => f.Name));
                        _d.Error("K2005", $"unknown field `{fe.Name}` in `{schema.Id}`{(suggestion is null ? "" : $"; did you mean `{suggestion}`?")}", fe.Origin.File, fe.Origin.Span);
                        continue;
                    }

                    var path = pathPrefix.Length == 0 ? fe.Name : pathPrefix + "." + fe.Name;
                    fields.TryGetValue(fe.Name, out var current);
                    var updated = ApplyField(current, fe, fs, prov, path, nesting, written, mixinId);
                    if (updated is not null)
                    {
                        fields = fields.With(fe.Name, updated);
                    }

                    break;
                }

                case KeyedEntry ke:
                {
                    var fs = schema?.Find(ke.Name);
                    if (!fields.TryGetValue(ke.Name, out var current) || current is not RList list)
                    {
                        _d.Error("K3004", $"`{ke.Name}[{ke.Key}]`: no earlier layer provides the list `{ke.Name}`", ke.Origin.File, ke.Origin.Span);
                        continue;
                    }

                    if (list.Strategy != ListStrategy.Keyed)
                    {
                        _d.Error("K3005", $"`{ke.Name}[{ke.Key}]` needs a keyed list; `{ke.Name}` is {list.Strategy.ToString().ToLowerInvariant()}", ke.Origin.File, ke.Origin.Span);
                        continue;
                    }

                    var idx = IndexOfKey(list, ke.Key);
                    if (idx < 0)
                    {
                        _d.Error("K3004", $"`{ke.Name}[{ke.Key}]`: no item with {list.KeyField} `{ke.Key}` exists in earlier layers", ke.Origin.File, ke.Origin.Span);
                        continue;
                    }

                    var elementSchema = fs?.Element?.RecordId is { } rid ? _schema.FindRecord(rid) : null;
                    var merged = MergeRecord((RRecord)list.Items[idx], ke.Body, elementSchema, prov with { Origin = ke.Origin }, $"{pathPrefix}{(pathPrefix.Length == 0 ? "" : ".")}{ke.Name}[{ke.Key}]", nesting + 1, written, mixinId);
                    var items = list.Items.ToList();
                    items[idx] = merged;
                    fields = fields.With(ke.Name, list with { Items = items, Provenance = prov with { Origin = ke.Origin } });
                    break;
                }

                case ComponentEntry or RemovalEntry or HookEntry or FunctionEntry:
                    _d.Error("K1002", "components, removals, hooks and functions are only allowed at the top level of an object or mixin", entry.Origin.File, entry.Origin.Span);
                    break;
            }
        }

        return existing with { Fields = fields, Provenance = prov with { Origin = authored.Origin } };
    }

    private ResolvedValue? ApplyField(ResolvedValue? current, FieldEntry fe, FieldSchema? fs, Provenance prov, string path, int nesting, Dictionary<string, string>? written, string? mixinId)
    {
        var p = prov with { Origin = fe.Origin };
        switch (fe.Op)
        {
            case FieldOp.Assign:
            {
                if (current is RRecord cr && fe.Value is RecordValue rv)
                {
                    return MergeRecord(cr, rv, fs?.RecordId is { } rid ? _schema.FindRecord(rid) : null, p, path, nesting + 1, written, mixinId);
                }

                var lowered = Lower(fe.Value, fs, p, current, nesting);
                if (lowered is null)
                {
                    return null;
                }

                if (current is RList cl && lowered is RList nl)
                {
                    switch (cl.Strategy)
                    {
                        case ListStrategy.Atomic:
                            if (cl.Items.Count > 0 && cl.Provenance.Layer != LayerKind.Default)
                            {
                                _d.Warning("K3011", $"`{path}` replaces an inherited list of {cl.Items.Count} item(s); write `=!` to make the replacement explicit", fe.Origin.File, fe.Origin.Span);
                            }

                            return nl with { Shadowed = new[] { cl } };
                        case ListStrategy.Set:
                            return AddToSet(cl, nl, p);
                        case ListStrategy.Keyed:
                            return MergeKeyed(cl, nl, p, path, fs, nesting, written, mixinId, fe.Origin);
                        case ListStrategy.Append:
                            return cl with { Items = cl.Items.Concat(nl.Items).ToList(), Provenance = p };
                    }
                }

                if (current is not null && current.GetType() != lowered.GetType() && current is not RList && lowered is not RList)
                {
                    _d.Error("K4001", $"`{path}` is {Describe(current)} in an earlier layer but {Describe(lowered)} here", fe.Origin.File, fe.Origin.Span);
                    return null;
                }

                if (written is not null && mixinId is not null && lowered is not RRecord && lowered is not RList)
                {
                    if (written.TryGetValue(path, out var other) && other != mixinId)
                    {
                        _d.Warning("K3010", $"mixins `{other}` and `{mixinId}` both set `{path}`; the later one wins", fe.Origin.File, fe.Origin.Span);
                    }

                    written[path] = mixinId;
                }

                return current is null ? lowered : lowered with { Shadowed = Prepend(current, current.Shadowed) };
            }

            case FieldOp.Replace:
            {
                if (fe.Value is not (RecordValue or ListValue))
                {
                    _d.Error("K3005", $"`=!` replaces records or lists; `{path}` gets {Describe(fe.Value)}", fe.Origin.File, fe.Origin.Span);
                    return null;
                }

                var lowered = Lower(fe.Value, fs, p, null, nesting);
                return lowered is null ? null : current is null ? lowered : lowered with { Shadowed = new[] { current } };
            }

            case FieldOp.Add or FieldOp.Remove:
            {
                if (fe.Value is not ListValue)
                {
                    _d.Error("K3005", $"`{(fe.Op == FieldOp.Add ? "+=" : "-=")}` needs a list on the right-hand side", fe.Origin.File, fe.Origin.Span);
                    return null;
                }

                if (current is null)
                {
                    if (fe.Op == FieldOp.Remove)
                    {
                        _d.Error("K3004", $"cannot remove from `{path}`: no earlier layer provides it", fe.Origin.File, fe.Origin.Span);
                        return null;
                    }

                    var created = Lower(fe.Value, fs, p, null, nesting) as RList;
                    if (created is { Strategy: ListStrategy.Atomic })
                    {
                        _d.Error("K3005", $"`+=` is not allowed on `{path}`: the list is atomic", fe.Origin.File, fe.Origin.Span);
                        return null;
                    }

                    return created;
                }

                if (current is not RList list)
                {
                    _d.Error("K3005", $"`{path}` is not a list", fe.Origin.File, fe.Origin.Span);
                    return null;
                }

                // `-=` on a keyed list takes keys (scalars), so the element schema does not apply.
                var rhs = Lower(fe.Value, fe.Op == FieldOp.Remove && list.Strategy == ListStrategy.Keyed ? null : fs, p, list, nesting) as RList;
                if (rhs is null)
                {
                    return null;
                }

                if (fe.Op == FieldOp.Add)
                {
                    return list.Strategy switch
                    {
                        ListStrategy.Set => AddToSet(list, rhs, p),
                        ListStrategy.Keyed => AddKeyed(list, rhs, p, path, fe.Origin),
                        ListStrategy.Append => list with { Items = list.Items.Concat(rhs.Items).ToList(), Provenance = p },
                        _ => Fail("K3005", $"`+=` is not allowed on `{path}`: the list is atomic", fe.Origin),
                    };
                }

                return list.Strategy switch
                {
                    ListStrategy.Set => RemoveFromSet(list, rhs, p, path, fe.Origin),
                    ListStrategy.Keyed => RemoveKeyed(list, rhs, p, path, fe.Origin),
                    _ => Fail("K3005", $"`-=` is not allowed on `{path}`: the list is {list.Strategy.ToString().ToLowerInvariant()}", fe.Origin),
                };
            }
        }

        return null;
    }

    private ResolvedValue? Fail(string code, string message, Origin origin)
    {
        _d.Error(code, message, origin.File, origin.Span);
        return null;
    }

    private static IReadOnlyList<ResolvedValue> Prepend(ResolvedValue head, IReadOnlyList<ResolvedValue> tail)
    {
        var list = new List<ResolvedValue>(tail.Count + 1) { head };
        list.AddRange(tail);
        return list;
    }

    private static int IndexOfKey(RList list, string key)
    {
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (RList.KeyOf(list.Items[i], list.KeyField) == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static RList AddToSet(RList set, RList incoming, Provenance p)
    {
        var items = set.Items.ToList();
        foreach (var item in incoming.Items)
        {
            if (items.All(x => x.CanonicalText != item.CanonicalText))
            {
                items.Add(item);
            }
        }

        return set with { Items = items, Provenance = p };
    }

    private RList? RemoveFromSet(RList set, RList incoming, Provenance p, string path, Origin origin)
    {
        var items = set.Items.ToList();
        foreach (var item in incoming.Items)
        {
            var idx = items.FindIndex(x => x.CanonicalText == item.CanonicalText);
            if (idx < 0)
            {
                _d.Error("K3004", $"cannot remove `{item.CanonicalText}` from `{path}`: no earlier layer provides it", origin.File, origin.Span);
                return null;
            }

            items.RemoveAt(idx);
        }

        return set with { Items = items, Provenance = p };
    }

    private RList? AddKeyed(RList list, RList incoming, Provenance p, string path, Origin origin)
    {
        var items = list.Items.ToList();
        foreach (var item in incoming.Items)
        {
            var key = RList.KeyOf(item, list.KeyField);
            if (key is null)
            {
                _d.Error("K4001", $"items of `{path}` need a `{list.KeyField}` field", origin.File, origin.Span);
                return null;
            }

            if (IndexOfKey(list, key) >= 0)
            {
                _d.Error("K3006", $"`{path}` already has an item with {list.KeyField} `{key}`; use `{path}[{key}] {{ }}` to change it", origin.File, origin.Span);
                return null;
            }

            items.Add(item);
        }

        return list with { Items = items, Provenance = p };
    }

    private RList? RemoveKeyed(RList list, RList incoming, Provenance p, string path, Origin origin)
    {
        var items = list.Items.ToList();
        foreach (var item in incoming.Items)
        {
            var key = item is RRecord ? RList.KeyOf(item, list.KeyField) : RList.KeyText(item);
            var idx = key is null ? -1 : items.FindIndex(x => RList.KeyOf(x, list.KeyField) == key);
            if (idx < 0)
            {
                _d.Error("K3004", $"cannot remove `{key}` from `{path}`: no item with that {list.KeyField} exists in earlier layers", origin.File, origin.Span);
                return null;
            }

            items.RemoveAt(idx);
        }

        return list with { Items = items, Provenance = p };
    }

    private RList? MergeKeyed(RList list, RList incoming, Provenance p, string path, FieldSchema? fs, int nesting, Dictionary<string, string>? written, string? mixinId, Origin origin)
    {
        var items = list.Items.ToList();
        var elementSchema = fs?.Element?.RecordId is { } rid ? _schema.FindRecord(rid) : null;
        foreach (var item in incoming.Items)
        {
            var key = RList.KeyOf(item, list.KeyField);
            if (key is null)
            {
                _d.Error("K4001", $"items of `{path}` need a `{list.KeyField}` field", origin.File, origin.Span);
                return null;
            }

            var idx = IndexOfKey(list, key);
            if (idx >= 0 && item is RRecord ir)
            {
                // Merge authored record over the existing one: re-lower through the entries would lose provenance, so merge resolved field-wise.
                var existing = (RRecord)items[idx];
                var fields = existing.Fields;
                foreach (var kv in ir.Fields)
                {
                    fields = fields.With(kv.Key, kv.Value);
                }

                items[idx] = existing with { Fields = fields, Provenance = p };
            }
            else
            {
                items.Add(item);
            }
        }

        return list with { Items = items, Provenance = p };
    }

    // ----- lowering authored values -----

    private ResolvedValue? Lower(Value value, FieldSchema? fs, Provenance p, ResolvedValue? current, int nesting)
    {
        var prov = p with { Origin = value.Origin };
        switch (value)
        {
            case ExprValue ev:
            {
                var folded = ConstEvaluator.Evaluate(ev, _d);
                return folded is null ? null : Lower(folded, fs, p, current, nesting);
            }

            case IntValue iv:
                if (fs is { Kind: ValueKind.Duration })
                {
                    return new RDuration(prov, iv.Number);
                }

                return Check(fs, ValueKind.Int, value) ? new RInt(prov, iv.Number) : null;
            case DurationValue dv:
                return Check(fs, ValueKind.Duration, value) ? new RDuration(prov, dv.Minutes) : null;
            case TextValue tv:
                return Check(fs, ValueKind.Text, value) ? new RText(prov, tv.Text) : null;
            case BoolValue bv:
                return Check(fs, ValueKind.Bool, value) ? new RBool(prov, bv.Flag) : null;
            case EnumValue en:
            {
                if (fs is { Kind: ValueKind.Enum, EnumId: { } eid } && _schema.FindEnum(eid) is { } e && !e.Values.Contains(en.Name))
                {
                    _d.Error("K4003", $"`{en.Name}` is not a value of enum `{eid}` ({string.Join(", ", e.Values)})", value.Origin.File, value.Origin.Span);
                    return null;
                }

                return Check(fs, ValueKind.Enum, value) ? new REnum(prov, en.Name) : null;
            }

            case RefValue rv:
            {
                var expected = rv.Type == "asset" ? ValueKind.Asset : ValueKind.Reference;
                return Check(fs, expected, value) ? new RRef(prov, rv.Type, rv.Module, rv.Id) : null;
            }

            case RecordValue rec:
            {
                if (!Check(fs, ValueKind.Record, value))
                {
                    return null;
                }

                var recordSchema = fs?.RecordId is { } rid ? _schema.FindRecord(rid) : null;
                var start = recordSchema is null ? RRecord.Empty(prov) : Defaults(recordSchema, value.Origin);
                return MergeRecord(start, rec, recordSchema, p, "", nesting + 1, null, null);
            }

            case ListValue lv:
            {
                if (!Check(fs, ValueKind.List, value))
                {
                    return null;
                }

                var items = new List<ResolvedValue>(lv.Items.Count);
                foreach (var item in lv.Items)
                {
                    var lowered = Lower(item, fs?.Element, p, null, nesting + 1);
                    if (lowered is not null)
                    {
                        items.Add(lowered);
                    }
                }

                var strategy = fs?.ListStrategy ?? (current as RList)?.Strategy ?? Infer(items);
                var keyField = fs?.KeyField ?? (current as RList)?.KeyField ?? "id";
                if (strategy == ListStrategy.Keyed)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in items)
                    {
                        var key = RList.KeyOf(item, keyField);
                        if (key is not null && !seen.Add(key))
                        {
                            _d.Error("K3006", $"duplicate {keyField} `{key}` in list", value.Origin.File, value.Origin.Span);
                        }
                    }
                }

                return new RList(prov, items, strategy, keyField);
            }

            default:
                return null;
        }
    }

    private static ListStrategy Infer(IReadOnlyList<ResolvedValue> items)
    {
        if (items.Count == 0)
        {
            return ListStrategy.Atomic;
        }

        if (items.All(i => i is RRecord r && r.Fields.TryGetValue("id", out _)))
        {
            return ListStrategy.Keyed;
        }

        if (items.All(i => i is REnum or RText or RRef))
        {
            return ListStrategy.Set;   // tags, references, keys: union by default (KEDO-02 R10)
        }

        return ListStrategy.Atomic;
    }

    private bool Check(FieldSchema? fs, ValueKind actual, Value value)
    {
        if (fs is null || fs.Kind == ValueKind.Any || fs.Kind == actual)
        {
            return true;
        }

        _d.Error("K4001", $"field `{fs.Name}` expects {fs.Kind.ToString().ToLowerInvariant()}, found {Describe(value)}", value.Origin.File, value.Origin.Span);
        return false;
    }

    private static string Describe(Value v) => v switch
    {
        IntValue => "an integer",
        DurationValue => "a duration",
        TextValue => "text",
        BoolValue => "a boolean",
        EnumValue e => $"the enum value `{e.Name}`",
        RefValue r => $"the reference `{r}`",
        ListValue => "a list",
        RecordValue => "a record",
        _ => "an expression",
    };

    private static string Describe(ResolvedValue v) => v switch
    {
        RInt => "an integer",
        RDuration => "a duration",
        RText => "text",
        RBool => "a boolean",
        REnum => "an enum value",
        RRef => "a reference",
        RList => "a list",
        RRecord => "a record",
        _ => "a value",
    };

    private static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = 3;
        foreach (var c in candidates)
        {
            var d = Levenshtein(name, c);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }

        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    // ----- stage B -----

    private void ValidateStageB(ResolvedCatalog catalog)
    {
        foreach (var obj in catalog.Objects)
        {
            if (obj.IsAbstract)
            {
                continue;
            }

            var before = _d.Items.Count;
            var typeSchema = _schema.FindType(obj.Type);
            ValidateRecord(obj.Body, typeSchema?.Body, obj, obj.Key, catalog);
            foreach (var kv in obj.Components)
            {
                ValidateRecord((RRecord)kv.Value, _schema.FindComponent(kv.Key), obj, $"{obj.Key}.{kv.Key}", catalog);
            }

            if (_d.Items.Skip(before).Any(d => d.IsError))
            {
                obj.HasErrors = true;
            }
        }
    }

    private void ValidateRecord(RRecord record, RecordSchema? schema, ResolvedObject obj, string path, ResolvedCatalog catalog)
    {
        if (schema is not null)
        {
            foreach (var f in schema.Fields)
            {
                if (f.Required && !record.Fields.TryGetValue(f.Name, out _))
                {
                    _d.Error("K4002", $"`{path}` is missing required field `{f.Name}`", obj.Declaration.Origin.File, obj.Declaration.Origin.Span);
                }
            }
        }

        foreach (var kv in record.Fields)
        {
            var fs = schema?.Find(kv.Key);
            ValidateValue(kv.Value, fs, obj, $"{path}.{kv.Key}", catalog);
        }
    }

    private void ValidateValue(ResolvedValue value, FieldSchema? fs, ResolvedObject obj, string path, ResolvedCatalog catalog)
    {
        var origin = value.Provenance.Origin;
        switch (value)
        {
            case RInt i when fs is not null:
                if ((fs.Min is { } min && i.Number < min) || (fs.Max is { } max && i.Number > max))
                {
                    _d.Add(WithProvenance("K4004", $"`{path}` = {i.Number} is out of range [{fs.Min?.ToString() ?? "-∞"}, {fs.Max?.ToString() ?? "∞"}]", obj, value));
                }

                break;
            case RDuration du when fs is not null:
                if ((fs.Min is { } dmin && du.Minutes < dmin) || (fs.Max is { } dmax && du.Minutes > dmax))
                {
                    _d.Add(WithProvenance("K4004", $"`{path}` = {du.Minutes}min is out of range", obj, value));
                }

                break;
            case RRef r:
            {
                var key = $"{r.Type}:{r.Id}";
                if (!catalog.ByKey.TryGetValue(key, out var target))
                {
                    _d.Add(WithProvenance("K4005", $"`{path}` references `{r.CanonicalText}`, which does not exist", obj, value));
                }
                else if (target.IsAbstract)
                {
                    _d.Add(WithProvenance("K4005", $"`{path}` references abstract `{r.CanonicalText}`; only concrete objects can be referenced", obj, value));
                }
                else if (fs is { Kind: ValueKind.Reference, TargetType: { } tt } && r.Type != tt)
                {
                    _d.Add(WithProvenance("K4006", $"`{path}` must reference a `{tt}`, found `{r.CanonicalText}`", obj, value));
                }

                break;
            }

            case RList l:
                foreach (var item in l.Items)
                {
                    ValidateValue(item, fs?.Element, obj, path + "[]", catalog);
                }

                break;
            case RRecord rec:
                ValidateRecord(rec, fs?.RecordId is { } rid ? _schema.FindRecord(rid) : null, obj, path, catalog);
                break;
        }
    }

    private static Diagnostic WithProvenance(string code, string message, ResolvedObject obj, ResolvedValue value)
    {
        var o = value.Provenance.Origin;
        var labels = new List<DiagnosticLabel>();
        if (value.Provenance.Layer != LayerKind.Own)
        {
            labels.Add(new DiagnosticLabel(obj.Declaration.Origin.File, obj.Declaration.Origin.Span, $"inherited by `{obj.Key}` via {value.Provenance}"));
        }

        return new Diagnostic(code, Severity.Error, message, o.File, o.Span, labels, Array.Empty<string>());
    }

    /// <summary>Explains where a value came from: winner and shadowed candidates (KEDO-02 R17).</summary>
    public static string Explain(ResolvedCatalog catalog, string key, string path)
    {
        if (!catalog.ByKey.TryGetValue(key, out var obj))
        {
            return $"no object `{key}`";
        }

        ResolvedValue? value;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && obj.Components.TryGetValue(segments[0], out var comp))
        {
            value = comp;
            segments = segments[1..];
        }
        else
        {
            value = obj.Body;
        }

        foreach (var seg in segments)
        {
            var name = seg;
            string? itemKey = null;
            var bracket = seg.IndexOf('[');
            if (bracket >= 0 && seg.EndsWith(']'))
            {
                name = seg[..bracket];
                itemKey = seg[(bracket + 1)..^1];
            }

            if (value is RRecord r && r.Fields.TryGetValue(name, out var next))
            {
                value = next;
            }
            else
            {
                return $"`{key}` has no `{path}`";
            }

            if (itemKey is not null)
            {
                if (value is RList list && IndexOfKey(list, itemKey) is var idx && idx >= 0)
                {
                    value = list.Items[idx];
                }
                else
                {
                    return $"`{key}.{path}`: no item `{itemKey}`";
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"{key}.{path} = {value!.CanonicalText} from {value.Provenance}");
        foreach (var shadowed in value.Shadowed)
        {
            sb.Append($"\n  shadowed: {shadowed.CanonicalText} from {shadowed.Provenance}");
        }

        return sb.ToString();
    }
}
