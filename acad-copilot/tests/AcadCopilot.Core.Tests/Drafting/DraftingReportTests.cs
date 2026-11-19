using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class DraftingReportTests
{
    private static PlannedStep Planned(int number, string command, params string[] inputs) =>
        DraftingPlan.Create(new RunCommandsRequest
        {
            Title = "t",
            Steps = Enumerable.Range(1, number).Select(i => new DraftingStep { Command = i == number ? command : "REGEN", Inputs = i == number ? inputs : [] }).ToList(),
        }).Steps[number - 1];

    [Fact]
    public void Recorder_builds_a_compact_step_report()
    {
        var recorder = new StepRecorder(Planned(1, "LINE", "0,0", "@360,0", "")) { Status = StepStatus.Finished, ElapsedMs = 1200 };
        recorder.CommandTyped("LINE");
        recorder.Typed("Specify first point:", "0,0");
        recorder.Typed("Specify next point or [Undo]:", "@360,0");
        recorder.Typed("Specify next point or [Undo]:", "");
        recorder.FinalPrompt("Command:");
        recorder.Messages(["1 found", "1 found", "  "]);
        var created = new List<JsonObject> { new() { ["handle"] = "2A1", ["type"] = "LINE" } };
        var report = recorder.Build(created, 1);

        Assert.Equal(1, report.Step);
        Assert.Equal("LINE", report.Command);
        Assert.Null(report.Requested);
        Assert.Equal(StepStatus.Finished, report.Status);
        Assert.Equal(
            ["Command: LINE", "Specify first point: 0,0", "Specify next point or [Undo]: @360,0", "Specify next point or [Undo]: <Enter>", "Command:"],
            report.Transcript);
        Assert.Equal(["1 found"], report.Messages);
        Assert.Null(report.Rejected);
        Assert.Null(report.NotTyped);
        Assert.Equal(1, report.CreatedCount);
        Assert.Single(report.Created!);
        Assert.Equal(3, recorder.TypedCount);

        var json = CopilotJson.ToNode(report)!.AsObject();
        Assert.Equal("finished", json["status"]!.GetValue<string>());
        Assert.False(json.ContainsKey("rejected"));
        Assert.False(json.ContainsKey("requested"));
    }

    [Fact]
    public void Requested_is_reported_when_the_command_was_replaced()
    {
        var recorder = new StepRecorder(Planned(1, "LAYER", "M", "A", "")) { Status = StepStatus.Finished };
        var report = recorder.Build();
        Assert.Equal("-LAYER", report.Command);
        Assert.Equal("LAYER", report.Requested);
    }

    [Fact]
    public void Leftover_enters_are_ignored_but_other_leftovers_are_reported()
    {
        var recorder = new StepRecorder(Planned(1, "TEXT", "0,0", "6", "0", "HALL", ""));
        recorder.CommandEndedEarly([""]);
        Assert.Equal(1, recorder.IgnoredEnters);
        Assert.Null(recorder.Build().NotTyped);

        recorder.CommandEndedEarly(["C", ""]);
        var report = recorder.Build();
        Assert.Equal(["C", "<Enter>"], report.NotTyped);
        Assert.Contains("already ended", report.NotTypedReason);
    }

    [Fact]
    public void Transcript_is_capped_in_the_middle()
    {
        var lines = Enumerable.Range(1, 100).Select(i => "line " + i).ToList();
        var capped = StepRecorder.CapTranscript(lines);
        Assert.Equal(StepRecorder.MaxTranscriptLines + 1, capped.Count);
        Assert.Equal("line 1", capped[0]);
        Assert.Equal("... 60 lines omitted ...", capped[20]);
        Assert.Equal("line 100", capped[^1]);
        Assert.Equal(5, StepRecorder.CapTranscript(lines.Take(5).ToList()).Count);
    }

    [Fact]
    public void Messages_are_capped()
    {
        var recorder = new StepRecorder(Planned(1, "LINE"));
        recorder.Messages(Enumerable.Range(1, 20).Select(i => "message " + i));
        var report = recorder.Build();
        Assert.Equal(StepRecorder.MaxMessages + 1, report.Messages!.Count);
        Assert.Equal("... 8 more messages", report.Messages[^1]);
    }

    [Fact]
    public void Entity_lists_are_capped_but_counts_are_exact()
    {
        var recorder = new StepRecorder(Planned(1, "ARRAY"));
        var many = Enumerable.Range(1, 40).Select(i => new JsonObject { ["handle"] = i.ToString("X") }).ToList();
        var report = recorder.Build(many, 40, many, 40, many, 40);
        Assert.Equal(StepRecorder.MaxEntitiesListed, report.Created!.Count);
        Assert.Equal(40, report.CreatedCount);
        Assert.Equal(StepRecorder.MaxEntitiesListed, report.Modified!.Count);
        Assert.Equal(StepRecorder.MaxEntitiesListed, report.Erased!.Count);
    }

    [Fact]
    public void Summary_lists_counts_and_problems_first()
    {
        var waiting = new StepRecorder(Planned(2, "PLINE", "0,0", "@10,0")) { Status = StepStatus.LeftWaiting, WaitingAt = "Specify next point or [Arc/Halfwidth/Length/Undo/Width]:" };
        waiting.Rejected(2, "@10'<0", "Specify next point:", "Invalid point.", null);
        var report = new RunCommandsReport
        {
            Title = "Walls",
            Outcome = RunOutcome.Completed,
            Pace = "watch",
            StepsTotal = 2,
            StepsRun = 2,
            Steps = [new StepRecorder(Planned(1, "LINE")) { Status = StepStatus.Finished }.Build(), waiting.Build()],
            CreatedCount = 5,
            CreatedByType = new Dictionary<string, int> { ["LINE"] = 4, ["LWPOLYLINE"] = 1 },
            ErasedCount = 1,
            OneUndoStep = true,
        };

        var summary = report.Summarize();
        Assert.StartsWith("Typed 2 commands (watch mode). Created 5 objects: LINE 4, LWPOLYLINE 1; erased 1.", summary);
        Assert.Contains("PROBLEMS: step 2 PLINE: input 2 '@10'<0' rejected (Invalid point).", summary);
        Assert.Contains("still waiting at 'Specify next point or [Arc/Halfwidth/Length/Undo/Width]:'", summary);
        Assert.EndsWith("The whole call is one undo step.", summary);
        Assert.Equal(2, report.Problems().Count);
    }

    [Theory]
    [InlineData(RunOutcome.Blocked, "Nothing was typed: a command is not allowed.")]
    [InlineData(RunOutcome.Declined, "Nothing was typed: the user declined.")]
    [InlineData(RunOutcome.Busy, "Nothing was typed: AutoCAD is busy.")]
    public void Summary_for_calls_that_typed_nothing(RunOutcome outcome, string expected)
    {
        var report = new RunCommandsReport { Title = "x", Outcome = outcome, Pace = "fast", StepsTotal = 3 };
        Assert.Equal(expected, report.Summarize());
    }

    [Fact]
    public void Summary_for_stopped_and_cancelled_calls()
    {
        var stopped = new RunCommandsReport
        {
            Title = "x",
            Outcome = RunOutcome.Stopped,
            Pace = "fast",
            StepsTotal = 4,
            StepsRun = 2,
            StoppedReason = "AutoCAD opened a dialog box (Text Style).",
        };
        Assert.Equal("Stopped after 2 of 4 commands. AutoCAD opened a dialog box (Text Style). No objects were created or changed.", stopped.Summarize());

        var cancelled = stopped with { Outcome = RunOutcome.Cancelled, StoppedReason = "Stopped by the user" };
        Assert.StartsWith("Cancelled after 2 of 4 commands. Stopped by the user.", cancelled.Summarize());
    }

    [Fact]
    public void Report_serializes_with_snake_case_enums()
    {
        var report = new RunCommandsReport { Title = "x", Outcome = RunOutcome.Completed, Pace = "watch", StepsTotal = 1, StepsRun = 1, Steps = [new StepRecorder(Planned(1, "LINE")) { Status = StepStatus.LeftWaiting }.Build()] };
        var json = CopilotJson.ToNode(report)!.AsObject();
        Assert.Equal("completed", json["outcome"]!.GetValue<string>());
        Assert.Equal("left_waiting", json["steps"]![0]!["status"]!.GetValue<string>());
        Assert.False(json.ContainsKey("warnings"));
    }
}
