using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.Runtime;

namespace AcadCopilot.Plugin.Commands;

/// <summary>AutoCAD commands of the plug-in: COPILOT (show / hide the chat palette) and COPILOTSTATUS (diagnostics).</summary>
public sealed class CopilotCommands
{
    /// <summary>Command group name.</summary>
    public const string Group = "ACADCOPILOT";

    /// <summary>COPILOT: shows the AI Copilot palette when hidden, hides it when shown. Also works during another command ('COPILOT).</summary>
    [CommandMethod(Group, "COPILOT", CommandFlags.Transparent | CommandFlags.NoHistory)]
    public void TogglePalette()
    {
        try
        {
            if (CopilotApp.Current is not { } app)
            {
                AcadHost.WriteLine("the plug-in is not initialized.");
                return;
            }

            app.TogglePalette();
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("COPILOT failed", ex);
            AcadHost.WriteLine("could not show the panel: " + ex.Message);
        }
    }

    /// <summary>COPILOTSTATUS: prints the pipe, discovery file, tool count, bridge path and log path.</summary>
    [CommandMethod(Group, "COPILOTSTATUS", CommandFlags.Modal)]
    public void Status()
    {
        try
        {
            if (CopilotApp.Current is not { } app)
            {
                AcadHost.WriteLine("the plug-in is not initialized.");
                return;
            }

            foreach (var line in app.DescribeStatus())
            {
                AcadHost.WriteLine(line);
            }
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("COPILOTSTATUS failed", ex);
            AcadHost.WriteLine("status failed: " + ex.Message);
        }
    }
}
