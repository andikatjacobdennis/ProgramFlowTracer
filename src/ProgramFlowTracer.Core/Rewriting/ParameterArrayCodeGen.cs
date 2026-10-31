using System.Text;

namespace ProgramFlowTracer.Core.Rewriting;

/// <summary>Generates the C# source text for a <c>FlowTraceParameter[]</c> literal.</summary>
internal static class ParameterArrayCodeGen
{
    private const string ParamTypeName = "global::ProgramFlowTracer.Runtime.FlowTraceParameter";

    /// <summary>Builds the array used at method entry: every parameter, with <c>out</c> parameters
    /// rendered as <see cref="ParamTypeName"/>.Unavailable(...) since their value does not exist
    /// yet (and reading an unassigned <c>out</c> parameter is a compile error).</summary>
    public static string BuildEntryArray(IReadOnlyList<ParameterCaptureInfo> parameters)
    {
        if (parameters.Count == 0)
        {
            return "null";
        }

        var sb = new StringBuilder();
        sb.Append("new ").Append(ParamTypeName).Append("[] { ");
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var p = parameters[i];
            if (p.IsOut || !p.CanCaptureValue)
            {
                sb.Append(ParamTypeName).Append(".Unavailable(\"").Append(Escape(p.Name)).Append("\", typeof(").Append(p.TypeForTypeof).Append("))");
            }
            else
            {
                sb.Append("new ").Append(ParamTypeName).Append("(\"").Append(Escape(p.Name)).Append("\", typeof(").Append(p.TypeForTypeof).Append("), ").Append(p.Name).Append(", ").Append(p.IsSensitive ? "true" : "false").Append(")");
            }
        }

        sb.Append(" }");
        return sb.ToString();
    }

    /// <summary>Builds the array used at method exit: only <c>ref</c>/<c>out</c> parameters, whose
    /// final values are only knowable once the method body has run. By the time control reaches
    /// any return point, the C# compiler already guarantees every <c>out</c> parameter has been
    /// definitely assigned, so it is always safe to read them here.</summary>
    public static string BuildExitArray(IReadOnlyList<ParameterCaptureInfo> parameters)
    {
        var refOut = parameters.Where(p => p.IsRefOrOut).ToList();
        if (refOut.Count == 0)
        {
            return "null";
        }

        var sb = new StringBuilder();
        sb.Append("new ").Append(ParamTypeName).Append("[] { ");
        for (var i = 0; i < refOut.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var p = refOut[i];
            if (!p.CanCaptureValue)
            {
                sb.Append(ParamTypeName).Append(".Unavailable(\"").Append(Escape(p.Name)).Append("\", typeof(").Append(p.TypeForTypeof).Append("))");
            }
            else
            {
                sb.Append("new ").Append(ParamTypeName).Append("(\"").Append(Escape(p.Name)).Append("\", typeof(").Append(p.TypeForTypeof).Append("), ").Append(p.Name).Append(", false)");
            }
        }

        sb.Append(" }");
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
