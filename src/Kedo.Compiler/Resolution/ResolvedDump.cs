using System.Text;

namespace Kedo.Compiler.Resolution;

/// <summary>Text dump of a resolved catalog for `kedo resolve` and the golden corpus. Deterministic.</summary>
public static class ResolvedDump
{
    /// <summary>One block per object: header, body fields, components, hooks, hash.</summary>
    public static string Dump(ResolvedCatalog catalog, bool withProvenance = false)
    {
        var sb = new StringBuilder();
        foreach (var obj in catalog.Objects)
        {
            DumpObject(sb, obj, withProvenance);
        }

        sb.Append("catalog ").Append(catalog.Hash).Append('\n');
        return sb.ToString();
    }

    /// <summary>Dump of one object.</summary>
    public static string DumpObject(ResolvedObject obj, bool withProvenance = false)
    {
        var sb = new StringBuilder();
        DumpObject(sb, obj, withProvenance);
        return sb.ToString();
    }

    private static void DumpObject(StringBuilder sb, ResolvedObject obj, bool withProvenance)
    {
        {
            sb.Append(obj.IsAbstract ? "abstract " : "").Append(obj.Type).Append(' ').Append(obj.Id);
            if (obj.Chain.Count > 1)
            {
                sb.Append(" <- ").Append(string.Join(" <- ", obj.Chain.Take(obj.Chain.Count - 1).Reverse()));
            }

            if (obj.Mixins.Count > 0)
            {
                sb.Append(" with ").Append(string.Join(", ", obj.Mixins));
            }

            if (obj.HasErrors)
            {
                sb.Append(" [errors]");
            }

            sb.Append('\n');
            WriteRecord(sb, obj.Body, 1, withProvenance);
            foreach (var kv in obj.Components)
            {
                sb.Append("    component ").Append(kv.Key).Append('\n');
                WriteRecord(sb, (RRecord)kv.Value, 2, withProvenance);
            }

            foreach (var hook in obj.Hooks)
            {
                sb.Append("    on ").Append(hook.HookId).Append(" (").Append(hook.Origin.File is null ? "" : Path.GetFileName(hook.Origin.File)).Append(")\n");
            }

            sb.Append("    hash ").Append(obj.Hash).Append('\n');
        }
    }

    private static void WriteRecord(StringBuilder sb, RRecord record, int depth, bool prov)
    {
        foreach (var kv in record.Fields)
        {
            sb.Append(' ', depth * 4).Append(kv.Key);
            if (kv.Value is RRecord nested)
            {
                sb.Append('\n');
                WriteRecord(sb, nested, depth + 1, prov);
                continue;
            }

            if (kv.Value is RList list)
            {
                sb.Append(" [").Append(list.Strategy.ToString().ToLowerInvariant()).Append(']');
                if (list.Items.All(i => i is not RRecord))
                {
                    sb.Append(' ').Append(list.CanonicalText).Append('\n');
                    continue;
                }

                sb.Append('\n');
                foreach (var item in list.Items)
                {
                    if (item is RRecord r)
                    {
                        sb.Append(' ', (depth + 1) * 4).Append("- item\n");
                        WriteRecord(sb, r, depth + 2, prov);
                    }
                    else
                    {
                        sb.Append(' ', (depth + 1) * 4).Append("- ").Append(item.CanonicalText).Append('\n');
                    }
                }

                continue;
            }

            sb.Append(" = ").Append(kv.Value.CanonicalText);
            if (prov)
            {
                sb.Append("   # ").Append(kv.Value.Provenance);
            }

            sb.Append('\n');
        }
    }
}
