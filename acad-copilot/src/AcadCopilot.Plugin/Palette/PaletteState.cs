using System.IO;
using System.Text.Json;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Plugin.Palette;

/// <summary>What the palette remembers between AutoCAD sessions (%LOCALAPPDATA%\AcadCopilot\palette.json).</summary>
public sealed record PaletteState
{
    /// <summary>The palette was open when AutoCAD last closed (or the user last showed / hid it): reopen it at start-up.</summary>
    public bool Visible { get; init; } = true;

    /// <summary>Last docked width in device-independent pixels (used when <see cref="WidthChosen"/>).</summary>
    public double Width { get; init; } = PaletteHost.DefaultWidth;

    /// <summary>
    /// True once the user resized the docked panel himself: <see cref="Width"/> is then his choice and is used at every
    /// start. False (also for files of older versions) docks it <see cref="PaletteHost.DefaultWidth"/> wide.
    /// </summary>
    public bool WidthChosen { get; init; }

    /// <summary>The docked width to use: the user's own when he chose one, else the default; always within limits.</summary>
    public double DockWidth => Math.Clamp(WidthChosen && double.IsFinite(Width) ? Width : PaletteHost.DefaultWidth, PaletteHost.MinDockWidth, PaletteHost.MaxDockWidth);

    /// <summary>Last height in device-independent pixels (floating palette).</summary>
    public double Height { get; init; } = PaletteHost.DefaultHeight;
}

/// <summary>Loads and saves <see cref="PaletteState"/>. Never throws (the palette must open even with a broken file).</summary>
public sealed class PaletteStateStore
{
    /// <summary>Creates the store.</summary>
    /// <param name="path">State file; defaults to %LOCALAPPDATA%\AcadCopilot\palette.json.</param>
    public PaletteStateStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.Root, "palette.json");
    }

    /// <summary>The state file.</summary>
    public string Path { get; }

    /// <summary>True when no state was ever saved (first start: show the palette docked right).</summary>
    public bool IsFirstRun => !File.Exists(Path);

    /// <summary>Reads the state (defaults when missing or unreadable).</summary>
    public PaletteState Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<PaletteState>(File.ReadAllText(Path), CopilotJson.Options) ?? new PaletteState()
                : new PaletteState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            CopilotLog.Warn("Palette state unreadable; using defaults", ex);
            return new PaletteState();
        }
    }

    /// <summary>Writes the state (best effort).</summary>
    public void Save(PaletteState state)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, CopilotJson.Indented));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopilotLog.Warn("Could not save the palette state", ex);
        }
    }
}
