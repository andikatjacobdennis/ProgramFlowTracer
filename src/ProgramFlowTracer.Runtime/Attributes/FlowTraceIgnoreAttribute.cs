namespace ProgramFlowTracer.Runtime.Attributes;

/// <summary>
/// Marks a method, constructor, parameter, or type so that ProgramFlowTracer never injects
/// tracing into it (for methods/types) or never captures the annotated value (for parameters).
/// </summary>
[AttributeUsage(
    AttributeTargets.Method
    | AttributeTargets.Constructor
    | AttributeTargets.Class
    | AttributeTargets.Struct
    | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = false)]
public sealed class FlowTraceIgnoreAttribute : Attribute
{
}
