using ProgramFlowTracer.Core.Engine;
using ProgramFlowTracer.Core.Workspace;

namespace ProgramFlowTracer.Cli.Commands;

/// <summary>
/// "restore" undoes "instrument": normally that is just deleting the sibling "*.instrumented"
/// directory, since instrumentation never touches the original source.
///
/// The exception is <c>--in-place</c>, which does modify the user's own files. Those are undone
/// from the manifest written alongside the target, and that happens first: if anything goes wrong
/// it matters far more than the copy, which can always be regenerated.
/// </summary>
internal static class RestoreCommand
{
    public static int Run(string projectOrSolutionPath)
    {
        var fullPath = Path.GetFullPath(projectOrSolutionPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"error: '{projectOrSolutionPath}' was not found.");
            return 1;
        }

        var restoredInPlace = RestoreInPlace(fullPath);

        var outputRoot = InstrumentationEngine.GetOutputRootFor(fullPath);
        if (!Directory.Exists(outputRoot))
        {
            if (restoredInPlace) { return 0; }

            Console.WriteLine("No instrumented copy found at the expected location:");
            Console.WriteLine($"  {outputRoot}");
            Console.WriteLine("If this project references other projects outside its own directory, 'instrument'/'run' may");
            Console.WriteLine("have written the copy under their common ancestor directory instead (it prints that path on");
            Console.WriteLine("success as \"Instrumented copy written to: ...\") - delete that \"*.instrumented\" directory by hand.");
            return 0;
        }

        DirectoryCopier.DeleteIfExists(outputRoot);
        Console.WriteLine($"Removed instrumented copy: {outputRoot}");

        if (!restoredInPlace)
        {
            Console.WriteLine("Original source was never modified, so there is nothing further to restore.");
        }

        return 0;
    }

    /// <summary>
    /// Puts back every file <c>--in-place</c> rewrote, from the backups the manifest recorded.
    /// A backup that has gone missing is reported rather than skipped silently: that file is
    /// still carrying instrumentation, and the user needs to know which one.
    /// </summary>
    private static bool RestoreInPlace(string targetPath)
    {
        var manifestPath = InPlaceManifest.PathFor(targetPath);
        var manifest = InPlaceManifest.TryLoad(manifestPath);

        if (manifest is null || manifest.Entries.Count == 0)
        {
            if (File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"warning: '{manifestPath}' could not be read. Restore the files ending in " +
                                        "'.pft-original' by hand.");
            }
            return false;
        }

        var restored = 0;
        var failed = 0;

        foreach (var entry in manifest.Entries)
        {
            try
            {
                if (!File.Exists(entry.BackupPath))
                {
                    Console.Error.WriteLine($"warning: backup missing for '{entry.OriginalPath}' - it is still instrumented.");
                    failed++;
                    continue;
                }

                File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                File.Delete(entry.BackupPath);
                restored++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: could not restore '{entry.OriginalPath}': {ex.Message}");
                failed++;
            }
        }

        // Only drop the manifest once everything in it is genuinely back, so a partial failure
        // stays recoverable by running restore again.
        if (failed == 0)
        {
            File.Delete(manifestPath);
            Console.WriteLine($"Restored {restored} in-place file(s) from backup.");
        }
        else
        {
            Console.WriteLine($"Restored {restored} in-place file(s); {failed} could not be restored - " +
                              $"'{manifestPath}' has been kept so you can retry.");
        }

        return true;
    }
}
