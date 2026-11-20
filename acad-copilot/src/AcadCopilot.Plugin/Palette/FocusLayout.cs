using System.IO;
using System.Text.Json;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Plugin.Palette;

/// <summary>
/// Focus mode as the panel sees it: on or off, and switching it. Implemented by <see cref="FocusLayoutController"/>
/// inside AutoCAD; tests use a stand-in. Main (UI) thread only.
/// </summary>
public interface IFocusLayoutHost
{
    /// <summary>Raised after focus mode was switched on or off (by the panel, the palette or AutoCAD closing).</summary>
    event EventHandler? Changed;

    /// <summary>True while focus mode is on.</summary>
    bool IsFocusMode { get; }

    /// <summary>
    /// Switches focus mode on or off. Returns what could not be done in one short sentence ("The ribbon could not be
    /// minimised"), or null when everything was applied or restored.
    /// </summary>
    string? SetFocusMode(bool on);
}

/// <summary>
/// The parts of AutoCAD's screen focus mode changes, behind an interface so <see cref="FocusLayoutController"/> is
/// unit-tested without AutoCAD (<c>AcadLayoutSurface</c> is the real one). Every member is "best effort": null or false
/// when AutoCAD cannot do it right now. Main thread only.
/// </summary>
public interface ILayoutSurface
{
    /// <summary>
    /// The ribbon's minimise state (AdWindows <c>RibbonControl.AutoHideMode</c>: 0 = full ribbon, 1 = tab names only,
    /// 2 = panel titles, 3 = panel buttons), or null when there is no ribbon or it cannot be minimised.
    /// </summary>
    int? RibbonMode { get; }

    /// <summary>Sets the ribbon's minimise state; false when it failed.</summary>
    bool TrySetRibbonMode(int mode);

    /// <summary>An integer system variable saved in the registry (NAVBARDISPLAY), or null when it cannot be read.</summary>
    int? GetSystemVariable(string name);

    /// <summary>Sets an integer system variable saved in the registry; false when AutoCAD refused.</summary>
    bool TrySetSystemVariable(string name, int value);

    /// <summary>Id of the active drawing (<c>DrawingDirectory.IdOf</c>), or null when no drawing is open.</summary>
    string? ActiveDrawingId { get; }

    /// <summary>NAVVCUBEDISPLAY of the active drawing (saved in the drawing), or null when it cannot be read.</summary>
    int? ActiveViewCube { get; }

    /// <summary>
    /// Sets NAVVCUBEDISPLAY of the active drawing without marking the drawing as changed (a drawing with no unsaved
    /// changes must not ask "Save changes?" because of focus mode). False when it failed.
    /// </summary>
    bool TrySetActiveViewCube(int value);
}

/// <summary>
/// What focus mode changed, kept in %LOCALAPPDATA%\AcadCopilot\focus-layout.json while it is on, so a crash of AutoCAD
/// can never leave the user's ribbon minimised or navigation bar hidden: the next start puts them back
/// (<see cref="FocusLayoutController.RecoverIfNeeded"/>).
/// </summary>
public sealed record FocusLayoutState
{
    /// <summary>True while focus mode is applied (false or no file: nothing to restore).</summary>
    public bool Active { get; init; }

    /// <summary>The user's ribbon minimise state before focus mode, or null when focus mode did not change the ribbon.</summary>
    public int? RibbonPrevious { get; init; }

    /// <summary>The ribbon minimise state focus mode set.</summary>
    public int? RibbonApplied { get; init; }

    /// <summary>The user's NAVBARDISPLAY before focus mode, or null when focus mode did not change it.</summary>
    public int? NavBarPrevious { get; init; }

    /// <summary>When focus mode was switched on (UTC).</summary>
    public DateTime? AppliedUtc { get; init; }

    /// <summary>True when something still has to be restored.</summary>
    public bool HasSomethingToRestore => Active && (RibbonPrevious is not null || NavBarPrevious is not null);
}

/// <summary>Loads and saves <see cref="FocusLayoutState"/>. Never throws.</summary>
public sealed class FocusLayoutStore
{
    /// <summary>Creates the store.</summary>
    /// <param name="path">State file; defaults to %LOCALAPPDATA%\AcadCopilot\focus-layout.json.</param>
    public FocusLayoutStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.Root, "focus-layout.json");
    }

    /// <summary>The state file.</summary>
    public string Path { get; }

    /// <summary>Reads the state (inactive when missing or unreadable).</summary>
    public FocusLayoutState Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<FocusLayoutState>(File.ReadAllText(Path), CopilotJson.Options) ?? new FocusLayoutState()
                : new FocusLayoutState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            CopilotLog.Warn("Focus layout state unreadable; nothing to restore", ex);
            return new FocusLayoutState();
        }
    }

    /// <summary>Writes the state (best effort).</summary>
    public void Save(FocusLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, CopilotJson.Indented));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopilotLog.Warn("Could not save the focus layout state", ex);
        }
    }
}

