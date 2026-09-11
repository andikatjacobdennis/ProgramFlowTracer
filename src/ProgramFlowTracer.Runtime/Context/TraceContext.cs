namespace ProgramFlowTracer.Runtime.Context;

/// <summary>
/// Immutable node in the logical call chain. <see cref="TraceScope"/> keeps an
/// <see cref="AsyncLocal{T}"/> pointing at the "current" node; entering a method pushes a new node
/// (whose <see cref="Parent"/> is the previous current node) and leaving restores the previous one.
/// Because it is a plain AsyncLocal value (not a mutable object touched after publishing), it flows
/// correctly across <c>await</c> continuations, thread-pool hops, and parallel Tasks without any
/// extra bookkeeping - each concurrent logical call stack gets its own chain.
/// </summary>
public sealed class TraceContext
{
    public TraceContext(Guid traceId, TraceContext? parent)
    {
        TraceId = traceId;
        Parent = parent;
    }

    public Guid TraceId { get; }

    public TraceContext? Parent { get; }

    public Guid? ParentTraceId => Parent?.TraceId;
}

/// <summary>
/// Owns the single <see cref="AsyncLocal{T}"/> that threads the current <see cref="TraceContext"/>
/// through the traced application. All access is funneled through here so the push/pop discipline
/// (see remarks on <see cref="TraceContext"/>) lives in exactly one place.
/// </summary>
public static class TraceScope
{
    private static readonly AsyncLocal<TraceContext?> Current = new();

    public static TraceContext? CurrentContext => Current.Value;

    /// <summary>Pushes a new current context for this invocation and returns it. The caller
    /// (<c>FlowTracer.Enter</c>) is responsible for restoring the previous context via
    /// <see cref="Restore"/> in a <c>finally</c> block, regardless of how the method exits.</summary>
    public static TraceContext Push(Guid traceId)
    {
        var parent = Current.Value;
        var context = new TraceContext(traceId, parent);
        Current.Value = context;
        return context;
    }

    /// <summary>Restores whatever context was current before <paramref name="pushed"/> was pushed.
    /// Safe to call even if the AsyncLocal value has since diverged (e.g. because the callee
    /// itself entered and left further nested scopes) - it always resets to that scope's captured
    /// parent, never to whatever happens to be current now.</summary>
    public static void Restore(TraceContext pushed)
    {
        Current.Value = pushed.Parent;
    }
}
