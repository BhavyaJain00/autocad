using System.IO;
using System.Runtime.CompilerServices;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.Views;

/// <summary>
/// Reads AutoCAD's UI theme (system variable COLORTHEME: 0 = dark, 1 = light). Returns null when AutoCAD's API is not
/// loaded (unit tests render the pane without AutoCAD) or the value cannot be read.
/// </summary>
internal static class AcadThemeProbe
{
    private static bool _failureLogged;

    /// <summary>The current AutoCAD theme, or null when unavailable.</summary>
    public static PanelTheme? TryGetCurrent()
    {
        try
        {
            return ReadTheme();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException or DllNotFoundException or TypeInitializationException)
        {
            // accoremgd.dll is not loaded (outside AutoCAD).
            return null;
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                CopilotLog.Warn("Could not read AutoCAD's UI theme; using the light theme", ex);
            }

            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PanelTheme ReadTheme()
    {
        var value = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("COLORTHEME");
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 0 ? PanelTheme.Dark : PanelTheme.Light;
    }
}
