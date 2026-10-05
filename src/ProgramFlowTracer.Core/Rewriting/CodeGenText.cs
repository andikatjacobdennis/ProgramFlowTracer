namespace ProgramFlowTracer.Core.Rewriting;

internal static class CodeGenText
{
    /// <summary>Renders a C# verbatim string literal, e.g. <c>@"C:\src\Foo.cs"</c>, safe for any
    /// file path (including ones containing backslashes or embedded quotes).</summary>
    public static string VerbatimStringLiteral(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        return "@\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string IntLiteralOrNull(int? value) => value?.ToString() ?? "null";
}
