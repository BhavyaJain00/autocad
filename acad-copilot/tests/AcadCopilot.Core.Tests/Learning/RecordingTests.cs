using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Learning;

/// <summary>A clock the tests move by hand.</summary>
internal sealed class TestClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 14, 12, 0, TimeSpan.FromHours(5.5));

    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

    public DateTimeOffset Read() => Now;
}

public sealed class CopilotActivityTests
{
    [Fact]
    public void Active_while_a_scope_is_open_and_for_a_short_tail()
    {
        var clock = new TestClock();
        var activity = new CopilotActivity(clock.Read, TimeSpan.FromSeconds(1));
        Assert.False(activity.IsActive);

        var outer = activity.Begin();
        var inner = activity.Begin();
        Assert.True(activity.IsBusy);
        inner.Dispose();
        inner.Dispose(); // twice is harmless
        Assert.True(activity.IsBusy);
        outer.Dispose();
        Assert.False(activity.IsBusy);
        Assert.True(activity.IsActive); // late events of the copilot's last command

        clock.Advance(0.5);
        Assert.True(activity.IsActive);
        clock.Advance(0.6);
        Assert.False(activity.IsActive);
    }
}

public sealed class UserRecordingTests
{
    private const string Dwg = "House-GF.dwg";

    private static UserRecording NewRecording(TestClock clock, int maxActions = UserRecording.DefaultMaxActions)
    {
        var recording = new UserRecording(clock.Read, maxActions);
        recording.SetUnits(Dwg, new DrawingUnits { LUnits = 4, InsUnits = 1 });
        recording.SetCurrentLayer("0");
        return recording;
    }

    [Fact]
    public void Records_the_users_commands_with_their_answers_and_objects()
    {
        var clock = new TestClock();
        var recording = NewRecording(clock);
        var offset = recording.CommandStarted(Dwg, "OFFSET", "A-WALL", byCopilot: false);
        Assert.NotNull(offset);
        clock.Advance(4);
        recording.CommandFinished(
            offset,
            CommandOutcome.Ended,
            ["Command: OFFSET", "Current settings: Erase source=No Layer=Source OFFSETGAPTYPE=0", "Specify offset distance or [Through/Erase/Layer] <Through>: 9", "Select object to offset or [Exit/Undo] <Exit>:", "Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:", "Select object to offset or [Exit/Undo] <Exit>:", "", "Command:"],
            [new RecordedObject(ObjectChange.Created, "LINE", "A-WALL", "from (9,9) to (351,9), length 342 (28'-6\")")]);

        var action = Assert.Single(recording.Actions);
        Assert.Equal("OFFSET", action.Name);
        Assert.Equal("A-WALL", action.Layer);
        Assert.Equal(CommandOutcome.Ended, action.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(4), action.EndedAt - action.At);
        Assert.Equal(1, action.Created);
        Assert.Equal(1, recording.TotalCreated);
        Assert.DoesNotContain(action.Lines, l => l.StartsWith("Command:", StringComparison.Ordinal) || l.Length == 0);
        Assert.Equal("Specify offset distance or [Through/Erase/Layer] <Through>: 9", action.Lines[1]);
        Assert.Equal(1, recording.ActionCount);
        Assert.Equal([Dwg], recording.Drawings);
    }

    [Fact]
    public void Ignores_everything_the_copilot_does()
    {
        var clock = new TestClock();
        var recording = NewRecording(clock);
        Assert.Null(recording.CommandStarted(Dwg, "PLINE", "A-WALL", byCopilot: true));
        recording.LayerChanged(Dwg, "A-DOOR", byCopilot: true);
        recording.DrawingSwitched("Other.dwg", byCopilot: true);
        Assert.Null(recording.CommandStarted(Dwg, "COPILOT", "A-DOOR", byCopilot: false));
        recording.CommandFinished(null, CommandOutcome.Ended, ["x"], [new RecordedObject(ObjectChange.Created, "LINE", "0", "")]);

        Assert.Empty(recording.Actions);
        Assert.Equal(0, recording.ActionCount);
        Assert.Equal(0, recording.TotalCreated);

        // The copilot's layer change still moved the current layer: the user's next command is on A-DOOR.
        var line = recording.CommandStarted(Dwg, "LINE", null, byCopilot: false);
        Assert.Equal("A-DOOR", line!.Layer);
    }