/// <summary>
/// FOCUS MODE: more room for the drawing while the AI Copilot works. On: the ribbon is minimised to its tab names
/// (<c>RibbonControl.AutoHideMode</c> = 1, reversible; a ribbon the user had minimised already is left alone), the
/// navigation bar is hidden (NAVBARDISPLAY = 0, registry) and the ViewCube is hidden in every drawing that becomes
/// active (NAVVCUBEDISPLAY = 0, saved in the drawing, so it is changed without marking the drawing modified and put back
/// while the drawing is being saved). The command line is never touched. Off (the button, closing the panel, AutoCAD
/// closing): everything is put back exactly as it was, unless the user changed that item himself in the meantime.
/// While it is on, <see cref="FocusLayoutStore"/> remembers the previous values, so the next start restores them after
/// a crash. Pure logic over <see cref="ILayoutSurface"/>; main thread only.
/// </summary>
public sealed class FocusLayoutController : IFocusLayoutHost
{
    /// <summary>RibbonControl.AutoHideMode: full ribbon.</summary>
    public const int RibbonFull = 0;

    /// <summary>RibbonControl.AutoHideMode: minimised to the tab names (what focus mode uses).</summary>
    public const int RibbonTabsOnly = 1;

    /// <summary>The navigation bar's system variable (registry).</summary>
    public const string NavBarVariable = "NAVBARDISPLAY";

    private readonly ILayoutSurface _surface;
    private readonly FocusLayoutStore _store;

    // Drawing id -> the drawing's NAVVCUBEDISPLAY before focus mode hid the ViewCube there.
    private readonly Dictionary<string, int> _viewCubes = new(StringComparer.OrdinalIgnoreCase);

    // Drawings whose ViewCube still has to come back (focus mode went off while another drawing was active).
    private readonly Dictionary<string, int> _restoreLater = new(StringComparer.OrdinalIgnoreCase);
    private FocusLayoutState _state = new();
    private bool _isFocusMode;

    /// <summary>Creates the controller.</summary>
    public FocusLayoutController(ILayoutSurface surface, FocusLayoutStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        _store = store ?? new FocusLayoutStore();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool IsFocusMode => _isFocusMode;

    /// <summary>Drawings whose ViewCube focus mode hid (or still has to put back); for tests and diagnostics.</summary>
    public int TrackedDrawings => _viewCubes.Count + _restoreLater.Count;

    /// <summary>
    /// After a crash with focus mode on: puts the ribbon and the navigation bar back as they were before (call at start-up,
    /// before focus mode is switched on again). Items AutoCAD cannot reach yet (no ribbon) stay remembered for later.
    /// </summary>
    public void RecoverIfNeeded()
    {
        if (_isFocusMode)
        {
            return;
        }

        var saved = _store.Load();
        if (!saved.HasSomethingToRestore)
        {
            return;
        }

        CopilotLog.Info("Focus mode was still on when AutoCAD last stopped: restoring the ribbon and the navigation bar.");
        var left = Restore(saved);
        _store.Save(left);
    }

    /// <inheritdoc />
    public string? SetFocusMode(bool on)
    {
        if (on == _isFocusMode)
        {
            return null;
        }

        var problem = on ? Apply() : Remove();
        Changed?.Invoke(this, EventArgs.Empty);
        return problem;
    }

    /// <summary>
    /// The active drawing changed (or a drawing opened): while focus mode is on its ViewCube is hidden; a drawing left
    /// behind when focus mode went off gets its ViewCube back.
    /// </summary>
    public void OnDrawingActivated()
    {
        if (_surface.ActiveDrawingId is not { } id)
        {
            return;
        }

        if (_restoreLater.Remove(id, out var previous))
        {
            RestoreViewCube(previous);
        }

        if (_isFocusMode)
        {
            HideViewCube(id);
        }
    }

    /// <summary>The active drawing is about to be saved: its file gets the user's ViewCube setting, not focus mode's.</summary>
    public void OnDrawingSaving(string drawingId)
    {
        if (IsActive(drawingId) && _viewCubes.TryGetValue(drawingId, out var previous))
        {
            RestoreViewCube(previous);
        }
    }

    /// <summary>The drawing was saved (or the save failed): the ViewCube is hidden again while focus mode is on.</summary>
    public void OnDrawingSaved(string drawingId)
    {
        if (_isFocusMode && IsActive(drawingId) && _viewCubes.ContainsKey(drawingId))
        {
            _surface.TrySetActiveViewCube(0);
        }
    }

    /// <summary>The drawing closed: nothing to put back there any more (its unsaved state is gone).</summary>
    public void OnDrawingClosed(string drawingId)
    {
        _viewCubes.Remove(drawingId);
        _restoreLater.Remove(drawingId);
    }

    private bool IsActive(string drawingId) => string.Equals(_surface.ActiveDrawingId, drawingId, StringComparison.OrdinalIgnoreCase);

    private string? Apply()
    {
        // Values a crash left behind are the user's real ones (the current ribbon may still be focus mode's).
        var leftover = _store.Load();
        var problems = new List<string>();
        int? ribbonPrevious = null;
        int? ribbonApplied = null;
        var ribbon = _surface.RibbonMode;
        if (leftover.Active && leftover.RibbonPrevious is { } crashed && (ribbon is null || ribbon == leftover.RibbonApplied))
        {
            ribbonPrevious = crashed;
            ribbonApplied = leftover.RibbonApplied;
        }
        else if (ribbon == RibbonFull)
        {
            if (_surface.TrySetRibbonMode(RibbonTabsOnly))
            {
                ribbonPrevious = RibbonFull;
                ribbonApplied = RibbonTabsOnly;
            }
            else
            {
                problems.Add("the ribbon could not be minimised");
            }
        }

        int? navPrevious = null;
        var navBar = _surface.GetSystemVariable(NavBarVariable);
        if (leftover.Active && leftover.NavBarPrevious is { } crashedNav && navBar is null or 0)
        {
            navPrevious = crashedNav;
        }
        else if (navBar is { } shown && shown != 0)
        {
            if (_surface.TrySetSystemVariable(NavBarVariable, 0))
            {
                navPrevious = shown;
            }
            else
            {
                problems.Add("the navigation bar could not be hidden");
            }
        }

        _isFocusMode = true;
        _state = new FocusLayoutState
        {
            Active = true,
            RibbonPrevious = ribbonPrevious,
            RibbonApplied = ribbonApplied,
            NavBarPrevious = navPrevious,
            AppliedUtc = DateTime.UtcNow,
        };
        _store.Save(_state);

        if (_surface.ActiveDrawingId is { } id && !HideViewCube(id))
        {
            problems.Add("the ViewCube could not be hidden");
        }

        CopilotLog.Info($"Focus mode on (ribbon {Describe(ribbonPrevious, ribbonApplied)}, navigation bar {(navPrevious is null ? "unchanged" : "hidden")}).");
        return Sentence(problems);
    }

    private string? Remove()
    {
        _isFocusMode = false;
        var left = Restore(_state);
        _state = left;
        _store.Save(left);

        // The ViewCube: back now in the active drawing, the others when they become active again.
        var active = _surface.ActiveDrawingId;
        var problems = new List<string>();
        foreach (var (id, previous) in _viewCubes)
        {
            if (string.Equals(id, active, StringComparison.OrdinalIgnoreCase))
            {
                if (!RestoreViewCube(previous))
                {
                    problems.Add("the ViewCube could not be shown again");
                }
            }
            else
            {
                _restoreLater[id] = previous;
            }
        }

        _viewCubes.Clear();
        if (left.RibbonPrevious is not null)
        {
            problems.Add("the ribbon could not be restored yet");
        }

        if (left.NavBarPrevious is not null)
        {
            problems.Add("the navigation bar could not be shown again");
        }

        CopilotLog.Info("Focus mode off" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : "; the layout is restored."));
        return Sentence(problems);
    }

