using ProgramFlowTracer.Core.Analysis;
using ProgramFlowTracer.Core.Tests.TestSupport;
using ProgramFlowTracer.Runtime.Configuration;
using ProgramFlowTracer.Runtime.Serialization;

namespace ProgramFlowTracer.Core.Tests;

[Collection("FlowTracerSequential")]
public class ConcurrencyAndSerializationTests
{
    private const string ConcurrencySource = """
    using System.Threading.Tasks;

    namespace Sample
    {
        public class Worker
        {
            public async Task<int> DoWorkAsync(int id, int delayMs)
            {
                await Task.Delay(delayMs);
                return id * 2;
            }
        }
    }
    """;

    [Fact]
    public async Task MultipleConcurrentTasks_EachGetsIndependentTraceChain()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(ConcurrencySource, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Concurrency1");
        var type = assembly.GetType("Sample.Worker")!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("DoWorkAsync")!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var tasks = Enumerable.Range(1, 8)
            .Select(i => (Task<int>)method.Invoke(instance, new object[] { i, (8 - i) * 2 })!)
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        Assert.Equal(8, results.Length);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal((i + 1) * 2, results[i]);
        }

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var enters = events.Where(e => e.GetProperty("eventType").GetString() == "MethodEnter").ToList();
        var exits = events.Where(e => e.GetProperty("eventType").GetString() == "MethodExit").ToList();

        Assert.Equal(8, enters.Count);
        Assert.Equal(8, exits.Count);

        // Every enter has a distinct traceId, and every exit's traceId matches exactly one enter -
        // concurrent invocations never share or cross-contaminate identity.
        var enterIds = enters.Select(e => e.GetProperty("traceId").GetString()).ToHashSet();
        var exitIds = exits.Select(e => e.GetProperty("traceId").GetString()).ToHashSet();
        Assert.Equal(8, enterIds.Count);
        Assert.True(enterIds.SetEquals(exitIds));

