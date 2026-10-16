using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Diagnostics;
using Kedo.Schema;

namespace Kedo.Compiler.Schema;

/// <summary>Loads a game schema written in KEDO (see <see cref="MetaSchema"/>) into a <see cref="SchemaRegistry"/>.</summary>
public static class SchemaLoader
{
    /// <summary>Resolves schema declarations against the meta schema and builds the registry. Errors are reported in the bag.</summary>
    public static SchemaRegistry Load(IEnumerable<FileDeclarations> files, DiagnosticBag diagnostics)
    {
        var catalog = Resolver.Resolve(files, MetaSchema.Build(), diagnostics);
        var registry = new SchemaRegistry();

        foreach (var obj in catalog.Concrete.Where(o => o.Type == "kedo_enum"))
        {
            registry.AddEnum(new EnumSchema(obj.Id, Strings(obj.Body, "values")));
        }

        foreach (var obj in catalog.Concrete.Where(o => o.Type == "kedo_record"))
        {
            registry.AddRecord(Record(obj.Id, obj.Body));
        }

        foreach (var obj in catalog.Concrete.Where(o => o.Type == "kedo_component"))
        {
            registry.AddComponent(Record(obj.Id, obj.Body));
        }

        foreach (var obj in catalog.Concrete.Where(o => o.Type == "kedo_hook"))
        {
            var context = new RecordSchema(obj.Id + ".context");
            foreach (var f in Fields(obj.Body, "context"))
            {
                context.Add(f);
            }

            registry.AddHook(new HookSchema(obj.Id, context)
            {
                AllowsAwait = Bool(obj.Body, "allows_await") ?? true,
                AllowsEffects = Bool(obj.Body, "allows_effects") ?? true,
                Doc = Text(obj.Body, "doc"),
            });
        }

        foreach (var obj in catalog.Concrete.Where(o => o.Type == "kedo_type"))
        {
            var components = obj.Body.Fields.TryGetValue("components", out _) ? new HashSet<string>(Strings(obj.Body, "components"), StringComparer.Ordinal) : null;
            var hooks = obj.Body.Fields.TryGetValue("hooks", out _) ? new HashSet<string>(Strings(obj.Body, "hooks"), StringComparer.Ordinal) : null;
            registry.AddType(new TypeSchema(obj.Id, Record(obj.Id, obj.Body)) { AllowedComponents = components, AllowedHooks = hooks, Doc = Text(obj.Body, "doc") });
        }

        // Cross-checks the meta schema cannot express: enum/record/component/hook ids referenced by descriptors must exist.
        foreach (var type in registry.Types.Values)
        {
            CheckRefs(type.Body, registry, diagnostics, catalog, type.Id);
            foreach (var c in type.AllowedComponents ?? Enumerable.Empty<string>())
            {
                if (registry.FindComponent(c) is null)
                {
                    Report(diagnostics, catalog, type.Id, $"type `{type.Id}` allows unknown component `{c}`");
                }
            }

            foreach (var h in type.AllowedHooks ?? Enumerable.Empty<string>())
            {
                if (registry.FindHook(h) is null)
                {
                    Report(diagnostics, catalog, type.Id, $"type `{type.Id}` allows unknown hook `{h}`");
                }
            }
        }

        foreach (var c in registry.Components.Values)
        {
            CheckRefs(c, registry, diagnostics, catalog, c.Id);
        }

        foreach (var rec in registry.Records.Values)
        {
            CheckRefs(rec, registry, diagnostics, catalog, rec.Id);
        }

        return registry;
    }

    private static void Report(DiagnosticBag d, ResolvedCatalog catalog, string ownerId, string message)
    {
        var obj = catalog.Objects.FirstOrDefault(o => o.Id == ownerId);
        var origin = obj?.Declaration.Origin ?? default;
        d.Error("K2001", message, origin.File, origin.Span);
    }

    private static void CheckRefs(RecordSchema record, SchemaRegistry registry, DiagnosticBag d, ResolvedCatalog catalog, string owner)
    {
        foreach (var f in record.Fields)
        {
            CheckField(f, registry, d, catalog, owner);
        }
    }

