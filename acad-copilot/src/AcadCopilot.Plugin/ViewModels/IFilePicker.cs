namespace AcadCopilot.Plugin.ViewModels;

/// <summary>Lets the user pick files to attach (implemented with the Windows open-file dialog in the view).</summary>
public interface IFilePicker
{
    /// <summary>Shows the picker (only on the user's click) and returns the chosen absolute paths; empty when cancelled.</summary>
    IReadOnlyList<string> PickFiles();
}