        // Each concurrent call is a root call (no outer traced caller), so parentTraceId is null
        // for all of them - none should have accidentally inherited another call's context.
        Assert.True(enters.All(e => e.GetProperty("parentTraceId").ValueKind == System.Text.Json.JsonValueKind.Null));
    }

    [Fact]
    public void PropertyThatThrows_DoesNotFailWholeCapture_OnlyThatPropertyIsMarked()
    {
        // A single unserializable/throwing property must not take the rest of the object down
        // with it - this is the behavior the previous all-or-nothing implementation got wrong.
        var config = FlowTracerConfig.Default;
        // This test is about what happens when a getter runs, so opt in to running them.
        config.CaptureComputedProperties = true;
        var serializer = new SafeObjectSerializer(config);

        var capture = serializer.Capture(new PartlyThrows(), typeof(PartlyThrows));

        Assert.Equal(Runtime.Models.SerializationStatus.Partial, capture.SerializationStatus);
        Assert.NotNull(capture.Error);

        var obj = (Dictionary<string, object?>)capture.Value!;
        Assert.Equal("fine", obj["Good"]);
        var badEntry = (Dictionary<string, object?>)obj["Bad"]!;
        Assert.Equal("cannot read this property", badEntry["$error"]);
    }

    [Fact]
    public void RefStructReturningProperty_IsSkippedWithoutInvokingGetter_RestOfObjectStillCaptured()
    {
        // Mirrors the real-world case that motivated this: an object graph containing a property
        // (like System.Text.Encoding.Preamble) whose type is a ref struct / ReadOnlySpan<T>.
        // Reflection can never invoke such a getter, so it must be skipped up front rather than
        // attempted-and-caught, and every sibling property must still come through untouched.
        var config = FlowTracerConfig.Default;
        config.CaptureComputedProperties = true;
        var serializer = new SafeObjectSerializer(config);

        var capture = serializer.Capture(new HasRefStructProperty(), typeof(HasRefStructProperty));

        Assert.Equal(Runtime.Models.SerializationStatus.Success, capture.SerializationStatus);
        var obj = (Dictionary<string, object?>)capture.Value!;
        Assert.Equal(42, obj["Ordinary"]);
        var note = (Dictionary<string, object?>)obj["Span"]!;
        Assert.Contains("not capturable", (string)note["$note"]!);
    }

    private sealed class PartlyThrows
    {
        public string Good => "fine";
        public string Bad => throw new InvalidOperationException("cannot read this property");
    }

    private sealed class HasRefStructProperty
    {
        public int Ordinary => 42;
        public ReadOnlySpan<byte> Span => default;
    }

    [Fact]
    public void LargeString_IsTruncated_NotDropped()
    {
        var config = FlowTracerConfig.Default;
        config.MaxStringLength = 100;
        var serializer = new SafeObjectSerializer(config);

        var big = new string('x', 5000);
        var capture = serializer.Capture(big, typeof(string));

        Assert.Equal(Runtime.Models.SerializationStatus.Truncated, capture.SerializationStatus);
        var value = (string)capture.Value!;
        Assert.True(value.Length <= 120);
    }

    [Fact]
    public void LargeCollection_IsCappedAtConfiguredLimit()
    {
        var config = FlowTracerConfig.Default;
        config.MaxCollectionItems = 10;
        var serializer = new SafeObjectSerializer(config);

        var items = Enumerable.Range(0, 1000).ToList();
        var capture = serializer.Capture(items, typeof(List<int>));

        Assert.Equal(Runtime.Models.SerializationStatus.Success, capture.SerializationStatus);
        var list = (List<object?>)capture.Value!;
        // 10 items + 1 "truncated" marker entry.
        Assert.Equal(11, list.Count);
    }

    private const string EligibilitySource = """
    using ProgramFlowTracer.Runtime.Attributes;

    namespace Sample
    {
        public class Included
        {
            public int Normal(int x) => x + 1;

            [FlowTraceIgnore]
            public int Ignored(int x) => x + 1;
        }

        [FlowTraceIgnore]
        public class ExcludedType
        {
            public int Normal(int x) => x + 1;
        }

        public class WithIterator
        {
            public System.Collections.Generic.IEnumerable<int> Range(int n)
            {
                for (var i = 0; i < n; i++)
                {
                    yield return i;
                }
            }
        }

        public abstract class AbstractBase
        {
            public abstract int DoSomething(int x);
        }
    }
    """;

    [Fact]
    public void FlowTraceIgnoreOnMethod_IsSkipped()
    {
        var (_, _, rewriter) = RoslynTestHelper.Instrument(EligibilitySource);
        Assert.Contains(rewriter.Skipped, s => s.MemberName.Contains("Ignored") && s.Reason.Contains("FlowTraceIgnore"));
    }

    [Fact]
    public void FlowTraceIgnoreOnClass_SkipsAllItsMethods()
    {
        var (_, _, rewriter) = RoslynTestHelper.Instrument(EligibilitySource);
        Assert.Contains(rewriter.Skipped, s => s.Reason.Contains("FlowTraceIgnore") && s.FilePath == "Sample.cs");
        // Every method inside ExcludedType should be skipped, and none of its methods should count
        // toward InstrumentedCount.
    }

    [Fact]
    public void IteratorMethod_IsSkipped_NotInstrumented()
    {
        var (instrumented, _, rewriter) = RoslynTestHelper.Instrument(EligibilitySource);
        Assert.Contains(rewriter.Skipped, s => s.MemberName.Contains("Range") && s.Reason.Contains("iterator"));

        // Compiles fine (i.e. we didn't try to wrap a yield in an illegal try/catch).
        RoslynTestHelper.CompileAndLoad(instrumented, "Eligibility1");
    }

    [Fact]
    public void AbstractMethod_IsSkipped_NoBodyToInstrument()
    {
        var (_, _, rewriter) = RoslynTestHelper.Instrument(EligibilitySource);
        Assert.Contains(rewriter.Skipped, s => s.MemberName.Contains("DoSomething") && s.Reason.Contains("no method body"));
    }

    [Fact]
    public void ExcludedNamespace_ConfigOption_SkipsMatchingMembers()
    {
        var config = FlowTracerConfig.Default;
        config.ExcludeNamespaces.Add("Sample");
        var (_, count, rewriter) = RoslynTestHelper.Instrument(EligibilitySource, config);

        Assert.Equal(0, count);
        Assert.True(rewriter.Skipped.All(s => s.Reason.Contains("excluded by configuration") || s.Reason.Contains("FlowTraceIgnore") || s.Reason.Contains("iterator") || s.Reason.Contains("no method body")));
    }
}
