using System.Text.Json.Serialization;

namespace Kedo.Runtime;

/// <summary>One activation record. <see cref="ResumeLabel"/> is a stable await label; the pc is recomputed on load.</summary>
public sealed record CallFrame(int FunctionIndex, string FunctionName, int RegisterBase, int RegisterCount, int ReturnRegister, string? ResumeLabel, int Pc);

/// <summary>
/// Serializable state of a suspended behaviour (KEDO-02, anexo E). Lives inside the game's own save snapshot.
/// Symbolic registers are stored by name so handles survive a new session; integers are stored as they are.
/// </summary>
public sealed record BehaviorFrame(
    int FormatVersion,
    string InstanceId,
    string BehaviorId,
    int DefinitionVersion,
    string DefinitionHash,
    string HookId,
    IReadOnlyList<CallFrame> Stack,
    long[] Registers,
    string?[] SymbolNames,
    AwaitRequest? Pending,
    IReadOnlyList<string> AppliedEffects,
    long StepsUsed,
    int EffectSequence)
{
    /// <summary>Current frame format.</summary>
    public const int CurrentFormat = 1;
}

/// <summary>Source-generated JSON context for frames (AOT-safe; no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = false)]
[JsonSerializable(typeof(BehaviorFrame))]
[JsonSerializable(typeof(CallFrame))]
[JsonSerializable(typeof(AwaitRequest))]
public sealed partial class BehaviorJsonContext : JsonSerializerContext
{
}
