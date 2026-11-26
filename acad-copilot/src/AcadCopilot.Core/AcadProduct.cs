namespace AcadCopilot.Core;

/// <summary>
/// Which AutoCAD-based product hosts the plug-in: plain AutoCAD (the default, e.g. "AutoCAD 2027") or a vertical such as
/// AutoCAD Architecture. Pure logic; the plug-in feeds it the facts it reads inside AutoCAD (the acad.exe command line,
/// HostApplicationServices.Product and whether the AEC managed modules are loaded).
/// </summary>
public static class AcadProduct
{
    /// <summary>Plain AutoCAD's name without the year.</summary>
    public const string AutoCad = "AutoCAD";

    /// <summary>AutoCAD Architecture's name without the year.</summary>
    public const string Architecture = "AutoCAD Architecture";

    /// <summary>Blender desktop host name prefix.</summary>
    public const string Blender = "Blender";

    /// <summary>True when the host product is Blender desktop (e.g. "Blender 5.3").</summary>
    public static bool IsBlender(string? productName) =>
        productName is not null && productName.Contains(Blender, StringComparison.OrdinalIgnoreCase);

    // acad.exe /product <code> starts a vertical (toolset) of "AutoCAD including specialized toolsets".
    private static readonly Dictionary<string, string> ProductCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACAD"] = AutoCad,
        ["ACA"] = Architecture,
        ["MEP"] = "AutoCAD MEP",
        ["ACADE"] = "AutoCAD Electrical",
        ["ACADM"] = "AutoCAD Mechanical",
        ["MAP"] = "AutoCAD Map 3D",
        ["C3D"] = "Civil 3D",
        ["PNID"] = "AutoCAD P&ID",
        ["PLNT3D"] = "AutoCAD Plant 3D",
    };

    /// <summary>
    /// Names the product: "AutoCAD 2027" for plain AutoCAD, "AutoCAD Architecture 2027" when acad.exe was started with
    /// <c>/product ACA</c>, the host reports Architecture, or the AEC managed modules (AecBaseMgd, shipped only with
    /// AutoCAD Architecture / MEP, not with plain AutoCAD's object enablers) are loaded.
    /// </summary>
    /// <param name="commandLineArgs">acad.exe's command line arguments.</param>
    /// <param name="hostProduct">HostApplicationServices.Current.Product (may be null or just "AutoCAD").</param>
    /// <param name="aecManagedLoaded">True when AecBaseMgd is loaded in the process.</param>
    /// <param name="year">Product year ("2027").</param>
    public static string Describe(IReadOnlyList<string>? commandLineArgs, string? hostProduct, bool aecManagedLoaded, string year)
    {
        var name = FromCommandLine(commandLineArgs);
        if (name is null && !string.IsNullOrWhiteSpace(hostProduct))
        {
            var host = hostProduct.Trim();
            if (host.Contains("Architecture", StringComparison.OrdinalIgnoreCase))
            {
                name = Architecture;
            }
            else if (host.Contains("MEP", StringComparison.Ordinal))
            {
                name = "AutoCAD MEP";
            }
        }

        if (name is null && aecManagedLoaded)
        {
            name = Architecture;
        }

        name ??= AutoCad;
        return string.IsNullOrWhiteSpace(year) ? name : name + " " + year.Trim();
    }

    /// <summary>
    /// True when the product offers AutoCAD Architecture objects and commands (WALLADD, DOORADD, WINDOWADD ...):
    /// AutoCAD Architecture and AutoCAD MEP. Plain AutoCAD only displays such objects (object enabler).
    /// </summary>
    public static bool HasArchitectureObjects(string? productName) =>
        productName is not null &&
        (productName.Contains(Architecture, StringComparison.OrdinalIgnoreCase) ||
         productName.Contains("AutoCAD MEP", StringComparison.OrdinalIgnoreCase));

    private static string? FromCommandLine(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i]?.Trim(), "/product", StringComparison.OrdinalIgnoreCase))
            {
                var code = (args[i + 1] ?? string.Empty).Trim().Trim('"');
                return ProductCodes.TryGetValue(code, out var known) ? known : (code.Length == 0 ? null : AutoCad + " (" + code + ")");
            }
        }

        return null;
    }
}
