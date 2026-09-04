namespace ProgramFlowTracer.Core.Rewriting;

/// <summary>Everything the code generator needs to know about one parameter (or, for setters, the
/// implicit <c>value</c> parameter) to generate its capture expression. Deliberately holds only
/// plain strings/bools - computed once from syntax+semantics by <c>InstrumentationRewriter</c> -
/// so the actual code-generation code has no dependency on the semantic model.</summary>
/// <param name="Name">Source-level parameter name.</param>
/// <param name="TypeForTypeof">Text to place inside <c>typeof(...)</c>; never <c>dynamic</c>
/// (callers normalize that to <c>object</c>, since <c>typeof(dynamic)</c> does not compile).</param>
/// <param name="IsOut">True for <c>out</c> parameters - value does not exist yet at method entry.</param>
/// <param name="IsRefOrOut">True for <c>ref</c> or <c>out</c> parameters - final value is captured
/// again at exit, since it may have changed (or, for <c>out</c>, been assigned for the first time).</param>
/// <param name="IsSensitive">True if this parameter should never have its real value written to
/// the trace.</param>
/// <param name="CanCaptureValue">False for pointer types and ref-like (<c>ref struct</c>) types,
/// whose values cannot be boxed to <c>object</c> and so can never be captured, only described.</param>
internal sealed record ParameterCaptureInfo(
    string Name,
    string TypeForTypeof,
    bool IsOut,
    bool IsRefOrOut,
    bool IsSensitive,
    bool CanCaptureValue);
