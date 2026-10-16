using System.Text;
using Kedo.Text;

namespace Kedo.Compiler.Syntax;

/// <summary>UTF-8 source of one file with a lazily built line map.</summary>
public sealed class SourceText
{
    private int[]? _lineStarts;

    private SourceText(string? path, byte[] bytes)
    {
        Path = path;
        Bytes = bytes;
    }

    /// <summary>File path or null for in-memory sources.</summary>
    public string? Path { get; }

    /// <summary>Raw UTF-8 bytes (no BOM).</summary>
    public byte[] Bytes { get; }

    /// <summary>Length in bytes.</summary>
    public int Length => Bytes.Length;

    /// <summary>Wraps UTF-8 bytes. A leading BOM is stripped.</summary>
    public static SourceText FromBytes(byte[] bytes, string? path = null)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes.AsSpan(3).ToArray();
        }

        return new SourceText(path, bytes);
    }

    /// <summary>Encodes a string as UTF-8.</summary>
    public static SourceText FromString(string text, string? path = null)
        => new(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Reads a file.</summary>
    public static SourceText FromFile(string path)
        => FromBytes(File.ReadAllBytes(path), path);

    /// <summary>Decodes a span as a string (for diagnostics, JSON output and tests; not on hot paths).</summary>
    public string GetText(TextSpan span) => Encoding.UTF8.GetString(Bytes, span.Start, span.Length);

    /// <summary>Bytes of a span.</summary>
    public ReadOnlySpan<byte> Slice(TextSpan span) => Bytes.AsSpan(span.Start, span.Length);

    /// <summary>One-based line and column (column counted in bytes) of an offset.</summary>
    public LinePosition GetLinePosition(int offset)
    {
        var starts = _lineStarts ??= BuildLineStarts();
        var line = Array.BinarySearch(starts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return new LinePosition(line + 1, offset - starts[line] + 1);
    }

    private int[] BuildLineStarts()
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < Bytes.Length; i++)
        {
            if (Bytes[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }
}
