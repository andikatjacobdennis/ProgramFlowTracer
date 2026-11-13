using System.Diagnostics;
using ProgramFlowTracer.Core.Engine;
using ProgramFlowTracer.Core.Workspace;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.IntegrationTests;

/// <summary>
/// Exercises the real, end-to-end pipeline: MSBuildWorkspace loads an actual .csproj from disk,
/// <see cref="InstrumentationEngine"/> writes an instrumented copy, and that copy is built and run
/// with the real <c>dotnet</c> CLI - the same path <c>ProgramFlowTracer.Cli</c> uses. Unlike
/// <c>ProgramFlowTracer.Core.Tests</c> (in-memory compilation, no real MSBuild), this validates
/// that generated projects actually restore/build/run outside the test process.
/// </summary>
public class EndToEndInstrumentationTests : IDisposable
{
    private readonly string _fixtureSource;
    private readonly List<string> _cleanupPaths = new();

    public EndToEndInstrumentationTests()
    {
        MsBuildEnvironment.EnsureRegistered();

        var here = Path.GetDirectoryName(typeof(EndToEndInstrumentationTests).Assembly.Location)!;
        // Walk up from bin/Debug/net10.0 to the test project directory, then into Fixtures.
        var projectDir = FindContainingDirectory(here, "ProgramFlowTracer.IntegrationTests");
        _fixtureSource = Path.Combine(projectDir, "Fixtures", "FixtureApp");
    }

    [Fact]
    public async Task Instrument_DoesNotModifyOriginalSource()
    {
        var original = Path.Combine(_fixtureSource, "Program.cs");
        var originalContent = await File.ReadAllTextAsync(original);
        var originalHash = originalContent.GetHashCode();

        var workDir = CopyFixtureToTempDir();
        var config = FlowTracerConfig.Default;
        var engine = new InstrumentationEngine();
        await engine.InstrumentAsync(Path.Combine(workDir, "FixtureApp.csproj"), config);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(workDir, "Program.cs"));
        Assert.Equal(originalHash, afterContent.GetHashCode());
    }

    [Fact]
    public async Task Instrument_ProducesCompilableProject()
    {
        var workDir = CopyFixtureToTempDir();
        var config = FlowTracerConfig.Default;
        var engine = new InstrumentationEngine();
        var summaries = await engine.InstrumentAsync(Path.Combine(workDir, "FixtureApp.csproj"), config);

        Assert.Single(summaries);
        Assert.True(summaries[0].MethodsInstrumented >= 3);

        var outputDir = InstrumentationEngine.GetOutputRootFor(Path.Combine(workDir, "FixtureApp.csproj"));
        Assert.True(Directory.Exists(outputDir));

        var buildResult = await RunDotnetAsync(outputDir, "build");
        Assert.Equal(0, buildResult.ExitCode);
    }

    [Fact]
    public async Task Run_ProducesTraceWithExpectedEvents_AndPreservesBehavior()
    {
        var workDir = CopyFixtureToTempDir();
        var config = FlowTracerConfig.Default;
        var engine = new InstrumentationEngine();
        await engine.InstrumentAsync(Path.Combine(workDir, "FixtureApp.csproj"), config);

        var outputDir = InstrumentationEngine.GetOutputRootFor(Path.Combine(workDir, "FixtureApp.csproj"));
        var runResult = await RunDotnetAsync(outputDir, "run");

        Assert.Equal(0, runResult.ExitCode);
        Assert.Contains("3 + 4 = 7", runResult.StdOut);
        Assert.Contains("10 / 2 = 5", runResult.StdOut);

        var eventsFile = Directory.GetFiles(Path.Combine(outputDir, ".flowtrace"), "events.jsonl", SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(eventsFile);

        var lines = await File.ReadAllLinesAsync(eventsFile!);
        Assert.True(lines.Length >= 6); // Main, Add, Divide each enter+exit at minimum.

        var hasAddExit = lines.Any(l => l.Contains("\"method\":\"Add\"") && l.Contains("\"eventType\":\"MethodExit\""));
        Assert.True(hasAddExit);
    }

    private string CopyFixtureToTempDir()
    {
        var dest = Path.Combine(Path.GetTempPath(), "pft_fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(_fixtureSource))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        _cleanupPaths.Add(dest);
        _cleanupPaths.Add(dest.TrimEnd(Path.DirectorySeparatorChar) + ".instrumented");
        return dest;
    }

    private static string FindContainingDirectory(string start, string directoryNameSuffix)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.Name.Contains(directoryNameSuffix))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate a directory containing '{directoryNameSuffix}' above '{start}'.");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDotnetAsync(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)!;
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    public void Dispose()
    {
        foreach (var path in _cleanupPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
