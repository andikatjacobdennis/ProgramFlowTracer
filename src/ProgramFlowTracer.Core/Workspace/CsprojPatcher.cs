using System.Xml.Linq;

namespace ProgramFlowTracer.Core.Workspace;

/// <summary>Adds a reference to the copied ProgramFlowTracer.Runtime.dll into an instrumented
/// project's .csproj file, idempotently (safe to call again on an already-patched file).</summary>
public static class CsprojPatcher
{
    private const string RuntimeReferenceName = "ProgramFlowTracer.Runtime";

    public static void AddRuntimeReference(string csprojPath, string runtimeDllPathRelativeToCsproj)
    {
        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"'{csprojPath}' is not a valid project file (no root element).");

        var alreadyPresent = root
            .Elements("ItemGroup")
            .Elements("Reference")
            .Any(r => (string?)r.Attribute("Include") == RuntimeReferenceName)
            || root
            .Elements("ItemGroup")
            .Elements("ProjectReference")
            .Any(r => ((string?)r.Attribute("Include"))?.Contains(RuntimeReferenceName, StringComparison.OrdinalIgnoreCase) == true);

        if (alreadyPresent)
        {
            return;
        }

        var itemGroup = new XElement("ItemGroup",
            new XElement("Reference",
                new XAttribute("Include", RuntimeReferenceName),
                new XElement("HintPath", runtimeDllPathRelativeToCsproj),
                new XElement("Private", "true")));

        root.Add(itemGroup);
        doc.Save(csprojPath);
    }

    /// <summary>Stamps a marker property so ProgramFlowTracer can recognize its own instrumented
    /// output on subsequent runs (used by <c>run</c>/<c>clean</c> to avoid re-instrumenting from
    /// scratch every time, and as a safety check before deleting a directory during
    /// <c>restore</c>).</summary>
    public static void MarkAsInstrumented(string csprojPath, string runId)
    {
        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException($"'{csprojPath}' is not a valid project file.");

        var propertyGroup = root.Elements("PropertyGroup").FirstOrDefault();
        if (propertyGroup is null)
        {
            propertyGroup = new XElement("PropertyGroup");
            root.AddFirst(propertyGroup);
        }

        var marker = propertyGroup.Element("ProgramFlowTracerInstrumented");
        if (marker is null)
        {
            propertyGroup.Add(new XElement("ProgramFlowTracerInstrumented", "true"));
        }

        var stamp = propertyGroup.Element("ProgramFlowTracerInstrumentId");
        if (stamp is null)
        {
            propertyGroup.Add(new XElement("ProgramFlowTracerInstrumentId", runId));
        }
        else
        {
            stamp.Value = runId;
        }

        doc.Save(csprojPath);
    }
}
