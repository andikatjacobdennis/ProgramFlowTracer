using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Models;

/// <summary>
/// Represents the result of capturing a single value: a parameter, a return value, or a field
/// inside an object graph. This is the unit that is either embedded inline in an event or
/// spilled out to a separate object file (see <see cref="ObjectRecord"/>) when it is large.
/// </summary>
public sealed class CapturedValue
{
    /// <summary>CLR type name (best-effort display string) of the captured value.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>The captured value itself, when it was small enough to inline and serialized
    /// successfully. This is a boxed, JSON-serializable representation (built from a
    /// <see cref="System.Text.Json.JsonDocument"/>/JsonElement), not the original CLR object.</summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary>How the capture went.</summary>
    [JsonPropertyName("serializationStatus")]
    public SerializationStatus SerializationStatus { get; set; }

    /// <summary>Set when <see cref="SerializationStatus"/> is <see cref="Models.SerializationStatus.Failed"/>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>The .NET exception type name that was thrown while attempting serialization.</summary>
    [JsonPropertyName("errorType")]
    public string? ErrorType { get; set; }

    /// <summary>Best-effort <c>ToString()</c> of the value, provided as a fallback when JSON
    /// serialization fails.</summary>
    [JsonPropertyName("toString")]
    public string? ToStringFallback { get; set; }

    /// <summary>When the value was large enough to be spilled to a separate object file, this is
    /// the id of that file (see <c>.flowtrace/runs/{runId}/objects/{objectId}.json</c>).</summary>
    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }
}
