namespace ProgramFlowTracer.Core.Tests.TestSupport;

/// <summary>
/// FlowTracer is process-wide static state (by design - see remarks on
/// <c>FlowTracer.ResetForTesting</c>). Any test that calls <c>Initialize</c>/<c>ShutdownAsync</c>
/// directly (rather than only checking a method's return value) must run in this collection so
/// xunit never executes two such tests concurrently against the same static fields.
/// </summary>
[CollectionDefinition("FlowTracerSequential", DisableParallelization = true)]
public sealed class FlowTracerSequentialCollection
{
}
