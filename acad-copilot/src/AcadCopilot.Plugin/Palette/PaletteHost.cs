using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Views;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Palette;

/// <summary>
/// The AutoCAD palette that hosts the WPF chat pane (<see cref="CopilotPane"/>): a PaletteSet with a fixed GUID, always
/// docked on the RIGHT at full height (the drawing stays on the left), <see cref="DefaultWidth"/> wide until the user
/// resizes it (his width is remembered in palette.json), reopened at start-up when it was open when AutoCAD last closed.
/// Docking is checked when the palette is created, on the next two idles (AutoCAD may restore an older layout of the
/// palette after creating it) and on every COPILOT. While it is open, focus mode (<see cref="FocusLayoutController"/>)
/// gives the drawing more room when "Use focus mode when the AI Copilot opens" is on; closing the palette or AutoCAD
/// restores the layout. Main thread only.
/// </summary>
public sealed class PaletteHost : IDisposable
{
    /// <summary>Fixed tool id of the palette set (AutoCAD keys its persisted dock/size state on it).</summary>
    public static readonly Guid PaletteId = new("2F6C8B1E-4D3A-4E5F-9B7C-1A2D3E4F5A6B");

    /// <summary>Palette title.</summary>
    public const string Title = "AI Copilot";

    /// <summary>Docked width until the user resizes the panel, device-independent pixels.</summary>
    public const double DefaultWidth = 460;

    /// <summary>Narrowest docked width the panel is given back (a narrower one counts as cramped).</summary>
    public const double MinDockWidth = 320;

    /// <summary>Widest docked width the panel is given back.</summary>
    public const double MaxDockWidth = 900;

    /// <summary>Height of the floating palette on the first start, device-independent pixels.</summary>
    public const double DefaultHeight = 720;

    private readonly CopilotBackend _backend;
    private readonly Learning.IUserActionRecorder? _recorder;
    private readonly PaletteStateStore _store;
    private PaletteSet? _paletteSet;
    private CopilotPane? _pane;
    private PaletteState _state = new();
    private AcadFocusLayout? _focus;
    private bool _frozen;
    private bool _sizing;
    private bool _settled;
    private int _dockChecksLeft;
    private bool _idleHooked;

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
        _focus = CreateFocusLayout();
        _pane = new CopilotPane(_backend, new CopilotPaneOptions
        {
            Recorder = _recorder,
            Interaction = CreateInteraction(),
            FocusLayout = _focus?.Controller,
        });
        var paletteSet = new PaletteSet(Title, PaletteId)
        {
            Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu,
            DockEnabled = DockSides.Left | DockSides.Right,
            MinimumSize = new System.Drawing.Size((int)MinDockWidth - 20, 320),
            KeepFocus = true,
        };
        paletteSet.AddVisual(Title, _pane, true);
        paletteSet.StateChanged += OnStateChanged;
        paletteSet.SizeChanged += OnSizeChanged;
        _paletteSet = paletteSet;

        // "Resume previous job" cards: the crash-recovery monitor offers unfinished jobs of opened drawings.
        Recovery.ResumeMonitor.OfferChanged += OnResumeOfferChanged;
        if (Recovery.ResumeMonitor.CurrentOffer is { } pending)
        {
            ShowResumeOffer(pending, Recovery.ResumeMonitor.CurrentDrawingId);
        }

