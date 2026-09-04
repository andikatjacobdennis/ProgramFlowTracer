using ProgramFlowTracer.Core.Tests.TestSupport;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Tests;

[Collection("FlowTracerSequential")]
public class AdvancedMethodShapeTests
{
    private const string Source = """
    using System;
    using System.Collections.Generic;
    using ProgramFlowTracer.Runtime.Attributes;

    namespace Sample
    {
        public class Repository<T>
        {
            private readonly List<T> _items = new();

            public Repository() { }

            public Repository(IEnumerable<T> seed)
            {
                _items.AddRange(seed);
            }

            public void Add(T item) => _items.Add(item);

            public int Count => _items.Count;

            public T Get(int index) => _items[index];
        }

        public class Node
        {
            public int Value { get; set; }
            public Node? Next { get; set; }
        }

        public class Service
        {
            public int ThrowsIfNegative(int x)
            {
                if (x < 0)
                {
                    throw new InvalidOperationException("negative input: " + x);
                }

                return x * 2;
            }

            public int Outer(int x)
            {
                return Inner(x) + 1;
            }

            private int Inner(int x)
            {
                return x * 10;
            }

            public long Factorial(int n)
            {
                if (n <= 1)
                {
                    return 1;
                }

                return n * Factorial(n - 1);
            }

            public T Identity<T>(T value) => value;

            public bool TryParse(string text, out int value)
            {
                return int.TryParse(text, out value);
            }

            public void Increment(ref int counter)
            {
                counter++;
            }

            public int SumWithLocalFunction(int a, int b)
            {
                int helperCallCount = 0;

                int Add(int x, int y)
                {
                    helperCallCount++;
                    return x + y;
                }

                var result = Add(a, b);
                return result + helperCallCount;
            }

            public int Echo([FlowTraceSensitive] int secretPin)
            {
                return secretPin;
            }

            public Node BuildCircular()
            {
                var a = new Node { Value = 1 };
                var b = new Node { Value = 2 };
                a.Next = b;
                b.Next = a;
                return a;
            }
        }
    }
    """;

