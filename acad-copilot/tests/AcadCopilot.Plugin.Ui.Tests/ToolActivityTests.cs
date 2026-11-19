using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.ViewModels;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>"CAD activity" rows: run_commands commands, plug-in reports, capture_view pictures (no rendering).</summary>
public sealed class ToolActivityTests : IDisposable
{
    private readonly string _temp;

    public ToolActivityTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-activity-").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Command_echo_shows_inputs_like_the_command_line()
    {
        Assert.Equal("LINE  0,0 -> @30'<0 -> @40'<90", new CommandStepItem(1, "line", ["0,0", "@30'<0", "@40'<90", ""], null).Text);
        Assert.Equal("OFFSET  9\" -> ↵ -> 15',20'", new CommandStepItem(2, "OFFSET", ["9\"", "", "15',20'", "", ""], null).Text);
        Assert.Equal("REGEN", new CommandStepItem(3, " regen ", [], null).Text);
        Assert.Equal("ZOOM  E", new CommandStepItem(4, "ZOOM", ["E"], "fit the plan").Text);
        Assert.Equal("-LAYER", new CommandStepItem(5, "-layer", ["", ""], null).Text);

        var shortened = CommandEcho.FormatInputs(["0,0", "@30'<0", "@40'<90", "@30'<180", "C"], 30);
        Assert.Equal("0,0 -> @30'<0 -> … +3 more", shortened);
        Assert.True(shortened.Length <= 30);
        Assert.Equal("0,0 -> @30'<0 -> @40'<90 -> @30'<180 -> C", CommandEcho.FormatInputs(["0,0", "@30'<0", "@40'<90", "@30'<180", "C"], int.MaxValue));

        var pline = new CommandStepItem(1, "PLINE", [.. Enumerable.Range(0, 40).Select(i => $"@{i + 1}'<{i * 9}"), ""], "outer face");
        Assert.True(pline.Text.Length <= CommandEcho.DefaultMaxLength, pline.Text);
        Assert.EndsWith("more", pline.Text, StringComparison.Ordinal);
        Assert.Contains("@40'<351", pline.ToolTip, StringComparison.Ordinal);
        Assert.Contains("outer face", pline.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_commands_summary_gives_the_title_and_command_count()
    {
        Assert.True(RunCommandsSummary.TryParse("title: Outer walls, steps: 3 items", out var title, out var count));
        Assert.Equal("Outer walls", title);
        Assert.Equal(3, count);

        Assert.True(RunCommandsSummary.TryParse("steps: 1 item, title: Plot boundary", out title, out count));
        Assert.Equal("Plot boundary", title);
        Assert.Equal(1, count);

        Assert.False(RunCommandsSummary.TryParse("", out _, out _));
        Assert.False(RunCommandsSummary.TryParse("layer: A-WALL", out _, out _));
    }

    [Fact]
    public void Run_commands_row_uses_the_cli_summary_until_the_plug_in_reports_the_call() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        backend.Script.Add(new ToolCallStarted("c1", "run_commands", "title: Outer walls, steps: 2 items", "acad"));
        vm.ComposerText = "Draw the outer walls";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(), "row");
        var row = vm.Items.OfType<ActivityGroupItem>().Single().Rows.Single();
        Assert.Equal(ActivityKind.Commands, row.Kind);
        Assert.Equal("Outer walls", row.Title);
        Assert.Equal("2 commands", row.SubtitleText);
        Assert.False(row.HasSteps);

        IToolActivitySink sink = vm;
        var arguments = OuterWalls();
        sink.OnToolCallStarting("run_commands", arguments);
        arguments["title"] = "changed by the caller later";
        await Sta.Settle();
        Assert.True(row.HasSteps);
        Assert.Equal("Outer walls (9\")", row.Title);
        Assert.Equal(["PLINE  0,0 -> @30'<0 -> @40'<90 -> @30'<180 -> C", "OFFSET  9\" -> L -> 15',20'"], row.Steps.Select(s => s.Text));
        Assert.Equal("plot boundary", row.Steps[0].Note);

        sink.OnToolCallFinished("run_commands", OuterWalls(), new ToolResult
        {
            Success = true,
            Message = "2 commands finished.",
            CreatedIds = [0x2A1, 0x2A2],
            DeletedIds = [0x1F0],
            Warnings = ["OFFSET was left waiting at \"Select object to offset or [Exit/Undo] <Exit>:\" - pressed Esc."],
        });
        await Sta.Settle();
        Assert.Equal("2 created · 1 erased", row.OutcomeText);
        Assert.Single(row.Warnings);
        Assert.True(row.IsRunning);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Reports_that_arrive_before_the_cli_row_wait_for_it_and_pair_by_arguments() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        vm.ComposerText = "Draw";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.IsBusy, "busy");

