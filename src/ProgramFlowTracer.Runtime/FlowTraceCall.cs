using ProgramFlowTracer.Runtime.Context;

namespace ProgramFlowTracer.Runtime;

/// <summary>
/// Token returned by <see cref="FlowTracer.Enter"/> and threaded through the rest of an
/// instrumented method (to <c>Exit</c>/<c>ExitVoid</c>/<c>Exception</c>, and finally to
/// <c>Leave</c>). It is a value type purely to avoid an extra heap allocation on every traced call;
/// all of its state is either immutable or write-once.
/// </summary>
public readonly struct FlowTraceCall
{
    internal FlowTraceCall(bool enabled, Guid traceId, TraceContext? pushedContext, long startTimestamp, string methodName, string declaringType, string? file, int? line, int? column)
    {
        Enabled = enabled;
        TraceId = traceId;
        PushedContext = pushedContext;
        StartTimestamp = startTimestamp;
        MethodName = methodName;
        DeclaringType = declaringType;
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>False when tracing is globally disabled; every other member on a disabled call is
    /// meaningless and every <see cref="FlowTracer"/> method treats <c>Enabled == false</c> as an
    /// immediate no-op so overhead when tracing is off stays negligible.</summary>
    public bool Enabled { get; }

    public Guid TraceId { get; }

    internal TraceContext? PushedContext { get; }

    internal long StartTimestamp { get; }

    public string MethodName { get; }

    public string DeclaringType { get; }

    public string? File { get; }

    public int? Line { get; }

    public int? Column { get; }

    internal static readonly FlowTraceCall Disabled = new(false, default, null, 0, string.Empty, string.Empty, null, null, null);
}
