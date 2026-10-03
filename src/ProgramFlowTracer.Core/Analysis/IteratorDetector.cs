using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProgramFlowTracer.Core.Analysis;

/// <summary>
/// Determines whether a member's own body contains a <c>yield return</c>/<c>yield break</c>
/// (making it an iterator method, which the C# language forbids wrapping in a
/// try/catch-with-catch-clause block). Deliberately does not descend into nested local functions
/// or lambdas/anonymous methods - a <c>yield</c> inside one of those belongs to that nested
/// member, not to the member being checked.
/// </summary>
internal static class IteratorDetector
{
    public static bool ContainsYield(SyntaxNode? body)
    {
        if (body is null)
        {
            return false;
        }

        var walker = new YieldWalker();
        walker.Visit(body);
        return walker.Found;
    }

    private sealed class YieldWalker : CSharpSyntaxWalker
    {
        public bool Found { get; private set; }

        public override void Visit(SyntaxNode? node)
        {
            if (Found || node is null)
            {
                return;
            }

            base.Visit(node);
        }

        public override void VisitYieldStatement(YieldStatementSyntax node)
        {
            Found = true;
        }

        // Nested members have their own eligibility check - never look inside them here.
        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) { }
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { }
        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { }
    }
}
