namespace Kedo.Runtime;

/// <summary>A value crossing the host boundary: an integer, or a symbol handle (enum value, reference, text).</summary>
public readonly record struct HostValue(long Number, bool IsSymbol)
{
    /// <summary>Integer, boolean (0/1) or duration in minutes.</summary>
    public static HostValue Int(long n) => new(n, false);

    /// <summary>Symbol interned in the session table.</summary>
    public static HostValue Symbol(int handle) => new(handle, true);

    /// <summary>No value.</summary>
    public static HostValue None => new(0, true);
}

/// <summary>Idempotency key of an effect or roll: a behaviour instance, a stable site label and a sequence number.</summary>
public readonly record struct EffectKey(string InstanceId, string SiteLabel, int Sequence)
{
    /// <inheritdoc />
    public override string ToString() => $"{InstanceId}/{SiteLabel}#{Sequence}";
}

/// <summary>
/// The restricted game API a behaviour sees (anexo E, Plan-06). Queries are pure; effects go through the host with an
/// idempotency key; rolls draw from a named stream the host owns; signals are queued, never dispatched re-entrantly.
/// The VM never touches game state directly.
/// </summary>
public interface IBehaviorHost
{
    /// <summary>Simulated time (minutes) at activation.</summary>
    long NowMinutes { get; }

    /// <summary>Symbol table shared with the VM.</summary>
    SymbolTable Symbols { get; }

    /// <summary>Value of a context field of the current activation (by name), or <see cref="HostValue.None"/>.</summary>
    HostValue Context(string field);

    /// <summary>Pure read-only query. <paramref name="args"/> may include a receiver as first argument for member calls.</summary>
    HostValue Query(string queryId, ReadOnlySpan<HostValue> args);

    /// <summary>Transactional write; must be idempotent by key. Returns false when the effect is rejected by game rules.</summary>
    bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args);

    /// <summary>Draws from a named stream; must be recorded under the key so a resume never redraws.</summary>
    long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args);

    /// <summary>Queues a signal for dispatch after the current hook completes.</summary>
    void Raise(string signalId, ReadOnlySpan<HostValue> args);

    /// <summary>Trace point.</summary>
    void Trace(string message);
}

/// <summary>Execution limits (anexo E §4).</summary>
public sealed record Budget(int MaxSteps = 10_000, int MaxCallDepth = 32, int MaxRegisters = 4096)
{
    /// <summary>Default budget.</summary>
    public static Budget Default { get; } = new();
}

/// <summary>Why a behaviour suspended.</summary>
public enum AwaitKind
{
    /// <summary>Waiting for simulated minutes.</summary>
    Minutes,
    /// <summary>Waiting for a player decision.</summary>
    Decision,
    /// <summary>Waiting for a roll result.</summary>
    Roll,
    /// <summary>Waiting for a signal.</summary>
    Signal,
}

/// <summary>Pending suspension stored in a frame.</summary>
public sealed record AwaitRequest(AwaitKind Kind, string Label, long DueAtMinutes, string? Id, IReadOnlyList<string> Options, int ResultRegister);

/// <summary>Input that resumes a suspended frame.</summary>
public sealed record Resumption(AwaitKind Kind, long Value);

/// <summary>Policy when a suspended frame no longer matches its definition (KEDO-02, anexo E §6).</summary>
public enum StalePolicy
{
    /// <summary>Discard the frame silently.</summary>
    AbortSilently,
    /// <summary>Discard the frame and let the host run a compensation.</summary>
    AbortWithCompensation,
    /// <summary>Restart the hook from its entry.</summary>
    RestartAtEntry,
}

/// <summary>Outcome of running a behaviour.</summary>
public enum RunStatus
{
    /// <summary>The hook finished.</summary>
    Completed,
    /// <summary>The hook suspended; the frame holds the resumption point.</summary>
    Suspended,
    /// <summary>The hook failed (budget, division by zero, verifier, stale frame); the command is unaffected.</summary>
    Failed,
}

/// <summary>Result of <see cref="BehaviorVm.Activate"/> or <see cref="BehaviorVm.Resume"/>.</summary>
public sealed record RunResult(RunStatus Status, BehaviorFrame? Frame, string? Error, IReadOnlyList<string> Trace);