    [Fact]
    public void View_commands_are_only_counted()
    {
        var recording = NewRecording(new TestClock());
        Assert.Null(recording.CommandStarted(Dwg, "ZOOM", "0", false));
        Assert.Null(recording.CommandStarted(Dwg, "'_PAN", "0", false));
        Assert.Null(recording.CommandStarted(Dwg, "ZOOM", "0", false));
        Assert.Empty(recording.Actions);
        Assert.Equal(0, recording.ActionCount);
        Assert.Equal(2, recording.ViewCommands["ZOOM"]);
        Assert.Equal(1, recording.ViewCommands["PAN"]);
    }

    [Fact]
    public void Pause_stops_capturing_without_losing_anything()
    {
        var clock = new TestClock();
        var recording = NewRecording(clock);
        var line = recording.CommandStarted(Dwg, "LINE", "A-WALL", false);
        clock.Advance(10);
        recording.Pause();
        Assert.Equal(RecorderState.Paused, recording.State);

        // A command that started before the pause still completes; new ones are not captured.
        recording.CommandFinished(line, CommandOutcome.Ended, ["Specify first point: 0,0"], []);
        Assert.Null(recording.CommandStarted(Dwg, "CIRCLE", "A-WALL", false));
        recording.LayerChanged(Dwg, "A-DOOR", false);
        clock.Advance(60);
        Assert.Equal(TimeSpan.FromSeconds(10), recording.Elapsed);

        recording.Resume();
        clock.Advance(5);
        Assert.NotNull(recording.CommandStarted(Dwg, "ARC", null, false));
        Assert.Equal(TimeSpan.FromSeconds(15), recording.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(60), recording.PausedTime);
        Assert.Equal(2, recording.ActionCount);
        Assert.Equal(["LINE", "Paused", "Resumed", "ARC"], recording.Actions.Select(a => a.Name));
        Assert.Equal("A-DOOR", recording.Actions[^1].Layer); // the layer change while paused is known, not recorded

        recording.Stop();
        clock.Advance(100);
        Assert.Equal(RecorderState.Off, recording.State);
        Assert.Equal(TimeSpan.FromSeconds(15), recording.Elapsed);
        Assert.Null(recording.CommandStarted(Dwg, "LINE", null, false));
    }

    [Fact]
    public void Memory_is_capped_and_the_rest_is_summarised()
    {
        var recording = NewRecording(new TestClock(), maxActions: 3);
        for (var i = 0; i < 5; i++)
        {
            var action = recording.CommandStarted(Dwg, i % 2 == 0 ? "LINE" : "TRIM", "A-WALL", false);
            recording.CommandFinished(action, CommandOutcome.Ended, ["Specify first point: 0,0"], [new RecordedObject(ObjectChange.Created, "LINE", "A-WALL", "x")]);
        }

        recording.LayerChanged(Dwg, "A-DOOR", false);
        Assert.Equal(3, recording.Actions.Count);
        Assert.Equal(6, recording.ActionCount);
        Assert.Equal(5, recording.TotalCreated);
        Assert.Equal(1, recording.Overflow["TRIM"]);
        Assert.Equal(1, recording.Overflow["LINE"]);
        Assert.Equal(1, recording.Overflow["layer change"]);

        var many = recording.Actions[0];
        Assert.True(many.Objects.Count <= UserRecording.MaxObjectsPerCommand);

        var big = new UserRecording();
        var array = big.CommandStarted(Dwg, "-ARRAY", "A-STRS", false);
        big.CommandFinished(array, CommandOutcome.Ended, Enumerable.Range(0, 100).Select(i => $"line {i}"), Enumerable.Range(0, 80).Select(_ => new RecordedObject(ObjectChange.Created, "LINE", "A-STRS", "tread")));
        Assert.Equal(UserRecording.MaxObjectsPerCommand, array!.Objects.Count);
        Assert.Equal(80, array.Created);
        Assert.Equal(UserRecording.MaxLinesPerCommand, array.Lines.Count);
        Assert.Equal(60, array.LinesOmitted);
    }

