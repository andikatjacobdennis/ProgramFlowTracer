using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Core.Engine;

/// <summary>
/// Records every file that <c>--in-place</c> rewrote, and where its untouched original was put.
///
/// In-place instrumentation is the one operation in this tool that modifies files the user owns,
/// so it is only safe if it is exactly reversible. The manifest is that guarantee: <c>restore</c>
/// needs no knowledge of MSBuild, project graphs or which files "looked" instrumented - it just
/// puts back what this file says was moved.
///
/// It is written next to the project or solution that was named on the command line, so the
/// argument that applied the change is the same argument that undoes it.
/// </summary>
public sealed class InPlaceManifest
{
    public const string FileName = ".flowtracer-inplace.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [JsonPropertyName("createdUtc")]
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<InPlaceEntry> Entries { get; set; } = new();

    public static string PathFor(string targetProjectOrSolution) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetProjectOrSolution))!, FileName);

    public static InPlaceManifest? TryLoad(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<InPlaceManifest>(File.ReadAllText(manifestPath), Options)
                : null;
        }
        catch
        {
            // A corrupt manifest must not make the originals unrecoverable by hand - the backup
            // files are still sitting next to them either way.
            return null;
        }
    }

    public void Save(string manifestPath) =>
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(this, Options));
}

public sealed class InPlaceEntry
{
    [JsonPropertyName("original")]
    public string OriginalPath { get; set; } = string.Empty;

    [JsonPropertyName("backup")]
    public string BackupPath { get; set; } = string.Empty;
}
