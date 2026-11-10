using System.Globalization;
using Kedo.Schema;

namespace Kedo.Runtime;

/// <summary>
/// The game API of <see cref="FastBehaviorVm"/> (ADR-0015): the same contract as <see cref="IBehaviorHost"/>, by the integer
/// ids of a <see cref="BindingTable"/> instead of names. Queries are pure; effects are idempotent by key; signals are queued,
/// never dispatched re-entrantly. Arguments are spans over a buffer the VM reuses: copy them to keep them.
/// </summary>
public interface IFastBehaviorHost
{
    /// <summary>Simulated time at activation, in <paramref name="unit"/> (the program's time unit, ADR-0014). Read only by <c>wait</c>.</summary>
    long Now(DurationUnit unit);

    /// <summary>Value of a context field of the activation of <paramref name="instance"/>, or <see cref="HostValue.None"/>.</summary>
    HostValue Context(long instance, int slot);

    /// <summary>Pure read-only query. <paramref name="args"/> starts with the receiver for member calls.</summary>
    HostValue Query(int queryId, ReadOnlySpan<HostValue> args);

    /// <summary>Transactional write; must be idempotent by key. Returns false when the effect is rejected by game rules.</summary>
    bool Apply(in FastEffectKey key, int effectId, ReadOnlySpan<HostValue> args);

    /// <summary>Queues a signal for dispatch after the current hook completes.</summary>
    void Raise(int signalId, ReadOnlySpan<HostValue> args);
}

/// <summary>
/// Idempotency key of an effect: the instance, the site (pc of the <c>apply</c>) and the effect sequence number of the
/// activation. Same identity as <see cref="EffectKey"/>, whose text is <c>{Instance}/pc{Site}#{Sequence}</c>.
/// </summary>
public readonly record struct FastEffectKey(long Instance, int Site, int Sequence);

/// <summary>Why an activation failed. A failure fails the behaviour, never the command (anexo E).</summary>
public enum RunError
{
    /// <summary>No failure.</summary>
    None,
    /// <summary>The hook handle is invalid or belongs to another program.</summary>
    NoSuchHook,
    /// <summary>The program did not pass verification at bind time (K6002).</summary>
    InvalidProgram,
    /// <summary>The instruction at <see cref="RunOutcome.Pc"/> uses a name the host did not bind (K6001).</summary>
    Unbound,
    /// <summary>The step budget ran out.</summary>
    StepBudget,
    /// <summary>The call depth budget was exceeded.</summary>
    CallDepth,
    /// <summary>The register budget was exceeded.</summary>
    RegisterBudget,
    /// <summary>Division or remainder by zero.</summary>
    DivisionByZero,
    /// <summary>Execution left the code.</summary>
    FellOffEnd,
    /// <summary>The frame handle is invalid, released, or not suspended.</summary>
    NotSuspended,
    /// <summary>The resumption is of another kind than the one the frame waits for.</summary>
    WrongResumption,
    /// <summary>A saved frame whose definition changed, discarded (<see cref="StalePolicy.AbortSilently"/>).</summary>
    StaleFrame,
    /// <summary>A saved frame whose definition changed, discarded with compensation (<see cref="StalePolicy.AbortWithCompensation"/>).</summary>
    StaleFrameCompensation,
    /// <summary>A saved frame this program cannot restore: format, instance, function or await label unknown.</summary>
    UnsupportedFrame,
}

/// <summary>Handle of a suspended activation in the frame pool of one <see cref="FastBehaviorVm"/>. The default value is invalid.</summary>
public readonly struct FrameHandle
{
    internal FrameHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    internal int Index { get; }

    internal int Generation { get; }

    /// <summary>True for a handle the VM returned; it may still have been resumed to the end or released since.</summary>
    public bool IsValid => Generation != 0;
}

/// <summary>
/// Result of an activation or resumption of <see cref="FastBehaviorVm"/>: a reusable struct the caller passes by reference,
/// so nothing is allocated. <see cref="FastBehaviorVm.Describe"/> renders a failure as text when needed.
/// </summary>
public struct RunOutcome
{
    /// <summary>Completed, suspended or failed.</summary>
    public RunStatus Status { readonly get; internal set; }

    /// <summary>The pooled frame of a suspended activation; invalid otherwise.</summary>
    public FrameHandle Frame { readonly get; internal set; }

    /// <summary>Why it failed, or <see cref="RunError.None"/>.</summary>
    public RunError Error { readonly get; internal set; }