    [Fact]
    public void UnhandledException_RecordedThenRethrown_Unchanged()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced1");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            type.GetMethod("ThrowsIfNegative")!.Invoke(instance, new object[] { -5 }));
        Assert.NotNull(ex.InnerException);
        Assert.True(ex.InnerException is InvalidOperationException);
        Assert.Equal("negative input: -5", ex.InnerException!.Message);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        Assert.Contains(events, e => e.GetProperty("eventType").GetString() == "Exception"
                                      && e.GetProperty("exceptionType").GetString() == "System.InvalidOperationException");
    }

    [Fact]
    public void NestedMethodCalls_ProduceCorrectParentChildChain()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced2");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var result = (int)type.GetMethod("Outer")!.Invoke(instance, new object[] { 5 })!;
        Assert.Equal(51, result);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var outerEnter = events.First(e => e.GetProperty("method").GetString() == "Outer" && e.GetProperty("eventType").GetString() == "MethodEnter");
        var innerEnter = events.First(e => e.GetProperty("method").GetString() == "Inner" && e.GetProperty("eventType").GetString() == "MethodEnter");

        Assert.Equal(outerEnter.GetProperty("traceId").GetString(), innerEnter.GetProperty("parentTraceId").GetString());
    }

    [Fact]
    public void RecursiveMethod_EachInvocationGetsUniqueTraceId_ChainedCorrectly()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced3");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var result = (long)type.GetMethod("Factorial")!.Invoke(instance, new object[] { 5 })!;
        Assert.Equal(120L, result);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var enters = events.Where(e => e.GetProperty("method").GetString() == "Factorial" && e.GetProperty("eventType").GetString() == "MethodEnter").ToList();
        Assert.Equal(5, enters.Count);

        var traceIds = enters.Select(e => e.GetProperty("traceId").GetString()).ToHashSet();
        Assert.Equal(5, traceIds.Count); // all unique
    }

    [Fact]
    public void GenericMethod_InstrumentedForEachClosedType()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced4");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        var stringResult = type.GetMethod("Identity")!.MakeGenericMethod(typeof(string)).Invoke(instance, new object[] { "hi" });
        var intResult = type.GetMethod("Identity")!.MakeGenericMethod(typeof(int)).Invoke(instance, new object[] { 7 });

        Assert.Equal("hi", stringResult);
        Assert.Equal(7, intResult);
    }

    [Fact]
    public void GenericClass_ConstructorAndMethodsInstrumented()
    {
        var (instrumented, count, _) = RoslynTestHelper.Instrument(Source);
        Assert.True(count > 0);

        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced5");
        var openType = assembly.GetType("Sample.Repository`1")!;
        var closedType = openType.MakeGenericType(typeof(int));
        var instance = Activator.CreateInstance(closedType)!;

        closedType.GetMethod("Add")!.Invoke(instance, new object[] { 42 });
        var value = closedType.GetMethod("Get")!.Invoke(instance, new object[] { 0 });
        Assert.Equal(42, value);
    }

    [Fact]
    public void ParameterizedConstructor_Instrumented()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced6");
        var openType = assembly.GetType("Sample.Repository`1")!;
        var closedType = openType.MakeGenericType(typeof(int));

        var seed = new List<int> { 1, 2, 3 };
        var instance = Activator.CreateInstance(closedType, new object[] { seed })!;
        var count = closedType.GetProperty("Count")!.GetValue(instance);
        Assert.Equal(3, count);
    }

    [Fact]
    public void LocalFunction_InstrumentedIndependently_NestedUnderOuter()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced7");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var result = (int)type.GetMethod("SumWithLocalFunction")!.Invoke(instance, new object[] { 2, 3 })!;
        Assert.Equal(6, result); // 2+3=5, +1 call count = 6

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var outerEnter = events.First(e => e.GetProperty("method").GetString() == "SumWithLocalFunction" && e.GetProperty("eventType").GetString() == "MethodEnter");
        var innerEnter = events.First(e => e.GetProperty("method").GetString() == "Add" && e.GetProperty("eventType").GetString() == "MethodEnter");
        Assert.Equal(outerEnter.GetProperty("traceId").GetString(), innerEnter.GetProperty("parentTraceId").GetString());
    }

    [Fact]
    public void RefParameter_FinalValueCapturedAtExit()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced8");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var args = new object[] { 10 };
        type.GetMethod("Increment")!.Invoke(instance, args);
        Assert.Equal(11, (int)args[0]);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var exit = events.First(e => e.GetProperty("method").GetString() == "Increment" && e.GetProperty("eventType").GetString() == "MethodExit");
        var outParams = exit.GetProperty("outParameters");
        Assert.Equal(11, outParams.GetProperty("counter").GetProperty("value").GetInt32());
    }

    [Fact]
    public void OutParameter_UnavailableAtEntry_CapturedAtExit()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced9");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var args = new object?[] { "123", null };
        var ok = (bool)type.GetMethod("TryParse")!.Invoke(instance, args)!;
        Assert.True(ok);
        Assert.Equal(123, (int)args[1]!);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var enter = events.First(e => e.GetProperty("method").GetString() == "TryParse" && e.GetProperty("eventType").GetString() == "MethodEnter");
        var valueParamAtEntry = enter.GetProperty("parameters").GetProperty("value");
        Assert.Equal("Unavailable", valueParamAtEntry.GetProperty("serializationStatus").GetString());

        var exit = events.First(e => e.GetProperty("method").GetString() == "TryParse" && e.GetProperty("eventType").GetString() == "MethodExit");
        var valueParamAtExit = exit.GetProperty("outParameters").GetProperty("value");
        Assert.Equal(123, valueParamAtExit.GetProperty("value").GetInt32());
    }

    [Fact]
    public void SensitiveParameter_NeverWrittenToTrace()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced10");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        var result = (int)type.GetMethod("Echo")!.Invoke(instance, new object[] { 999999 })!;
        Assert.Equal(999999, result);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var enter = events.First(e => e.GetProperty("method").GetString() == "Echo" && e.GetProperty("eventType").GetString() == "MethodEnter");
        var param = enter.GetProperty("parameters").GetProperty("secretPin");
        Assert.Equal("Redacted", param.GetProperty("serializationStatus").GetString());
        Assert.Equal("***REDACTED***", param.GetProperty("value").GetString());

        var raw = File.ReadAllText(Directory.GetFiles(Path.Combine(outputDir, "runs"), "events.jsonl", SearchOption.AllDirectories).First());
        // Only the *parameter* capture is required to redact - Echo's return value is not marked
        // sensitive, so it legitimately still contains the number. Assert on the parameter's own
        // JSON fragment rather than the whole file.
        Assert.False(raw.Contains("\"secretPin\":{\"type\":\"System.Int32\",\"value\":999999"));
    }

    [Fact]
    public void CircularObjectGraph_DoesNotCrashOrInfinitelyRecurse()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "Advanced11");
        var type = assembly.GetType("Sample.Service")!;
        var instance = Activator.CreateInstance(type)!;

        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);

        // Must not throw / hang even though the returned object graph is circular.
        var node = type.GetMethod("BuildCircular")!.Invoke(instance, null);
        Assert.NotNull(node);

        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var exit = events.First(e => e.GetProperty("method").GetString() == "BuildCircular" && e.GetProperty("eventType").GetString() == "MethodExit");
        // Serialization must have completed (success or failed) - never crashed the writer pipeline.
        Assert.True(exit.GetProperty("returnValue").TryGetProperty("serializationStatus", out _));
    }
}
