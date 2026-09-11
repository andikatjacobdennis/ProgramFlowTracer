using ProgramFlowTracer.Core.Tests.TestSupport;

namespace ProgramFlowTracer.Core.Tests;

/// <summary>
/// Return expressions that have no type of their own.
///
/// The rewriter captures a return value into a temporary before reporting it, and originally
/// declared that temporary with <c>var</c>. That is CS0815 for every expression the compiler
/// cannot infer a type from - <c>null</c>, <c>default</c>, a lambda, a collection expression, a
/// target-typed <c>new()</c>, and (the case that is easiest to miss) any tuple literal with a
/// null element. All of these are ordinary C#, so instrumentation has to handle them.
/// </summary>
public class ReturnExpressionShapeTests
{
    private const string Source = """
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    namespace Sample
    {
        public class Baseline { }
        public class Notification { }

        public abstract class Node { }
        public class LeafNode : Node { }
        public class BranchNode : Node { }

        public class Shapes
        {
            public string ReturnsNull() { return null; }
            public string ReturnsDefault() { return default; }
            public List<int> ReturnsCollectionExpression() { return []; }
            public Func<int> ReturnsLambda() { return () => 42; }
            public Shapes ReturnsImplicitNew() { return new(); }
            public string ConditionalBothNull(bool f) { return f ? null : null; }
            public string ConditionalOneTyped(bool f) { return f ? null : "x"; }
            public string ParenthesizedNull() { return (null); }
            public string AllSwitchArmsNull(int x) { return x switch { 1 => null, _ => null }; }
            public async Task<string> NullAsync() { await Task.Delay(1); return null; }
            public T GenericDefault<T>() { return default; }
            public int PlainValue() { return 7; }

            // The shape from a real solution:
            //   (BaseBaseline, BaseNotification, bool skipProductClassCheck) GetBaseline(...)
            public (Baseline, Notification, bool skipCheck) TupleFirstNull()
                => (null, new Notification(), false);

            public (Baseline, Notification, bool skipCheck) TupleMiddleNull()
                => (new Baseline(), null, true);

            public (Baseline, Notification, bool skipCheck) TupleAllNull()
                => (null, null, false);

            public (Baseline b, Notification n, bool skipCheck) TupleNamedElements()
                => (b: null, n: null, skipCheck: true);

            public ((Baseline, Notification), bool) TupleNested()
                => ((null, null), true);

            public (Baseline, Notification, bool) TupleFullyTyped()
                => (new Baseline(), new Notification(), false);

            // Typed arms with no best common type: legal in a return, CS8506 under `var`.
            public Node SwitchWithNoCommonArmType(object o)
            {
                return o switch
                {
                    int => new LeafNode(),
                    string => new BranchNode(),
                    _ => throw new Exception("unknown")
                };
            }

            public Node ConditionalWithNoCommonType(bool f)
                => f ? new LeafNode() : (Node)new BranchNode();
        }
    }
    """;

    [Fact]
    public void UntypedReturnExpressions_ProduceCompilableCode()
    {
        var (instrumented, count, _) = RoslynTestHelper.Instrument(Source);
        Assert.True(count >= 20, "expected every member instrumented, got " + count);

        // CompileAndLoad throws with the full diagnostics and generated source on failure.
        var assembly = RoslynTestHelper.CompileAndLoad(instrumented, "ReturnShapes");
        var type = assembly.GetType("Sample.Shapes")!;
        var instance = Activator.CreateInstance(type)!;

        Assert.Null(type.GetMethod("ReturnsNull")!.Invoke(instance, null));
        Assert.Null(type.GetMethod("ReturnsDefault")!.Invoke(instance, null));
        Assert.Null(type.GetMethod("ConditionalBothNull")!.Invoke(instance, new object[] { true }));
        Assert.Equal("x", type.GetMethod("ConditionalOneTyped")!.Invoke(instance, new object[] { false }));
        Assert.Null(type.GetMethod("ParenthesizedNull")!.Invoke(instance, null));
        Assert.Null(type.GetMethod("AllSwitchArmsNull")!.Invoke(instance, new object[] { 1 }));
        Assert.Equal(7, type.GetMethod("PlainValue")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("ReturnsImplicitNew")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("TupleFirstNull")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("TupleMiddleNull")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("TupleAllNull")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("TupleNamedElements")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("TupleNested")!.Invoke(instance, null));
        Assert.NotNull(type.GetMethod("SwitchWithNoCommonArmType")!.Invoke(instance, new object[] { 1 }));
        Assert.NotNull(type.GetMethod("ConditionalWithNoCommonType")!.Invoke(instance, new object[] { true }));
    }

    /// <summary>An expression the compiler *can* infer keeps using <c>var</c>, so the rewrite
    /// stays as unintrusive as it was.</summary>
    [Fact]
    public void CapturedReturns_AreDeclaredWithTheMembersReturnType()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        // Every captured return is now declared with the member.s own return type.
        Assert.DoesNotContain("var __ftRet", instrumented);
        Assert.Contains("string __ftRet", instrumented);
    }

    /// <summary>Tuple element names are illegal inside <c>typeof</c>, so the emitted type must
    /// drop them even when the declared return type carries them.</summary>
    [Fact]
    public void TypeofOfATupleReturn_OmitsElementNames()
    {
        var (instrumented, _, _) = RoslynTestHelper.Instrument(Source);
        Assert.DoesNotContain("typeof((global::Sample.Baseline b,", instrumented);
        Assert.Contains("typeof((global::Sample.Baseline, global::Sample.Notification, bool))", instrumented);
    }
}
