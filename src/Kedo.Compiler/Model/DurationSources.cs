using System.Runtime.CompilerServices;

namespace Kedo.Compiler.Model;

/// <summary>
/// Side channel for the literal text of a <see cref="DurationValue"/> as written (`1_500ms`), so K4014 can quote it. It lives
/// outside the record so value equality and the public API stay as in 1.0.
/// </summary>
internal static class DurationSources
{
    private static readonly ConditionalWeakTable<DurationValue, string> Texts = new();

    /// <summary>Remembers <paramref name="text"/> for <paramref name="value"/> (ignored when null) and returns the value.</summary>
    public static DurationValue Remember(DurationValue value, string? text)
    {
        if (text is not null)
        {
            Texts.AddOrUpdate(value, text);
        }

        return value;
    }

    /// <summary>The literal as written, when known.</summary>
    public static string? TextOf(DurationValue value) => Texts.TryGetValue(value, out var text) ? text : null;
}