    [Fact]
    public void Layer_changes_and_drawing_switches_are_recorded_once()
    {
        var recording = NewRecording(new TestClock());
        recording.CommandStarted(Dwg, "LINE", null, false);
        recording.LayerChanged(Dwg, "A-DOOR", false);
        recording.LayerChanged(Dwg, "A-DOOR", false);
        recording.DrawingSwitched(Dwg, false);
        recording.DrawingSwitched("FF.dwg", false);
        recording.DrawingSwitched("FF.dwg", false);
        Assert.Equal([RecordedActionKind.Command, RecordedActionKind.LayerChanged, RecordedActionKind.DrawingSwitched], recording.Actions.Select(a => a.Kind));
        Assert.Equal(2, recording.ActionCount);
        Assert.Equal([Dwg, "FF.dwg"], recording.Drawings);
    }
}

public sealed class RecordingStepsTests
{
    private const string Dwg = "House.dwg";

    [Fact]
    public void Steps_follow_layers_breaks_and_size()
    {
        var clock = new TestClock();
        var recording = new UserRecording(clock.Read);
        recording.SetCurrentLayer("A-WALL");
        void Run(string command, double seconds = 3)
        {
            var action = recording.CommandStarted(Dwg, command, null, false);
            clock.Advance(seconds);
            recording.CommandFinished(action, CommandOutcome.Ended, [], []);
            clock.Advance(1);
        }

        Run("PLINE");
        Run("OFFSET");
        Run("OFFSET");

        // -LAYER makes A-DOOR current while it runs: the command opens the new step.
        var layer = recording.CommandStarted(Dwg, "-LAYER", "A-WALL", false);
        clock.Advance(1);
        recording.LayerChanged(Dwg, "A-DOOR", false);
        clock.Advance(1);
        recording.CommandFinished(layer, CommandOutcome.Ended, [], []);
        Run("LINE");
        Run("ARC");

        clock.Advance(300); // a long break
        Run("TRIM");
        for (var i = 0; i < RecordingSteps.MaxCommandsPerStep + 1; i++)
        {
            Run("LINE", 1);
        }

        var steps = RecordingSteps.Group(recording.Actions);
        Assert.Equal("On A-WALL: PLINE, OFFSET x2", steps[0].Title);
        Assert.Equal(["-LAYER", "A-DOOR", "LINE", "ARC"], steps[1].Actions.Select(a => a.Name));
        Assert.Equal("On A-DOOR: -LAYER, LINE, ARC", steps[1].Title);
        Assert.Equal("A-DOOR", steps[1].Layer);
        Assert.Equal("TRIM", steps[2].Actions[0].Name);
        Assert.Equal(RecordingSteps.MaxCommandsPerStep, steps[2].Actions.Count);
        Assert.Equal(4, steps.Count);
        Assert.Equal([1, 2, 3, 4], steps.Select(s => s.Number));
        Assert.All(steps, s => Assert.True(s.End >= s.Start));
    }

    [Fact]
    public void Pauses_and_drawing_switches_close_a_step()
    {
        var clock = new TestClock();
        var recording = new UserRecording(clock.Read);
        recording.CommandStarted(Dwg, "LINE", "0", false);
        recording.Pause();
        recording.Resume();
        recording.CommandStarted(Dwg, "CIRCLE", "0", false);
        recording.DrawingSwitched("FF.dwg", false);
        recording.CommandStarted("FF.dwg", "TEXT", "0", false);

        var steps = RecordingSteps.Group(recording.Actions);
        Assert.Equal(3, steps.Count);
        Assert.Equal(["LINE", "Paused"], steps[0].Actions.Select(a => a.Name));
        Assert.Equal(["Resumed", "CIRCLE"], steps[1].Actions.Select(a => a.Name));
        Assert.Equal("FF.dwg", steps[2].Drawing);
        Assert.Equal("On 0: TEXT", steps[2].Title);
        Assert.Empty(RecordingSteps.Group([]));
    }
}

