namespace ProgramFlowTracer.Core.Model;

/// <summary>A method/constructor/local function that instrumentation deliberately left alone,
/// and why - surfaced by the CLI so instrumentation decisions are never a silent black box.</summary>
public sealed record SkippedMethodInfo(string FilePath, int Line, string MemberName, string Reason);
