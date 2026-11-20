using System.IO;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Palette;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// The focus layout: focus mode (ribbon minimised, ViewCube and navigation bar hidden, restored exactly, crash
/// recovery), following the AI's drawing (zoom decision), the remembered palette width, the Focus button and settings,
/// and the slim one-line header below 420 px. Renders artifacts\ui\21-*.png.
/// </summary>
public sealed class FocusLayoutTests : IDisposable
{
    private const double Width = 460;
    private const double Height = 760;

    private readonly string _temp;

    public FocusLayoutTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-focus-ui-").FullName;
    }

    public void Dispose()
    {
        DraftingPace.Watch = true;
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be open on a slow machine; the OS cleans the temp folder.
        }
    }

    // ------------------------------------------------------------------ focus mode

    [Fact]
    public void Focus_mode_minimises_the_ribbon_hides_navigation_and_restores_exactly()
    {
        var surface = new FakeSurface { Ribbon = 0, NavBar = 1, Drawing = "A", ViewCubes = { ["A"] = 3 } };
        var store = NewStore();
        var focus = new FocusLayoutController(surface, store);
        var changes = 0;
        focus.Changed += (_, _) => changes++;

        Assert.Null(focus.SetFocusMode(true));
        Assert.True(focus.IsFocusMode);
        Assert.Equal(FocusLayoutController.RibbonTabsOnly, surface.Ribbon);
        Assert.Equal(0, surface.NavBar);
        Assert.Equal(0, surface.ViewCubes["A"]);
        Assert.Equal(1, changes);

        // While it is on, the previous values are on disk (crash safety).
        var saved = store.Load();
        Assert.True(saved.Active);
        Assert.Equal(0, saved.RibbonPrevious);
        Assert.Equal(1, saved.NavBarPrevious);

        Assert.Null(focus.SetFocusMode(false));
        Assert.False(focus.IsFocusMode);
        Assert.Equal(0, surface.Ribbon);
        Assert.Equal(1, surface.NavBar);
        Assert.Equal(3, surface.ViewCubes["A"]);
        Assert.False(store.Load().Active);
        Assert.Equal(2, changes);

        // Switching to the state it is in does nothing.
        Assert.Null(focus.SetFocusMode(false));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Settings_the_user_had_already_are_left_alone_and_later_changes_by_the_user_win()
    {
        // The user's ribbon was minimised to panel buttons and his navigation bar was off already.
        var surface = new FakeSurface { Ribbon = 3, NavBar = 0, Drawing = "A", ViewCubes = { ["A"] = 0 } };
        var focus = new FocusLayoutController(surface, NewStore());
        focus.SetFocusMode(true);
        Assert.Equal(3, surface.Ribbon);
        Assert.Equal(0, surface.NavBar);
        Assert.Equal(0, surface.RibbonWrites + surface.NavBarWrites + surface.ViewCubeWrites);
        focus.SetFocusMode(false);
        Assert.Equal(3, surface.Ribbon);
        Assert.Equal(0, surface.NavBar);
        Assert.Equal(0, surface.ViewCubes["A"]);

        // A full ribbon is minimised; when the user opens it again himself during focus mode, that choice stays.
        var other = new FakeSurface { Ribbon = 0, NavBar = 1, Drawing = "B", ViewCubes = { ["B"] = 3 } };
        var second = new FocusLayoutController(other, NewStore());
        second.SetFocusMode(true);
        other.Ribbon = 2;
        other.NavBar = 1;
        second.SetFocusMode(false);
        Assert.Equal(2, other.Ribbon);
        Assert.Equal(1, other.NavBar);
    }

    [Fact]
    public void After_a_crash_the_next_start_puts_the_ribbon_and_navigation_bar_back()
    {
        var store = NewStore();
        var crashed = new FakeSurface { Ribbon = 0, NavBar = 1, Drawing = "A", ViewCubes = { ["A"] = 3 } };
        new FocusLayoutController(crashed, store).SetFocusMode(true);

        // AutoCAD stopped without switching focus mode off: its profile kept the minimised ribbon and the hidden bar.
        var restarted = new FakeSurface { Ribbon = crashed.Ribbon, NavBar = crashed.NavBar, Drawing = "A", ViewCubes = { ["A"] = 3 } };
        var focus = new FocusLayoutController(restarted, store);
        focus.RecoverIfNeeded();
        Assert.Equal(0, restarted.Ribbon);
        Assert.Equal(1, restarted.NavBar);
        Assert.False(store.Load().Active);

        // Nothing left: a second start changes nothing.
        restarted.RibbonWrites = 0;
        new FocusLayoutController(restarted, store).RecoverIfNeeded();
        Assert.Equal(0, restarted.RibbonWrites);
    }

    [Fact]
    public void A_ribbon_that_is_not_there_yet_is_restored_later_and_never_forgotten()
    {
        var store = NewStore();
        var crashed = new FakeSurface { Ribbon = 0, NavBar = 1 };
        new FocusLayoutController(crashed, store).SetFocusMode(true);

        // Next start: no ribbon yet (still loading); the navigation bar comes back, the ribbon stays remembered.
        var restarted = new FakeSurface { Ribbon = null, NavBar = 0 };
        var focus = new FocusLayoutController(restarted, store);
        focus.RecoverIfNeeded();
        Assert.Equal(1, restarted.NavBar);
        Assert.Equal(0, store.Load().RibbonPrevious);

        // Focus mode goes on before the ribbon exists: the user's real value (full ribbon) is kept as "previous".
        focus.SetFocusMode(true);
        restarted.Ribbon = FocusLayoutController.RibbonTabsOnly; // the ribbon appears, still minimised from last time
        focus.SetFocusMode(false);
        Assert.Equal(0, restarted.Ribbon);
        Assert.False(store.Load().Active);
    }

    [Fact]
    public void The_viewcube_follows_the_active_drawing_and_saves_keep_the_users_setting()
    {
        var surface = new FakeSurface { Ribbon = 0, NavBar = 1, Drawing = "A", ViewCubes = { ["A"] = 3, ["B"] = 2 } };
        var focus = new FocusLayoutController(surface, NewStore());
        focus.SetFocusMode(true);
        Assert.Equal(0, surface.ViewCubes["A"]);

        surface.Drawing = "B";
        focus.OnDrawingActivated();
        Assert.Equal(0, surface.ViewCubes["B"]);

        // Saving B: the file gets the user's value, then the ViewCube hides again.
        focus.OnDrawingSaving("B");
        Assert.Equal(2, surface.ViewCubes["B"]);
        focus.OnDrawingSaved("B");
        Assert.Equal(0, surface.ViewCubes["B"]);

        // Off while B is active: B now, A when it becomes active again.
        focus.SetFocusMode(false);
        Assert.Equal(2, surface.ViewCubes["B"]);
        Assert.Equal(0, surface.ViewCubes["A"]);
        surface.Drawing = "A";
        focus.OnDrawingActivated();
        Assert.Equal(3, surface.ViewCubes["A"]);
        Assert.Equal(0, focus.TrackedDrawings);

        // A drawing closed during focus mode is forgotten.
        focus.SetFocusMode(true);
        focus.OnDrawingClosed("A");
        Assert.Equal(0, focus.TrackedDrawings);
    }

    [Fact]
    public void What_cannot_be_done_is_reported_in_one_sentence()
    {
        var surface = new FakeSurface { Ribbon = 0, NavBar = 1, FailWrites = true };
        var focus = new FocusLayoutController(surface, NewStore());
        var problem = focus.SetFocusMode(true);
        Assert.Equal("Focus mode: the ribbon could not be minimised, the navigation bar could not be hidden.", problem);
        Assert.True(focus.IsFocusMode);
        Assert.Null(focus.SetFocusMode(false)); // nothing was changed, so nothing is left to restore
    }

    [Fact]
    public void The_state_file_survives_a_broken_file()
    {
        var path = Path.Combine(_temp, "broken-focus.json");
        File.WriteAllText(path, "{ not json");
        var store = new FocusLayoutStore(path);
        Assert.False(store.Load().Active);
        new FocusLayoutController(new FakeSurface { Ribbon = 0 }, store).RecoverIfNeeded();
    }

    // ------------------------------------------------------------------ follow the AI's drawing

    [Fact]
    public void Objects_inside_the_view_and_big_enough_keep_the_view()
    {
        var view = new PlanBox(0, 0, 400, 300);
        Assert.Null(ViewFraming.Decide(view, new PlanBox(100, 100, 200, 180)));
        Assert.Null(ViewFraming.Decide(view, new PlanBox(0, 0, 400, 300)));
        Assert.Null(ViewFraming.Decide(view, new PlanBox(10, 10, 30, 10))); // 20 wide = 5% of the view: visible
    }

    [Fact]
    public void Objects_outside_the_view_get_their_extents_with_a_ten_percent_margin()
    {
        var view = new PlanBox(0, 0, 400, 300); // aspect 0.75
        var decision = ViewFraming.Decide(view, new PlanBox(500, 0, 900, 100));
        Assert.NotNull(decision);
        Assert.Equal(FramingReason.Outside, decision.Value.Reason);
        var window = decision.Value.Window;

        // 400 x 100 + 10% each side = 480 x 120, fitted to the 4:3 viewport = 480 x 360, centred on (700, 50).
        Assert.Equal(480, window.Width, 6);
        Assert.Equal(360, window.Height, 6);
        Assert.Equal(700, window.CenterX, 6);
        Assert.Equal(50, window.CenterY, 6);

        // Partly outside counts as outside.
        Assert.Equal(FramingReason.Outside, ViewFraming.Decide(view, new PlanBox(350, 100, 450, 200))!.Value.Reason);
    }

    [Fact]
    public void Tiny_objects_are_zoomed_to_but_never_more_than_eight_times_in_one_call()
    {
        var view = new PlanBox(0, 0, 400, 300);
        var decision = ViewFraming.Decide(view, new PlanBox(200, 150, 203, 152));
        Assert.NotNull(decision);
        Assert.Equal(FramingReason.TooSmall, decision.Value.Reason);
        Assert.Equal(50, decision.Value.Window.Width, 6);  // 400 / 8
        Assert.Equal(37.5, decision.Value.Window.Height, 6);
        Assert.Equal(201.5, decision.Value.Window.CenterX, 6);

        // A single point outside the view: centred there at the same limit.
        var point = ViewFraming.Decide(view, new PlanBox(1000, 1000, 1000, 1000));
        Assert.Equal(FramingReason.Outside, point!.Value.Reason);
        Assert.Equal(50, point.Value.Window.Width, 6);

        // Bad input never moves the view.
        Assert.Null(ViewFraming.Decide(new PlanBox(0, 0, 0, 0), new PlanBox(1, 1, 2, 2)));
        Assert.Null(ViewFraming.Decide(view, new PlanBox(double.NaN, 0, 1, 1)));
    }

    [Fact]
    public void The_union_of_boxes_skips_invalid_ones()
    {
        Assert.Null(ViewFraming.Union([]));
        var union = ViewFraming.Union([new PlanBox(0, 0, 1, 1), new PlanBox(double.NaN, 0, 1, 1), new PlanBox(-5, 2, 3, 9)]);
        Assert.Equal(new PlanBox(-5, 0, 3, 9), union);
    }

    // ------------------------------------------------------------------ settings and palette width

    [Fact]
    public void Focus_on_open_and_follow_drawing_default_to_on_also_for_older_settings_files()
    {
        Assert.True(new PanelSettings().FocusModeOnOpen);
        Assert.True(new PanelSettings().FollowAiDrawing);
        var path = Path.Combine(_temp, "old-panel.json");
        File.WriteAllText(path, "{ \"approvalMode\": \"autoApprove\", \"voiceAutoSend\": true }");
        var settings = new PanelSettingsStore(path).Load();
        Assert.Equal(ApprovalMode.AutoApprove, settings.ApprovalMode);
        Assert.True(settings.FocusModeOnOpen);
        Assert.True(settings.FollowAiDrawing);
    }

    [Fact]
    public void The_palette_is_docked_460_wide_until_the_user_chooses_a_width()
    {
        Assert.Equal(460, PaletteHost.DefaultWidth);
        Assert.Equal(460, new PaletteState().DockWidth);

        // A file of the older version (430 px, never chosen by the user) docks at the new default.
        var path = Path.Combine(_temp, "palette.json");
        File.WriteAllText(path, "{ \"visible\": true, \"width\": 312, \"height\": 720 }");
        Assert.Equal(460, new PaletteStateStore(path).Load().DockWidth);

        Assert.Equal(520, new PaletteState { Width = 520, WidthChosen = true }.DockWidth);
        Assert.Equal(PaletteHost.MinDockWidth, new PaletteState { Width = 150, WidthChosen = true }.DockWidth);
        Assert.Equal(PaletteHost.MaxDockWidth, new PaletteState { Width = 3000, WidthChosen = true }.DockWidth);
    }

    [Fact]
    public void The_focus_button_switches_focus_mode_and_the_settings_are_remembered() => Sta.Run(() =>
    {
        var host = new FakeFocusHost();
        var store = new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"));
        var problems = new List<string>();
        var layout = new FocusLayoutViewModel(host, store, problems.Add);
        var raised = new List<string?>();
        layout.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(layout.CanFocus);
        Assert.False(layout.IsFocusMode);
        Assert.StartsWith(FocusLayoutViewModel.FocusToolTipText, layout.FocusToolTip, StringComparison.Ordinal);

        layout.ToggleFocusCommand.Execute(null);
        Assert.True(host.IsFocusMode);
        Assert.True(layout.IsFocusMode);
        Assert.Contains(nameof(FocusLayoutViewModel.IsFocusMode), raised);
        Assert.Contains("(on)", layout.FocusToolTip, StringComparison.Ordinal);

        host.Problem = "Focus mode: the ribbon could not be restored yet.";
        layout.ToggleFocusCommand.Execute(null);
        Assert.False(layout.IsFocusMode);
        Assert.Equal(host.Problem, Assert.Single(problems));

        layout.FocusModeOnOpen = false;
        layout.FollowAiDrawing = false;
        var saved = store.Load();
        Assert.False(saved.FocusModeOnOpen);
        Assert.False(saved.FollowAiDrawing);
        Assert.False(new FocusLayoutViewModel(null, store).FollowAiDrawing);

        var outside = new FocusLayoutViewModel(null, store);
        Assert.False(outside.CanFocus);
        Assert.False(outside.ToggleFocusCommand.CanExecute(null));
        layout.Dispose();
        return Task.CompletedTask;
    });

    // ------------------------------------------------------------------ renders

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Renders_the_focus_button_the_slim_header_and_the_drawing_area_settings(PanelTheme theme) => Sta.Run(async () =>
    {
        var host = new FakeFocusHost();
        var pane = new CopilotPane(FakeBackend.Create(), new CopilotPaneOptions
        {
            Theme = theme,
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
            EnableVoice = false,
            OpenFolder = _ => { },
            FocusLayout = host,
        });
        await pane.ViewModel.RefreshStatusesAsync();
        await Sta.Settle();

        Save(pane, "21-focus-off", theme, Width, 520);
        Assert.False(pane.IsCompactHeader);
        Assert.Equal(System.Windows.Visibility.Visible, pane.FocusButton.Visibility);

        pane.Layout.ToggleFocusCommand.Execute(null);
        await Sta.Settle();
        Save(pane, "21-focus-on", theme, Width, 520);

        // Narrower than 420 px: one slim line; the conversation gets the height.
        Save(pane, "21-slim-header-narrow", theme, 400, 520);
        Assert.True(pane.IsCompactHeader);
        Assert.Equal(System.Windows.Visibility.Visible, pane.CompactProviderBar.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, pane.ProviderSegment.Visibility);

        // A provider that needs signing in shows "Sign in" in the slim bar too.
        pane.ViewModel.SelectedProvider = pane.ViewModel.Providers.First(p => p.Id == ProviderId.Codex);
        await Sta.Settle();
        Save(pane, "21-slim-header-sign-in-narrow", theme, 340, 520);

        // Back to the full header when widened again.
        Save(pane, "21-focus-on-wide-again", theme, Width, 300);
        Assert.False(pane.IsCompactHeader);
        Assert.Equal(System.Windows.Visibility.Visible, pane.ProviderSegment.Visibility);

        pane.ViewModel.IsSettingsOpen = true;
        await Sta.Settle();
        Save(pane, "21-drawing-area-settings", theme, Width, 1000);
        pane.Shell.Dispose();
    });

    // ------------------------------------------------------------------ helpers

    private FocusLayoutStore NewStore() => new(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-focus.json"));

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width, double height)
    {
        var path = Snapshot.Render(pane, width, height, $"{name}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    /// <summary>AutoCAD's screen as focus mode sees it: ribbon state, NAVBARDISPLAY and each drawing's NAVVCUBEDISPLAY.</summary>
    private sealed class FakeSurface : ILayoutSurface
    {
        public int? Ribbon { get; set; }

        public int? NavBar { get; set; }

        public string? Drawing { get; set; }

        public Dictionary<string, int> ViewCubes { get; } = [];

        public bool FailWrites { get; init; }

        public int RibbonWrites { get; set; }

        public int NavBarWrites { get; private set; }

        public int ViewCubeWrites { get; private set; }

        public int? RibbonMode => Ribbon;

        public string? ActiveDrawingId => Drawing;

        public int? ActiveViewCube => Drawing is { } id && ViewCubes.TryGetValue(id, out var value) ? value : null;

        public bool TrySetRibbonMode(int mode)
        {
            if (FailWrites || Ribbon is null)
            {
                return false;
            }

            RibbonWrites++;
            Ribbon = mode;
            return true;
        }

        public int? GetSystemVariable(string name) => name == FocusLayoutController.NavBarVariable ? NavBar : null;

        public bool TrySetSystemVariable(string name, int value)
        {
            if (FailWrites || name != FocusLayoutController.NavBarVariable)
            {
                return false;
            }

            NavBarWrites++;
            NavBar = value;
            return true;
        }

        public bool TrySetActiveViewCube(int value)
        {
            if (FailWrites || Drawing is not { } id)
            {
                return false;
            }

            ViewCubeWrites++;
            ViewCubes[id] = value;
            return true;
        }
    }

    /// <summary>Focus mode stand-in for the panel tests.</summary>
    private sealed class FakeFocusHost : IFocusLayoutHost
    {
        public event EventHandler? Changed;

        public bool IsFocusMode { get; private set; }

        public string? Problem { get; set; }

        public string? SetFocusMode(bool on)
        {
            IsFocusMode = on;
            Changed?.Invoke(this, EventArgs.Empty);
            return Problem;
        }
    }
}