    private static void CheckField(FieldSchema f, SchemaRegistry registry, DiagnosticBag d, ResolvedCatalog catalog, string owner)
    {
        if (f.Kind == ValueKind.Enum && f.EnumId is not null && registry.FindEnum(f.EnumId) is null)
        {
            Report(d, catalog, owner, $"field `{f.Name}` of `{owner}` uses unknown enum `{f.EnumId}`");
        }

        if (f.Kind == ValueKind.Record && (f.RecordId is null || registry.FindRecord(f.RecordId) is null))
        {
            Report(d, catalog, owner, $"field `{f.Name}` of `{owner}` uses unknown record `{f.RecordId}`");
        }

        if (f.Kind == ValueKind.List)
        {
            if (f.Element is null)
            {
                Report(d, catalog, owner, $"list field `{f.Name}` of `{owner}` needs an `element` descriptor");
            }
            else
            {
                CheckField(f.Element, registry, d, catalog, owner);
                if (f.ListStrategy == ListStrategy.Keyed && f.Element.Kind != ValueKind.Record)
                {
                    Report(d, catalog, owner, $"keyed list `{f.Name}` of `{owner}` needs record elements");
                }
            }
        }
    }

    private static RecordSchema Record(string id, RRecord body)
    {
        var rec = new RecordSchema(id) { Closed = Bool(body, "closed") ?? true, Doc = Text(body, "doc") };
        foreach (var f in Fields(body, "fields"))
        {
            rec.Add(f);
        }

        return rec;
    }

    private static IEnumerable<FieldSchema> Fields(RRecord body, string listName)
    {
        if (!body.Fields.TryGetValue(listName, out var v) || v is not RList list)
        {
            yield break;
        }

        foreach (var item in list.Items.OfType<RRecord>())
        {
            var f = Field(item);
            if (f is not null)
            {
                yield return f;
            }
        }
    }

    private static FieldSchema? Field(RRecord item)
    {
        var name = Text(item, "name") ?? "item";
        var kindText = Text(item, "kind") ?? "any";
        var kind = kindText switch
        {
            "int" => ValueKind.Int,
            "bool" => ValueKind.Bool,
            "text" => ValueKind.Text,
            "duration" => ValueKind.Duration,
            "enum" => ValueKind.Enum,
            "reference" => ValueKind.Reference,
            "asset" => ValueKind.Asset,
            "record" => ValueKind.Record,
            "list" => ValueKind.List,
            _ => ValueKind.Any,
        };
        var strategy = Text(item, "strategy") switch
        {
            "set" => ListStrategy.Set,
            "keyed" => ListStrategy.Keyed,
            "append" => ListStrategy.Append,
            _ => ListStrategy.Atomic,
        };
        item.Fields.TryGetValue("default", out var def);
        FieldSchema? element = null;
        if (item.Fields.TryGetValue("element", out var el) && el is RRecord er)
        {
            element = Field(er);
        }

        return new FieldSchema(name, kind)
        {
            Required = Bool(item, "required") ?? false,
            Doc = Text(item, "doc"),
            Min = Int(item, "min"),
            Max = Int(item, "max"),
            DefaultInt = def is RInt di ? di.Number : def is RDuration dd ? dd.Minutes : null,
            DefaultBool = def is RBool db ? db.Flag : null,
            DefaultText = def is RText dt ? dt.Text : def is REnum de ? de.Name : null,
            EnumId = Text(item, "enum"),
            TargetType = Text(item, "target"),
            AssetKind = Text(item, "asset_kind"),
            RecordId = Text(item, "record"),
            ListStrategy = strategy,
            KeyField = Text(item, "key") ?? "id",
            Element = element,
        };
    }

    private static string? Text(RRecord r, string name) => r.Fields.TryGetValue(name, out var v) ? v switch { RText t => t.Text, REnum e => e.Name, _ => v.CanonicalText } : null;

    private static bool? Bool(RRecord r, string name) => r.Fields.TryGetValue(name, out var v) && v is RBool b ? b.Flag : null;

    private static long? Int(RRecord r, string name) => r.Fields.TryGetValue(name, out var v) && v is RInt i ? i.Number : null;

    private static string[] Strings(RRecord r, string name)
        => r.Fields.TryGetValue(name, out var v) && v is RList l ? l.Items.Select(i => i is RText t ? t.Text : i.CanonicalText).ToArray() : Array.Empty<string>();
}
