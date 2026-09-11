using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Analysis;

/// <summary>
/// Central policy: decides, for a given method/constructor/local function/accessor, whether
/// ProgramFlowTracer should inject tracing into it. Every rule from spec section 2 ("Do not
/// instrument...") is implemented here so the rest of the instrumentation pipeline can stay
/// mechanical (rewrite whatever this says is eligible).
/// </summary>
public static class MethodEligibility
{
    private const string IgnoreAttributeShortName = "FlowTraceIgnoreAttribute";

    public static EligibilityResult CheckMethod(MethodDeclarationSyntax node, SemanticModel semanticModel, FlowTracerConfig config)
    {
        if (node.Body is null && node.ExpressionBody is null)
        {
            return EligibilityResult.Ineligible("no method body (abstract, extern, or a partial declaration without an implementation)");
        }

        if (node.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword) && node.Body is null && node.ExpressionBody is null)
        {
            return EligibilityResult.Ineligible("partial method declaration without a body");
        }

        var symbol = semanticModel.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            return EligibilityResult.Ineligible("could not resolve a symbol for this method");
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet })
        {
            // Should not normally reach here (accessors are AccessorDeclarationSyntax, not
            // MethodDeclarationSyntax), but guard defensively.
            return CheckPropertyAccessorPolicy(config);
        }

        var body = (SyntaxNode?)node.Body ?? node.ExpressionBody;
        if (IteratorDetector.ContainsYield(body))
        {
            return EligibilityResult.Ineligible("iterator method (contains yield return/yield break); not currently instrumented");
        }

        // "return ref x;" cannot be captured into an ordinary local and returned again - the
        // reference itself is the return value, and copying it to a temporary would silently
        // change what the caller gets.
        if (symbol is IMethodSymbol { ReturnsByRef: true } or IMethodSymbol { ReturnsByRefReadonly: true })
        {
            return EligibilityResult.Ineligible("ref-returning method; capturing the return value would break the reference");
        }

        return CheckCommon(symbol, config);
    }

    public static EligibilityResult CheckConstructor(ConstructorDeclarationSyntax node, SemanticModel semanticModel, FlowTracerConfig config)
    {
        if (node.Body is null && node.ExpressionBody is null)
        {
            return EligibilityResult.Ineligible("no constructor body");
        }

        var symbol = semanticModel.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            return EligibilityResult.Ineligible("could not resolve a symbol for this constructor");
        }

        return CheckCommon(symbol, config);
    }

    public static EligibilityResult CheckLocalFunction(LocalFunctionStatementSyntax node, SemanticModel semanticModel, FlowTracerConfig config)
    {
        if (!config.InstrumentLocalFunctions)
        {
            return EligibilityResult.Ineligible("local function instrumentation disabled by configuration");
        }

        if (node.Body is null && node.ExpressionBody is null)
        {
            return EligibilityResult.Ineligible("no local function body (extern)");
        }

        var symbol = semanticModel.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            return EligibilityResult.Ineligible("could not resolve a symbol for this local function");
        }

        var body = (SyntaxNode?)node.Body ?? node.ExpressionBody;
        if (IteratorDetector.ContainsYield(body))
        {
            return EligibilityResult.Ineligible("iterator local function (contains yield return/yield break); not currently instrumented");
        }

        return CheckCommon(symbol, config);
    }

    public static EligibilityResult CheckAccessor(AccessorDeclarationSyntax node, SemanticModel semanticModel, FlowTracerConfig config)
    {
        var policyResult = CheckPropertyAccessorPolicy(config);
        if (!policyResult.IsEligible)
        {
            return policyResult;
        }

        if (node.Body is null && node.ExpressionBody is null)
        {
            return EligibilityResult.Ineligible("auto-implemented accessor has no body");
        }

        var symbol = semanticModel.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            return EligibilityResult.Ineligible("could not resolve a symbol for this accessor");
        }

        return CheckCommon(symbol, config);
    }

    private static EligibilityResult CheckPropertyAccessorPolicy(FlowTracerConfig config) =>
        config.InstrumentPropertyAccessors
            ? EligibilityResult.Eligible()
            : EligibilityResult.Ineligible("property accessor instrumentation disabled by configuration");

    private static EligibilityResult CheckCommon(ISymbol symbol, FlowTracerConfig config)
    {
        if (!config.InstrumentCompilerGeneratedMethods)
        {
            if (symbol.IsImplicitlyDeclared)
            {
                return EligibilityResult.Ineligible("compiler-generated member");
            }

            if (HasAttribute(symbol, "CompilerGeneratedAttribute") || HasAttribute(symbol, "GeneratedCodeAttribute"))
            {
                return EligibilityResult.Ineligible("marked with [CompilerGenerated]/[GeneratedCode]");
            }
        }

        if (HasAttribute(symbol, IgnoreAttributeShortName) ||
            (symbol.ContainingType is not null && HasAttribute(symbol.ContainingType, IgnoreAttributeShortName)))
        {
            return EligibilityResult.Ineligible("marked with [FlowTraceIgnore]");
        }

        var containingNamespace = symbol.ContainingNamespace?.ToDisplayString();
        if (config.IsNamespaceExcluded(containingNamespace))
        {
            return EligibilityResult.Ineligible($"namespace '{containingNamespace}' is excluded by configuration");
        }

        // Never instrument ProgramFlowTracer's own runtime, even if a project accidentally
        // includes its source (rather than referencing the compiled assembly).
        if (containingNamespace is not null &&
            (containingNamespace == "ProgramFlowTracer.Runtime" || containingNamespace.StartsWith("ProgramFlowTracer.Runtime.", StringComparison.Ordinal)))
        {
            return EligibilityResult.Ineligible("ProgramFlowTracer's own runtime code is never instrumented");
        }

        var containingTypeName = symbol.ContainingType?.ToDisplayString();
        if (config.IsClassExcluded(containingTypeName))
        {
            return EligibilityResult.Ineligible($"type '{containingTypeName}' is excluded by configuration");
        }

        var signature = symbol.ToDisplayString();
        if (config.IsMethodExcluded(signature))
        {
            return EligibilityResult.Ineligible($"'{signature}' is excluded by configuration");
        }

        return EligibilityResult.Eligible();
    }

    private static bool HasAttribute(ISymbol symbol, string shortAttributeName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            var name = attribute.AttributeClass?.Name;
            if (name is not null && name.Equals(shortAttributeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