    // Puts back what focus mode changed when it is still focus mode's value; returns what is left to restore.
    private FocusLayoutState Restore(FocusLayoutState state)
    {
        int? ribbonLeft = null;
        if (state.RibbonPrevious is { } ribbonPrevious)
        {
            var current = _surface.RibbonMode;
            if (current is null)
            {
                ribbonLeft = ribbonPrevious; // no ribbon right now: try again later
            }
            else if (current == state.RibbonApplied && current != ribbonPrevious && !_surface.TrySetRibbonMode(ribbonPrevious))
            {
                ribbonLeft = ribbonPrevious;
            }
        }

        int? navLeft = null;
        if (state.NavBarPrevious is { } navPrevious)
        {
            var current = _surface.GetSystemVariable(NavBarVariable);
            if (current is null)
            {
                navLeft = navPrevious;
            }
            else if (current == 0 && navPrevious != 0 && !_surface.TrySetSystemVariable(NavBarVariable, navPrevious))
            {
                navLeft = navPrevious;
            }
        }

        return ribbonLeft is null && navLeft is null
            ? new FocusLayoutState()
            : state with { RibbonPrevious = ribbonLeft, NavBarPrevious = navLeft };
    }

    private bool HideViewCube(string id)
    {
        if (_viewCubes.ContainsKey(id))
        {
            return true;
        }

        if (_surface.ActiveViewCube is not { } current)
        {
            return false;
        }

        if (current == 0)
        {
            return true; // the user has it off already: nothing to do, nothing to put back
        }

        if (!_surface.TrySetActiveViewCube(0))
        {
            return false;
        }

        _viewCubes[id] = current;
        return true;
    }

    // Back to the user's value when it is still hidden (the user may have switched it on again himself).
    private bool RestoreViewCube(int previous) =>
        _surface.ActiveViewCube is not 0 || _surface.TrySetActiveViewCube(previous);

    private static string Describe(int? previous, int? applied) =>
        previous is null ? "unchanged" : $"minimised ({previous} -> {applied})";

    private static string? Sentence(List<string> problems)
    {
        if (problems.Count == 0)
        {
            return null;
        }

        var text = "Focus mode: " + string.Join(", ", problems) + ".";
        CopilotLog.Warn(text);
        return text;
    }
}
