using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ProgramFlowTracer.Core.Rewriting;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Tests.TestSupport;

/// <summary>Shared plumbing for tests that need to instrument a snippet of C# source and then
/// actually compile/emit/execute the result, so tests assert on real runtime behavior rather than
/// just "the syntax tree looks plausible".</summary>
internal static class RoslynTestHelper
{
    public static IEnumerable<MetadataReference> TrustedPlatformReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (File.Exists(path))
            {
                yield return MetadataReference.CreateFromFile(path);
            }
        }
    }

    public static (string InstrumentedSource, int InstrumentedCount, InstrumentationRewriter Rewriter) Instrument(
        string source,
        FlowTracerConfig? config = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Sample.cs");
        var references = TrustedPlatformReferences()
            .Append(MetadataReference.CreateFromFile(typeof(Runtime.FlowTracer).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "SampleAssembly",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        IMethodSymbol? entryPoint = null;
        try
        {
            entryPoint = compilation.GetEntryPoint(default);
        }
        catch
        {
            // no entry point for a library compilation - expected.
        }

        var rewriter = new InstrumentationRewriter(semanticModel, config ?? FlowTracerConfig.Default, tree.FilePath, entryPoint);
        var newRoot = rewriter.Visit(tree.GetRoot())!.NormalizeWhitespace();

        return (newRoot.ToFullString(), rewriter.InstrumentedCount, rewriter);
    }

    /// <summary>Compiles instrumented source text into a real in-memory assembly and loads it, so
    /// tests can invoke the instrumented methods via reflection and observe both the return value
    /// and the resulting trace.</summary>
    public static Assembly CompileAndLoad(string instrumentedSource, string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(instrumentedSource, path: assemblyName + ".cs");
        var references = TrustedPlatformReferences()
            .Append(MetadataReference.CreateFromFile(typeof(Runtime.FlowTracer).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Instrumented code failed to compile:{Environment.NewLine}{errors}{Environment.NewLine}---{Environment.NewLine}{instrumentedSource}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    /// <summary>Points a fresh <see cref="FlowTracerConfig"/> at a unique temp directory so tests
    /// never collide with each other's trace output, and returns that directory for inspection.</summary>
    public static FlowTracerConfig NewIsolatedConfig(out string outputDirectory)
    {
        outputDirectory = Path.Combine(Path.GetTempPath(), "pft_test_" + Guid.NewGuid().ToString("N"));
        var config = FlowTracerConfig.Default;
        config.OutputDirectory = outputDirectory;
        return config;
    }

    public static IReadOnlyList<System.Text.Json.JsonElement> ReadEvents(string outputDirectory)
    {
        var runsDir = Path.Combine(outputDirectory, "runs");
        if (!Directory.Exists(runsDir))
        {
            return Array.Empty<System.Text.Json.JsonElement>();
        }

        var events = new List<System.Text.Json.JsonElement>();
        foreach (var runDir in Directory.GetDirectories(runsDir))
        {
            var eventsFile = Path.Combine(runDir, "events.jsonl");
            if (!File.Exists(eventsFile))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(eventsFile))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                events.Add(System.Text.Json.JsonDocument.Parse(line).RootElement.Clone());
            }
        }

        return events;
    }
}