public sealed class RecordingTextTests
{
    private static readonly DrawingUnits Architectural = new() { LUnits = 4, InsUnits = 1 };

    [Fact]
    public void Keeps_prompts_and_answers_without_echoes_notices_and_repeats()
    {
        var lines = RecordingText.CommandLines(
            ["Command: _line", "Specify first point: 0,0", "Specify next point or [Undo]: @10',0", "Specify next point or [Undo]: @10',0", "**** System Variable Changed ****", "1 of the monitored system variables has changed from the preferred value. Use SYSVARMONITOR command to view changes.", "   ", "Command:"],
            "LINE");
        Assert.Equal(["Specify first point: 0,0", "Specify next point or [Undo]: @10',0"], lines);
    }

    [Theory]
    [InlineData("Specify offset distance or [Through/Erase/Layer] <Through>: 9", "Specify offset distance or [Through/Erase/Layer] <Through>:", "9", null)]
    [InlineData("Specify next point or [Undo]:", "Specify next point or [Undo]:", null, null)]
    [InlineData("Enter text: BED ROOM", "Enter text:", "BED ROOM", null)]
    [InlineData("1 found", null, null, "1 found")]
    [InlineData("Current settings: Offset distance = 9\"", null, null, "Current settings: Offset distance = 9\"")]
    public void Splits_prompt_and_answer(string line, string? prompt, string? answer, string? message)
    {
        Assert.Equal((prompt, answer, message), RecordingText.SplitPromptLine(line));
    }

    [Fact]
    public void Typed_distances_get_feet_inches()
    {
        Assert.Equal("= 0'-9\"", RecordingText.LengthNote("Specify offset distance or [Through]:", "9", Architectural));
        Assert.Equal("= 42 drawing units", RecordingText.LengthNote("Specify height <0'-6\">:", "3'6", Architectural));
        Assert.Null(RecordingText.LengthNote("Specify next point or [Undo]:", "@10',0", Architectural));
        Assert.Null(RecordingText.LengthNote("Specify offset distance:", "T", Architectural));
        Assert.Null(RecordingText.LengthNote("Specify offset distance:", "9", new DrawingUnits { LUnits = 2, InsUnits = 0 }));
    }

    [Fact]
    public void Describes_objects_from_entity_summaries()
    {
        static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
        Assert.Equal(
            "from (0,0) to (120,0), length 120 (10'-0\"), angle 0°",
            RecordingText.DescribeObject(Parse("""{"handle":"2A","type":"LINE","layer":"A-WALL","start":{"x":0,"y":0},"end":{"x":120,"y":0},"length":{"du":120,"ftIn":"10'-0\""},"angle":0}""")));
        Assert.Equal(
            "closed, 4 vertices, (0,0) (360,0) (360,480) (0,480), length 1680 (140'-0\")",
            RecordingText.DescribeObject(Parse("""{"type":"LWPOLYLINE","layer":"A-WALL","closed":true,"vertexCount":4,"points":[[0,0],[360,0],[360,480],[0,480]],"length":{"du":1680,"ftIn":"140'-0\""}}""")));
        Assert.Equal(
            "\"BED ROOM\", at (120,96), height 9 (0'-9\"), style ARCH",
            RecordingText.DescribeObject(Parse("""{"type":"TEXT","layer":"A-ANNO-TEXT","text":"BED ROOM","position":{"x":120,"y":96},"height":{"du":9,"ftIn":"0'-9\""},"rotation":0,"style":"ARCH"}""")));
        Assert.Equal(
            "block DOOR-36, at (36,0), rotated 90°",
            RecordingText.DescribeObject(Parse("""{"type":"INSERT","layer":"A-DOOR","block":"DOOR-36","position":{"x":36,"y":0},"rotation":90,"scale":[1,1,1]}""")));
        Assert.Equal(
            "rotated, measures 144 (12'-0\"), style ARCH-48, from (0,0) to (144,0)",
            RecordingText.DescribeObject(Parse("""{"type":"DIMENSION","layer":"A-ANNO-DIMS","kind":"rotated","measurement":{"du":144,"ftIn":"12'-0\""},"style":"ARCH-48","point1":{"x":0,"y":0},"point2":{"x":144,"y":0}}""")));
        Assert.Equal(
            "pattern ANSI31, scale 24, area 1440 (10 sq ft), colour 8",
            RecordingText.DescribeObject(Parse("""{"type":"HATCH","layer":"A-HATCH","pattern":"ANSI31","patternScale":24,"area":{"sqDu":1440,"sqFt":10},"color":"8"}""")));
        Assert.Equal(
            "box (0,0) to (10,10)",
            RecordingText.DescribeObject(Parse("""{"type":"AEC_THING","layer":"0","box":{"min":{"x":0,"y":0},"max":{"x":10,"y":10}}}""")));
    }
}

