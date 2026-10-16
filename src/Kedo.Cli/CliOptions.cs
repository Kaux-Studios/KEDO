namespace Kedo.Cli;

/// <summary>Options shared by the CLI commands.</summary>
internal static class CliOptions
{
    /// <summary>Schema paths given with <c>--schema=path[;path]</c>; files under them are schema, never content.</summary>
    public static string[] SchemaPaths { get; set; } = Array.Empty<string>();
}
