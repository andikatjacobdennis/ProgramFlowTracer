namespace ProgramFlowTracer.Runtime.Attributes;

/// <summary>
/// Marks a parameter (or a property/field, when captured as part of an object graph) as
/// sensitive. ProgramFlowTracer will never write its real value to the trace; it will instead
/// record a redacted placeholder.
/// </summary>
[AttributeUsage(
    AttributeTargets.Parameter
    | AttributeTargets.Property
    | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = false)]
public sealed class FlowTraceSensitiveAttribute : Attribute
{
}
