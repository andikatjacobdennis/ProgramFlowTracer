using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Models;

/// <summary>
/// A single file under <c>.flowtrace/runs/{runId}/objects/{objectId}.json</c>. Large parameter,
/// return, or field values are spilled here (rather than inlined into <c>events.jsonl</c>) so
/// that the main event stream stays small and append-friendly.
/// </summary>
public sealed class ObjectRecord
{
    [JsonPropertyName("objectId")]
    public string ObjectId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("parameterName")]
    public string? ParameterName { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("serializationStatus")]
    public SerializationStatus SerializationStatus { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("toString")]
    public string? ToStringFallback { get; set; }
}
