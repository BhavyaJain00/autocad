using System.IO;
using System.Globalization;
using System.Reflection;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Acad;

/// <summary>Facts about the hosting AutoCAD session (product, version, process, active drawing).</summary>
public static class AcadHost
{
    private static string? _productName;

    /// <summary>The AutoCAD process id.</summary>
    public static int ProcessId { get; } = Environment.ProcessId;

    /// <summary>Version of this plug-in ("0.1.0").</summary>
    public static string PluginVersion { get; } =
        typeof(AcadHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AcadHost).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>AutoCAD release number ("R26.0"). Any thread after start-up.</summary>
    public static string Release
    {
        get
        {
            var version = AcadApp.Version;
            return string.Create(CultureInfo.InvariantCulture, $"R{version.Major}.{version.Minor}");
        }
    }

    /// <summary>Product year of the release ("2027" for R26.0), or the release number when unknown.</summary>
    public static string VersionYear
    {
        get
        {
            var version = AcadApp.Version;
            return (version.Major, version.Minor) switch
            {
                (26, 0) => "2027",
                (25, 1) => "2026",
                (25, 0) => "2025",
                (24, 3) => "2024",
                _ => Release,
            };
        }
    }

    /// <summary>
    /// The product AutoCAD runs as: "AutoCAD 2027" for plain AutoCAD (the normal case), or a vertical such as
    /// "AutoCAD Architecture 2027" (acad.exe started with /product ACA, HostApplicationServices reporting it, or the AEC
    /// managed modules loaded; plain AutoCAD ships only the AEC object enablers, never AecBaseMgd). See
    /// <see cref="AcadProduct"/>. A vertical is cached; plain AutoCAD is re-checked (the AEC modules may load later).
    /// </summary>
    public static string ProductName
    {
        get
        {
            if (_productName is { } cached)
            {
                return cached;
            }

            string? host = null;
            try
            {
                host = HostApplicationServices.Current?.Product;
            }
            catch (System.Exception ex)
            {
                CopilotLog.Warn("Could not read HostApplicationServices.Product", ex);
            }

            var aec = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, "AecBaseMgd", StringComparison.OrdinalIgnoreCase));
            var name = AcadProduct.Describe(Environment.GetCommandLineArgs(), host, aec, VersionYear);
            if (!string.Equals(name, AcadProduct.AutoCad + " " + VersionYear, StringComparison.Ordinal))
            {
                _productName = name;
            }

            return name;
        }
    }

    /// <summary>True when the host offers AutoCAD Architecture objects (WALLADD, DOORADD ...); false in plain AutoCAD.</summary>
    public static bool HasArchitectureObjects => AcadProduct.HasArchitectureObjects(ProductName);

    /// <summary>File name of the active drawing ("Drawing1.dwg"), or null when none is open. Main thread.</summary>
    public static string? ActiveDocumentTitle()
    {
        try
        {
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            return document is null ? null : Path.GetFileName(document.Name);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not read the active drawing name", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes one line to AutoCAD's command line ("AI Copilot: ..."). Main thread; never throws (no drawing open = no output).
    /// </summary>
    public static void WriteLine(string message)
    {
        try
        {
            AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nAI Copilot: " + message);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not write to the command line", ex);
        }
    }
}
