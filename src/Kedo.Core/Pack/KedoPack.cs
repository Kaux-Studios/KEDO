using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Kedo.Runtime;

namespace Kedo.Pack;

/// <summary>
/// The compiled artifact (ADR-0007 D7): little-endian sectioned binary. Header = magic, format version, language major,
/// catalog hash, section table. Sections: strings, objects, programs. Trailer: XxHash128 of everything before it.
/// Sources are the truth; a pack with another format version is rejected and must be rebuilt.
/// </summary>
public sealed class KedoPack
{
    /// <summary>Magic bytes.</summary>
    public static ReadOnlySpan<byte> Magic => "KEDO"u8;

    /// <summary>Current pack format. 2 adds the decimal tag (ADR-0008); format 1 packs still load.</summary>
    public const int FormatVersion = 2;

    /// <summary>Oldest pack format this runtime reads.</summary>
    public const int MinFormatVersion = 1;

    /// <summary>Section id of the string table.</summary>
    public const int SectionStrings = 1;
    /// <summary>Section id of the objects.</summary>
    public const int SectionObjects = 2;
    /// <summary>Section id of the programs.</summary>
    public const int SectionPrograms = 3;

    private readonly Dictionary<string, PackObject> _byKey;

    private KedoPack(string catalogHash, string toolchain, IReadOnlyList<PackObject> objects, IReadOnlyList<BehaviorProgram> programs)
    {
        CatalogHash = catalogHash;
        Toolchain = toolchain;
        Objects = objects;
        Programs = programs;
        _byKey = objects.ToDictionary(o => o.Key, o => o, StringComparer.Ordinal);
    }

    /// <summary>Catalog hash computed by the compiler (hex).</summary>
    public string CatalogHash { get; }

    /// <summary>Toolchain version that produced the pack.</summary>
    public string Toolchain { get; }

    /// <summary>Concrete objects, ordered by type then id.</summary>
    public IReadOnlyList<PackObject> Objects { get; }

    /// <summary>Behaviour programs.</summary>
    public IReadOnlyList<BehaviorProgram> Programs { get; }

    /// <summary>Object by <c>type:id</c> or null.</summary>
    public PackObject? Find(string key) => _byKey.TryGetValue(key, out var o) ? o : null;

    /// <summary>Object by type and id or null.</summary>
    public PackObject? Find(string type, string id) => Find(type + ":" + id);

    /// <summary>Objects of a type.</summary>
    public IEnumerable<PackObject> OfType(string type) => Objects.Where(o => o.Type == type);

    /// <summary>Program of an object, or null.</summary>
    public BehaviorProgram? ProgramOf(PackObject obj) => obj.ProgramIndex >= 0 ? Programs[obj.ProgramIndex] : null;

    /// <summary>Loads and verifies a pack. Throws <see cref="InvalidDataException"/> on corruption or version mismatch.</summary>
    public static KedoPack Load(byte[] bytes)
    {
        var r = new Reader(bytes);
        if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("not a KEDO pack");
        }

        r.Pos = 4;
        var format = r.U32();
        if (format < MinFormatVersion || format > FormatVersion)
        {
            throw new InvalidDataException($"pack format {format} is not supported by this runtime (format {FormatVersion}); rebuild from source");
        }

        var major = r.U32();
        if (major != KedoVersion.LanguageMajor)
        {
            throw new InvalidDataException($"pack was built for language {major}; this runtime is language {KedoVersion.LanguageMajor}");
        }

