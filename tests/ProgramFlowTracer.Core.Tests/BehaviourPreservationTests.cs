using ProgramFlowTracer.Core.Tests.TestSupport;
using ProgramFlowTracer.Runtime.Configuration;
using ProgramFlowTracer.Runtime.Serialization;

namespace ProgramFlowTracer.Core.Tests;

/// <summary>
/// Instrumentation must observe a program without running any of it. Each test here pins one way
/// that capturing a value used to execute the traced application's own code.
/// </summary>
[Collection("FlowTracerSequential")]
public class BehaviourPreservationTests
{
    private const string ThrowingSource = """
    using System;

    namespace Sample
    {
        public class Service
        {
            public int Throws() { throw new InvalidOperationException("boom"); }
        }
    }
    """;

    [Fact]
    public void ExceptionsAreObservedFromAFilter_SoTheMethodNeverBecomesAHandler()
    {
        // `catch (Exception) { record; throw; }` would end the runtime's first pass at this frame,
        // which changes when an outer `catch ... when (filter)` runs relative to inner finally
        // blocks, and makes the exception look handled. A filter returning false does neither.
        var (instrumented, count, _) = RoslynTestHelper.Instrument(ThrowingSource, FlowTracerConfig.Default);

        Assert.Equal(1, count);
        Assert.Contains("when (global::ProgramFlowTracer.Runtime.FlowTracer.ObserveException(", instrumented);

        // The old shape must be gone: no catch body that records and rethrows.
        Assert.DoesNotContain("FlowTracer.Exception(", instrumented);
    }

    [Fact]
    public void ObserveExceptionAlwaysReturnsFalse_SoTheExceptionKeepsPropagating()
    {
        // If this ever returned true the generated `throw;` would run, turning one propagating
        // exception into a rethrow - a different stack trace and a different handler ordering.
        var call = default(Runtime.FlowTraceCall);
        Assert.False(Runtime.FlowTracer.ObserveException(call, new InvalidOperationException("x")));
    }

    [Fact]
    public void DeferredSequence_IsDescribed_NotEnumerated()
    {
        // Enumerating a LINQ chain, an IQueryable or a `yield return` iterator executes the traced
        // program: queries fire, side effects run, and a single-pass sequence is consumed so the
        // caller receives an empty one.
        var executed = 0;
        var source = new[] { 1, 2, 3 };
        var deferred = source.Where(x =>
        {
            executed++;
            return true;
        });

        var serializer = new SafeObjectSerializer(FlowTracerConfig.Default);
        var capture = serializer.Capture(deferred, deferred.GetType());

        Assert.Equal(0, executed);
        var note = (Dictionary<string, object?>)capture.Value!;
        Assert.Contains("not enumerated", (string)note["$note"]!);
    }

    [Fact]
    public void MaterializedCollections_AreStillCapturedInFull()
    {
        // The guard must not cost anything for ordinary in-memory collections, which cannot run
        // user code when read.
        var serializer = new SafeObjectSerializer(FlowTracerConfig.Default);

        var fromList = (List<object?>)serializer.Capture(new List<int> { 1, 2, 3 }, typeof(List<int>)).Value!;
        var fromArray = (List<object?>)serializer.Capture(new[] { 4, 5 }, typeof(int[])).Value!;
        var fromSet = (List<object?>)serializer.Capture(new HashSet<int> { 6 }, typeof(HashSet<int>)).Value!;

        Assert.Equal(3, fromList.Count);
        Assert.Equal(2, fromArray.Count);
        Assert.Single(fromSet);
    }

    [Fact]
    public void ComputedPropertyGetters_AreNotInvokedByDefault()
    {
        // A navigation property on an ORM entity issues a query when read (and throws once its
        // context is disposed); a computed property can mutate, cache, log or block.
        var probe = new GetterProbe();
        var serializer = new SafeObjectSerializer(FlowTracerConfig.Default);

        var obj = (Dictionary<string, object?>)serializer.Capture(probe, typeof(GetterProbe)).Value!;

        Assert.Equal(0, GetterProbe.Reads);

        // The auto-property is still captured - returning a backing field has no side effects.
        Assert.Equal(7, obj["Id"]);

        var note = (Dictionary<string, object?>)obj["Computed"]!;
        Assert.Contains("not read", (string)note["$note"]!);
    }

    [Fact]
    public void ComputedPropertyGetters_AreInvokedWhenExplicitlyEnabled()
    {
        GetterProbe.Reads = 0;
        var config = FlowTracerConfig.Default;
        config.CaptureComputedProperties = true;

        var obj = (Dictionary<string, object?>)new SafeObjectSerializer(config)
            .Capture(new GetterProbe(), typeof(GetterProbe)).Value!;

        Assert.Equal(1, GetterProbe.Reads);
        Assert.Equal("computed", obj["Computed"]);
    }

    private sealed class GetterProbe
    {
        public static int Reads;

        public int Id { get; set; } = 7;

        public string Computed
        {
            get
            {
                Reads++;
                return "computed";
            }
        }
    }
}