    /// <summary>Pc of the failure or of the await, or -1.</summary>
    public int Pc { readonly get; internal set; }

    /// <summary>Steps used by the activation so far (budget, ADR-0007).</summary>
    public long Steps { readonly get; internal set; }
}

/// <summary>What a suspended frame waits for, read without allocating (the strings belong to the program).</summary>
public readonly struct PendingAwait
{
    internal PendingAwait(AwaitKind kind, string label, long dueAt, DurationUnit unit, int id, string? name, ReadOnlyMemory<string> options)
    {
        Kind = kind;
        Label = label;
        DueAt = dueAt;
        Unit = unit;
        Id = id;
        Name = name;
        Options = options;
    }

    /// <summary>Time, decision, roll or signal.</summary>
    public AwaitKind Kind { get; }

    /// <summary>Stable label of the await site.</summary>
    public string Label { get; }

    /// <summary>Due time in <see cref="Unit"/> for <see cref="AwaitKind.Minutes"/>; 0 otherwise.</summary>
    public long DueAt { get; }

    /// <summary>Unit of <see cref="DueAt"/>: the program's time unit.</summary>
    public DurationUnit Unit { get; }

    /// <summary>Bound id of the awaited signal or roll stream; -1 for time and decisions.</summary>
    public int Id { get; }

    /// <summary>Prompt, stream or signal as written; null for time.</summary>
    public string? Name { get; }

    /// <summary>Option labels of a decision, in order (the resumption value is the index).</summary>
    public ReadOnlyMemory<string> Options { get; }
}

/// <summary>Options of <see cref="FastBehaviorVm"/>.</summary>
public sealed class VmOptions
{
    /// <summary>Default options: no trace.</summary>
    public static VmOptions Default { get; } = new();

    /// <summary>
    /// Record the <c>trace</c> points and failure messages of each run in <see cref="FastBehaviorVm.Trace"/> (allocates; for tools
    /// and tests). Off, nothing is written.
    /// </summary>
    public bool Trace { get; init; }
}

/// <summary>
/// Runs an <see cref="IBehaviorHost"/> on <see cref="FastBehaviorVm"/>: ids go back to names through the binding table, keys
/// become <see cref="EffectKey"/>. Allocates like the 1.0 host contract does; it exists so an old host needs no rewrite.
/// </summary>
public sealed class BehaviorHostAdapter : IFastBehaviorHost
{
    private readonly IBehaviorHost _host;
    private readonly IBehaviorClock? _clock;
    private readonly BindingTable _bindings;

    /// <summary>Adapts <paramref name="host"/>; <paramref name="bindings"/> must use <see cref="IBehaviorHost.Symbols"/> as its symbol table.</summary>
    public BehaviorHostAdapter(IBehaviorHost host, BindingTable bindings)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(host.Symbols, bindings.Symbols))
        {
            throw new ArgumentException("the binding table must share the host's symbol table", nameof(bindings));
        }

        _host = host;
        _clock = host as IBehaviorClock;
        _bindings = bindings;
    }

    /// <inheritdoc />
    public long Now(DurationUnit unit) => _clock?.Now(unit) ?? DurationUnits.ConvertTruncating(_host.NowMinutes, DurationUnit.Minute, unit);

    /// <inheritdoc />
    public HostValue Context(long instance, int slot) => _bindings.NameOf(BindingKind.Context, slot) is { } name ? _host.Context(name) : HostValue.None;

    /// <inheritdoc />
    public HostValue Query(int queryId, ReadOnlySpan<HostValue> args) => _host.Query(_bindings.NameOf(BindingKind.Query, queryId) ?? "", args);

    /// <inheritdoc />
    public bool Apply(in FastEffectKey key, int effectId, ReadOnlySpan<HostValue> args)
        => _host.Apply(new EffectKey(key.Instance.ToString(CultureInfo.InvariantCulture), "pc" + key.Site.ToString(CultureInfo.InvariantCulture), key.Sequence),
            _bindings.NameOf(BindingKind.Effect, effectId) ?? "", args);

    /// <inheritdoc />
    public void Raise(int signalId, ReadOnlySpan<HostValue> args) => _host.Raise(_bindings.NameOf(BindingKind.Signal, signalId) ?? "", args);

    // Trace points reach the old host as with BehaviorVm (only when VmOptions.Trace is on).
    internal void ForwardTrace(string message) => _host.Trace(message);
}
