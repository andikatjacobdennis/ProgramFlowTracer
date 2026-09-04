using Microsoft.Build.Locator;

namespace ProgramFlowTracer.Core.Workspace;

/// <summary>
/// Registers the .NET SDK's MSBuild assemblies with the current process so that
/// <c>Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace</c> can load real .csproj/.sln files.
/// This must happen exactly once, and before any MSBuild/Roslyn workspace types are touched -
/// <see cref="MSBuildLocator"/> works by hooking assembly resolution, which is too late once the
/// (wrong, reference-only) MSBuild assemblies have already loaded.
/// </summary>
public static class MsBuildEnvironment
{
    /// <summary>
    /// Point this at an SDK folder - the one containing <c>MSBuild.dll</c>, e.g.
    /// <c>C:\Program Files\dotnet\sdk\9.0.305</c> - to skip discovery entirely.
    /// </summary>
    public const string OverrideVariable = "PROGRAMFLOWTRACER_MSBUILD_PATH";

    private static readonly object Lock = new();
    private static bool _registered;

    public static void EnsureRegistered() => EnsureRegistered(null);

    /// <param name="log">Receives the MSBuild that was chosen, for --verbose.</param>
    public static void EnsureRegistered(Action<string>? log)
    {
        lock (Lock)
        {
            if (_registered || MSBuildLocator.IsRegistered)
            {
                _registered = true;
                return;
            }

            var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                MSBuildLocator.RegisterMSBuildPath(overridePath.Trim());
                log?.Invoke($"MSBuild: {overridePath.Trim()} (from {OverrideVariable})");
                _registered = true;
                return;
            }

            var chosen = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (chosen is not null)
            {
                MSBuildLocator.RegisterInstance(chosen);
                log?.Invoke($"MSBuild: {chosen.Name} {chosen.Version} at {chosen.MSBuildPath}");
                _registered = true;
                return;
            }

            // MSBuildLocator returns nothing in two situations this fallback covers:
            //
            //  - the `dotnet` muxer is not on PATH and DOTNET_HOST_PATH is unset, which is what
            //    happens when the tool is launched as a bare .exe from the Visual Studio debugger;
            //  - every installed SDK is newer than the runtime this process is on. MSBuildLocator
            //    rejects those outright, but in practice such an SDK still loads (a net9.0 build
            //    of this tool drives the .NET 10 SDK's MSBuild without complaint), so preferring a
            //    matching SDK and *trying* a newer one beats refusing to run at all.
            var sdk = InstalledSdks().FirstOrDefault();
            if (sdk is not null)
            {
                MSBuildLocator.RegisterMSBuildPath(sdk.Path);
                var note = IsSameMajorOrOlder(sdk.Version) ? string.Empty : ", newer than this process";
                log?.Invoke($"MSBuild: .NET SDK {sdk.Version} at {sdk.Path} (found on disk{note})");
                _registered = true;
                return;
            }

            throw new InvalidOperationException(Explain());
        }
    }

    private sealed record Sdk(Version Version, string Path);

    /// <summary>Every SDK on disk that ships an MSBuild, newest first.</summary>
    private static List<Sdk> InstalledSdks()
    {
        var found = new Dictionary<string, Sdk>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in DotNetRoots())
        {
            var sdkRoot = Path.Combine(root, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(sdkRoot))
            {
                if (!File.Exists(Path.Combine(dir, "MSBuild.dll")))
                {
                    continue;
                }

                // "9.0.305" parses, and so does the release part of "10.0.100-preview.3.12345".
                var release = Path.GetFileName(dir).Split('-')[0];
                if (Version.TryParse(release, out var version))
                {
                    found[dir] = new Sdk(version, dir);
                }
            }
        }

        // An SDK built for this runtime or an older one is the safe choice, so those come first;
        // within each group, newest wins.
        return found.Values
            .OrderByDescending(s => IsSameMajorOrOlder(s.Version))
            .ThenByDescending(s => s.Version)
            .ToList();
    }

    private static IEnumerable<string> DotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        static IEnumerable<string?> Candidates()
        {
            yield return Environment.GetEnvironmentVariable("DOTNET_ROOT");

            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            yield return string.IsNullOrWhiteSpace(host) ? null : Path.GetDirectoryName(host);

            foreach (var onPath in DotNetOnPath())
            {
                yield return onPath;
            }

            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            yield return "/usr/share/dotnet";
            yield return "/usr/local/share/dotnet";
        }
    }

    private static IEnumerable<string> DotNetOnPath()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string? directory = null;
            try
            {
                var trimmed = entry.Trim();
                if (File.Exists(Path.Combine(trimmed, exe)))
                {
                    directory = trimmed;
                }
            }
            catch
            {
                // A malformed PATH entry is not worth failing discovery over.
            }

            if (directory is not null)
            {
                yield return directory;
            }
        }
    }

    /// <summary>An SDK built for this runtime or an older one - the safe pick.</summary>
    private static bool IsSameMajorOrOlder(Version sdkVersion) =>
        sdkVersion.Major <= Environment.Version.Major;

    private static string Explain()
    {
        var host = Environment.Version;

        var message = new List<string>
        {
            "No .NET SDK was found, so .csproj and .sln files cannot be opened.",
            string.Empty,
            $"ProgramFlowTracer is running on .NET {host.Major}.{host.Minor}. It needs an SDK " +
            "that ships MSBuild.dll; none was found under:"
        };

        message.AddRange(DotNetRoots().Select(r => "  " + Path.Combine(r, "sdk")));
        message.Add(string.Empty);
        message.Add($"Fix: install the .NET {host.Major} SDK, or set {OverrideVariable} to an SDK");
        message.Add("folder containing MSBuild.dll.");

        return string.Join(Environment.NewLine, message);
    }
}
