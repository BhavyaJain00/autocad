using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Views;
using Autodesk.AutoCAD.Windows;

namespace AcadCopilot.Plugin.Palette;

/// <summary>
/// The AutoCAD palette that hosts the WPF chat pane (<see cref="CopilotPane"/>): a PaletteSet with a fixed GUID (AutoCAD
/// remembers its dock side and size per workspace), docked on the right about 430 px wide on the very first start, and
/// reopened at start-up when it was open when AutoCAD last closed. Main thread only.
/// </summary>
public sealed class PaletteHost : IDisposable
{
    /// <summary>Fixed tool id of the palette set (AutoCAD keys its persisted dock/size state on it).</summary>
    public static readonly Guid PaletteId = new("2F6C8B1E-4D3A-4E5F-9B7C-1A2D3E4F5A6B");

    /// <summary>Palette title.</summary>
    public const string Title = "AI Copilot";

    /// <summary>Width on the first start, device-independent pixels.</summary>
    public const double DefaultWidth = 430;

    /// <summary>Height of the floating palette on the first start, device-independent pixels.</summary>
    public const double DefaultHeight = 720;

    private readonly CopilotBackend _backend;
    private readonly Learning.IUserActionRecorder? _recorder;
    private readonly PaletteStateStore _store;
    private PaletteSet? _paletteSet;
    private CopilotPane? _pane;
    private PaletteState _state = new();
    private bool _frozen;

    /// <summary>Creates the host (the palette itself is created by <see cref="Create"/>).</summary>
    /// <param name="backend">The chat backend.</param>
    /// <param name="store">Where the palette's visibility and size are kept.</param>
    /// <param name="recorder">The "Learn from me" recorder shown in the panel (null hides it).</param>
    public PaletteHost(CopilotBackend backend, PaletteStateStore? store = null, Learning.IUserActionRecorder? recorder = null)
    {
        _backend = backend;
        _recorder = recorder;
        _store = store ?? new PaletteStateStore();
    }

    /// <summary>True once the palette set exists.</summary>
    public bool IsCreated => _paletteSet is not null;

    /// <summary>True while the palette is shown.</summary>
    public bool IsVisible
    {
        get
        {
            try
            {
                return _paletteSet?.Visible == true;
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Could not read the palette visibility", ex);
                return false;
            }
        }
    }

    /// <summary>The chat pane, once created.</summary>
    public CopilotPane? Pane => _pane;

    /// <summary>
    /// Creates the palette set with the chat pane (once). Shows it docked right on the very first start, and on later
    /// starts when it was open last time.
    /// </summary>
    public void Create()
    {
        if (_paletteSet is not null)
        {
            return;
        }

        var firstRun = _store.IsFirstRun;
        _state = _store.Load();
        _pane = new CopilotPane(_backend, new CopilotPaneOptions { Recorder = _recorder });
        var paletteSet = new PaletteSet(Title, PaletteId)
        {
            Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu,
            DockEnabled = DockSides.Left | DockSides.Right,
            MinimumSize = new System.Drawing.Size(300, 320),
            KeepFocus = true,
        };
        paletteSet.AddVisual(Title, _pane, true);
        paletteSet.StateChanged += OnStateChanged;
        paletteSet.SizeChanged += OnSizeChanged;
        _paletteSet = paletteSet;

        if (firstRun)
        {
            paletteSet.Visible = true;
            DockRight(paletteSet);
            SaveState(_state with { Visible = true });
            CopilotLog.Info("Palette created and docked right (first start).");
        }
        else if (_state.Visible)
        {
            paletteSet.Visible = true;
            DockRight(paletteSet);
            CopilotLog.Info("Palette reopened (it was open last time).");
        }
        else
        {
            CopilotLog.Info("Palette created hidden (it was closed last time).");
        }
    }

    /// <summary>Shows the palette (creating it if needed).</summary>
    public void Show()
    {
        Create();
        if (_paletteSet is { } paletteSet)
        {
            paletteSet.Visible = true;
            DockRight(paletteSet);
        }
    }

    /// <summary>
    /// Docks the palette on the right edge of AutoCAD (full height, about <see cref="DefaultWidth"/> px wide) when it is
    /// floating. The user wants the panel as a full-height side panel, like the drawing on the left and the AI on the right.
    /// </summary>
    private static void DockRight(PaletteSet paletteSet)
    {
        try
        {
            if (paletteSet.Dock == DockSides.Right)
            {
                return;
            }

            // The size set before docking becomes the docked width.
            paletteSet.DeviceIndependentSize = new System.Windows.Size(DefaultWidth, DefaultHeight);
            paletteSet.Dock = DockSides.Right;
            paletteSet.DeviceIndependentSize = new System.Windows.Size(DefaultWidth, paletteSet.DeviceIndependentSize.Height);
            CopilotLog.Info("Palette docked on the right.");
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not dock the palette on the right", ex);
        }
    }

    /// <summary>Hides the palette.</summary>
    public void Hide()
    {
        if (_paletteSet is { } paletteSet)
        {
            paletteSet.Visible = false;
        }
    }

    /// <summary>Shows the palette when hidden, hides it when shown. Returns true when it is now visible.</summary>
    public bool Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return false;
        }

        Show();
        return true;
    }

    /// <summary>
    /// Saves the current visibility and stops recording changes (call when AutoCAD starts closing: AutoCAD hides
    /// palettes while it shuts down, which must not be remembered as "closed by the user").
    /// </summary>
    public void Freeze()
    {
        if (_frozen)
        {
            return;
        }

        SaveState(_state with { Visible = IsVisible });
        _frozen = true;
    }

    /// <summary>Releases the chat tabs (stops running turns). The palette set itself is destroyed by AutoCAD.</summary>
    public void Dispose()
    {
        Freeze();
        if (_paletteSet is { } paletteSet)
        {
            paletteSet.StateChanged -= OnStateChanged;
            paletteSet.SizeChanged -= OnSizeChanged;
        }

        _pane?.Shell.Dispose();
    }

    private void OnStateChanged(object? sender, PaletteSetStateEventArgs e)
    {
        try
        {
            switch (e.NewState)
            {
                case StateEventIndex.Show:
                    SaveState(_state with { Visible = true });
                    break;
                case StateEventIndex.Hide:
                    SaveState(_state with { Visible = false });
                    break;
                case StateEventIndex.ThemeChange:
                    if (_pane is not null && AcadThemeProbe.TryGetCurrent() is { } theme && theme != _pane.Theme)
                    {
                        _pane.ApplyTheme(theme);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Palette state change handling failed", ex);
        }
    }

    private void OnSizeChanged(object? sender, PaletteSetSizeEventArgs e)
    {
        if (e.DeviceIndependentWidth > 0 && e.DeviceIndependentHeight > 0)
        {
            SaveState(_state with { Width = Math.Round(e.DeviceIndependentWidth), Height = Math.Round(e.DeviceIndependentHeight) });
        }
    }

    private void SaveState(PaletteState state)
    {
        if (_frozen || state == _state && !_store.IsFirstRun)
        {
            return;
        }

        _state = state;
        _store.Save(state);
    }
}
