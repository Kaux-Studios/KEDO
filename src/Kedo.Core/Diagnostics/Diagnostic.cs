using Kedo.Text;

namespace Kedo.Diagnostics;

/// <summary>Severity of a diagnostic.</summary>
public enum Severity
{
    /// <summary>Informational note.</summary>
    Info,
    /// <summary>Something suspicious that does not block compilation.</summary>
    Warning,
    /// <summary>Compilation cannot produce a valid result.</summary>
    Error,
}

/// <summary>A secondary span with its own message, pointing at related code.</summary>
public sealed record DiagnosticLabel(string? File, TextSpan Span, string Message);

/// <summary>
/// A diagnostic produced by any stage (lexer, parser, resolver, validator, VM compiler).
/// Codes are stable (<c>K1002</c>); messages start lowercase, end without a period and quote identifiers in backticks.
/// </summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? File,
    TextSpan Span,
    IReadOnlyList<DiagnosticLabel> Labels,
    IReadOnlyList<string> Notes)
{
    /// <summary>Creates a diagnostic with no labels or notes.</summary>
    public Diagnostic(string code, Severity severity, string message, string? file, TextSpan span)
        : this(code, severity, message, file, span, Array.Empty<DiagnosticLabel>(), Array.Empty<string>())
    {
    }

    /// <summary>True when <see cref="Severity"/> is <see cref="Severity.Error"/>.</summary>
    public bool IsError => Severity == Severity.Error;
}

/// <summary>Accumulates diagnostics. Stages never throw on content problems; they append here.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    /// <summary>Diagnostics in the order they were reported.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>True when at least one error was reported.</summary>
    public bool HasErrors { get; private set; }

    /// <summary>Appends a diagnostic.</summary>
    public void Add(Diagnostic diagnostic)
    {
        _items.Add(diagnostic);
        if (diagnostic.IsError)
        {
            HasErrors = true;
        }
    }

    /// <summary>Appends an error without labels or notes.</summary>
    public void Error(string code, string message, string? file, TextSpan span)
        => Add(new Diagnostic(code, Severity.Error, message, file, span));

    /// <summary>Appends a warning without labels or notes.</summary>
    public void Warning(string code, string message, string? file, TextSpan span)
        => Add(new Diagnostic(code, Severity.Warning, message, file, span));

    /// <summary>Appends every diagnostic of another bag.</summary>
    public void AddRange(DiagnosticBag other)
    {
        foreach (var item in other._items)
        {
            Add(item);
        }
    }
}
