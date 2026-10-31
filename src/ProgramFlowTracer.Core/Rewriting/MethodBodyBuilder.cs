using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProgramFlowTracer.Core.Rewriting;

/// <summary>
/// Builds the final instrumented body for one member: the <c>FlowTracer.Enter</c> call, the
/// try/catch/finally wrapper, and (for void-like members) the fallthrough exit call. Operates
/// purely on text/statement lists that callers have already prepared (return-rewritten, and with
/// any nested local functions already independently instrumented), then reparses the composed
/// text into a single <see cref="BlockSyntax"/> - simpler and far less error-prone than building
/// the equivalent tree via raw <c>SyntaxFactory</c> calls, at the cost of one extra parse pass.
/// </summary>
internal static class MethodBodyBuilder
{
    /// <summary>
    /// Entry-only instrumentation: one statement at the top of the body, and nothing else.
    ///
    /// The original block is kept and the call is *inserted* into it, rather than the body being
    /// re-composed from text and reparsed. That keeps every existing statement's trivia byte for
    /// byte - no re-indentation, no blank lines gained or lost - so the diff for a method is
    /// exactly one added line. There is no wrapper, so the method's control flow, exception
    /// behaviour and return statements are all untouched.
    /// </summary>
    public static BlockSyntax BuildEntryOnly(
        BlockSyntax body,
        string methodName,
        string declaringType,
        string? filePath,
        int? line,
        int? column,
        string entryParametersArray)
    {
        var call = SyntaxFactory.ParseStatement(
            $"global::ProgramFlowTracer.Runtime.FlowTracer.EnterOnly(" +
            $"\"{EscapeStringLiteral(methodName)}\", " +
            $"\"{EscapeStringLiteral(declaringType)}\", " +
            $"{CodeGenText.VerbatimStringLiteral(filePath)}, " +
            $"{CodeGenText.IntLiteralOrNull(line)}, " +
            $"{CodeGenText.IntLiteralOrNull(column)}, " +
            $"{entryParametersArray});");

        // Match the body's own layout instead of imposing one.
        //
        // The indentation of the statement that currently comes first is the call's leading
        // trivia, and a newline becomes its trailing trivia, so the call occupies its own line and
        // every existing line stays byte-identical - just one line further down. A body written on
        // a single line (`{ return null; }`) keeps that shape, with one space after the call, so a
        // one-line method does not become four.
        var anchor = body.Statements.Count > 0
            ? body.Statements[0].GetLeadingTrivia()
            : body.CloseBraceToken.LeadingTrivia;

        var newLine = FindEndOfLine(body, anchor);

        var placed = newLine is null
            ? call.WithTrailingTrivia(SyntaxFactory.Space)
            : call.WithLeadingTrivia(anchor.Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia)))
                  .WithTrailingTrivia(newLine.Value);

        return body.WithStatements(body.Statements.Insert(0, placed));
    }

    /// <summary>
    /// The newline separating the opening brace from the first statement, or <c>null</c> when
    /// there is none because the body is written on one line.
    /// <para>
    /// Deliberately looks only between the brace and that first statement. Searching the whole
    /// body would find the newline that ends the *closing* brace's line, and conclude that
    /// <c>{ return null; }</c> is multi-line.
    /// </para>
    /// </summary>
    private static SyntaxTrivia? FindEndOfLine(BlockSyntax body, SyntaxTriviaList anchor)
    {
        foreach (var trivia in body.OpenBraceToken.TrailingTrivia.Concat(anchor))
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return trivia;
            }
        }

        return null;
    }

    public static BlockSyntax Build(
        UniqueNameGenerator names,
        string callToken,
        string methodName,
        string declaringType,
        string? filePath,
        int? line,
        int? column,
        string entryParametersArray,
        string exitParametersArrayForFallthrough,
        bool isVoidLike,
        IEnumerable<StatementSyntax> processedStatements,
        bool isEntryPoint,
        bool recordExits)
    {
        var exVar = names.NextExceptionVar();

        var statementsText = string.Join("\n", processedStatements.Select(s => s.ToFullString()));

        var fallthrough = isVoidLike && recordExits
            ? $"global::ProgramFlowTracer.Runtime.FlowTracer.ExitVoid({callToken}, {exitParametersArrayForFallthrough});\n"
            : string.Empty;

        var shutdownCall = isEntryPoint
            ? "global::ProgramFlowTracer.Runtime.FlowTracer.ShutdownAsync().GetAwaiter().GetResult();\n"
            : string.Empty;

        var entryCall =
            $"var {callToken} = global::ProgramFlowTracer.Runtime.FlowTracer.Enter(" +
            $"\"{EscapeStringLiteral(methodName)}\", " +
            $"\"{EscapeStringLiteral(declaringType)}\", " +
            $"{CodeGenText.VerbatimStringLiteral(filePath)}, " +
            $"{CodeGenText.IntLiteralOrNull(line)}, " +
            $"{CodeGenText.IntLiteralOrNull(column)}, " +
            $"{entryParametersArray});\n";

        // Exceptions are observed from a *filter*, not a handler.
        //
        // `catch (Exception) { record; throw; }` would make the traced method a genuine handler:
        // the runtime's first pass stops there and unwinds, so an outer `catch ... when (filter)`
        // runs its filter after inner `finally` blocks rather than before it, and the rethrow makes
        // the exception look handled. `when (ObserveException(...))` records and returns false, so
        // the first pass walks straight past - indistinguishable from no handler at all.
        //
        // The body is unreachable while ObserveException returns false; `throw;` keeps it correct
        // rather than merely unreachable.
        var catchClause = recordExits
            ? $"catch (global::System.Exception {exVar}) " +
              $"when (global::ProgramFlowTracer.Runtime.FlowTracer.ObserveException({callToken}, {exVar}))\n" +
              "{\n" +
              "throw;\n" +
              "}\n"
            : string.Empty;

        var text =
            "{\n" +
            entryCall +
            "try\n{\n" +
            statementsText + "\n" +
            fallthrough +
            "}\n" +
            catchClause +
            "finally\n{\n" +
            // Leave writes no event; it restores the AsyncLocal call context. Without it the
            // parent/child chain would be wrong for everything that ran afterwards, so it stays
            // even when no exit event is recorded.
            $"global::ProgramFlowTracer.Runtime.FlowTracer.Leave({callToken});\n" +
            shutdownCall +
            "}\n" +
            "}\n";

        // Annotated so the engine can format *only* what was injected, leaving every line the
        // user wrote exactly as they wrote it.
        return ((BlockSyntax)SyntaxFactory.ParseStatement(text))
            .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);
    }

    private static string EscapeStringLiteral(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
