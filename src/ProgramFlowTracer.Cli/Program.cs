using ProgramFlowTracer.Cli.Commands;
using ProgramFlowTracer.Core.Workspace;

try
{
    // Registration has to happen before anything touches a workspace type, which is well before
    // the arguments are parsed - so --verbose is read straight off argv here.
    MsBuildEnvironment.EnsureRegistered(
        args.Contains("--verbose") ? Console.WriteLine : null);
}
catch (InvalidOperationException ex)
{
    // The message already says which SDKs were found and what to install. A stack trace through
    // MSBuildLocator on top of that would only bury it.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        // ---------------------------------------------------------------------------------
        // Local debugging shortcut.
        //
        // Launching from an IDE (F5) passes no arguments, which would otherwise just print the
        // usage text. Running the real scenario from here instead means the whole pipeline -
        // MSBuild load, rewrite, in-place patching - can be stepped through in a debugger.
        //
        // EDIT THE PATHS BELOW. They are absolute on purpose: --in-place matches a project by
        // full path as well as by name, and a full path is unambiguous when the same project
        // name appears more than once in a large solution.
        //
        // To go back to the normal behaviour, comment out `args = ...` and uncomment the two
        // lines under it.
        // ---------------------------------------------------------------------------------
        args = new[]
        {
            "instrument",
            @"C:\Users\A549124\source\repos\SoftwareBreakdown\src\SoftwareBreakdownService.sln",
            "--in-place",
            @"C:\Users\A549124\source\repos\SoftwareBreakdown\src\NVSComponents\NonUIServiceComponents\SoftwareBreakdownHost\SoftwareBreakdownHost.csproj," +
            @"C:\Users\A549124\source\repos\SoftwareBreakdown\src\NVSComponents\BreakdownDomain\BreakdownDomain.Services\BreakdownDomain.Services.csproj",
            "--no-values",
            "--no-backup",
            "--verbose"
        };

        Console.WriteLine("No arguments given - using the debugging defaults in Program.cs:");
        Console.WriteLine("  " + string.Join(" ", args));
        Console.WriteLine();

        // PrintUsage();
        // return 1;
    }

    var command = args[0];
    var rest = args.Skip(1).ToArray();
    var verbose = rest.Contains("--verbose") || rest.Contains("-v");
    rest = rest.Where(a => a is not ("--verbose" or "-v")).ToArray();

    // Structure-only tracing: record the call tree and timings, and nothing about values.
    var noValues = rest.Contains("--no-values");
    rest = rest.Where(a => a is not "--no-values").ToArray();

    // Projects to rewrite where they live instead of in the "*.instrumented" copy.
    var inPlaceProjects = TakeListOption(ref rest, "--in-place");

    // Version control is the better undo when there is one, so allow skipping the .pft-original
    // copies rather than littering the tree with them.
    var noBackup = rest.Contains("--no-backup");
    rest = rest.Where(a => a is not "--no-backup").ToArray();

    // Lightest possible instrumentation: a MethodEnter event and nothing else.
    var entryOnly = rest.Contains("--entry-only");
    rest = rest.Where(a => a is not "--entry-only").ToArray();

    try
    {
        switch (command)
        {
            case "instrument":
                if (rest.Length < 1)
                {
                    Console.Error.WriteLine("usage: ProgramFlowTracer instrument <project-or-solution> [--verbose] [--no-values] [--in-place <projects>]");
                    return 1;
                }

                return await InstrumentCommand.RunAsync(rest[0], verbose, noValues, inPlaceProjects, !noBackup, entryOnly);

            case "restore":
                if (rest.Length < 1)
                {
                    Console.Error.WriteLine("usage: ProgramFlowTracer restore <project-or-solution>");
                    return 1;
                }

                return RestoreCommand.Run(rest[0]);

            case "run":
                if (rest.Length < 1)
                {
                    Console.Error.WriteLine("usage: ProgramFlowTracer run <project> [-- <app args>]");
                    return 1;
                }

                var separatorIndex = Array.IndexOf(rest, "--");
                var projectArg = rest[0];
                var appArgs = separatorIndex >= 0 ? rest[(separatorIndex + 1)..] : Array.Empty<string>();
                return await RunCommand.RunAsync(projectArg, appArgs, verbose, noValues, inPlaceProjects, !noBackup, entryOnly);

            case "clean":
                return CleanCommand.Run(rest.Length > 0 ? rest[0] : null);

            case "--help":
            case "-h":
            case "help":
                PrintUsage();
                return 0;

            default:
                Console.Error.WriteLine($"error: unknown command '{command}'.");
                PrintUsage();
                return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        if (verbose)
        {
            Console.Error.WriteLine(ex.ToString());
        }

        return 1;
    }
}

static string[] TakeListOption(ref string[] args, string name)
{
    // Accepts both "--in-place A,B" and a repeated "--in-place A --in-place B".
    var values = new List<string>();
    var remaining = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        if (!string.Equals(args[i], name, StringComparison.Ordinal))
        {
            remaining.Add(args[i]);
            continue;
        }

        if (i + 1 < args.Length)
        {
            values.AddRange(args[++i].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    args = remaining.ToArray();
    return values.ToArray();
}

static void PrintUsage()
{
    Console.WriteLine("""
        ProgramFlowTracer - Roslyn-based runtime program-flow tracer for C#

        Usage:
          ProgramFlowTracer instrument <project-or-solution> [--verbose] [--no-values]
              Analyzes the project/solution and writes an instrumented copy to a sibling
              "<name>.instrumented" directory. The original source is never modified.

          ProgramFlowTracer run <project.csproj> [--verbose] [--no-values] [-- <app args>]
              Instruments a fresh copy, then builds and runs it. Trace output is written to
              ".flowtrace/" inside the instrumented copy's directory.

          ProgramFlowTracer restore <project-or-solution>
              Deletes the instrumented copy, and undoes any --in-place rewriting by restoring
              every backed-up original file.

          ProgramFlowTracer clean [project-or-solution]
              Removes trace output (.flowtrace/) and, if a project/solution is given, its
              instrumented copy too.

        Options:
          --verbose     Report every member that was left uninstrumented, and why.
          --no-values   Record only which methods were entered and exited, with timings,
                        threads and exceptions. No parameters, return values or ref/out
                        values are captured - the code to capture them is never generated,
                        so no arguments are boxed and no property getters are invoked.
          --entry-only  Record a MethodEnter event per call and nothing else: no exits, no
                        durations, no exception events. Injects exactly one statement at the
                        top of each method - no try/catch/finally, no temporary variable, and
                        every return statement left as written - so control flow and exception
                        behaviour are identical to uninstrumented, and the diff for a method is
                        one added line. The lightest and least invasive mode: use it to answer
                        "was this method reached?".
          --no-backup   Skip the "<file>.pft-original" copies that --in-place writes. Use when
                        version control is already your undo.
          --in-place <projects>
                        Comma-separated projects to rewrite WHERE THEY LIVE instead of in the
                        "*.instrumented" copy. Use this for projects the copy cannot reach -
                        typically ones referenced from outside the solution directory, which
                        are otherwise left uninstrumented and silently produce no events.
                        Each rewritten file is backed up as "<file>.pft-original" and listed
                        in ".flowtracer-inplace.json"; "restore" puts them all back.

        Configuration is read from the nearest ".flowtrace.json" file, searched for starting at
        the target project's directory and walking up through its parent directories.
        """);
}
