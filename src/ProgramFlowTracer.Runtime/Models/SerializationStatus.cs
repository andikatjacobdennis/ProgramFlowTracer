using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Models;

/// <summary>
/// Outcome of attempting to capture a value (parameter, return value, or object graph node).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SerializationStatus>))]
public enum SerializationStatus
{
    /// <summary>The value was serialized in full.</summary>
    Success,

    /// <summary>The value could not be serialized; a best-effort fallback is provided.</summary>
    Failed,

    /// <summary>The value itself was captured, but one or more nested properties/elements inside
    /// it could not be (e.g. a property whose type or getter isn't capturable). Those spots are
    /// marked individually inside <see cref="CapturedValue.Value"/> rather than failing the whole
    /// capture.</summary>
    Partial,

    /// <summary>The value was intentionally not captured because it is marked sensitive.</summary>
    Redacted,

    /// <summary>The value was not captured because tracing was not able to observe it (e.g. an
    /// out parameter before the call completes).</summary>
    Unavailable,

    /// <summary>The value was truncated because it exceeded configured size limits.</summary>
    Truncated,

    /// <summary>The value is a null reference.</summary>
    Null
}
