using System.Diagnostics;
using ProgramFlowTracer.Core.Engine;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Cli.Commands;

internal static class RunCommand
{
    public static async Task<int> RunAsync(string projectOrSolutionPath, string[] appArgs, bool verbose, bool noValues = false, IReadOnlyCollection<string>? inPlaceProjects = null, bool keepBackups = true, bool entryOnly = false)
    {
        var fullPath = Path.GetFullPath(projectOrSolutionPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"error: '{projectOrSolutionPath}' was not found.");
            return 1;
        }

        if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("error: 'run' needs a specific .csproj (a solution can contain more than one runnable project).");
            Console.Error.WriteLine("Instrument the solution with 'ProgramFlowTracer instrument MySolution.sln', then run the instrumented .csproj directly with 'dotnet run' from inside the '*.instrumented' output directory.");
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
            config.CaptureParameters = false;
            config.CaptureReturnValues = false;
            Console.WriteLine("Values disabled: recording call structure and timings only.");
        }

        Console.WriteLine("Instrumenting (fresh copy)...");
        var engine = new InstrumentationEngine();
        var summaries = await engine.InstrumentAsync(fullPath, config, log: verbose ? Console.WriteLine : null, inPlaceProjects: inPlaceProjects, keepBackups: keepBackups);

        // Don't re-derive the output location from `fullPath` alone (via the old
        // GetOutputRootFor-style path math): when this project pulls in ProjectReferences that
        // live outside its own directory, InstrumentAsync copies from their common ancestor
        // instead, so the real output root can differ from "this project's own directory +
        // .instrumented". Read back exactly what InstrumentAsync actually used.
        var rootSummary = summaries.FirstOrDefault(s => string.Equals(s.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (rootSummary is null || rootSummary.SourceRoot.Length == 0)
        {
            Console.Error.WriteLine($"error: instrumentation did not report a result for '{fullPath}'.");
            return 1;
        }

        var outputRoot = rootSummary.OutputDirectory;
        var copiedProject = Path.Combine(outputRoot, Path.GetRelativePath(rootSummary.SourceRoot, fullPath));
        if (!File.Exists(copiedProject))
        {
            Console.Error.WriteLine($"error: instrumentation did not produce '{copiedProject}'.");
            return 1;
        }

        WarnIfNotRunnable(copiedProject);

        Console.WriteLine($"Running '{copiedProject}'...");
        Console.WriteLine();

        // The instrumented copy holds the *code*; the application still runs from its own
        // directory. Anything it writes to a relative path - its own log files above all - then
        // lands exactly where an uninstrumented run would put it, instead of inside the
        // "*.instrumented" tree that the next instrument deletes wholesale.
        //
        // 'dotnet run' launches the app with the caller's working directory, so setting it here
        // really does decide where the application thinks it is running.
        //
        // This also puts the trace output beside the real project rather than inside the
        // throwaway copy - which is where 'clean' has always looked for it.
        var appWorkingDirectory = Path.GetDirectoryName(fullPath)!;

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = appWorkingDirectory,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        // A full path, because the working directory is no longer the copy's own directory.
        psi.ArgumentList.Add(copiedProject);
        foreach (var a in appArgs)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            Console.Error.WriteLine("error: could not start 'dotnet run'.");
            return 1;
        }

        await process.WaitForExitAsync();

        Console.WriteLine();
        ReportTraceOutput(appWorkingDirectory, outputRoot, config, process.ExitCode);
        return process.ExitCode;
    }

    /// <summary>
    /// 'dotnet run' only launches runnable applications. A project without OutputType Exe/WinExe
    /// (e.g. a library that some other host process loads, such as a Windows Service component)
    /// will fail to run - but that failure happens deep inside 'dotnet run' and easily gets lost
    /// among its own build output, followed by a misleadingly confident "Trace output: ..." line.
    /// Catching the common case upfront, before spending time on a doomed run, makes that much
    /// easier to notice.
    /// </summary>
    private static void WarnIfNotRunnable(string csprojPath)
    {
        try
        {
            var text = File.ReadAllText(csprojPath);
            var isExe = text.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase);

            // Web SDK projects (Microsoft.NET.Sdk.Web) and Worker SDK projects (Microsoft.NET.Sdk.Worker)
            // are runnable even without an explicit <OutputType> - the SDK defaults it to Exe for them.
            // Only projects using the plain Microsoft.NET.Sdk (or Sdk.Razor, etc.) actually default to
            // Library, so only warn when there's no OutputType AND no Sdk that implies Exe.
            var usesRunnableSdk = text.Contains("Sdk=\"Microsoft.NET.Sdk.Web\"", StringComparison.OrdinalIgnoreCase)
                                   || text.Contains("Sdk=\"Microsoft.NET.Sdk.Worker\"", StringComparison.OrdinalIgnoreCase);

            if (!isExe && !usesRunnableSdk)
            {
                Console.WriteLine();
                Console.WriteLine($"warning: '{Path.GetFileName(csprojPath)}' does not declare <OutputType>Exe</OutputType> (or WinExe).");
                Console.WriteLine("         'dotnet run' only works for runnable applications. If this project is a library that");
                Console.WriteLine("         another executable hosts (e.g. a Windows Service component), 'run' cannot launch it here -");
                Console.WriteLine("         instrument the solution instead ('ProgramFlowTracer instrument MySolution.sln') and run the");
                Console.WriteLine("         actual host executable directly; the instrumented library will still produce trace output");
                Console.WriteLine("         once the host process loads and calls into it.");
                Console.WriteLine();
            }
        }
        catch
        {
            // Best-effort hint only; never block the actual run attempt over this.
        }
    }

    /// <summary>
    /// Confirms trace output actually exists before reporting success, and gives a detailed,
    /// concrete diagnosis when it doesn't - rather than unconditionally printing a path that may
    /// never have been created. The most common reasons trace output is missing even though this
    /// command "ran" something: the traced process never actually started/ran successfully (see
    /// the exit code and 'dotnet run' output above), tracing is disabled in the nearest
    /// '.flowtrace.json', or tracer initialization failed inside the traced process for a reason
    /// recorded in an 'init-error.log' (checked below).
    /// </summary>
    /// <summary>The trace now lands beside the real project (the app.s working directory), while
    /// the runtime DLL still lives in the instrumented copy - so both paths are needed here.</summary>
    private static void ReportTraceOutput(string appWorkingDirectory, string outputRoot, FlowTracerConfig config, int exitCode)
    {
        var traceOutputPath = Path.Combine(appWorkingDirectory, config.OutputDirectory);
        var runsPath = Path.Combine(traceOutputPath, "runs");
        var hasRuns = Directory.Exists(runsPath) && Directory.EnumerateDirectories(runsPath).Any();

        if (hasRuns)
        {
            var runCount = Directory.EnumerateDirectories(runsPath).Count();
            Console.WriteLine($"Trace output: {traceOutputPath} ({runCount} run{(runCount == 1 ? string.Empty : "s")})");
            if (exitCode != 0)
            {
                Console.WriteLine($"note: the traced application exited with code {exitCode}; the trace above reflects execution up to that point.");
            }

            return;
        }

        Console.WriteLine($"No trace output was found at the expected location: {traceOutputPath}");
        Console.WriteLine();
        Console.WriteLine("Diagnostics:");
        Console.WriteLine($"  - 'dotnet run' exit code:        {exitCode}{(exitCode != 0 ? " (non-zero - scroll up for the build/app's own error output)" : " (the process ran and exited cleanly)")}");
        Console.WriteLine($"  - Expected trace directory:      {traceOutputPath}");
        Console.WriteLine($"  - ...exists on disk:             {Directory.Exists(traceOutputPath)}");

        var runtimeDll = Path.Combine(outputRoot, "_flowtracer_runtime", "ProgramFlowTracer.Runtime.dll");
        Console.WriteLine($"  - Runtime assembly present:      {File.Exists(runtimeDll)} ({runtimeDll})");
        Console.WriteLine($"  - Tracing enabled in config:      {config.Enabled}");

        var initErrorInOutput = Path.Combine(traceOutputPath, "init-error.log");
        var initErrorFallback = Path.Combine(Path.GetTempPath(), "ProgramFlowTracer", "last-init-error.log");
        var foundLog = false;
        foreach (var candidate in new[] { initErrorInOutput, initErrorFallback })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            foundLog = true;
            Console.WriteLine();
            Console.WriteLine($"Found a tracer diagnostics file at '{candidate}':");
            try
            {
                Console.WriteLine(File.ReadAllText(candidate));
            }
            catch (Exception readEx)
            {
                Console.WriteLine($"  (could not read it: {readEx.Message})");
            }
        }

        if (!foundLog)
        {
            Console.WriteLine();
            Console.WriteLine("No init-error.log was found either. Most likely explanations:");
            Console.WriteLine("  - The traced application never actually launched successfully - check the exit code and the");
            Console.WriteLine("    'dotnet run' output above.");
            Console.WriteLine("  - The process exited before any instrumented method ran, so tracing never got a chance to start.");
            Console.WriteLine("  - A '.flowtrace.json' file near this project sets \"enabled\": false.");
            Console.WriteLine("  - This project isn't the one that actually runs the traced code - see the OutputType warning");
            Console.WriteLine("    above, if one was printed.");
        }

        Console.WriteLine();
        Console.WriteLine("Re-run with --verbose for more detail from the instrumentation step, or set the");
        Console.WriteLine("PROGRAMFLOWTRACER_DEBUG=1 environment variable before running the traced app so tracer");
        Console.WriteLine("initialization failures print to its own stderr immediately instead of only to a log file.");
    }
}
