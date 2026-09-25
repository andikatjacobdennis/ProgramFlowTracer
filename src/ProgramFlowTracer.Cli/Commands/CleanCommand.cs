using ProgramFlowTracer.Core.Engine;
using ProgramFlowTracer.Core.Workspace;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Cli.Commands;

/// <summary>
/// "clean" removes trace output (<c>.flowtrace/</c>) and, when a project/solution is given, the
/// corresponding "*.instrumented" copy too. With no argument it just cleans the current directory's
/// trace output, matching the bare "ProgramFlowTracer clean" form from the spec.
/// </summary>
internal static class CleanCommand
{
    public static int Run(string? projectOrSolutionPath)
    {
        var cleanedAnything = false;

        if (projectOrSolutionPath is not null)
        {
            var fullPath = Path.GetFullPath(projectOrSolutionPath);
            if (!File.Exists(fullPath))
            {
                Console.Error.WriteLine($"error: '{projectOrSolutionPath}' was not found.");
                return 1;
            }

            var outputRoot = InstrumentationEngine.GetOutputRootFor(fullPath);
            if (Directory.Exists(outputRoot))
            {
                DirectoryCopier.DeleteIfExists(outputRoot);
                Console.WriteLine($"Removed instrumented copy: {outputRoot}");
                cleanedAnything = true;
            }
            else
            {
                Console.WriteLine($"No instrumented copy found at the expected location: {outputRoot}");
                Console.WriteLine("If this project references other projects outside its own directory, the copy may live under");
                Console.WriteLine("their common ancestor instead - check the path 'instrument'/'run' printed on success.");
            }

            var sourceDir = Path.GetDirectoryName(fullPath)!;
            var config = FlowTracerConfig.LoadFromNearestOrDefault(sourceDir);
            cleanedAnything |= CleanTraceOutput(Path.Combine(sourceDir, config.OutputDirectory));
        }
        else
        {
            var config = FlowTracerConfig.LoadFromNearestOrDefault(Directory.GetCurrentDirectory());
            cleanedAnything = CleanTraceOutput(Path.Combine(Directory.GetCurrentDirectory(), config.OutputDirectory));
        }

        if (!cleanedAnything)
        {
            Console.WriteLine("Nothing to clean.");
        }

        return 0;
    }

    private static bool CleanTraceOutput(string traceDirectory)
    {
        if (!Directory.Exists(traceDirectory))
        {
            return false;
        }

        Directory.Delete(traceDirectory, recursive: true);
        Console.WriteLine($"Removed trace output: {traceDirectory}");
        return true;
    }
}
