using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using ProgramFlowTracer.Core.Model;
using ProgramFlowTracer.Core.Rewriting;
using ProgramFlowTracer.Core.Workspace;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Engine;

/// <summary>
/// Top-level orchestrator for the <c>instrument</c> command: loads a project or solution with
/// Roslyn, copies its source tree to a sibling "*.instrumented" directory (never touching the
/// original files), rewrites eligible members in the copy, and wires the copy up to reference
/// ProgramFlowTracer.Runtime.
/// </summary>
public sealed class InstrumentationEngine
{
    private const string InstrumentedSuffix = ".instrumented";
    private const string RuntimeDllFolderName = "_flowtracer_runtime";

    /// <summary>Suffix for the untouched copy kept beside every file --in-place rewrites.</summary>
    private const string BackupSuffix = ".pft-original";

    /// <summary>Formatting needs a workspace only for its options; an adhoc one carries the
    /// defaults and costs nothing to keep for the process.</summary>
    private static readonly AdhocWorkspace FormattingWorkspace = new();

    /// <summary>
    /// The encoding a source file is already in, so it can be written back the same way.
    /// Falls back to UTF-8 with a BOM, which is what Visual Studio writes by default.
    /// </summary>
    private static Encoding EncodingOf(string path)
    {
        // Read the signature bytes directly rather than asking StreamReader.CurrentEncoding: for a
        // file with no BOM it hands back whatever default was passed in, and the static
        // Encoding.UTF8 *emits* a BOM - so a BOM-less file would quietly gain one, which is the
        // very problem this exists to avoid.
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[3];
            var read = stream.Read(signature);

            if (read == 3 && signature[0] == 0xEF && signature[1] == 0xBB && signature[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }
        }
        catch
        {
            // Fall through to the no-BOM default.
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    public async Task<IReadOnlyList<InstrumentationSummary>> InstrumentAsync(
        string projectOrSolutionPath,
        FlowTracerConfig config,
        Action<string>? log = null,
        IReadOnlyCollection<string>? inPlaceProjects = null,
        bool keepBackups = true,
        CancellationToken cancellationToken = default)
    {
        var fullInputPath = Path.GetFullPath(projectOrSolutionPath);
        if (!File.Exists(fullInputPath))
        {
            throw new FileNotFoundException($"Project or solution file not found: {fullInputPath}");
        }

        var isSolution = fullInputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

        MsBuildEnvironment.EnsureRegistered();

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) => log?.Invoke($"[workspace] {e.Diagnostic.Kind}: {e.Diagnostic.Message}");

        var projects = new List<Project>();
        if (isSolution)
        {
            var solution = await workspace.OpenSolutionAsync(fullInputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            projects.AddRange(solution.Projects);
        }
        else
        {
            // Instrumenting a single project used to mean *only* that project's own files ever
            // got copied or rewritten. Anything it reached via <ProjectReference> - which is
            // exactly where most of a real call chain lives - was left completely untouched: not
            // copied, not instrumented, and therefore structurally incapable of ever producing a
            // trace event. Calls crossing into one of those projects don't fail or get dropped;
            // they were simply never wired up to report anything, which looks identical to "no
            // nested calls" from the trace viewer. MSBuildWorkspace already loads the full
            // referenced-project closure into the workspace to resolve project-to-project
            // references for compilation - OpenProjectAsync just doesn't hand it back to us, so we
            // walk it out ourselves and instrument the whole reachable graph, not just the root.
            var project = await workspace.OpenProjectAsync(fullInputPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            projects.AddRange(CollectProjectAndReferencesTransitively(project));

            if (projects.Count > 1)
            {
                log?.Invoke($"'{project.Name}' references {projects.Count - 1} other project(s) in this workspace - " +
                            "instrumenting all of them too so calls that cross project boundaries are still captured:");
                foreach (var referenced in projects.Where(p => p.Id != project.Id))
                {
                    log?.Invoke($"  + {referenced.Name}");
                }
            }
        }

        // For a solution, its own directory is the natural root. For a single project (now
        // possibly pulling in sibling projects that live outside its own directory), the root has
        // to be the lowest common ancestor of every involved project so the copy step below
        // actually captures all of them, not just the one that was named on the command line.
        var sourceRoot = isSolution
            ? Path.GetDirectoryName(fullInputPath)!
            : ComputeCommonRoot(projects.Where(p => p.FilePath is not null).Select(p => Path.GetDirectoryName(p.FilePath)!));

        var outputRoot = ComputeOutputRoot(sourceRoot);

        log?.Invoke($"Copying '{sourceRoot}' -> '{outputRoot}'");
        DirectoryCopier.DeleteIfExists(outputRoot);
        DirectoryCopier.CopyTree(sourceRoot, outputRoot);

        var runtimeDllSource = RuntimeAssemblyLocator.Locate();
        string? runtimeDllCopyPath = null;
        if (runtimeDllSource is not null)
        {
            var runtimeDir = Path.Combine(outputRoot, RuntimeDllFolderName);
            Directory.CreateDirectory(runtimeDir);
            runtimeDllCopyPath = Path.Combine(runtimeDir, Path.GetFileName(runtimeDllSource));
            File.Copy(runtimeDllSource, runtimeDllCopyPath, overwrite: true);
        }
        else
        {
            log?.Invoke("Warning: could not locate ProgramFlowTracer.Runtime.dll next to the running tool; instrumented projects will not compile until you add a reference to it manually.");
        }

        var summaries = new List<InstrumentationSummary>();
        var runId = Guid.NewGuid().ToString("N");

        // A .cs file linked into more than one project (a common pattern for shared source in a
        // multi-project repo, rather than factoring it into its own referenced project) would
        // otherwise be visited and rewritten once per project that includes it - each pass
        // internally consistent on its own, but each starting its injected-identifier numbering
        // from scratch and overwriting the same output path as the last. Tracking already-handled
        // absolute paths here guarantees every physical file is instrumented exactly once for the
        // whole run, no matter how many projects reference it.
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var inPlaceSelector = new InPlaceSelector(inPlaceProjects);
        var manifestPath = InPlaceManifest.PathFor(fullInputPath);
        var manifest = new InPlaceManifest { Target = fullInputPath };

        if (inPlaceSelector.Any && InPlaceManifest.TryLoad(manifestPath) is not null)
        {
            throw new InvalidOperationException(
                $"In-place instrumentation is already applied (see '{manifestPath}'). " +
                "Run 'restore' first - instrumenting over it again would overwrite the backups of your original files.");
        }

        foreach (var project in projects)
        {
            if (project.FilePath is null)
            {
                continue;
            }

            var inPlace = inPlaceSelector.Matches(project);

            if (!inPlace && !IsUnder(sourceRoot, project.FilePath))
            {
                // Silent until now, and the most confusing failure this tool has: the project is
                // neither copied nor rewritten, so the build still succeeds and the application
                // still behaves correctly - it simply never reports a single event. From the
                // viewer that is indistinguishable from "this code was never called".
                summaries.Add(new InstrumentationSummary
                {
                    ProjectPath = project.FilePath,
                    OutputDirectory = outputRoot,
                    SourceRoot = sourceRoot,
                    Warnings =
                    {
                        $"NOT instrumented: '{project.Name}' lives outside the copied tree ('{sourceRoot}'), " +
                        "so calls into it produce no trace events. Instrument a solution that contains it, " +
                        $"or rewrite it where it is with --in-place {Path.GetFileName(project.FilePath)}."
                    }
                });

                log?.Invoke($"  ! '{project.Name}' is outside '{sourceRoot}' - left uninstrumented");
                continue;
            }

            log?.Invoke($"Instrumenting project '{project.Name}'{(inPlace ? " (in place)" : string.Empty)}...");
            var summary = await InstrumentProjectAsync(
                project, sourceRoot, outputRoot, config, processedFiles, inPlace, manifest, keepBackups, log, cancellationToken).ConfigureAwait(false);
            summaries.Add(summary);

            if (summary.FilesModified > 0 && inPlace)
            {
                PatchInPlaceProject(project.FilePath, runtimeDllSource, runId, manifest, keepBackups, log);
                continue;
            }

            if (summary.FilesModified > 0)
            {
                var relativeCsproj = Path.GetRelativePath(sourceRoot, project.FilePath);
                var copiedCsproj = Path.Combine(outputRoot, relativeCsproj);
                if (File.Exists(copiedCsproj))
                {
                    if (runtimeDllCopyPath is not null)
                    {
                        var hintPath = Path.GetRelativePath(Path.GetDirectoryName(copiedCsproj)!, runtimeDllCopyPath);
                        CsprojPatcher.AddRuntimeReference(copiedCsproj, hintPath);
                    }

                    CsprojPatcher.MarkAsInstrumented(copiedCsproj, runId);
                }
            }
        }

        if (manifest.Entries.Count > 0)
        {
            manifest.Save(manifestPath);
            log?.Invoke($"In-place changes recorded in '{manifestPath}' - 'restore' undoes them.");
        }

        return summaries;
    }

    /// <summary>
    /// Adds the runtime reference to a project that was rewritten where it lives.
    /// <para>
    /// The DLL goes in a folder beside the project rather than in the instrumented copy: an
    /// in-place project is not in that copy, and may not even be under the same root.
    /// </para>
    /// </summary>
    private static void PatchInPlaceProject(
        string csprojPath, string? runtimeDllSource, string runId, InPlaceManifest manifest, bool keepBackups, Action<string>? log)
    {
        if (runtimeDllSource is null)
        {
            log?.Invoke($"  ! no runtime DLL found; '{Path.GetFileName(csprojPath)}' will not compile until one is referenced manually");
            return;
        }

        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var runtimeDir = Path.Combine(projectDir, RuntimeDllFolderName);
        Directory.CreateDirectory(runtimeDir);

        var runtimeDllPath = Path.Combine(runtimeDir, Path.GetFileName(runtimeDllSource));
        File.Copy(runtimeDllSource, runtimeDllPath, overwrite: true);

        if (keepBackups) { BackUp(csprojPath, manifest); }
        CsprojPatcher.AddRuntimeReference(csprojPath, Path.GetRelativePath(projectDir, runtimeDllPath));
        CsprojPatcher.MarkAsInstrumented(csprojPath, runId);
    }

    /// <summary>Copies a file aside before it is rewritten, once, and records it for restore.</summary>
    private static void BackUp(string path, InPlaceManifest manifest)
    {
        var backupPath = path + BackupSuffix;

        // Only the first write of a given file may create its backup; a later one would capture
        // already-instrumented content and make the original unrecoverable.
        if (!File.Exists(backupPath))
        {
            File.Copy(path, backupPath, overwrite: false);
            manifest.Entries.Add(new InPlaceEntry { OriginalPath = path, BackupPath = backupPath });
        }
    }

    /// <summary>Matches projects named on the command line, by project name, file name or path.</summary>
    private sealed class InPlaceSelector
    {
        private readonly string[] _tokens;

        public InPlaceSelector(IEnumerable<string>? tokens)
        {
            _tokens = (tokens ?? Enumerable.Empty<string>())
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToArray();
        }

        public bool Any => _tokens.Length > 0;

        public bool Matches(Project project)
        {
            if (!Any || project.FilePath is null) { return false; }

            var fullPath = Path.GetFullPath(project.FilePath);
            var fileName = Path.GetFileName(fullPath);
            var withoutExtension = Path.GetFileNameWithoutExtension(fullPath);

            foreach (var token in _tokens)
            {
                if (token.Equals(project.Name, StringComparison.OrdinalIgnoreCase) ||
                    token.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                    token.Equals(withoutExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // A path, given either absolutely or relative to wherever the command was run.
                if (Path.IsPathRooted(token) && Path.GetFullPath(token).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static async Task<InstrumentationSummary> InstrumentProjectAsync(
        Project project,
        string sourceRoot,
        string outputRoot,
        FlowTracerConfig config,
        HashSet<string> processedFiles,
        bool inPlace,
        InPlaceManifest manifest,
        bool keepBackups,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var summary = new InstrumentationSummary
        {
            ProjectPath = project.FilePath ?? project.Name,
            OutputDirectory = outputRoot,
            SourceRoot = sourceRoot,
            InstrumentedInPlace = inPlace
        };

        // An in-place project need not sit under the copied tree at all - that is the point of it -
        // so its files are bounded by its own directory instead.
        var documentRoot = inPlace && project.FilePath is not null
            ? Path.GetDirectoryName(project.FilePath)!
            : sourceRoot;

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            summary.Errors.Add("Could not build a compilation for this project (check that it restores/builds normally with 'dotnet build').");
            return summary;
        }

        IMethodSymbol? entryPoint = null;
        try
        {
            entryPoint = compilation.GetEntryPoint(cancellationToken);
        }
        catch
        {
            // Library projects (and some SDK styles) legitimately have no entry point.
        }

        foreach (var document in project.Documents)
        {
            if (document.FilePath is null || !IsUnder(documentRoot, document.FilePath))
            {
                continue;
            }

            if (!document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsLikelyGenerated(document.FilePath))
            {
                continue;
            }

            var fullDocumentPath = Path.GetFullPath(document.FilePath);
            if (!processedFiles.Add(fullDocumentPath))
            {
                // Already instrumented via another project that links the same physical file -
                // skip it here rather than rewriting (and overwriting) it a second time.
                continue;
            }

            summary.FilesProcessed++;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
            {
                continue;
            }

            var rewriter = new Rewriting.InstrumentationRewriter(semanticModel, config, document.FilePath, entryPoint);
            var newRoot = rewriter.Visit(root);

            summary.MethodsInstrumented += rewriter.InstrumentedCount;
            summary.SkippedMethods.AddRange(rewriter.Skipped);

            if (rewriter.InstrumentedCount == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(documentRoot, document.FilePath);
            string targetPath;

            if (inPlace)
            {
                targetPath = document.FilePath;
                if (keepBackups) { BackUp(targetPath, manifest); } else { manifest.Entries.Add(new InPlaceEntry { OriginalPath = targetPath, BackupPath = string.Empty }); }
            }
            else
            {
                targetPath = Path.Combine(outputRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            }

            // Format only the nodes that were injected, not the whole file.
            //
            // NormalizeWhitespace() reformats every line of the document - collapsing the author's
            // blank lines, turning expression-bodied members into blocks - so a file with three
            // instrumented methods came back with hundreds of changed lines. That is noise in any
            // diff, and actively harmful for --in-place, where those are the user's own files.
            // Formatter.Annotation marks the generated blocks (see MethodBodyBuilder and
            // ReturnRewriter); everything else keeps its original trivia untouched.
            var formatted = Formatter.Format(newRoot!, Formatter.Annotation, FormattingWorkspace).ToFullString();

            // Write the bytes back the way the file was encoded. File.WriteAllText defaults to
            // UTF-8 *without* a BOM, which silently strips the signature from every file that had
            // one - a whole-file change in any diff, and a real difference to tools that rely on it.
            await File.WriteAllTextAsync(targetPath, formatted, EncodingOf(document.FilePath), cancellationToken).ConfigureAwait(false);

            summary.FilesModified++;
            log?.Invoke($"  {relative}: {rewriter.InstrumentedCount} member(s) instrumented");
        }

        return summary;
    }

    private static string ComputeOutputRoot(string sourceRoot)
    {
        var trimmed = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + InstrumentedSuffix;
    }

    /// <summary>Breadth-first walk of <paramref name="root"/>'s <c>ProjectReference</c> graph
    /// within its own (already fully-loaded, by MSBuildWorkspace) <see cref="Solution"/>, so
    /// instrumenting one project also instruments everything it can actually call into.</summary>
    private static IReadOnlyList<Project> CollectProjectAndReferencesTransitively(Project root)
    {
        var solution = root.Solution;
        var seen = new HashSet<ProjectId> { root.Id };
        var queue = new Queue<ProjectId>();
        queue.Enqueue(root.Id);

        var result = new List<Project>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var project = solution.GetProject(id);
            if (project is null)
            {
                continue;
            }

            result.Add(project);

            foreach (var reference in project.ProjectReferences)
            {
                if (seen.Add(reference.ProjectId))
                {
                    queue.Enqueue(reference.ProjectId);
                }
            }
        }

        return result;
    }

    /// <summary>Lowest common ancestor directory of every path in <paramref name="directories"/>,
    /// so a set of sibling (or nested) project directories can be copied/instrumented as one tree
    /// even when none of them individually contains all the others.</summary>
    private static string ComputeCommonRoot(IEnumerable<string> directories)
    {
        string? common = null;
        foreach (var dir in directories)
        {
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            common = common is null ? full : CommonPrefix(common, full);
        }

        if (common is null)
        {
            throw new InvalidOperationException("No project directories to compute a common root from.");
        }

        return common;
    }

    private static string CommonPrefix(string a, string b)
    {
        var aParts = a.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bParts = b.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var count = Math.Min(aParts.Length, bParts.Length);

        var common = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(aParts[i], bParts[i], StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            common.Add(aParts[i]);
        }

        if (common.Count == 0)
        {
            // Different drives/roots entirely - nothing sensible to copy as one tree. Falling
            // back to `a` keeps this non-throwing (copy will simply miss the other branch, same
            // as the old single-project behavior), rather than taking down the whole command.
            return a;
        }

        var joined = string.Join(Path.DirectorySeparatorChar, common);
        if (joined.Length == 2 && joined[1] == ':')
        {
            // A bare Windows drive letter ("C:") needs the trailing separator to be a valid
            // rooted path ("C:\") that Path.GetFullPath/DirectoryInfo will accept.
            joined += Path.DirectorySeparatorChar;
        }

        return joined;
    }

    private static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyGenerated(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)
               || filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A cheap, workspace-free guess at where <c>instrument</c>/<c>run</c> would write (or did
    /// write) an instrumented copy - used by <c>restore</c>/<c>clean</c>, which intentionally
    /// avoid loading the full Roslyn workspace just to delete a directory.
    ///
    /// This guess is only correct when <paramref name="projectOrSolutionPath"/> is a solution, or
    /// a project whose <c>ProjectReference</c>s all live under its own directory - the common
    /// case. When a project reaches outside its own directory via ProjectReference,
    /// <see cref="InstrumentAsync"/> copies from the common ancestor of the whole reachable set
    /// instead (see <see cref="ComputeCommonRoot"/>), and this static guess will disagree with
    /// that. In that situation <c>restore</c>/<c>clean</c> may report nothing to remove even
    /// though an instrumented copy exists elsewhere; delete the "*.instrumented" sibling of the
    /// common ancestor directory (printed by <c>instrument</c>/<c>run</c> on success) by hand.
    /// </summary>
    public static string GetOutputRootFor(string projectOrSolutionPath)
    {
        var fullInputPath = Path.GetFullPath(projectOrSolutionPath);
        var sourceRoot = Path.GetDirectoryName(fullInputPath)!;
        return ComputeOutputRoot(sourceRoot);
    }
}
