using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// Renders every panel state off-screen (RenderTargetBitmap, no window) to artifacts\ui\*.png so the look can be
/// reviewed, and asserts the key elements exist in each state. 430 px is the width of the palette docked on the right
/// of AutoCAD; 320 px checks a narrow palette.
/// </summary>
public sealed class PaneRenderTests : IDisposable
{
    private const double Width = 430;
    private const double NarrowWidth = 320;
    private const double Height = 760;

    private readonly string _temp;

    public PaneRenderTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-ui-").FullName;
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
            // Best effort.
        }
    }

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Setup_state_when_no_provider_is_signed_in(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create(claude: SignInState.SignedOut, codexInstalled: false, gemini: SignInState.SignedOut);
        var pane = await CreatePaneAsync(backend, theme);
        Assert.True(pane.ViewModel.ShowSetup);
        Assert.False(pane.ViewModel.ShowWelcome);
        Save(pane, "01-setup", theme);
        pane.ViewModel.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Welcome_state_with_example_prompts(PanelTheme theme) => Sta.Run(async () =>
    {
        var pane = await CreatePaneAsync(FakeBackend.Create(), theme);
        Assert.True(pane.ViewModel.ShowWelcome);
        Assert.Contains("Draw the front elevation of this plan", pane.ViewModel.ExamplePrompts);
        Assert.True(pane.ViewModel.HasModelChoice);
        Assert.True(pane.ViewModel.IsWatchPace);
        Save(pane, "02-welcome", theme);
        pane.ViewModel.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light, Width, "03-drafting")]
    [InlineData(PanelTheme.Dark, Width, "03-drafting")]
    [InlineData(PanelTheme.Light, NarrowWidth, "09-narrow-drafting")]
    [InlineData(PanelTheme.Dark, NarrowWidth, "09-narrow-drafting")]
    public void Drafting_turn_with_commands_picture_and_task_progress(PanelTheme theme, double width, string name) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, theme);
        var vm = pane.ViewModel;
        backend.HoldOpen = true;
        backend.Script.AddRange(DraftingScript());
        vm.ComposerText = ExamplePlanPrompt;
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(g => g.Rows.Count == 5), "tool rows");
        ReportDraftingCalls(vm);
        backend.Ui!.UpdatePlan(Plan(PlanTaskStatus.Completed, PlanTaskStatus.Completed, PlanTaskStatus.InProgress, PlanTaskStatus.Pending, PlanTaskStatus.Pending, PlanTaskStatus.Pending, PlanTaskStatus.Pending));
        await Sta.Settle();

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsPlanPinned);
        Assert.Equal("Task Progress (2/7)", vm.Plan.Header);
        var rows = vm.Items.OfType<ActivityGroupItem>().Single().Rows;
        Assert.Equal(["Read the drawing info", "Layers", "Plot and outer walls", "Looked at the drawing", "Inner walls (4 1/2\")"], rows.Select(r => r.Title));
        Assert.Equal([ActivityState.Succeeded, ActivityState.Succeeded, ActivityState.Succeeded, ActivityState.Succeeded, ActivityState.Running], rows.Select(r => r.State));
        Assert.True(rows[3].HasPicture);
        Assert.Equal("4 created · 1 erased", rows[2].OutcomeText);
        Save(pane, name, theme, width);
        if (width == Width)
        {
            // The whole conversation (the normal view keeps the newest part in sight).
            Save(pane, name + "-full", theme, width, 1560);
        }

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Room_list_and_draw_it_question(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, theme);
        var vm = pane.ViewModel;
        backend.HoldOpen = true;
        backend.Script.Add(new TextDone(RoomListMarkdown));
        vm.ComposerText = ExamplePlanPrompt;
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<AssistantMessageItem>().Any(), "room list");
        var answer = backend.Ui!.AskUserAsync(DrawItQuestion(), CancellationToken.None);
        await Sta.Settle();

        var card = vm.Items.OfType<QuestionCardItem>().Single();
        Assert.False(card.ShowSubmit);
        Assert.Equal(" (Recommended)", card.Questions[0].Options[0].RecommendedTag);
        Save(pane, "04-room-list-question", theme, Width, 900);

        card.Questions[0].Options[0].SelectCommand.Execute(null);
        var response = await answer;
        Assert.Equal(["Draw it"], response.Answers[0].Selected);
        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Brief_questions_with_typed_other_answer() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, PanelTheme.Light);
        var vm = pane.ViewModel;
        backend.HoldOpen = true;
        backend.Script.Add(new TextDone("A few details before I plan the rooms:"));
        vm.ComposerText = "Draw a ground floor plan for my plot";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<AssistantMessageItem>().Any(), "assistant text");
        var answer = backend.Ui!.AskUserAsync(BriefQuestions(), CancellationToken.None);
        await Sta.Settle();

        var card = vm.Items.OfType<QuestionCardItem>().Single();
        Assert.True(card.ShowSubmit);
        card.Questions[0].Options[1].SelectCommand.Execute(null);
        card.Questions[1].OtherCommand.Execute(null);
        card.Questions[1].CustomText = "North-east corner, 3'-6\" wide";
        await Sta.Settle();
        Save(pane, "04b-brief-questions", PanelTheme.Light);

        card.SubmitCommand.Execute(null);
        var response = await answer;
        Assert.False(response.Cancelled);
        Assert.Equal("North-east corner, 3'-6\" wide", response.Answers[1].Custom);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Approval_card_for_saving_the_drawing(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, theme);
        var vm = pane.ViewModel;
        backend.HoldOpen = true;
        backend.Script.Add(new TextDone("The ground floor plan is complete. I'll save the drawing now."));
        backend.Script.Add(new ToolCallStarted("s1", "run_commands", "title: Save, steps: 1 item", "acad"));
        vm.ComposerText = "Save the drawing";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(), "row");

        var approval = backend.Ui!.RequestApprovalAsync(
            new ApprovalRequest
            {
                ToolName = "run_commands:Save the drawing (QSAVE)",
                ToolTitle = "Save the drawing (QSAVE)",
                Summary = "QSAVE writes the drawing to C:\\Projects\\Sharma House\\GF-plan-30x40.dwg.",
                Risk = ToolRisk.Destructive,
            },
            CancellationToken.None);
        await Sta.Settle();
        var card = vm.Items.OfType<ApprovalCardItem>().Single();
        Assert.False(card.ShowAlways);
        Save(pane, "05-approval", theme);

        card.ApproveCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Approve, await approval);
        await Sta.Settle();
        Save(pane, "05b-approved", theme);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Not_signed_in_error_with_sign_in_button() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create(claude: SignInState.SignedOut, codex: SignInState.SignedIn);
        var pane = await CreatePaneAsync(backend, PanelTheme.Light);
        var vm = pane.ViewModel;
        backend.Script.Add(new TurnFailed("Claude Code is not signed in on this PC.", AgentFailureKind.NotSignedIn));
        vm.ComposerText = "What is in this drawing?";
        await vm.SendAsync();
        await Sta.Settle();

        var error = vm.Items.OfType<ErrorItem>().Single();
        Assert.True(error.ShowSignIn);
        Assert.True(vm.Items.OfType<TurnFooterItem>().Single().IsFailed);
        Save(pane, "06-not-signed-in", PanelTheme.Light);

        error.SignInCommand!.Execute(null);
        Assert.Equal([ProviderId.Claude], backend.SignIns);
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Completed_turn_with_markdown_plan_and_footer(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, theme);
        var vm = pane.ViewModel;
        backend.HoldOpen = true;
        backend.Script.AddRange(
        [
            new ToolCallStarted("a", "get_drawing_info", "", "acad"),
            new ToolCallFinished("a", true, "Architectural units (feet-inches), 214 objects on 9 layers"),
            new ToolCallStarted("b", "list_layers", "", "acad"),
            new ToolCallFinished("b", true, "9 layers"),
            new ToolCallStarted("c", "capture_view", "", "acad"),
            new ToolCallFinished("c", true, "View 46' x 32' around the plan"),
            new TextDone(SummaryMarkdown),
            new Usage("claude-opus", 18_400, 1_300),
        ]);
        vm.ComposerText = "What is in this drawing?";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<AssistantMessageItem>().Any(), "summary");
        backend.Ui!.UpdatePlan(Plan(PlanTaskStatus.Completed, PlanTaskStatus.Completed, PlanTaskStatus.Completed, PlanTaskStatus.Completed, PlanTaskStatus.Completed, PlanTaskStatus.Completed, PlanTaskStatus.Skipped));
        await Sta.Settle();
        backend.Release();
        await send;
        await Sta.Settle(6);

        Assert.False(vm.IsBusy);
        Assert.Single(vm.Items.OfType<PlanCardItem>());
        Assert.Contains("19.7k tokens", vm.Items.OfType<TurnFooterItem>().Single().Text, StringComparison.Ordinal);
        vm.Items.OfType<PlanCardItem>().Single().Plan.IsExpanded = true;
        await Sta.Settle();
        Save(pane, "07-completed", theme, Width, 980);
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Settings_open_with_sign_in_notice(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, theme);
        var vm = pane.ViewModel;
        vm.IsSettingsOpen = true;
        vm.SignIn(vm.Providers[1]);
        await Sta.Settle();
        Assert.True(vm.HasNotice);
        Assert.Equal([ProviderId.Codex], backend.SignIns);
        Save(pane, "08-settings", theme);
        vm.Dispose();
    });

    [Fact]
    public void Composer_with_attachments_and_fast_mode() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, PanelTheme.Light);
        var vm = pane.ViewModel;
        var pdf = Path.Combine(_temp, "Site plan - plot 14.pdf");
        var jpg = Path.Combine(_temp, "hand-sketch.jpg");
        File.WriteAllText(pdf, "pdf");
        File.WriteAllText(jpg, "jpg");
        vm.AddAttachments([pdf, jpg, Path.Combine(_temp, "missing.png")]);
        vm.ComposerText = "Use my hand sketch for the room layout and the site plan for the setbacks.\nKeep the car parking on the east side.";
        vm.IsFastPace = true;
        await Sta.Settle();
        Assert.Equal(2, vm.Attachments.Count);
        Assert.True(vm.NoticeIsError);
        Assert.False(DraftingPace.Watch);
        Save(pane, "10-attachments-fast", PanelTheme.Light);
        vm.Dispose();
    });

    [Fact]
    public void Switching_theme_recolors_the_same_pane() => Sta.Run(async () =>
    {
        var pane = await CreatePaneAsync(FakeBackend.Create(), PanelTheme.Light);
        pane.ApplyTheme(PanelTheme.Dark);
        Assert.Equal(PanelTheme.Dark, pane.Theme);
        Assert.True((bool)pane.FindResource("IsDarkTheme"));
        pane.ViewModel.Dispose();
    });

    [Fact]
    public void Both_themes_define_the_same_colors() => Sta.Run(() =>
    {
        var light = PanelThemes.Create(PanelTheme.Light);
        var dark = PanelThemes.Create(PanelTheme.Dark);
        Assert.Equal(light.Keys.Cast<object>().Select(k => k.ToString()).Order(), dark.Keys.Cast<object>().Select(k => k.ToString()).Order());
        Assert.Contains("AccentText", light.Keys.Cast<object>().Select(k => k.ToString()));
        return Task.CompletedTask;
    });

    internal const string ExamplePlanPrompt = "Draw the ground floor plan for a 30' x 40' east-facing plot: car parking, living, kitchen, 2 bedrooms, 1 toilet, staircase";

    internal static IEnumerable<AgentEvent> DraftingScript() =>
    [
        new SessionStarted("session-1"),
        new TextDelta("Starting with the layers, then the plot and the 9\" outer walls. "),
        new TextDone("Starting with the layers, then the plot and the 9\" outer walls. I'll look at the drawing after each stage."),
        new ToolCallStarted("c1", "get_drawing_info", "", "acad"),
        new ToolCallFinished("c1", true, "Architectural units (feet-inches), empty drawing"),
        new ToolCallStarted("c2", "run_commands", "title: Layers, steps: 3 items", "acad"),
        new ToolCallFinished("c2", true, "3 commands finished."),
        new ToolCallStarted("c3", "run_commands", "title: Plot and outer walls, steps: 6 items", "acad"),
        new ToolCallFinished("c3", true, "6 commands finished."),
        new ToolCallStarted("c4", "capture_view", "", "acad"),
        new ToolCallFinished("c4", true, "View 46' x 32' around the plan"),
        new ToolCallStarted("c5", "run_commands", "title: Inner walls (4 1/2\"), steps: 5 items", "acad"),
    ];

    /// <summary>What the plug-in reports for the calls of <see cref="DraftingScript"/> (full arguments and results).</summary>
    internal static void ReportDraftingCalls(IToolActivitySink sink)
    {
        var layers = Commands("Layers",
            ("-LAYER", ["M", "A-WALL", "C", "7", "A-WALL", ""], "walls"),
            ("-LAYER", ["M", "A-PLOT", "C", "8", "A-PLOT", "L", "DASHED", "A-PLOT", ""], "plot line"),
            ("-LAYER", ["S", "A-PLOT", ""], null));
        sink.OnToolCallStarting("run_commands", layers);
        sink.OnToolCallFinished("run_commands", layers, new ToolResult { Success = true, Message = "3 commands finished." });

        var walls = Commands("Plot and outer walls",
            ("RECTANG", ["0,0", "@30',40'"], "plot boundary"),
            ("-LAYER", ["S", "A-WALL", ""], null),
            ("PLINE", ["3',5'", "@24'<0", "@30'<90", "@24'<180", "C"], "outer face of the walls"),
            ("OFFSET", ["9\"", "L", "15',20'", ""], "inner face"),
            ("FILLET", ["R", "0", "L", "L"], null),
            ("ZOOM", ["E"], null));
        sink.OnToolCallStarting("run_commands", walls);
        sink.OnToolCallFinished("run_commands", walls, new ToolResult
        {
            Success = true,
            Message = "6 commands finished.",
            CreatedIds = [0x2A1, 0x2A2, 0x2A3, 0x2A4],
            DeletedIds = [0x2A0],
            Warnings = ["FILLET was left waiting at \"Select first object:\" - pressed Esc."],
        });

        sink.OnToolCallFinished("capture_view", new JsonObject(), new ToolResult
        {
            Success = true,
            Message = "View 46' x 32' around the plan",
            Images = [ToolImage.FromPng(TestImages.DrawingPng(360, 240))],
        });

        var inner = Commands("Inner walls (4 1/2\")",
            ("-LAYER", ["S", "A-WALL", ""], null),
            ("LINE", ["3'9\",19'", "@23'6\"<0", ""], "living / bedroom wall"),
            ("OFFSET", ["4-1/2\"", "L", "15',22'", ""], null),
            ("LINE", ["15',5'9\"", "@13'3\"<90", ""], "kitchen wall"),
            ("OFFSET", ["4-1/2\"", "L", "16',12'", ""], null));
        sink.OnToolCallStarting("run_commands", inner);
    }

    internal static JsonObject Commands(string title, params (string Command, string[] Inputs, string? Note)[] steps)
    {
        var array = new JsonArray();
        foreach (var (command, inputs, note) in steps)
        {
            var step = new JsonObject { ["command"] = command, ["inputs"] = new JsonArray([.. inputs.Select(i => (JsonNode)i)]) };
            if (note is not null)
            {
                step["note"] = note;
            }

            array.Add(step);
        }

        return new JsonObject { ["title"] = title, ["steps"] = array };
    }

    internal static PlanUpdate Plan(params PlanTaskStatus[] statuses)
    {
        string[] titles = ["Setup: units and layers", "Plot and outer walls (9\")", "Inner walls (4 1/2\")", "Doors and windows", "Staircase", "Room names", "Dimensions"];
        return new PlanUpdate
        {
            Title = "Ground floor plan · 30' x 40' plot, east facing",
            Tasks = [.. statuses.Select((s, i) => new PlanTask { Title = titles[i], Status = s })],
        };
    }

    internal static QuestionRequest DrawItQuestion() => new()
    {
        Questions =
        [
            new UserQuestion
            {
                Header = "Room sizes",
                Question = "Shall I draw this plan?",
                Options =
                [
                    new QuestionOption { Label = "Draw it", Description = "Plot, 9\" outer walls, rooms as listed", Recommended = true },
                    new QuestionOption { Label = "Change sizes" },
                    new QuestionOption { Label = "Change rooms" },
                ],
            },
        ],
    };

    internal static QuestionRequest BriefQuestions() => new()
    {
        Questions =
        [
            new UserQuestion
            {
                Header = "Road side",
                Question = "Which side of the plot faces the road?",
                Options =
                [
                    new QuestionOption { Label = "North" },
                    new QuestionOption { Label = "East", Description = "Main door and car parking on the east", Recommended = true },
                    new QuestionOption { Label = "South" },
                    new QuestionOption { Label = "West" },
                ],
                AllowCustom = false,
            },
            new UserQuestion
            {
                Header = "Staircase",
                Question = "Where should the staircase go?",
                Options =
                [
                    new QuestionOption { Label = "Inside, near the living room" },
                    new QuestionOption { Label = "Outside, along the side setback" },
                ],
            },
        ],
    };

    private const string RoomListMarkdown = """
        Here is the plan I propose for the **30' x 40'** east-facing plot (road on the east):

        | Room | Clear size | Position |
        |---|---|---|
        | Car parking | 10'-0" x 18'-0" | front-left (NE) |
        | Living | 12'-0" x 14'-0" | front-right |
        | Kitchen | 8'-0" x 10'-0" | middle-right |
        | Bedroom 1 | 11'-0" x 12'-0" | rear-left |
        | Bedroom 2 | 10'-0" x 11'-0" | rear-right |
        | Toilet | 5'-0" x 7'-0" | rear-middle |
        | Staircase | 3'-6" wide, dog-legged | middle-left |

        Outer walls 9", inner walls 4 1/2". Building 24'-0" x 30'-0" with 3' side and 5' front setbacks.
        """;

    private const string SummaryMarkdown = """
        ## Drawing summary

        **GF-plan-30x40.dwg** uses architectural units (feet-inches) and holds the ground floor plan.

        | Layer | Objects | Notes |
        |---|---|---|
        | A-WALL | 48 | 9" outer, 4 1/2" inner |
        | A-DOOR | 7 | 3'-0" main door |
        | A-DIMS | 22 | all in feet-inches |

        Worth checking:
        - **Toilet** has no window yet
        - The staircase has 18 risers of 6 1/2"
          - Landing 3'-6" deep
        1. Add the toilet ventilator
        2. Dimension the staircase

        > Each command run is one undo step (Ctrl+Z).

        ```
        Plot      30'-0" x 40'-0"
        Building  24'-0" x 30'-0"
        ```
        """;

    private async Task<CopilotPane> CreatePaneAsync(FakeBackend backend, PanelTheme theme)
    {
        var pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = theme,
            FilePicker = new NoFiles(),
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
        });
        await pane.ViewModel.RefreshStatusesAsync();
        Assert.Same(pane.ViewModel, backend.Ui);
        return pane;
    }

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width = Width, double height = Height)
    {
        var path = Snapshot.Render(pane, width, height, $"{name}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    private sealed class NoFiles : IFilePicker
    {
        public IReadOnlyList<string> PickFiles() => [];
    }
}
