using Kedo.Text;

namespace Kedo.Compiler.Syntax;

/// <summary>Token kinds. Trivia kinds (whitespace, newlines, comments) are kept in the token array for the formatter.</summary>
public enum TokenKind : ushort
{
    /// <summary>End of file.</summary>
    EndOfFile = 0,
    /// <summary>Byte sequence the lexer could not classify.</summary>
    Unknown,

    // Trivia
    /// <summary>Spaces and tabs.</summary>
    Whitespace,
    /// <summary>A line break (<c>\n</c> or <c>\r\n</c>).</summary>
    Newline,
    /// <summary><c>// ...</c></summary>
    LineComment,
    /// <summary><c>/* ... */</c></summary>
    BlockComment,
    /// <summary><c>/// ...</c></summary>
    DocComment,

    // Literals and names
    /// <summary><c>[a-z_][a-z0-9_]*</c> that is not a keyword.</summary>
    Identifier,
    /// <summary>Decimal integer with optional <c>_</c> separators.</summary>
    Integer,
    /// <summary>Integer immediately followed by <c>s</c>, <c>min</c>, <c>h</c> or <c>d</c>.</summary>
    Duration,
    /// <summary>Double-quoted string with escapes.</summary>
    String,
    /// <summary><c>type:id</c> or <c>type:module/id</c> as one token.</summary>
    Reference,

    // Punctuation
    /// <summary><c>{</c></summary>
    LBrace,
    /// <summary><c>}</c></summary>
    RBrace,
    /// <summary><c>[</c></summary>
    LBracket,
    /// <summary><c>]</c></summary>
    RBracket,
    /// <summary><c>(</c></summary>
    LParen,
    /// <summary><c>)</c></summary>
    RParen,
    /// <summary><c>=</c></summary>
    Equals,
    /// <summary><c>=!</c></summary>
    EqualsBang,
    /// <summary><c>+=</c></summary>
    PlusEquals,
    /// <summary><c>-=</c></summary>
    MinusEquals,
    /// <summary><c>==</c></summary>
    EqualsEquals,
    /// <summary><c>!=</c></summary>
    BangEquals,
    /// <summary><c>&lt;</c></summary>
    Less,
    /// <summary><c>&lt;=</c></summary>
    LessEquals,
    /// <summary><c>&gt;</c></summary>
    Greater,
    /// <summary><c>&gt;=</c></summary>
    GreaterEquals,
    /// <summary><c>+</c></summary>
    Plus,
    /// <summary><c>-</c></summary>
    Minus,
    /// <summary><c>*</c></summary>
    Star,
    /// <summary><c>/</c></summary>
    Slash,
    /// <summary><c>%</c></summary>
    Percent,
    /// <summary><c>,</c></summary>
    Comma,
    /// <summary><c>;</c></summary>
    Semicolon,
    /// <summary><c>:</c> (only when not part of a reference, e.g. <c>name: type</c>).</summary>
    Colon,
    /// <summary><c>.</c></summary>
    Dot,
    /// <summary><c>@</c></summary>
    At,
    /// <summary><c>-&gt;</c></summary>
    Arrow,

    // Keywords
    /// <summary><c>kedo</c></summary>
    KwKedo,
    /// <summary><c>module</c></summary>
    KwModule,
    /// <summary><c>manifest</c></summary>
    KwManifest,
    /// <summary><c>abstract</c></summary>
    KwAbstract,
    /// <summary><c>mixin</c></summary>
    KwMixin,
    /// <summary><c>extends</c></summary>
    KwExtends,
    /// <summary><c>with</c></summary>
    KwWith,
    /// <summary><c>component</c></summary>
    KwComponent,
    /// <summary><c>remove</c></summary>
    KwRemove,
    /// <summary><c>on</c></summary>
    KwOn,
    /// <summary><c>function</c></summary>
    KwFunction,
    /// <summary><c>let</c></summary>
    KwLet,
    /// <summary><c>set</c></summary>
    KwSet,
    /// <summary><c>if</c></summary>
    KwIf,
    /// <summary><c>else</c></summary>
    KwElse,
    /// <summary><c>wait</c></summary>
    KwWait,
    /// <summary><c>apply</c></summary>
    KwApply,
    /// <summary><c>emit</c></summary>
    KwEmit,
    /// <summary><c>stop</c></summary>
    KwStop,
    /// <summary><c>return</c></summary>
    KwReturn,
    /// <summary><c>decision</c></summary>
    KwDecision,
    /// <summary><c>roll</c></summary>
    KwRoll,
    /// <summary><c>signal</c></summary>
    KwSignal,
    /// <summary><c>option</c></summary>
    KwOption,
    /// <summary><c>as</c></summary>
    KwAs,
    /// <summary><c>and</c></summary>
    KwAnd,
    /// <summary><c>or</c></summary>
    KwOr,
    /// <summary><c>not</c></summary>
    KwNot,
    /// <summary><c>true</c></summary>
    KwTrue,
    /// <summary><c>false</c></summary>
    KwFalse,
}

/// <summary>Flags attached to a token.</summary>
[Flags]
public enum TokenFlags : ushort
{
    /// <summary>No flags.</summary>
    None = 0,
    /// <summary>At least one newline token precedes this token since the previous significant token.</summary>
    NewlineBefore = 1,
    /// <summary>The token text is malformed (unterminated string, bad escape); a diagnostic was reported.</summary>
    Malformed = 2,
}

/// <summary>A lexical token: kind, flags and byte span. 12 bytes.</summary>
public readonly record struct Token(TokenKind Kind, TokenFlags Flags, int Start, int Length)
{
    /// <summary>Byte span of the token.</summary>
    public TextSpan Span => new(Start, Length);

    /// <summary>Exclusive end offset.</summary>
    public int End => Start + Length;

    /// <summary>True for whitespace, newlines and comments.</summary>
    public bool IsTrivia => Kind is >= TokenKind.Whitespace and <= TokenKind.DocComment;

    /// <summary>True when the token is a keyword.</summary>
    public bool IsKeyword => Kind >= TokenKind.KwKedo;
}