public sealed class RecordingMarkdownTests
{
    [Fact]
    public void Writes_a_readable_recording_grouped_into_steps()
    {
        var clock = new TestClock();
        var recording = new UserRecording(clock.Read);
        recording.SetUnits("House-GF.dwg", new DrawingUnits { LUnits = 4, InsUnits = 1 });
        recording.SetCurrentLayer("A-WALL");
        var pline = recording.CommandStarted("House-GF.dwg", "PLINE", "A-WALL", false);
        clock.Advance(20);
        recording.CommandFinished(pline, CommandOutcome.Ended, ["Specify start point: 0,0", "Specify next point or [Arc/Halfwidth/Length/Undo/Width]: @30',0", "Specify next point or [Arc/Close/Halfwidth/Length/Undo/Width]: C"],
            [new RecordedObject(ObjectChange.Created, "LWPOLYLINE", "A-WALL", "closed, 4 vertices, length 1680 (140'-0\")")]);
        var offset = recording.CommandStarted("House-GF.dwg", "OFFSET", "A-WALL", false);
        recording.CommandFinished(offset, CommandOutcome.Cancelled, ["Specify offset distance or [Through/Erase/Layer] <Through>: 9", "Select object to offset or [Exit/Undo] <Exit>:"], []);
        recording.LayerChanged("House-GF.dwg", "A-DOOR", false);
        recording.CommandStarted("House-GF.dwg", "ZOOM", "A-DOOR", false);
        var arc = recording.CommandStarted("House-GF.dwg", "ARC", "A-DOOR", false);
        recording.Stop();

        var markdown = RecordingMarkdown.Write(recording);
        Assert.StartsWith("# Recording 2026-11-19 14:12 - House-GF.dwg", markdown, StringComparison.Ordinal);
        Assert.Contains("The copilot's own commands are not", markdown, StringComparison.Ordinal);
        Assert.Contains("- Drawing House-GF.dwg: architectural (LUNITS 4", markdown, StringComparison.Ordinal);
        Assert.Contains("- 4 actions (3 commands, 1 layer changes); objects: 1 created, 0 modified, 0 erased", markdown, StringComparison.Ordinal);
        Assert.Contains("- Commands used: ARC, OFFSET, PLINE", markdown, StringComparison.Ordinal);
        Assert.Contains("- View changes (not listed): ZOOM x1", markdown, StringComparison.Ordinal);
        Assert.Contains("## Step 1 - On A-WALL: PLINE, OFFSET (14:12)", markdown, StringComparison.Ordinal);
        Assert.Contains("1. **PLINE** on layer A-WALL (14:12:00)", markdown, StringComparison.Ordinal);
        Assert.Contains("   - `Specify start point:` `0,0`", markdown, StringComparison.Ordinal);
        Assert.Contains("   - Created LWPOLYLINE on A-WALL: closed, 4 vertices, length 1680 (140'-0\")", markdown, StringComparison.Ordinal);
        Assert.Contains("2. **OFFSET** on layer A-WALL (14:12:20, cancelled)", markdown, StringComparison.Ordinal);
        Assert.Contains("`Specify offset distance or [Through/Erase/Layer] <Through>:` `9` = 0'-9\"", markdown, StringComparison.Ordinal);
        Assert.Contains("`Select object to offset or [Exit/Undo] <Exit>:` (picked on screen or Enter)", markdown, StringComparison.Ordinal);
        Assert.Contains("## Step 2 - On A-DOOR: ARC", markdown, StringComparison.Ordinal);
        Assert.Contains("- Current layer set to **A-DOOR**", markdown, StringComparison.Ordinal);
        Assert.Contains("**ARC** on layer A-DOOR (14:12:20, still running when the recording stopped)", markdown, StringComparison.Ordinal);
        Assert.Equal(CommandOutcome.Running, arc!.Outcome);
    }

