using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.QuickAsk;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Commands;

/// <summary>
/// Quick Ask commands: AI / QUICKAI open the Quick Ask box next to the cursor; -AI / -QUICKAI ask on the command line
/// ("AI: Ask the copilot:") and send at once; AIBUTTON shows / hides the floating AI button. The request goes to the chat
/// of the active drawing, the side panel stays as it is.
/// </summary>
public sealed class QuickAskCommands
{
    /// <summary>AI: opens the Quick Ask box next to the cursor.</summary>
    [CommandMethod(CopilotCommands.Group, "AI", CommandFlags.Modal)]
    public void Ask() => OpenBox();

    /// <summary>QUICKAI: the same as AI (for when another add-in defines AI).</summary>
    [CommandMethod(CopilotCommands.Group, "QUICKAI", CommandFlags.Modal)]
    public void QuickAsk() => OpenBox();

    /// <summary>-AI: asks for the request on the command line and sends it.</summary>
    [CommandMethod(CopilotCommands.Group, "-AI", CommandFlags.Modal)]
    public void AskOnCommandLine() => AskWithPrompt();

    /// <summary>-QUICKAI: the same as -AI.</summary>
    [CommandMethod(CopilotCommands.Group, "-QUICKAI", CommandFlags.Modal)]
    public void QuickAskOnCommandLine() => AskWithPrompt();

    /// <summary>AIBUTTON: shows the floating AI button when hidden, hides it when shown.</summary>
    [CommandMethod(CopilotCommands.Group, "AIBUTTON", CommandFlags.Modal | CommandFlags.NoHistory)]
    public void ToggleButton()
    {
        try
        {
            var options = QuickAskOptions.Shared;
            options.ShowButton = !options.ShowButton;
            AcadHost.WriteLine(options.ShowButton
                ? $"the floating AI button is shown in the drawing area ({options.Shortcut} or AI opens Quick Ask too)."
                : $"the floating AI button is hidden. {options.Shortcut} or the AI command still open Quick Ask; AIBUTTON shows it again.");
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("AIBUTTON failed", ex);
            AcadHost.WriteLine("could not change the AI button: " + ex.Message);
        }
    }

    private static void OpenBox()
    {
        try
        {
            if (!QuickAskService.OpenFromCommand(out var problem))
            {
                AcadHost.WriteLine("Quick Ask: " + problem);
            }
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("AI command failed", ex);
            AcadHost.WriteLine("Quick Ask could not open: " + ex.Message);
        }
    }

    private static void AskWithPrompt()
    {
        try
        {
            if (AcadApp.DocumentManager.MdiActiveDocument is not { } document)
            {
                return;
            }

            var answer = document.Editor.GetString(new PromptStringOptions("\nAI: Ask the copilot: ") { AllowSpaces = true });
            if (answer.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(answer.StringResult))
            {
                return;
            }

            if (QuickAskService.SubmitText(answer.StringResult.Trim(), out var problem))
            {
                document.Editor.WriteMessage("\nAI: sent to the chat of this drawing; the bubble next to the AI button shows the progress.");
            }
            else if (problem is not null)
            {
                AcadHost.WriteLine("Quick Ask: " + problem);
            }
        }
        catch (System.Exception ex)
        {
            CopilotLog.Error("-AI command failed", ex);
            AcadHost.WriteLine("Quick Ask could not send: " + ex.Message);
        }
    }
}
