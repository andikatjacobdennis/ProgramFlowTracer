using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProgramFlowTracer.Core.Analysis;
using ProgramFlowTracer.Core.Model;
using ProgramFlowTracer.Runtime.Configuration;

namespace ProgramFlowTracer.Core.Rewriting;

/// <summary>
/// The document-level Roslyn rewriter: walks one source file's syntax tree and replaces the body
/// of every eligible method, constructor, local function, and (optionally) property accessor with
/// an instrumented version. This is the only place in Core that mutates syntax trees; everything
/// else in <c>Rewriting</c>/<c>Analysis</c> is either pure code generation or pure policy.
/// </summary>
internal sealed class InstrumentationRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly FlowTracerConfig _config;
    private readonly string? _filePath;
    private readonly IMethodSymbol? _entryPointSymbol;
    private readonly UniqueNameGenerator _names = new();

    public int InstrumentedCount { get; private set; }

    public List<SkippedMethodInfo> Skipped { get; } = new();

    public InstrumentationRewriter(SemanticModel semanticModel, FlowTracerConfig config, string? filePath, IMethodSymbol? entryPointSymbol)
    {
        _semanticModel = semanticModel;
        _config = config;
        _filePath = filePath;
        _entryPointSymbol = entryPointSymbol;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var eligibility = MethodEligibility.CheckMethod(node, _semanticModel, _config);
        var symbol = _semanticModel.GetDeclaredSymbol(node);

        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        if (!eligibility.IsEligible || symbol is null)
        {
            RecordSkip(node.Identifier.Text, node, eligibility.Reason);
            return visited;
        }

        var isEntryPoint = _entryPointSymbol is not null && SymbolEqualityComparer.Default.Equals(symbol, _entryPointSymbol);
        var (isVoidLike, effectiveReturnType) = ClassifyReturn(symbol);
        var parameters = BuildParameterInfos(node.ParameterList, symbol.Parameters);
        var declaringType = symbol.ContainingType?.ToDisplayString() ?? string.Empty;

        var newBody = InstrumentMember(
            visited.Body,
            visited.ExpressionBody,
            symbol.Name,
            declaringType,
            node,
            parameters,
            isVoidLike,
            effectiveReturnType,
            isEntryPoint);

        InstrumentedCount++;
        return visited
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var eligibility = MethodEligibility.CheckConstructor(node, _semanticModel, _config);
        var symbol = _semanticModel.GetDeclaredSymbol(node);

        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

        if (!eligibility.IsEligible || symbol is null)
        {
            RecordSkip(node.Identifier.Text + " (constructor)", node, eligibility.Reason);
            return visited;
        }

        var parameters = BuildParameterInfos(node.ParameterList, symbol.Parameters);
        var declaringType = symbol.ContainingType?.ToDisplayString() ?? string.Empty;

        var newBody = InstrumentMember(
            visited.Body,
            visited.ExpressionBody,
            ".ctor",
            declaringType,
            node,
            parameters,
            isVoidLike: true,
            effectiveReturnType: null,
            isEntryPoint: false);

        InstrumentedCount++;
        return visited
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var eligibility = MethodEligibility.CheckLocalFunction(node, _semanticModel, _config);
        var symbol = _semanticModel.GetDeclaredSymbol(node);

        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;

        if (!eligibility.IsEligible || symbol is null)
        {
            RecordSkip(node.Identifier.Text + " (local function)", node, eligibility.Reason);
            return visited;
        }

        var (isVoidLike, effectiveReturnType) = ClassifyReturn(symbol);
        var parameters = BuildParameterInfos(node.ParameterList, symbol.Parameters);
        var declaringType = (symbol.ContainingType?.ToDisplayString() ?? string.Empty) + "+" + symbol.Name;

        var newBody = InstrumentMember(
            visited.Body,
            visited.ExpressionBody,
            symbol.Name,
            declaringType,
            node,
            parameters,
            isVoidLike,
            effectiveReturnType,
            isEntryPoint: false);

        InstrumentedCount++;
        return visited
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
    {
        var eligibility = MethodEligibility.CheckAccessor(node, _semanticModel, _config);
        var symbol = _semanticModel.GetDeclaredSymbol(node);

        var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;

        if (!eligibility.IsEligible || symbol is not IMethodSymbol accessorSymbol)
        {
            if (node.Body is not null || node.ExpressionBody is not null)
            {
                RecordSkip(node.Keyword.Text + " accessor", node, eligibility.Reason);
            }

            return visited;
        }

        var isGetter = accessorSymbol.MethodKind == MethodKind.PropertyGet;
        var declaringType = accessorSymbol.ContainingType?.ToDisplayString() ?? string.Empty;
        var parameters = isGetter
            ? new List<ParameterCaptureInfo>()
            : BuildSetterParameterInfo(accessorSymbol);

        ITypeSymbol? effectiveReturnType = isGetter ? accessorSymbol.ReturnType : null;

        var newBody = InstrumentMember(
            visited.Body,
            visited.ExpressionBody,
            accessorSymbol.Name,
            declaringType,
            node,
            parameters,
            isVoidLike: !isGetter,
            effectiveReturnType,
            isEntryPoint: false);

        InstrumentedCount++;
        return visited
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(default);
    }

    // Lambdas/anonymous methods are never instrumented directly (too fragile with type inference
    // and expression trees) - leave them exactly as visited (which still recurses into their body
    // in case it contains further eligible members, e.g. a local function declared inside one is
    // not legal C#, but nested method-call arguments containing more lambdas are handled fine by
    // normal recursion).

    private BlockSyntax InstrumentMember(
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? expressionBody,
        string methodName,
        string declaringType,
        SyntaxNode declarationNode,
        List<ParameterCaptureInfo> parameters,
        bool isVoidLike,
        ITypeSymbol? effectiveReturnType,
        bool isEntryPoint)
    {
        var (line, column) = GetLineAndColumn(declarationNode);

        var statements = body is not null
            ? (IEnumerable<StatementSyntax>)body.Statements
            : expressionBody is not null
                ? new StatementSyntax[] { ExpressionBodyToStatement(expressionBody, isVoidLike) }
                : Array.Empty<StatementSyntax>();

        // With value capture switched off, the arrays are not merely ignored at runtime - they are
        // never generated. That is the whole point: no array allocation, no boxing of arguments,
        // and no property getters invoked on the traced application's thread.
        var entryArray = _config.CaptureParameters ? ParameterArrayCodeGen.BuildEntryArray(parameters) : "null";
        var exitArray = _config.CaptureParameters ? ParameterArrayCodeGen.BuildExitArray(parameters) : "null";

        if (!_config.RecordMethodExits)
        {
            // One inserted statement, into the body as it already stands.
            var originalBlock = body ?? SyntaxFactory.Block(statements);
            return MethodBodyBuilder.BuildEntryOnly(
                originalBlock, methodName, declaringType, _filePath, line, column, entryArray);
        }

        // Minted once up front so ReturnRewriter (which runs first, rewriting "return" statements)
        // and MethodBodyBuilder (which wraps the result in try/catch/finally) agree on the name of
        // the FlowTraceCall local.
        var callToken = _names.NextCallToken();

        var returnTypeForTypeof = effectiveReturnType is null ? "void" : TypeForTypeof(effectiveReturnType);
        var returnTypeForDeclaration = effectiveReturnType is null ? null : TypeForDeclaration(effectiveReturnType);

        // With no exit events to record there is nothing to do at a return, so every `return`
        // statement is left exactly as written. That removes the largest source of generated
        // code - and with it every target-typed-return problem, since no temporary is declared.
        List<StatementSyntax> processedStatements;
        if (_config.RecordMethodExits)
        {
            var returnRewriter = new ReturnRewriter(
                _names, callToken, isVoidLike, returnTypeForTypeof, returnTypeForDeclaration, exitArray,
                _config.CaptureReturnValues);
            processedStatements = statements
                .Select(s => (StatementSyntax)returnRewriter.Visit(s)!)
                .ToList();
        }
        else
        {
            processedStatements = statements.ToList();
        }

        return MethodBodyBuilder.Build(
            _names,
            callToken,
            methodName,
            declaringType,
            _filePath,
            line,
            column,
            entryArray,
            exitArray,
            isVoidLike,
            processedStatements,
            isEntryPoint,
            _config.RecordMethodExits);
    }

    private static StatementSyntax ExpressionBodyToStatement(ArrowExpressionClauseSyntax expressionBody, bool isVoidLike)
    {
        var exprText = expressionBody.Expression.ToString();
        var text = isVoidLike ? $"{exprText};" : $"return {exprText};";
        return SyntaxFactory.ParseStatement(text);
    }

    private static (int? Line, int? Column) GetLineAndColumn(SyntaxNode node)
    {
        var token = node switch
        {
            MethodDeclarationSyntax m => m.Identifier,
            ConstructorDeclarationSyntax c => c.Identifier,
            LocalFunctionStatementSyntax l => l.Identifier,
            AccessorDeclarationSyntax a => a.Keyword,
            _ => default
        };

        if (token == default)
        {
            var span = node.GetLocation().GetLineSpan();
            return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }

        var lineSpan = token.GetLocation().GetLineSpan();
        return (lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1);
    }

    private List<ParameterCaptureInfo> BuildParameterInfos(BaseParameterListSyntax parameterList, IReadOnlyList<IParameterSymbol> parameterSymbols)
    {
        var result = new List<ParameterCaptureInfo>();
        var symbolsByName = parameterSymbols.ToDictionary(p => p.Name, p => p);

        foreach (var p in parameterList.Parameters)
        {
            var name = p.Identifier.Text;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            symbolsByName.TryGetValue(name, out var paramSymbol);

            var isOut = p.Modifiers.Any(SyntaxKind.OutKeyword);
            var isRef = p.Modifiers.Any(SyntaxKind.RefKeyword);
            var isThis = p.Modifiers.Any(SyntaxKind.ThisKeyword);

            var typeSymbol = paramSymbol?.Type;
            var isPointerOrRefStruct = typeSymbol is not null && (typeSymbol.TypeKind == TypeKind.Pointer || typeSymbol.TypeKind == TypeKind.FunctionPointer || typeSymbol.IsRefLikeType);

            var isIgnored = p.AttributeLists.SelectMany(al => al.Attributes).Any(a => AttributeNameMatches(a.Name, "FlowTraceIgnore"));
            var isSensitiveAttr = p.AttributeLists.SelectMany(al => al.Attributes).Any(a => AttributeNameMatches(a.Name, "FlowTraceSensitive"));
            var isSensitive = isSensitiveAttr || _config.IsSensitiveName(name);

            var typeForTypeof = typeSymbol is not null ? TypeForTypeof(typeSymbol) : (p.Type?.ToString() ?? "object");

            result.Add(new ParameterCaptureInfo(
                Name: name,
                TypeForTypeof: typeForTypeof,
                IsOut: isOut,
                IsRefOrOut: isOut || isRef,
                IsSensitive: isSensitive,
                CanCaptureValue: !isPointerOrRefStruct && !isIgnored && !isThis));
        }

        return result;
    }

    private List<ParameterCaptureInfo> BuildSetterParameterInfo(IMethodSymbol setterSymbol)
    {
        var valueParam = setterSymbol.Parameters.LastOrDefault();
        var propertyName = setterSymbol.AssociatedSymbol?.Name ?? "value";
        var isSensitive = _config.IsSensitiveName(propertyName) ||
                           (setterSymbol.AssociatedSymbol?.GetAttributes().Any(a => AttributeNameMatches(a.AttributeClass?.Name, "FlowTraceSensitiveAttribute")) ?? false);

        var typeSymbol = valueParam?.Type;
        var isPointerOrRefStruct = typeSymbol is not null && (typeSymbol.TypeKind == TypeKind.Pointer || typeSymbol.IsRefLikeType);
        var typeForTypeof = typeSymbol is not null ? TypeForTypeof(typeSymbol) : "object";

        return new List<ParameterCaptureInfo>
        {
            new("value", typeForTypeof, IsOut: false, IsRefOrOut: false, isSensitive, CanCaptureValue: !isPointerOrRefStruct)
        };
    }

    private static bool AttributeNameMatches(NameSyntax? name, string shortName)
    {
        if (name is null)
        {
            return false;
        }

        var text = name switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            _ => name.ToString()
        };

        return text.Equals(shortName, StringComparison.Ordinal) || text.Equals(shortName + "Attribute", StringComparison.Ordinal);
    }

    private static bool AttributeNameMatches(string? simpleName, string fullAttributeClassName) =>
        simpleName is not null && simpleName.Equals(fullAttributeClassName, StringComparison.Ordinal);

    private static (bool IsVoidLike, ITypeSymbol? EffectiveReturnType) ClassifyReturn(IMethodSymbol symbol)
    {
        var returnType = symbol.ReturnType;
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return (true, null);
        }

        if (symbol.IsAsync && returnType is INamedTypeSymbol named)
        {
            var unwrapped = TryUnwrapTaskLike(named);
            if (unwrapped is null)
            {
                return (true, null);
            }

            return (false, unwrapped);
        }

        return (false, returnType);
    }

    private static ITypeSymbol? TryUnwrapTaskLike(INamedTypeSymbol type)
    {
        if (!type.IsGenericType)
        {
            var full = type.ToDisplayString();
            return full is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" ? null : type;
        }

        var openGeneric = type.ConstructedFrom.ToDisplayString();
        if (openGeneric.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
            openGeneric.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal))
        {
            return type.TypeArguments.Length == 1 ? type.TypeArguments[0] : type;
        }

        return type;
    }

    /// <summary>
    /// The return type written out so it can declare a local, or <c>null</c> when it cannot be.
    /// <para>
    /// Differs from <see cref="TypeForTypeof"/> in two ways. Nullable reference annotations are
    /// kept, so <c>string?</c> declares <c>string?</c> and no annotation is introduced that the
    /// source did not already imply. And types that cannot legally declare a local that holds the
    /// returned value - pointers, anonymous types - return <c>null</c> so the caller falls back to
    /// <c>var</c> rather than emitting something that will not compile.
    /// </para>
    /// </summary>
    private static string? TypeForDeclaration(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Dynamic)
        {
            return "dynamic";
        }

        if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer || type.IsAnonymousType)
        {
            return null;
        }

        return type.ToDisplayString(DeclarationFormat);
    }

    private static readonly SymbolDisplayFormat DeclarationFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string TypeForTypeof(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Dynamic)
        {
            return "object";
        }

        if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
        {
            return "object";
        }

        // Tuple element names are not permitted inside typeof - "typeof((A, B, bool skip))" does
        // not compile. The underlying ValueTuple<...> is the same type without the names.
        if (type is INamedTypeSymbol { IsTupleType: true } tuple && tuple.TupleUnderlyingType is not null)
        {
            type = tuple.TupleUnderlyingType;
        }

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private void RecordSkip(string memberName, SyntaxNode node, string? reason)
    {
        var (line, _) = GetLineAndColumn(node);
        Skipped.Add(new SkippedMethodInfo(_filePath ?? string.Empty, line ?? 0, memberName, reason ?? "not eligible"));
    }
}
