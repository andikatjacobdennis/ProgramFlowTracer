namespace ProgramFlowTracer.Core.Workspace;

/// <summary>
/// Copies a project/solution's source tree to an output directory without touching the
/// original files (spec section 24: "avoid modifying the user's source"). Skips build output,
/// VCS metadata, IDE folders, and any previously-generated instrumentation/trace output so that
/// re-running <c>instrument</c> never copies its own leftovers.
/// </summary>
public static class DirectoryCopier
{
    private static readonly string[] ExcludedDirectoryNames =
    {
        "bin", "obj", ".git", ".vs", ".vscode", ".idea", ".flowtrace", "node_modules"
    };

    public static void CopyTree(string sourceRoot, string destinationRoot, string? excludeSuffix = ".instrumented")
    {
        var source = new DirectoryInfo(sourceRoot);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceRoot}");
        }

        Directory.CreateDirectory(destinationRoot);
        CopyDirectory(source, destinationRoot, excludeSuffix);
    }

    private static void CopyDirectory(DirectoryInfo source, string destinationPath, string? excludeSuffix)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var file in source.EnumerateFiles())
        {
            var target = Path.Combine(destinationPath, file.Name);
            file.CopyTo(target, overwrite: true);
        }

        foreach (var dir in source.EnumerateDirectories())
        {
            if (ShouldSkip(dir.Name, excludeSuffix))
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(destinationPath, dir.Name), excludeSuffix);
        }
    }

    private static bool ShouldSkip(string directoryName, string? excludeSuffix)
    {
        if (ExcludedDirectoryNames.Contains(directoryName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return excludeSuffix is not null && directoryName.EndsWith(excludeSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
