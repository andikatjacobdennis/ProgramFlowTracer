using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Models;

/// <summary>
/// Written once as <c>.flowtrace/runs/{runId}/run.json</c> when a traced run starts (and updated
/// with an end time when <see cref="FlowTracer.ShutdownAsync"/> completes).
/// </summary>
public sealed class RunMetadata
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("application")]
    public string Application { get; set; } = string.Empty;

    [JsonPropertyName("machineName")]
    public string MachineName { get; set; } = Environment.MachineName;

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; } = Environment.ProcessId;

    [JsonPropertyName("commandLine")]
    public string? CommandLine { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public string StartedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("endedAtUtc")]
    public string? EndedAtUtc { get; set; }

    [JsonPropertyName("eventCount")]
    public long EventCount { get; set; }

    [JsonPropertyName("droppedEventCount")]
    public long DroppedEventCount { get; set; }

    [JsonPropertyName("tracerVersion")]
    public string TracerVersion { get; set; } = "1.0.0";
}
