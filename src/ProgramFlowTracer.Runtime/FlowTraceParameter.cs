namespace ProgramFlowTracer.Runtime;

/// <summary>
/// Describes a single parameter (or, at exit time, a single <c>ref</c>/<c>out</c> parameter's
/// final value) for capture. Instrumented code builds an array of these and passes it to
/// <see cref="FlowTracer.Enter"/>/<see cref="FlowTracer.Exit"/>.
/// </summary>
/// <param name="Name">Parameter name as declared in source.</param>
/// <param name="StaticType">The parameter's declared (compile-time) type; used when the runtime
/// value is <c>null</c> and there is no runtime type to fall back on.</param>
/// <param name="Value">The runtime value. Ignored when <paramref name="IsAvailable"/> is false.</param>
/// <param name="IsSensitive">True if the parameter (or its declaring context) is marked
/// <c>[FlowTraceSensitive]</c> or matches a configured sensitive-name pattern.</param>
/// <param name="IsAvailable">False for <c>out</c> parameters at method-entry time, since their
/// value does not exist yet.</param>
public readonly record struct FlowTraceParameter(
    string Name,
    Type? StaticType,
    object? Value,
    bool IsSensitive = false,
    bool IsAvailable = true)
{
    public static FlowTraceParameter Unavailable(string name, Type? staticType) =>
        new(name, staticType, null, IsSensitive: false, IsAvailable: false);
}
