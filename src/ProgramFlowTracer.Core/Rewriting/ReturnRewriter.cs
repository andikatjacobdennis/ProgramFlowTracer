using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProgramFlowTracer.Core.Rewriting;

/// <summary>
/// Rewrites every <c>return</c> statement that belongs directly to one member (method/constructor
/// /local function/accessor) into a block that records a MethodExit event before returning.
/// Never descends into nested local functions, lambdas, or anonymous methods - those are
/// instrumented independently (their own <c>return</c> statements belong to them, not to the
/// member currently being rewritten).
/// </summary>
internal sealed class ReturnRewriter : CSharpSyntaxRewriter
{
    private readonly UniqueNameGenerator _names;
    private readonly string _callToken;
    private readonly bool _isVoidLike;
    private readonly string _returnTypeForTypeof;
    private readonly string? _returnTypeForDeclaration;
    private readonly string _exitParametersArray;
    private readonly bool _captureReturnValues;

    public ReturnRewriter(
        UniqueNameGenerator names,
        string callToken,
        bool isVoidLike,
        string returnTypeForTypeof,
        string? returnTypeForDeclaration,
        string exitParametersArray,
        bool captureReturnValues)
    {
        _names = names;
        _callToken = callToken;
        _isVoidLike = isVoidLike;
        _returnTypeForTypeof = returnTypeForTypeof;
        _returnTypeForDeclaration = returnTypeForDeclaration;
        _exitParametersArray = exitParametersArray;
        _captureReturnValues = captureReturnValues;
    }


    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        string text;
        if (node.Expression is null || _isVoidLike)
        {
            // "return;" (or, defensively, a value-less return encountered in a void-like member).
            text = $"{{ global::ProgramFlowTracer.Runtime.FlowTracer.ExitVoid({_callToken}, {_exitParametersArray}); return; }}";
        }
        else
        {
            var returnVar = _names.NextReturnVar();
            var exprText = node.Expression.ToString();

            // The member's own return type, not `var`.
            //
            // `return expr;` converts expr to the return type, and declaring the temporary with
            // that same type reproduces exactly that conversion. `var` instead asks the compiler
            // to infer a type from the expression alone, which fails for every *target-typed*
            // expression C# allows in a return:
            //
            //   return null;                       CS0815 - no type to infer
            //   return default;                    CS0815
            //   return (baseline, null, false);    CS0815 - one untyped element sinks the tuple
            //   return x switch { A => new Foo(),  CS8506 - arms have types, but no common one
            //                     B => new Bar() };
            //
            // Guessing which expressions are target-typed from syntax alone kept missing cases;
            // naming the type is correct for all of them at once. It is also more faithful: the
            // recorded runtime type is the declared return type rather than whatever more-derived
            // type inference happened to pick.
            var declaredType = _returnTypeForDeclaration ?? "var";

            // The temporary stays even when the value is not recorded: the expression must be
            // evaluated *before* the exit is reported, so that a MethodExit is never written for a
            // return whose expression is still to run (or is about to throw).
            var report = _captureReturnValues
                ? $"global::ProgramFlowTracer.Runtime.FlowTracer.Exit({_callToken}, {returnVar}, typeof({_returnTypeForTypeof}), {_exitParametersArray});"
                : $"global::ProgramFlowTracer.Runtime.FlowTracer.ExitVoid({_callToken}, {_exitParametersArray});";

            text =
                $"{{ {declaredType} {returnVar} = {exprText}; " +
                report + " " +
                $"return {returnVar}; }}";
        }

        var block = ((BlockSyntax)SyntaxFactory.ParseStatement(text))
            .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);
        return block.WithTriviaFrom(node);
    }
}