        if (firstRun)
        {
            Open(paletteSet);
            SaveState(_state with { Visible = true });
            CopilotLog.Info("Palette created and docked right (first start).");
        }
        else if (_state.Visible)
        {
            Open(paletteSet);
            CopilotLog.Info("Palette reopened (it was open last time).");
        }
        else
        {
            CopilotLog.Info("Palette created hidden (it was closed last time).");
        }
    }

    // "Click to point" and "select and ask" (Interaction/AcadDrawingInteraction): the composer's Point button and selection
    // chip; panel picks wait for their turn in the chats' AutoCAD queue. Null (buttons hidden) when it cannot start.
    private Interaction.IDrawingInteraction? CreateInteraction()
    {
        try
        {
            return Acad.CopilotApp.Current?.Dispatcher is { } dispatcher
                ? Interaction.AcadDrawingInteraction.GetOrCreate(dispatcher, _backend.DrawingQueue)
                : null;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Click-to-point and the selection chip are not available", ex);
            return null;
        }
    }

    // ResumeMonitor raises this on AutoCAD's main thread (the pane's UI thread).
    private void OnResumeOfferChanged(object? sender, Recovery.ResumeOfferEventArgs e)
    {
        if (e.Offer is { } offer)
        {
            ShowResumeOffer(offer, e.DrawingId);
        }
    }

    // The card goes into the tab working on that drawing, else the tab shown. Resume and Dismiss forget the journal.
    private void ShowResumeOffer(Core.Recovery.ResumeInfo offer, string? drawingId)
    {
        try
        {
            if (_pane is not { } pane)
            {
                return;
            }

            var shell = pane.Shell;
            var tab = (drawingId is null ? null : shell.TabForDrawing(drawingId)) ?? shell.SelectedTab;
            tab.ShowResumeOffer(offer, job => Recovery.ResumeMonitor.Dismiss(job.Session), job => Resume(job, drawingId));
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not show the unfinished job card", ex);
        }
    }

    // Sends ResumeMonitor.ResumePrompt in a tab bound to the job's drawing (a new tab when none is, which binds to the
    // active drawing, so that drawing must be the active one). False keeps the card open. Main thread.
    private bool Resume(Core.Recovery.ResumeInfo offer, string? drawingId)
    {
        if (_pane is not { } pane)
        {
            return false;
        }

        var shell = pane.Shell;
        var selected = shell.SelectedTab;
        var tab = drawingId is null ? selected : shell.TabForDrawing(drawingId);
        if (tab is null)
        {
            var title = offer.DrawingTitle ?? "the drawing of that job";
            if (!Acad.DrawingDirectory.IsActive(drawingId!))
            {
                selected.ShowNotice($"Switch to {title} in AutoCAD first, then click Resume again.");
                return false;
            }

            tab = selected.Backend is ICopilotSession { Drawing: null } && !selected.IsBusy ? selected : shell.NewTab();
            if (tab is null)
            {
                selected.ShowNotice("Close a chat tab first (too many are open), then click Resume again.");
                return false;
            }
        }

        shell.SelectTab(tab);
        var job = offer.PlanTitle is { Length: > 0 } plan ? $": {plan}" : string.Empty;
        tab.SendOrQueue("Resume previous job" + job, Recovery.ResumeMonitor.ResumePrompt(offer));
        CopilotLog.Info($"Recovery: resuming the unfinished job of {offer.DrawingTitle} (journal {offer.Session}).");
        return true;
    }

    /// <summary>Shows the palette (creating it if needed), docked right at full height, wide enough, in focus mode when set.</summary>
    public void Show()
    {
        Create();
        if (_paletteSet is { } paletteSet)
        {
            Open(paletteSet);
        }
    }

    /// <summary>Hides the palette (focus mode ends with it).</summary>
    public void Hide()
    {
        if (_paletteSet is { } paletteSet)
        {
            paletteSet.Visible = false;
        }

        LeaveFocusMode();
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
    /// Saves the current visibility, puts AutoCAD's layout back (focus mode off, so AutoCAD saves the user's own ribbon
    /// state in his profile) and stops recording changes (call when AutoCAD starts closing: AutoCAD hides palettes while
    /// it shuts down, which must not be remembered as "closed by the user").
    /// </summary>
    public void Freeze()
    {
        if (_frozen)
        {
            return;
        }

        SaveState(_state with { Visible = IsVisible });
        _frozen = true;
        LeaveFocusMode();
    }

    /// <summary>Releases the chat tabs (stops running turns). The palette set itself is destroyed by AutoCAD.</summary>
    public void Dispose()
    {
        Freeze();
        Recovery.ResumeMonitor.OfferChanged -= OnResumeOfferChanged;
        UnhookIdle();
        if (_paletteSet is { } paletteSet)
        {
            paletteSet.StateChanged -= OnStateChanged;
            paletteSet.SizeChanged -= OnSizeChanged;
        }

        _pane?.Shell.Dispose();
        _pane?.Layout.Dispose();
        _focus?.Dispose();
        _focus = null;
    }

    // Focus mode inside AutoCAD; a crash with focus mode on is repaired first (the user's ribbon and navigation bar come
    // back before anything else). Null (no Focus button) when it cannot start.
    private static AcadFocusLayout? CreateFocusLayout()
    {
        try
        {
            var focus = new AcadFocusLayout();
            focus.Controller.RecoverIfNeeded();
            return focus;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Focus mode is not available", ex);
            return null;
        }
    }

    // Visible, docked right and wide enough now and on the next idles, then focus mode when the user wants it.
    private void Open(PaletteSet paletteSet)
    {
        paletteSet.Visible = true;
        EnsureDockedRight(paletteSet);
        QueueDockChecks();
        EnterFocusModeIfWanted();
    }

    private void EnterFocusModeIfWanted()
    {
        try
        {
            if (_frozen || _focus is not { } focus || focus.Controller.IsFocusMode || !new ViewModels.PanelSettingsStore().Load().FocusModeOnOpen)
            {
                return;
            }

            if (focus.Controller.SetFocusMode(true) is { } problem)
            {
                _pane?.ViewModel.ShowNotice(problem, isError: true);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Focus mode could not be switched on", ex);
        }
    }

    private void LeaveFocusMode()
    {
        try
        {
            _focus?.Controller.SetFocusMode(false);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Focus mode could not be switched off", ex);
        }
    }

    /// <summary>
    /// Docks the palette on the right edge of AutoCAD (full height) at the user's width (<see cref="PaletteState.DockWidth"/>,
    /// <see cref="DefaultWidth"/> until he resizes it): when it floats or sits on the left, when its docked width is not the
    /// remembered one (AutoCAD restored an old, cramped size), and when auto-hide rolled it up to a strip.
    /// </summary>
    private void EnsureDockedRight(PaletteSet paletteSet)
    {
        var width = _state.DockWidth;
        _sizing = true;
        try
        {
            if (paletteSet.AutoRollUp)
            {
                // Auto-hide keeps the panel a thin strip until hovered: the user asked for it fully open.
                paletteSet.AutoRollUp = false;
            }

            if (paletteSet.Dock != DockSides.Right)
            {
                // The size set before docking becomes the docked width.
                paletteSet.DeviceIndependentSize = new System.Windows.Size(width, DefaultHeight);
                paletteSet.Dock = DockSides.Right;
                paletteSet.DeviceIndependentSize = new System.Windows.Size(width, paletteSet.DeviceIndependentSize.Height);
                CopilotLog.Info($"Palette docked on the right, {width:0} px wide.");
            }
            else if (Math.Abs(paletteSet.DeviceIndependentSize.Width - width) > 4)
            {
                var was = paletteSet.DeviceIndependentSize.Width;
                paletteSet.DeviceIndependentSize = new System.Windows.Size(width, paletteSet.DeviceIndependentSize.Height);
                CopilotLog.Info($"Palette width set from {was:0} to {width:0} px.");
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not dock the palette on the right", ex);
        }
        finally
        {
            _sizing = false;
        }
    }

    // AutoCAD may apply the palette's saved workspace layout after it was shown: check the docking again on the next two
    // idles; after that, size changes are the user's and his width is remembered.
    private void QueueDockChecks()
    {
        _dockChecksLeft = 2;
        if (_idleHooked)
        {
            return;
        }

        try
        {
            AcadApp.Idle += OnIdleDockCheck;
            _idleHooked = true;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not schedule the palette docking check", ex);
            _settled = true;
        }
    }

    private void OnIdleDockCheck(object? sender, EventArgs e)
    {
        if (_paletteSet is { Visible: true } paletteSet)
        {
            EnsureDockedRight(paletteSet);
        }

        if (--_dockChecksLeft <= 0)
        {
            UnhookIdle();
            _settled = true;
        }
    }

    private void UnhookIdle()
    {
        if (!_idleHooked)
        {
            return;
        }

        try
        {
            AcadApp.Idle -= OnIdleDockCheck;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not release the palette docking check", ex);
        }

        _idleHooked = false;
    }

    private void OnStateChanged(object? sender, PaletteSetStateEventArgs e)
    {
        try
        {
            switch (e.NewState)
            {
                case StateEventIndex.Show:
                    SaveState(_state with { Visible = true });
                    EnterFocusModeIfWanted();
                    break;
                case StateEventIndex.Hide:
                    SaveState(_state with { Visible = false });
                    LeaveFocusMode();
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

    // Only the user's own resizing of the docked panel is remembered (not AutoCAD's restores or our own docking).
    private void OnSizeChanged(object? sender, PaletteSetSizeEventArgs e)
    {
        try
        {
            if (_sizing || !_settled || e.DeviceIndependentWidth < MinDockWidth - 20 || e.DeviceIndependentHeight <= 0 ||
                _paletteSet is not { Dock: DockSides.Left or DockSides.Right })
            {
                return;
            }

            var width = Math.Clamp(Math.Round(e.DeviceIndependentWidth), MinDockWidth, MaxDockWidth);
            if (!_state.WidthChosen || Math.Abs(_state.Width - width) >= 1)
            {
                SaveState(_state with { Width = width, WidthChosen = true });
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not remember the palette width", ex);
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