        var trailerStart = bytes.Length - 16;
        var expected = bytes.AsSpan(trailerStart, 16);
        Span<byte> actual = stackalloc byte[16];
        XxHash128.Hash(bytes.AsSpan(0, trailerStart), actual);
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException("pack is corrupted (hash mismatch)");
        }

        var toolchain = r.Str();
        var catalogHash = r.Str();
        var sectionCount = r.U32();
        var sections = new Dictionary<int, (int offset, int length)>();
        for (var i = 0; i < sectionCount; i++)
        {
            var id = (int)r.U32();
            var offset = (int)r.U64();
            var length = (int)r.U64();
            sections[id] = (offset, length);
        }

        // Strings.
        r.Pos = sections[SectionStrings].offset;
        var stringCount = (int)r.U32();
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
        {
            strings[i] = r.Str();
        }

        // Programs (objects reference them by index).
        r.Pos = sections[SectionPrograms].offset;
        var programCount = (int)r.U32();
        var programs = new List<BehaviorProgram>(programCount);
        for (var i = 0; i < programCount; i++)
        {
            var behaviorId = strings[r.U32()];
            var version = (int)r.U32();
            var hash = strings[r.U32()];
            var codeLen = (int)r.U32();
            var code = new uint[codeLen];
            for (var c = 0; c < codeLen; c++) code[c] = r.U32();
            var constLen = (int)r.U32();
            var constants = new long[constLen];
            for (var c = 0; c < constLen; c++) constants[c] = r.I64();
            var strLen = (int)r.U32();
            var pstrings = new string[strLen];
            for (var c = 0; c < strLen; c++) pstrings[c] = strings[r.U32()];
            var fnLen = (int)r.U32();
            var functions = new List<FunctionInfo>(fnLen);
            for (var c = 0; c < fnLen; c++)
            {
                functions.Add(new FunctionInfo(strings[r.U32()], (int)r.U32(), (int)r.U32(), (int)r.U32()));
            }

            var hookLen = (int)r.U32();
            var hooks = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var c = 0; c < hookLen; c++)
            {
                hooks[strings[r.U32()]] = (int)r.U32();
            }

            var awaitLen = (int)r.U32();
            var awaits = new List<AwaitSite>(awaitLen);
            for (var c = 0; c < awaitLen; c++)
            {
                awaits.Add(new AwaitSite(strings[r.U32()], (int)r.U32()));
            }

            var mapLen = (int)r.U32();
            var map = new string[mapLen];
            for (var c = 0; c < mapLen; c++) map[c] = strings[r.U32()];
            programs.Add(new BehaviorProgram(behaviorId, version, hash, code, constants, pstrings, functions, hooks, awaits, map));
        }

        // Objects.
        r.Pos = sections[SectionObjects].offset;
        var objectCount = (int)r.U32();
        var objects = new List<PackObject>(objectCount);
        for (var i = 0; i < objectCount; i++)
        {
            var type = strings[r.U32()];
            var id = strings[r.U32()];
            var hash = strings[r.U32()];
            var body = (PackRecord)ReadValue(ref r, strings);
            var compCount = (int)r.U32();
            var components = new Dictionary<string, PackRecord>(StringComparer.Ordinal);
            for (var c = 0; c < compCount; c++)
            {
                var ctype = strings[r.U32()];
                components[ctype] = (PackRecord)ReadValue(ref r, strings);
            }

            var hookCount = (int)r.U32();
            var hookIds = new string[hookCount];
            for (var c = 0; c < hookCount; c++) hookIds[c] = strings[r.U32()];
            var programIndex = (int)r.U32() - 1;
            objects.Add(new PackObject(type, id, hash, body, components, hookIds, programIndex));
        }

        return new KedoPack(catalogHash, toolchain, objects, programs);
    }

    private static PackValue ReadValue(ref Reader r, string[] strings)
    {
        var tag = (PackTag)r.U8();
        switch (tag)
        {
            case PackTag.Int: return new PackInt(r.I64(), false);
            case PackTag.Duration: return new PackInt(r.I64(), true);
            case PackTag.Bool: return new PackBool(r.U8() != 0);
            case PackTag.Decimal: return new PackDecimal(r.I64(), r.U8());
            case PackTag.Text or PackTag.Enum or PackTag.Ref: return new PackSymbol(strings[r.U32()], tag);
            case PackTag.List:
            {
                var strategy = r.U8();
                var key = strings[r.U32()];
                var count = (int)r.U32();
                var items = new PackValue[count];
                for (var i = 0; i < count; i++) items[i] = ReadValue(ref r, strings);
                return new PackList(items, strategy, key);
            }

            case PackTag.Record:
            {
                var count = (int)r.U32();
                var order = new string[count];
                var fields = new Dictionary<string, PackValue>(count, StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var name = strings[r.U32()];
                    order[i] = name;
                    fields[name] = ReadValue(ref r, strings);
                }

                return new PackRecord(fields, order);
            }

            default:
                throw new InvalidDataException($"invalid value tag {tag}");
        }
    }

    /// <summary>Little-endian cursor over the pack bytes.</summary>
    internal ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;

        public Reader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            Pos = 0;
        }

        public int Pos;

        public byte U8() => _bytes[Pos++];

        public uint U32()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Slice(Pos, 4));
            Pos += 4;
            return v;
        }

        public ulong U64()
        {
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.Slice(Pos, 8));
            Pos += 8;
            return v;
        }

        public long I64()
        {
            var v = BinaryPrimitives.ReadInt64LittleEndian(_bytes.Slice(Pos, 8));
            Pos += 8;
            return v;
        }

        public string Str()
        {
            var len = (int)U32();
            var s = Encoding.UTF8.GetString(_bytes.Slice(Pos, len));
            Pos += len;
            return s;
        }
    }
}
