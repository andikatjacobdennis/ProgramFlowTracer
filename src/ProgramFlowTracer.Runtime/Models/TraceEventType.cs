using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Models;

[JsonConverter(typeof(JsonStringEnumConverter<TraceEventType>))]
public enum TraceEventType
{
    MethodEnter,
    MethodExit,
    Exception
}