        IToolActivitySink sink = vm;
        var first = Layers();
        var second = OuterWalls();
        sink.OnToolCallStarting("run_commands", first);
        sink.OnToolCallFinished("run_commands", first, new ToolResult { Success = true, Message = "3 layers" });
        await Sta.Settle();
        Assert.Empty(vm.Items.OfType<ActivityGroupItem>());

        vm.Apply(new ToolCallStarted("a", "run_commands", "title: Layers, steps: 2 items", "acad"), CurrentTurn(vm));
        vm.Apply(new ToolCallStarted("b", "run_commands", "title: Outer walls (9\"), steps: 2 items", "acad"), CurrentTurn(vm));
        var rows = vm.Items.OfType<ActivityGroupItem>().Single().Rows;
        Assert.True(rows[0].HasCallDetails);
        Assert.True(rows[0].HasResultDetails);
        Assert.False(rows[1].HasCallDetails);

        sink.OnToolCallStarting("run_commands", second);
        sink.OnToolCallFinished("run_commands", second, new ToolResult { Success = true, CreatedIds = [1, 2] });
        await Sta.Settle();
        Assert.Equal("2 created", rows[1].OutcomeText);
        Assert.Null(rows[0].OutcomeText);
        Assert.Equal("-LAYER  M -> A-WALL -> C -> 7 -> A-WALL", rows[0].Steps[0].Text);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Reports_outside_a_panel_turn_are_ignored() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        IToolActivitySink sink = vm;
        sink.OnToolCallStarting("run_commands", OuterWalls());
        sink.OnToolCallFinished("run_commands", OuterWalls(), new ToolResult { Success = true, CreatedIds = [1] });
        await Sta.Settle();

