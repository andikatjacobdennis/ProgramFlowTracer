using ProgramFlowTracer.Core.Tests.TestSupport;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Tests;

/// <summary>
/// Structure-only instrumentation (the CLI's <c>--no-values</c>).
///
/// The point is that the capture code is never *generated*, not merely ignored at runtime: no
/// FlowTraceParameter array is allocated, no argument is boxed, and no property getter runs on
/// the traced application's thread. The call tree, timings, threads and exceptions are unchanged.
/// </summary>
[Collection("FlowTracerSequential")]
public class NoValuesModeTests
{
    private const string Source = """
    using System;
    namespace Sample
    {
        public class Calculator
        {
            public int Add(int a, int b) => a + b;
            public string Describe(string label, out int count) { count = 2; return label + "!"; }
            public void Nothing() { var x = 1; x++; }
            public int Throws() { throw new InvalidOperationException("boom"); }
        }
    }
    """;

    private static FlowTracerConfig StructureOnly(out string outputDirectory)
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out outputDirectory);
        config.CaptureParameters = false;
        config.CaptureReturnValues = false;
        return config;
    }

    [Fact]
    public void NoCaptureCodeIsGenerated()
    {
        var config = StructureOnly(out _);
        var (instrumented, count, _) = RoslynTestHelper.Instrument(Source, config);

        Assert.True(count >= 4, "instrumented " + count);

        // The array type appears nowhere, so nothing is allocated or boxed per call.
        Assert.DoesNotContain("FlowTraceParameter", instrumented);
        // Values are not reported, so Exit(value, type) is never used.
        Assert.DoesNotContain("FlowTracer.Exit(", instrumented);
        // Structure still is: entry, exit, exception and leave all remain.
        Assert.Contains("FlowTracer.Enter(", instrumented);
        Assert.Contains("FlowTracer.ExitVoid(", instrumented);
        // Exceptions are observed from a filter, so the method never becomes a handler.
        Assert.Contains("FlowTracer.ObserveException(", instrumented);
        Assert.Contains("FlowTracer.Leave(", instrumented);
    }

    [Fact]
    public void BehaviourIsUnchanged_AndTheCallTreeIsStillRecorded()
    {
        var config = StructureOnly(out var outputDirectory);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "NoValues1");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        Assert.Equal(5, type.GetMethod("Add")!.Invoke(instance, new object[] { 2, 3 }));

        var args = new object?[] { "hi", null };
        Assert.Equal("hi!", type.GetMethod("Describe")!.Invoke(instance, args));
        Assert.Equal(2, args[1]);

        type.GetMethod("Nothing")!.Invoke(instance, null);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDirectory);

        var enter = events.First(e => e.GetProperty("eventType").GetString() == "MethodEnter" &&
                                      e.GetProperty("method").GetString() == "Add");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, enter.GetProperty("parameters").ValueKind);

        var exit = events.First(e => e.GetProperty("eventType").GetString() == "MethodExit" &&
                                     e.GetProperty("method").GetString() == "Add");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, exit.GetProperty("returnValue").ValueKind);

        // Timing survives - it is the reason to use this mode rather than not tracing at all.
        Assert.True(exit.GetProperty("durationMicroseconds").GetDouble() >= 0);

        // And the structure: every call still produced an enter and an exit.
        Assert.Contains(events, e => e.GetProperty("method").GetString() == "Describe" &&
                                     e.GetProperty("eventType").GetString() == "MethodExit");
        Assert.Contains(events, e => e.GetProperty("method").GetString() == "Nothing" &&
                                     e.GetProperty("eventType").GetString() == "MethodExit");
    }

    [Fact]
    public void ExceptionsAreStillRecordedAndRethrown()
    {
        var config = StructureOnly(out var outputDirectory);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "NoValues2");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => type.GetMethod("Throws")!.Invoke(instance, null));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Equal("boom", thrown.InnerException!.Message);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDirectory);
        Assert.Contains(events, e => e.GetProperty("eventType").GetString() == "Exception" &&
                                     e.GetProperty("method").GetString() == "Throws");
    }
}
