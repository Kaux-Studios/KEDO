namespace Kedo.Text;

/// <summary>Byte range inside a UTF-8 source. <see cref="Start"/> is inclusive, <see cref="End"/> exclusive.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>Exclusive end offset.</summary>
    public int End => Start + Length;

    /// <summary>Span covering both operands.</summary>
    public static TextSpan Union(TextSpan a, TextSpan b)
    {
        var start = Math.Min(a.Start, b.Start);
        var end = Math.Max(a.End, b.End);
        return new TextSpan(start, end - start);
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>One-based line and column of a byte offset.</summary>
public readonly record struct LinePosition(int Line, int Column)
{
    /// <inheritdoc />
    public override string ToString() => $"{Line}:{Column}";
}