        backend.Script.Add(new ToolCallStarted("c1", "run_commands", "title: Outer walls, steps: 2 items", "acad"));
        backend.Script.Add(new ToolCallFinished("c1", true, "2 commands finished."));
        vm.ComposerText = "Draw";
        await vm.SendAsync();
        var row = vm.Items.OfType<ActivityGroupItem>().Single().Rows.Single();
        Assert.False(row.HasCallDetails);
        Assert.True(row.ShowResultLine);
        Assert.Equal("2 commands finished.", row.ResultSummary);
        vm.Dispose();
    });

    [Fact]
    public void Capture_view_row_says_looked_at_the_drawing_and_shows_the_picture() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        backend.Script.Add(new ToolCallStarted("v1", "capture_view", string.Empty, "acad"));
        vm.ComposerText = "What is in this drawing?";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(), "row");
        var row = vm.Items.OfType<ActivityGroupItem>().Single().Rows.Single();
        Assert.Equal("Looking at the drawing", row.Title);

        IToolActivitySink sink = vm;
        sink.OnToolCallFinished("capture_view", null, new ToolResult
        {
            Success = true,
            Message = "View 46' x 32'",
            Images = [ToolImage.FromPng(TestImages.DrawingPng(320, 200))],
        });
        vm.Apply(new ToolCallFinished("v1", true, "View 46' x 32'"), CurrentTurn(vm));
        await Sta.Settle();
        Assert.Equal("Looked at the drawing", row.Title);
        Assert.True(row.HasPicture);
        Assert.Equal(320, ((BitmapSource)row.Picture!).PixelWidth);
        Assert.Equal(string.Empty, row.SubtitleText);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Run_commands_report_marks_how_each_command_ended() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        backend.Script.Add(new ToolCallStarted("c1", "run_commands", "title: Outer walls (9\"), steps: 3 items", "acad"));
        vm.ComposerText = "Draw the outer walls";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(), "row");
        var row = vm.Items.OfType<ActivityGroupItem>().Single().Rows.Single();

        var arguments = OuterWalls();
        ((JsonArray)arguments["steps"]!).Add(new JsonObject { ["command"] = "LAYER", ["inputs"] = new JsonArray("S", "A-WALL", "") });
        var report = new RunCommandsReport
        {
            Title = "Outer walls (9\")",
            Outcome = RunOutcome.Completed,
            Pace = "watch",
            StepsTotal = 3,
            StepsRun = 3,
            Steps =
            [
                new StepReport { Step = 1, Command = "PLINE", Status = StepStatus.Finished, CreatedCount = 1 },
                new StepReport
                {
                    Step = 2,
                    Command = "OFFSET",
                    Status = StepStatus.LeftWaiting,
                    WaitingAt = "Select object to offset or [Exit/Undo] <Exit>:",
                    Rejected = [new RejectedInput { Input = 2, Text = "L", Message = "Invalid selection" }],
                },
                new StepReport { Step = 3, Command = "-LAYER", Requested = "LAYER", Status = StepStatus.Finished },
            ],
            CreatedCount = 1,
            ErasedCount = 2,
            Warnings = ["LAYER opens a dialog; typed -LAYER instead."],
        };
        IToolActivitySink sink = vm;
        sink.OnToolCallStarting("run_commands", arguments);
        sink.OnToolCallFinished("run_commands", arguments, new ToolResult { Success = true, Message = report.Summarize(), Data = CopilotJson.ToNode(report) });
        await Sta.Settle();

        Assert.True(row.Steps[0].IsDone);
        Assert.True(row.Steps[1].IsWarning);
        Assert.Contains("Input 2 \"L\" rejected: Invalid selection.", row.Steps[1].Problem, StringComparison.Ordinal);
        Assert.Contains("Still waiting at \"Select object to offset", row.Steps[1].Problem, StringComparison.Ordinal);
        Assert.Equal("-LAYER", row.Steps[2].Command);
        Assert.Equal("-LAYER  S -> A-WALL", row.Steps[2].Text);
        Assert.Contains("Asked for LAYER", row.Steps[2].ToolTip, StringComparison.Ordinal);
        Assert.Equal("1 created · 2 erased", row.OutcomeText);
        Assert.Equal(["LAYER opens a dialog; typed -LAYER instead."], row.Warnings);
        Assert.False(row.ShowResultLine);

        backend.Release();
        await send;
        vm.Dispose();
    });

    [Fact]
    public void Inspect_tools_get_readable_titles()
    {
        Assert.Equal("Listed layers", ToolActivityText.Title("list_layers", ActivityState.Succeeded));
        Assert.Equal("Reading the drawing info", ToolActivityText.Title("get_drawing_info", ActivityState.Running));
        Assert.Equal("Look at the drawing", ToolActivityText.Title("capture_view", ActivityState.Failed));
        Assert.Equal("Some new tool", ToolActivityText.Title("some_new_tool", ActivityState.Succeeded));
        Assert.Null(ToolActivityText.Outcome(new ToolResult { Success = true }));
        Assert.Equal("1 created · 2 changed", ToolActivityText.Outcome(new ToolResult { CreatedIds = [1], ModifiedIds = [2, 3] }));
    }

    internal static JsonObject OuterWalls() => new()
    {
        ["title"] = "Outer walls (9\")",
        ["steps"] = new JsonArray(
            new JsonObject { ["command"] = "PLINE", ["inputs"] = new JsonArray("0,0", "@30'<0", "@40'<90", "@30'<180", "C"), ["note"] = "plot boundary" },
            new JsonObject { ["command"] = "OFFSET", ["inputs"] = new JsonArray("9\"", "L", "15',20'", "") }),
    };

    internal static JsonObject Layers() => new()
    {
        ["title"] = "Layers",
        ["steps"] = new JsonArray(
            new JsonObject { ["command"] = "-LAYER", ["inputs"] = new JsonArray("M", "A-WALL", "C", "7", "A-WALL", "") },
            new JsonObject { ["command"] = "-LAYER", ["inputs"] = new JsonArray("M", "A-DIMS", "C", "2", "A-DIMS", "") }),
    };

    private static TurnInfo CurrentTurn(ChatViewModel vm) => vm.CurrentTurn ?? throw new InvalidOperationException("No turn is running.");

    private ChatViewModel Create(FakeBackend backend) =>
        new(backend, Dispatcher.CurrentDispatcher, null,
            new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json")),
            new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json")));
}
