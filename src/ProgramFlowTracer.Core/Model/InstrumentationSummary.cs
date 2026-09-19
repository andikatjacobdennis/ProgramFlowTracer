namespace ProgramFlowTracer.Core.Model;

/// <summary>Aggregate result of instrumenting one project.</summary>
public sealed class InstrumentationSummary
{
    public string ProjectPath { get; init; } = string.Empty;

    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>The root directory that was copied to produce <see cref="OutputDirectory"/> (its
    /// ".instrumented" sibling). For a single project whose <c>ProjectReference</c>s reach outside
    /// its own directory, this is the lowest common ancestor across the whole instrumented set,
    /// not necessarily this project's own directory - callers that need to map
    /// <see cref="ProjectPath"/> to its copied location should do so relative to this, not to
    /// <c>Path.GetDirectoryName(ProjectPath)</c>.</summary>
    public string SourceRoot { get; init; } = string.Empty;

    public int FilesProcessed { get; set; }

    public int FilesModified { get; set; }

    public int MethodsInstrumented { get; set; }

    /// <summary>True when this project's own source files were rewritten where they live, rather
    /// than in the "*.instrumented" copy. See <c>--in-place</c>.</summary>
    public bool InstrumentedInPlace { get; init; }

    public List<SkippedMethodInfo> SkippedMethods { get; } = new();

    /// <summary>Conditions worth telling the user about that are not failures - most importantly
    /// a project that was left uninstrumented because it lives outside the copied tree, which is
    /// otherwise invisible: the build still succeeds and the application still runs, it just
    /// never reports anything.</summary>
    public List<string> Warnings { get; } = new();

    public List<string> Errors { get; } = new();
}