    [Fact]
    public void Says_what_was_left_out_beyond_the_limit()
    {
        var recording = new UserRecording(maxActions: 1);
        recording.CommandStarted("A.dwg", "LINE", "0", false);
        recording.CommandStarted("A.dwg", "LINE", "0", false);
        var markdown = RecordingMarkdown.Write(recording);
        Assert.Contains("- Not detailed: 1 more actions beyond the limit of 1 (LINE x1)", markdown, StringComparison.Ordinal);
        Assert.Equal("01:05", RecordingMarkdown.Duration(TimeSpan.FromSeconds(65)));
        Assert.Equal("1:00:05", RecordingMarkdown.Duration(TimeSpan.FromSeconds(3605)));
    }
}

public sealed class RecordingLibraryTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Names_files_by_date_and_drawing()
    {
        var at = new DateTimeOffset(2026, 11, 19, 14, 7, 0, TimeSpan.Zero);
        Assert.Equal("2026-11-19_1407_House-GF-30x40.md", RecordingLibrary.FileNameFor(at, @"C:\Plans\House GF (30x40).dwg"));
        Assert.Equal("2026-11-19_1407_drawing.md", RecordingLibrary.FileNameFor(at, null));
        Assert.Equal("2026-11-19_1407_Drawing1.md", RecordingLibrary.FileNameFor(at, "Drawing1.dwg"));
    }

    [Fact]
    public void Saves_lists_reads_in_parts_and_deletes_only_its_own_files()
    {
        var library = new RecordingLibrary(_dir.Path);
        Assert.Empty(library.List());
        Assert.Null(library.Resolve("latest"));

        var first = library.Save(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), "A.dwg", "# Recording 2026-11-18 09:00 - A.dwg\n\n## Summary\n\n- 2 actions (2 commands, 0 layer changes); objects: 2 created\n");
        var body = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"{i}. **LINE** on layer A-WALL"));
        var second = library.Save(new DateTimeOffset(2026, 11, 19, 14, 12, 0, TimeSpan.Zero), "B.dwg", "# Recording 2026-11-19 14:12 - B.dwg\n" + body);
        var third = library.Save(new DateTimeOffset(2026, 11, 19, 14, 12, 0, TimeSpan.Zero), "B.dwg", "# again");
        Assert.Equal("2026-11-19_1412_B_2.md", Path.GetFileName(third));
        Assert.Equal(Path.Combine(_dir.Path, "recordings"), Path.GetDirectoryName(first));

        var list = library.List();
        Assert.Equal(["2026-11-19_1412_B_2.md", "2026-11-19_1412_B.md", "2026-11-18_0900_A.md"], list.Select(r => r.Name));
        Assert.Equal("Recording 2026-11-18 09:00 - A.dwg", list[2].Title);
        Assert.Equal("2 actions (2 commands, 0 layer changes); objects: 2 created", list[2].Summary);

        Assert.Equal(third, library.Resolve(null));
        Assert.Equal(first, library.Resolve("2026-11-18_0900_A"));
        Assert.Equal(first, library.Resolve(@"..\..\somewhere\2026-11-18_0900_A.md"));
        Assert.Equal(first, library.Resolve("_A"));
        Assert.Null(library.Resolve("_B")); // two match
        Assert.Null(library.Resolve(@"..\rules.md"));

        var part = library.Read("2026-11-19_1412_B.md", 0, 5_000);
        Assert.True(part.HasMore);
        Assert.True(part.Text.Length <= 5_000);
        Assert.EndsWith("\n", part.Text, StringComparison.Ordinal);
        var rest = library.Read("2026-11-19_1412_B.md", part.Offset + part.Text.Length, RecordingLibrary.MaxReadCharacters);
        Assert.False(rest.HasMore);
        Assert.Equal(File.ReadAllText(second), part.Text + rest.Text);
        Assert.Throws<FileNotFoundException>(() => library.Read("nothing-like-this"));

        var outside = Path.Combine(_dir.Path, "rules.md");
        File.WriteAllText(outside, "- keep me");
        Assert.False(library.Delete(outside));
        Assert.True(File.Exists(outside));
        Assert.True(library.Delete(third));
        Assert.False(File.Exists(third));
    }

    [Fact]
    public void Bridge_tools_list_and_read_recordings()
    {
        var store = new KnowledgeStore(Path.Combine(_dir.Path, "knowledge"));
        var empty = KnowledgeTools.Execute(store, KnowledgeTools.ListRecordings, null);
        Assert.True(empty.Success);
        Assert.Contains("No recordings yet", empty.Message, StringComparison.Ordinal);
        Assert.False(KnowledgeTools.Execute(store, KnowledgeTools.ReadRecording, null).Success);

        var clock = new TestClock();
        var recording = new UserRecording(clock.Read);
        recording.SetUnits("House.dwg", new DrawingUnits { LUnits = 4, InsUnits = 1 });
        var line = recording.CommandStarted("House.dwg", "LINE", "A-DOOR", false);
        recording.CommandFinished(line, CommandOutcome.Ended, ["Specify first point: 3'1.5,10'4.5", "Specify next point or [Undo]: @0,3'"], []);
        recording.Stop();
        var path = new RecordingLibrary(store.Directory).Save(recording.Started, "House.dwg", RecordingMarkdown.Write(recording));

        var listed = KnowledgeTools.Execute(store, KnowledgeTools.ListRecordings, new JsonObject { ["limit"] = 5 });
        Assert.True(listed.Success, listed.Message);
        var item = listed.Data!["recordings"]![0]!;
        Assert.Equal(Path.GetFileName(path), item["name"]!.GetValue<string>());
        Assert.Contains("1 actions", item["summary"]!.GetValue<string>(), StringComparison.Ordinal);

        var read = KnowledgeTools.Execute(store, KnowledgeTools.ReadRecording, new JsonObject { ["name"] = Path.GetFileName(path) });
        Assert.True(read.Success, read.Message);
        Assert.Contains("`Specify first point:` `3'1.5,10'4.5`", read.Data!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(read.Data!["more"]!.GetValue<bool>());

        var missing = KnowledgeTools.Execute(store, KnowledgeTools.ReadRecording, new JsonObject { ["name"] = "nope" });
        Assert.False(missing.Success);
        Assert.Contains(Path.GetFileName(path), missing.Message, StringComparison.Ordinal);
        Assert.False(KnowledgeTools.Execute(store, KnowledgeTools.ReadRecording, new JsonObject { ["maxCharacters"] = 5 }).Success);

        Assert.Equal(ToolRisk.Read, KnowledgeTools.ReadRecordingDefinition().Risk);
        Assert.Equal(ToolRisk.Read, KnowledgeTools.ListRecordingsDefinition().Risk);
        Assert.Contains(KnowledgeTools.ReadRecording, BridgeTools.ReservedNames);
        Assert.Contains(KnowledgeTools.ListRecordings, BridgeTools.ReservedNames);
    }

    [Fact]
    public void Instructions_explain_learning_from_recordings()
    {
        Assert.Contains("read_recording", AcadCopilot.Core.Agents.CopilotInstructions.Default, StringComparison.Ordinal);
        Assert.Contains("Learn from my recording", AcadCopilot.Core.Agents.CopilotInstructions.Default, StringComparison.Ordinal);
    }
}
