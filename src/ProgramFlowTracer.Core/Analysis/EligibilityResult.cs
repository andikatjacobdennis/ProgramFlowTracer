namespace ProgramFlowTracer.Core.Analysis;

public readonly record struct EligibilityResult(bool IsEligible, string? Reason)
{
    public static EligibilityResult Eligible() => new(true, null);

    public static EligibilityResult Ineligible(string reason) => new(false, reason);
}
