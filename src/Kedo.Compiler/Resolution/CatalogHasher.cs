using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Kedo.Schema;

namespace Kedo.Compiler.Resolution;

/// <summary>
/// Content hashing (KEDO-02 §9, ADR-0007 D8): XxHash128 over a canonical binary form of each resolved object
/// (fields in resolved order, set lists sorted, references by id, no provenance, no annotations); the catalog hash
/// covers the sorted <c>type:id=hash</c> lines of concrete objects.
/// </summary>
public static class CatalogHasher
{
    /// <summary>Computes and stores the hashes of every object and of the catalog.</summary>
    public static void Hash(ResolvedCatalog catalog)
    {
        var lines = new StringBuilder();
        foreach (var obj in catalog.Objects)
        {
            var hasher = new XxHash128();
            WriteString(hasher, obj.Type);
            WriteString(hasher, obj.Id);
            WriteValue(hasher, obj.Body);
            foreach (var kv in obj.Components)
            {
                WriteString(hasher, "component:" + kv.Key);
                WriteValue(hasher, kv.Value);
            }

            obj.Hash = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
            if (!obj.IsAbstract)
            {
                lines.Append(obj.Key).Append('=').Append(obj.Hash).Append('\n');
            }
        }

        catalog.Hash = Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(lines.ToString()))).ToLowerInvariant();
    }

    private static void WriteString(XxHash128 h, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteInt(h, bytes.Length);
        h.Append(bytes);
    }

    private static void WriteInt(XxHash128 h, long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, v);
        h.Append(buf);
    }

    private static void WriteValue(XxHash128 h, ResolvedValue v)
    {
        switch (v)
        {
            case RInt i: h.Append("i"u8); WriteInt(h, i.Number); break;
            case RBool b: h.Append(b.Flag ? "T"u8 : "F"u8); break;
            case RText t: h.Append("s"u8); WriteString(h, t.Text); break;
            case RDuration d: h.Append("d"u8); WriteInt(h, d.Minutes); break;
            case RDecimal m: h.Append("m"u8); WriteInt(h, m.Units); WriteInt(h, m.Scale); break;
            case REnum e: h.Append("e"u8); WriteString(h, e.Name); break;
            case RRef r: h.Append("r"u8); WriteString(h, r.CanonicalText); break;
            case RList l:
            {
                h.Append("l"u8);
                var items = l.Strategy == ListStrategy.Set ? l.Items.OrderBy(i => i.CanonicalText, StringComparer.Ordinal).ToList() : l.Items.ToList();
                WriteInt(h, items.Count);
                foreach (var item in items)
                {
                    WriteValue(h, item);
                }

                break;
            }

            case RRecord rec:
            {
                h.Append("{"u8);
                WriteInt(h, rec.Fields.Count);
                foreach (var kv in rec.Fields)
                {
                    WriteString(h, kv.Key);
                    WriteValue(h, kv.Value);
                }

                break;
            }
        }
    }
}
