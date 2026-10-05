using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Models;

/// <summary>
/// One line of <c>events.jsonl</c>. Represents a method-enter, method-exit, or exception
/// occurrence for a single method invocation (identified by <see cref="TraceId"/>).
/// </summary>
public sealed class TraceEvent
{
    [JsonPropertyName("eventType")]
    public TraceEventType EventType { get; set; }

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>Unique id of this particular method invocation.</summary>
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>Id of the invocation that (synchronously or asynchronously) called this one, or
    /// <c>null</c> for a root invocation.</summary>
    [JsonPropertyName("parentTraceId")]
    public string? ParentTraceId { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("declaringType")]
    public string? DeclaringType { get; set; }

    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("column")]
    public int? Column { get; set; }

    [JsonPropertyName("timestampUtc")]
    public string TimestampUtc { get; set; } = string.Empty;

    [JsonPropertyName("threadId")]
    public int? ThreadId { get; set; }

    [JsonPropertyName("taskId")]
    public int? TaskId { get; set; }

    [JsonPropertyName("isThreadPoolThread")]
    public bool? IsThreadPoolThread { get; set; }

    /// <summary>Present on <see cref="TraceEventType.MethodEnter"/> events.</summary>
    [JsonPropertyName("parameters")]
    public Dictionary<string, CapturedValue>? Parameters { get; set; }

    /// <summary>Present on <see cref="TraceEventType.MethodExit"/> events, when the method is
    /// non-void and a value could be observed.</summary>
    [JsonPropertyName("returnValue")]
    public CapturedValue? ReturnValue { get; set; }

    /// <summary>Present on <see cref="TraceEventType.MethodExit"/> events for <c>ref</c>/<c>out</c>
    /// parameters, whose final values are only known once the method has completed.</summary>
    [JsonPropertyName("outParameters")]
    public Dictionary<string, CapturedValue>? OutParameters { get; set; }

    /// <summary>Present on <see cref="TraceEventType.MethodExit"/> events.</summary>
    [JsonPropertyName("durationMicroseconds")]
    public double? DurationMicroseconds { get; set; }

    /// <summary>Present on <see cref="TraceEventType.Exception"/> events.</summary>
    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }
}
