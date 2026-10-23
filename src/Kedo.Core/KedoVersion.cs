namespace Kedo;

/// <summary>Version of the KEDO language and toolchain. Majors introduce breaking changes; 1.x is additive only.</summary>
public static class KedoVersion
{
    /// <summary>Toolchain version string.</summary>
    public static readonly string Current = "1.0.0";

    /// <summary>Language major. Content written for major N is readable by every N.x toolchain.</summary>
    public const int LanguageMajor = 1;
}
