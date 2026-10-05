using System.Reflection;

namespace ProgramFlowTracer.Core.Workspace;

/// <summary>
/// Finds the compiled <c>ProgramFlowTracer.Runtime.dll</c> that ships alongside
/// <c>ProgramFlowTracer.Core</c> (since the CLI has a <c>ProjectReference</c> to Runtime, the
/// build system already copies it next to Core/Cli's own output). Instrumented projects get a
/// plain assembly reference to a copy of this DLL rather than a <c>ProjectReference</c> back into
/// the tool's own source tree, so <c>instrument</c> works no matter where
/// ProgramFlowTracer itself is installed relative to the target project.
/// </summary>
public static class RuntimeAssemblyLocator
{
    private const string RuntimeAssemblyFileName = "ProgramFlowTracer.Runtime.dll";

    public static string? Locate()
    {
        var candidateDirectories = new List<string>();

        var coreAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (coreAssemblyDir is not null)
        {
            candidateDirectories.Add(coreAssemblyDir);
        }

        var entryAssemblyDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
        if (entryAssemblyDir is not null)
        {
            candidateDirectories.Add(entryAssemblyDir);
        }

        candidateDirectories.Add(AppContext.BaseDirectory);

        foreach (var dir in candidateDirectories.Distinct())
        {
            var candidate = Path.Combine(dir, RuntimeAssemblyFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
