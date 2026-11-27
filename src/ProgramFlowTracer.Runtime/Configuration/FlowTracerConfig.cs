using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProgramFlowTracer.Runtime.Configuration;

/// <summary>
/// Shape of <c>.flowtrace.json</c>. Read by both <c>ProgramFlowTracer.Core</c> (to decide what to
/// instrument) and <c>ProgramFlowTracer.Runtime</c> (to decide what to capture/write at runtime).
/// The same file drives both stages so behavior stays consistent between instrumentation time and
/// run time.
/// </summary>
public sealed class FlowTracerConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("outputDirectory")]
    public string OutputDirectory { get; set; } = ".flowtrace";

    [JsonPropertyName("captureParameters")]
    public bool CaptureParameters { get; set; } = true;

    [JsonPropertyName("captureReturnValues")]
    public bool CaptureReturnValues { get; set; } = true;

    /// <summary>
    /// Whether a MethodExit event is recorded at all.
    /// <para>
    /// Turning this off is the lightest possible instrumentation: one Enter call plus a bare
    /// try/finally that restores the call context. No exit events, no durations, no exception
    /// events - and no catch clause, so the traced program's exception behaviour is untouched.
    /// Return statements are left exactly as written, because there is nothing to record at one.
    /// </para>
    /// </summary>
    [JsonPropertyName("recordMethodExits")]
    public bool RecordMethodExits { get; set; } = true;

    /// <summary>
    /// Whether properties that run real code are read while capturing a value.
    /// <para>
    /// Off by default, because reading one executes the traced program: an ORM's navigation
    /// property issues a query (and throws once its context is disposed), a computed property can
    /// mutate, cache, log or block, and any of them can throw. Auto-properties are always read -
    /// returning a backing field has no side effects - so ordinary DTOs and entities still capture
    /// in full, and only genuinely computed members are skipped.
    /// </para>
    /// <para>
    /// Turn it on for richer values when the code being traced is known to have side-effect-free
    /// getters.
    /// </para>
    /// </summary>
    [JsonPropertyName("captureComputedProperties")]
    public bool CaptureComputedProperties { get; set; } = false;

    [JsonPropertyName("captureExceptions")]
    public bool CaptureExceptions { get; set; } = true;

    [JsonPropertyName("captureThreadInfo")]
    public bool CaptureThreadInfo { get; set; } = true;

    [JsonPropertyName("instrumentPropertyAccessors")]
    public bool InstrumentPropertyAccessors { get; set; } = false;

    [JsonPropertyName("instrumentLocalFunctions")]
    public bool InstrumentLocalFunctions { get; set; } = true;

    [JsonPropertyName("instrumentCompilerGeneratedMethods")]
    public bool InstrumentCompilerGeneratedMethods { get; set; } = false;

    [JsonPropertyName("maxObjectSizeBytes")]
    public int MaxObjectSizeBytes { get; set; } = 1_048_576;

    /// <summary>Values larger than this (once serialized as a JSON value) are spilled out to a
    /// separate object file under <c>objects/</c> instead of being inlined into the event.</summary>
    [JsonPropertyName("inlineObjectThresholdBytes")]
    public int InlineObjectThresholdBytes { get; set; } = 2048;

    [JsonPropertyName("maxStringLength")]
    public int MaxStringLength { get; set; } = 10_000;

    [JsonPropertyName("maxCollectionItems")]
    public int MaxCollectionItems { get; set; } = 100;

    [JsonPropertyName("maxObjectGraphDepth")]
    public int MaxObjectGraphDepth { get; set; } = 12;

    /// <summary>
    /// Hard ceiling on how many nodes a single captured value may expand to.
    /// <para>
    /// Depth and collection limits alone bound the graph only per level, and they multiply: 100
    /// items nested three deep is already a million nodes inside the defaults. This is the only
    /// limit that bounds the <em>total</em> work of capturing one value, and unlike
    /// <see cref="MaxObjectSizeBytes"/> it is enforced during the walk rather than after it - so
    /// an oversized parameter costs a bounded amount of time instead of being fully traversed and
    /// then thrown away.
    /// </para>
    /// </summary>
    [JsonPropertyName("maxCapturedNodes")]
    public int MaxCapturedNodes { get; set; } = 5_000;

    [JsonPropertyName("excludeNamespaces")]
    public List<string> ExcludeNamespaces { get; set; } = new() { "System", "Microsoft" };

    [JsonPropertyName("excludeClasses")]
    public List<string> ExcludeClasses { get; set; } = new();

    [JsonPropertyName("excludeMethods")]
    public List<string> ExcludeMethods { get; set; } = new();

    /// <summary>Parameter/property names (case-insensitive, matched anywhere in the name) that
    /// are treated as sensitive even without an explicit <c>[FlowTraceSensitive]</c> attribute,
    /// e.g. "password", "token", "secret", "connectionstring".</summary>
    [JsonPropertyName("sensitiveNamePatterns")]
    public List<string> SensitiveNamePatterns { get; set; } = new()
    {
        "password", "passwd", "secret", "token", "apikey", "api_key",
        "connectionstring", "connection_string", "authorization", "creditcard", "ssn"
    };

    /// <summary>0.0-1.0. Fraction of invocations that are actually recorded once tracing has
    /// determined a method should be instrumented; used to reduce overhead/volume on very hot
    /// methods. 1.0 (default) records everything.</summary>
    [JsonPropertyName("samplingRate")]
    public double SamplingRate { get; set; } = 1.0;

    /// <summary>Bounded channel capacity for the background writer. When full, new events are
    /// dropped (counted in <c>droppedEventCount</c>) rather than blocking the traced application.</summary>
    [JsonPropertyName("writerQueueCapacity")]
    public int WriterQueueCapacity { get; set; } = 100_000;

    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// A brand-new config carrying the default values. Deliberately a fresh instance per access
    /// rather than a cached singleton: <see cref="FlowTracerConfig"/> is mutable (including its
    /// list properties), so a shared instance would let any caller that tweaks a setting silently
    /// change the defaults every other caller sees.
    /// </summary>
    public static FlowTracerConfig Default => new();

    /// <summary>
    /// Loads configuration from <paramref name="path"/> if it exists; otherwise returns
    /// <see cref="Default"/>. Never throws - a malformed config file is treated as "use defaults"
    /// so a broken config can never take down the traced application.
    /// </summary>
    public static FlowTracerConfig LoadOrDefault(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<FlowTracerConfig>(json, LoadOptions);
            return config ?? Default;
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>
    /// Searches <paramref name="startDirectory"/> and its ancestors for <c>.flowtrace.json</c>,
    /// mirroring how tools like git/dotnet find their nearest config file.
    /// </summary>
    public static FlowTracerConfig LoadFromNearestOrDefault(string startDirectory, string fileName = ".flowtrace.json")
    {
        try
        {
            var dir = new DirectoryInfo(startDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return LoadOrDefault(candidate);
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // Fall through to defaults - config discovery must never crash the app.
        }

        return Default;
    }

    public bool IsNamespaceExcluded(string? namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return false;
        }

        foreach (var excluded in ExcludeNamespaces)
        {
            if (string.IsNullOrEmpty(excluded))
            {
                continue;
            }

            if (namespaceName.Equals(excluded, StringComparison.Ordinal) ||
                namespaceName.StartsWith(excluded + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsClassExcluded(string? fullyQualifiedClassName)
    {
        if (string.IsNullOrEmpty(fullyQualifiedClassName))
        {
            return false;
        }

        return ExcludeClasses.Contains(fullyQualifiedClassName, StringComparer.Ordinal);
    }

    public bool IsMethodExcluded(string? fullyQualifiedMethodSignature)
    {
        if (string.IsNullOrEmpty(fullyQualifiedMethodSignature))
        {
            return false;
        }

        return ExcludeMethods.Contains(fullyQualifiedMethodSignature, StringComparer.Ordinal);
    }

    public bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var pattern in SensitiveNamePatterns)
        {
            if (!string.IsNullOrEmpty(pattern) && name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
