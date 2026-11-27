using ProgramFlowTracer.Runtime.Models;

namespace ProgramFlowTracer.Runtime.Writing;

/// <summary>Discriminated union of the two kinds of records the background writer persists.</summary>
internal abstract class TraceQueueItem
{
    public sealed class Event : TraceQueueItem
    {
        public required TraceEvent Value { get; init; }
    }

    public sealed class Object : TraceQueueItem
    {
        public required ObjectRecord Value { get; init; }
    }
}
