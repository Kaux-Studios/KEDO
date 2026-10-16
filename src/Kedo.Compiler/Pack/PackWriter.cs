using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Kedo.Compiler.Resolution;
using Kedo.Pack;
using Kedo.Runtime;

namespace Kedo.Compiler.Pack;

/// <summary>Writes a resolved catalog and its programs as a <see cref="KedoPack"/> (format 1). Only concrete objects are written.</summary>
public static class PackWriter
{
    /// <summary>Serialises the pack.</summary>
    public static byte[] Write(ResolvedCatalog catalog, IReadOnlyList<BehaviorProgram> programs)
    {
        var strings = new StringPool();
        var programIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < programs.Count; i++)
        {
            programIndex[programs[i].BehaviorId] = i;
        }

        // Encode sections into their own buffers first (strings are interned on the way).
        var objects = new MemoryStream();
        var objectCount = 0;
        foreach (var obj in catalog.Concrete)
        {
            objectCount++;
            W32(objects, strings.Id(obj.Type));
            W32(objects, strings.Id(obj.Id));
            W32(objects, strings.Id(obj.Hash));
            WriteValue(objects, obj.Body, strings);
            W32(objects, (uint)obj.Components.Count);
            foreach (var kv in obj.Components)
            {
                W32(objects, strings.Id(kv.Key));
                WriteValue(objects, kv.Value, strings);
            }

            W32(objects, (uint)obj.Hooks.Count);
            foreach (var h in obj.Hooks)
            {
                W32(objects, strings.Id(h.HookId));
            }

            W32(objects, (uint)(programIndex.TryGetValue(obj.Key, out var pi) ? pi + 1 : 0));
        }

        var progs = new MemoryStream();
        foreach (var p in programs)
        {
            W32(progs, strings.Id(p.BehaviorId));
            W32(progs, (uint)p.DefinitionVersion);
            W32(progs, strings.Id(p.DefinitionHash));
            W32(progs, (uint)p.Code.Length);
            foreach (var w in p.Code) W32(progs, w);
            W32(progs, (uint)p.Constants.Length);
            foreach (var c in p.Constants) W64(progs, c);
            W32(progs, (uint)p.Strings.Length);
            foreach (var s in p.Strings) W32(progs, strings.Id(s));
            W32(progs, (uint)p.Functions.Count);
            foreach (var f in p.Functions)
            {
                W32(progs, strings.Id(f.Name));
                W32(progs, (uint)f.Entry);
                W32(progs, (uint)f.RegisterCount);
                W32(progs, (uint)f.ParameterCount);
            }

            W32(progs, (uint)p.Hooks.Count);
            foreach (var kv in p.Hooks.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                W32(progs, strings.Id(kv.Key));
                W32(progs, (uint)kv.Value);
            }

            W32(progs, (uint)p.AwaitSites.Count);
            foreach (var a in p.AwaitSites)
            {
                W32(progs, strings.Id(a.Label));
                W32(progs, (uint)a.Pc);
            }

            W32(progs, (uint)p.SourceMap.Count);
            foreach (var s in p.SourceMap) W32(progs, strings.Id(s));
        }

        var stringsSection = new MemoryStream();
        W32(stringsSection, (uint)strings.Count);
        foreach (var s in strings.Items)
        {
            WStr(stringsSection, s);
        }

        // Assemble: header, section table, sections, trailer.
        var output = new MemoryStream();
        output.Write(KedoPack.Magic);
        W32(output, KedoPack.FormatVersion);
        W32(output, (uint)KedoVersion.LanguageMajor);
        WStr(output, KedoVersion.Current);
        WStr(output, catalog.Hash);
        W32(output, 3);
        var tablePos = output.Position;
        for (var i = 0; i < 3; i++)
        {
            W32(output, 0);
            W64(output, 0);
            W64(output, 0);
        }

        var offsets = new (int id, long offset, long length)[3];
        offsets[0] = (KedoPack.SectionStrings, output.Position, stringsSection.Length);
        stringsSection.WriteTo(output);
        offsets[1] = (KedoPack.SectionObjects, output.Position, objects.Length + 4);
        W32(output, (uint)objectCount);
        objects.WriteTo(output);
        offsets[2] = (KedoPack.SectionPrograms, output.Position, progs.Length + 4);
        W32(output, (uint)programs.Count);
        progs.WriteTo(output);

        var end = output.Position;
        output.Position = tablePos;
        foreach (var (id, offset, length) in offsets)
        {
            W32(output, (uint)id);
            W64(output, offset);
            W64(output, length);
        }

        output.Position = end;
        var body = output.ToArray();
        var trailer = new byte[16];
        XxHash128.Hash(body, trailer);
        var result = new byte[body.Length + 16];
        body.CopyTo(result, 0);
        trailer.CopyTo(result, body.Length);
        return result;
    }

    private static void WriteValue(Stream s, ResolvedValue v, StringPool strings)
    {
        switch (v)
        {
            case RInt i: s.WriteByte((byte)PackTag.Int); W64(s, i.Number); break;
            case RDuration d: s.WriteByte((byte)PackTag.Duration); W64(s, d.Minutes); break;
            case RDecimal m: s.WriteByte((byte)PackTag.Decimal); W64(s, m.Units); s.WriteByte((byte)m.Scale); break;
            case RBool b: s.WriteByte((byte)PackTag.Bool); s.WriteByte(b.Flag ? (byte)1 : (byte)0); break;
            case RText t: s.WriteByte((byte)PackTag.Text); W32(s, strings.Id(t.Text)); break;
            case REnum e: s.WriteByte((byte)PackTag.Enum); W32(s, strings.Id(e.Name)); break;
            case RRef r: s.WriteByte((byte)PackTag.Ref); W32(s, strings.Id(r.CanonicalText)); break;
            case RList l:
                s.WriteByte((byte)PackTag.List);
                s.WriteByte((byte)l.Strategy);
                W32(s, strings.Id(l.KeyField));
                W32(s, (uint)l.Items.Count);
                foreach (var item in l.Items) WriteValue(s, item, strings);
                break;
            case RRecord rec:
                s.WriteByte((byte)PackTag.Record);
                W32(s, (uint)rec.Fields.Count);
                foreach (var kv in rec.Fields)
                {
                    W32(s, strings.Id(kv.Key));
                    WriteValue(s, kv.Value, strings);
                }

                break;
            default:
                s.WriteByte((byte)PackTag.None);
                break;
        }
    }

    private static void W32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void W64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        s.Write(b);
    }

    private static void WStr(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        W32(s, (uint)bytes.Length);
        s.Write(bytes);
    }

    private sealed class StringPool
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _items = new();

        public int Count => _items.Count;

        public IReadOnlyList<string> Items => _items;

        public uint Id(string s)
        {
            if (!_ids.TryGetValue(s, out var id))
            {
                id = (uint)_items.Count;
                _items.Add(s);
                _ids[s] = id;
            }

            return id;
        }
    }
}
