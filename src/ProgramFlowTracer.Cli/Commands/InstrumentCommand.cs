using ProgramFlowTracer.Core.Engine;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Cli.Commands;

internal static class InstrumentCommand
{
    public static async Task<int> RunAsync(string projectOrSolutionPath, bool verbose, bool noValues = false, IReadOnlyCollection<string>? inPlaceProjects = null, bool keepBackups = true, bool entryOnly = false)
    {
        var fullPath = Path.GetFullPath(projectOrSolutionPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"error: '{projectOrSolutionPath}' was not found.");
            return 1;
        }

        var config = FlowTracerConfig.LoadFromNearestOrDefault(Path.GetDirectoryName(fullPath)!);

        if (entryOnly)
        {
            config.RecordMethodExits = false;
            Console.WriteLine("Entry only: recording a MethodEnter event per call - no exits, durations or exceptions.");
        }

        if (noValues)
        {
            // Applied before instrumentation, not just at runtime: the rewriter reads these to
            // decide whether to emit the capture code at all.
            config.CaptureParameters = false;
            config.CaptureReturnValues = false;
            Console.WriteLine("Values disabled: recording call structure and timings only.");
        }

        Console.WriteLine($"Instrumenting {Path.GetFileName(fullPath)}...");

        var engine = new InstrumentationEngine();
        var summaries = await engine.InstrumentAsync(fullPath, config, log: verbose ? Console.WriteLine : null, inPlaceProjects: inPlaceProjects, keepBackups: keepBackups);

        var warnings = summaries.SelectMany(s => s.Warnings).ToList();
        var inPlaceCount = summaries.Count(s => s.InstrumentedInPlace && s.FilesModified > 0);

        var totalInstrumented = 0;
        var totalSkipped = 0;
        var totalFiles = 0;
        var hadErrors = false;

        foreach (var summary in summaries)
        {
            totalInstrumented += summary.MethodsInstrumented;
            totalSkipped += summary.SkippedMethods.Count;
            totalFiles += summary.FilesModified;

            foreach (var error in summary.Errors)
            {
                hadErrors = true;
                Console.Error.WriteLine($"error [{Path.GetFileName(summary.ProjectPath)}]: {error}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Done. {totalInstrumented} member(s) instrumented across {totalFiles} file(s).");

        if (inPlaceCount > 0)
        {
            Console.WriteLine($"{inPlaceCount} project(s) were rewritten IN PLACE - your own files were modified.");
            Console.WriteLine("Run 'ProgramFlowTracer restore' on the same target to put the originals back.");
        }

        if (warnings.Count > 0)
        {
            Console.WriteLine();
            foreach (var warning in warnings)
            {
                Console.WriteLine($"warning: {warning}");
            }
        }

        if (totalSkipped > 0)
        {
            Console.WriteLine($"{totalSkipped} member(s) were left uninstrumented (use --verbose to see why).");
            if (verbose)
            {
                foreach (var summary in summaries)
                {
                    foreach (var skip in summary.SkippedMethods)
                    {
                        Console.WriteLine($"  skip: {skip.MemberName} ({Path.GetFileName(skip.FilePath)}:{skip.Line}) - {skip.Reason}");
                    }
                }
            }
        }

        if (summaries.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Instrumented copy written to: {summaries[0].OutputDirectory}");
            Console.WriteLine(inPlaceCount > 0
                ? "Everything except the in-place project(s) above was left untouched. Run 'ProgramFlowTracer run <project>' to build and execute the instrumented copy."
                : "The original source was not modified. Run 'ProgramFlowTracer run <project>' to build and execute the instrumented copy.");
        }

        return hadErrors ? 1 : 0;
    }
}
