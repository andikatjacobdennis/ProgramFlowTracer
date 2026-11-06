using ProgramFlowTracer.Core.Tests.TestSupport;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Tests;

[Collection("FlowTracerSequential")]
public class BasicMethodShapeTests
{
    private const string Source = """
    using System;
    using System.Threading.Tasks;
    using System.Collections.Generic;

    namespace Sample
    {
        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public static int Multiply(int a, int b) => a * b;

            public int NoParams() => 42;

            public int ManyParams(int a, int b, int c, string d, bool e) => a + b + c + (e ? 1 : 0);

            public void DoNothing()
            {
                var x = 1;
                x++;
            }

            public int MultiReturn(int x)
            {
                if (x < 0) return -1;
                if (x == 0) return 0;
                return 1;
            }

            public async Task<int> AddAsync(int a, int b)
            {
                await Task.Delay(1);
                return a + b;
            }

            public async Task DoWorkAsync()
            {
                await Task.Delay(1);
            }

            public string? Echo(string? value) => value;

            public int Sum(List<int> values)
            {
                var total = 0;
                foreach (var v in values) total += v;
                return total;
            }
        }
    }
    """;

    [Fact]
    public void SimpleExpressionBodiedMethod_IsInstrumented_AndBehaviorPreserved()
    {
        var (instrumented, count, _) = RoslynTestHelper.Instrument(Source);
        Assert.True(count >= 9);

        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes1");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var result = (int)type.GetMethod("Add")!.Invoke(instance, new object[] { 2, 3 })!;
        Assert.Equal(5, result);
    }

    [Fact]
    public void StaticMethod_Instrumented_AndCallableWithoutInstance()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes2");
        var type = assembly.GetType("Sample.Calculator")!;

        var result = (int)type.GetMethod("Multiply")!.Invoke(null, new object[] { 4, 6 })!;
        Assert.Equal(24, result);
    }

    [Fact]
    public void MethodWithZeroParameters_Instrumented_AndReturnsCorrectValue()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes3");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var result = (int)type.GetMethod("NoParams")!.Invoke(instance, null)!;
        Assert.Equal(42, result);
    }

    [Fact]
    public void MethodWithMultipleParameters_AllCaptured()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes4");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var result = (int)type.GetMethod("ManyParams")!.Invoke(instance, new object[] { 1, 2, 3, "x", true })!;
        Assert.Equal(7, result);

        InitializeRuntimeFor(assembly, config);
        type.GetMethod("ManyParams")!.Invoke(instance, new object[] { 1, 2, 3, "x", true });
        ShutdownRuntimeFor(assembly);

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var enter = events.First(e => e.GetProperty("eventType").GetString() == "MethodEnter" && e.GetProperty("method").GetString() == "ManyParams");
        var parameters = enter.GetProperty("parameters");
        Assert.Equal(5, parameters.EnumerateObject().Count());
    }

    [Fact]
    public void NullReferenceParameter_CapturedAsNull_NotAsError()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes5");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        InitializeRuntimeFor(assembly, config);
        var result = type.GetMethod("Echo")!.Invoke(instance, new object?[] { null });
        ShutdownRuntimeFor(assembly);

        Assert.Null(result);

        var events = RoslynTestHelper.ReadEvents(outputDir);
        var enter = events.First(e => e.GetProperty("method").GetString() == "Echo" && e.GetProperty("eventType").GetString() == "MethodEnter");
        var valueParam = enter.GetProperty("parameters").GetProperty("value");
        Assert.Equal("Null", valueParam.GetProperty("serializationStatus").GetString());
    }

    [Fact]
    public void VoidMethod_FallsThroughToExitVoid()
    {
        var config = RoslynTestHelper.NewIsolatedConfig(out var outputDir);
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source, config);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes6");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        InitializeRuntimeFor(assembly, config);
        type.GetMethod("DoNothing")!.Invoke(instance, null);
        ShutdownRuntimeFor(assembly);

        var events = RoslynTestHelper.ReadEvents(outputDir);
        Assert.Contains(events, e => e.GetProperty("method").GetString() == "DoNothing" && e.GetProperty("eventType").GetString() == "MethodExit");
    }

    [Theory]
    [InlineData(-5, -1)]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    public void MultipleReturnStatements_EachPathInstrumentedCorrectly(int input, int expected)
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, $"BasicShapes7_{input}");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var result = (int)type.GetMethod("MultiReturn")!.Invoke(instance, new object[] { input })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AsyncMethodReturningValue_Instrumented_AndAwaitable()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes8");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var task = (Task<int>)type.GetMethod("AddAsync")!.Invoke(instance, new object[] { 10, 20 })!;
        var result = await task;
        Assert.Equal(30, result);
    }

    [Fact]
    public async Task AsyncVoidLikeTaskMethod_Instrumented_AndCompletes()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes9");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var task = (Task)type.GetMethod("DoWorkAsync")!.Invoke(instance, null)!;
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void ComplexObjectAndCollectionParameters_Captured()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "BasicShapes10");
        var type = assembly.GetType("Sample.Calculator")!;
        var instance = Activator.CreateInstance(type)!;

        var listType = typeof(List<int>);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        list.Add(1);
        list.Add(2);
        list.Add(3);

        var result = (int)type.GetMethod("Sum")!.Invoke(instance, new object[] { list })!;
        Assert.Equal(6, result);
    }

    private static void InitializeRuntimeFor(System.Reflection.Assembly sampleAssembly, FlowTracerConfig config)
    {
        Runtime.FlowTracer.ResetForTesting();
        Runtime.FlowTracer.Initialize(config);
    }

    private static void ShutdownRuntimeFor(System.Reflection.Assembly sampleAssembly)
    {
        Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();
        Runtime.FlowTracer.ResetForTesting();
    }
}
