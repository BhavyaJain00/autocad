using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class InputRepairTests
{
    private static readonly DrawingUnits DecimalInches = new() { LUnits = 2, InsUnits = 1 };
    private static readonly DrawingUnits DecimalMillimetres = new() { LUnits = 2, InsUnits = 4 };
    private static readonly DrawingUnits DecimalUnitless = new() { LUnits = 2, InsUnits = 0 };
    private static readonly DrawingUnits Architectural = new() { LUnits = 4, InsUnits = 1 };

    // ----- Before typing: pure syntax -----

    [Theory]
    [InlineData("10, 20", "Specify first point:", "10,20")]
    [InlineData("@ 360 , 0", "Specify next point or [Undo]:", "@360,0")]
    [InlineData("@10 < 90", "Specify next point or [Undo]:", "@10<90")]
    [InlineData("10 20", "Specify first point:", "10,20")]
    [InlineData("10 20 5", "Specify insertion point:", "10,20,5")]
    [InlineData("-5.5 .25", "Specify other corner point or [Area/Dimensions/Rotation]:", "-5.5,.25")]
    [InlineData("−90", "Specify included angle:", "-90")]
    [InlineData("@12′6″,0", "Specify next point or [Undo]:", "@12'6\",0")]
    [InlineData("12'-6 1/2\",0", "Specify first point:", "12'6-1/2\",0")]
    public void Fixes_coordinate_and_number_syntax_before_typing(string input, string prompt, string expected)
    {
        var fix = InputRepair.BeforeTyping(input, prompt);

        Assert.NotNull(fix);
        Assert.Equal(expected, fix!.Replacement);
        Assert.Equal(input, fix.Original);
        Assert.False(string.IsNullOrWhiteSpace(fix.Reason));
    }

    [Theory]
    [InlineData("0,0", "Specify first point:")]
    [InlineData("", "Specify next point or [Undo]:")]
    [InlineData("BED ROOM", "Enter text:")]
    [InlineData("10, 20", "Enter text:")]
    [InlineData("−90", "Enter text:")]
    [InlineData("10 20", "Specify radius of circle or [Diameter]:")]
    [InlineData("M", "Enter an option [?/Make/Set/New]:")]
    [InlineData("A WALL", "Enter name for new layer(s):")]
    [InlineData("12'6\"", "Specify offset distance or [Through/Erase/Layer] <Through>:")]
    public void Leaves_good_inputs_names_and_text_alone(string input, string prompt)
    {
        Assert.Null(InputRepair.BeforeTyping(input, prompt));
    }

    // ----- After rejection: units -----

    [Theory]
    [InlineData("30'", "360")]
    [InlineData("@30',40'", "@360,480")]
    [InlineData("@12'6\"<90", "@150<90")]
    [InlineData("4-1/2\"", "4.5")]
    [InlineData("0,20'", "0,240")]
    [InlineData("@10',5", "@120,5")]
    [InlineData("9in", "9")]
    public void Feet_inches_become_drawing_units_in_a_decimal_inch_drawing(string input, string expected)
    {
        var fix = InputRepair.AfterRejection(input, "Specify next point or [Undo]:", "Invalid point.", DecimalInches, 0);

        Assert.NotNull(fix);
        Assert.Equal(InputFixKind.UnitConversion, fix!.Kind);
        Assert.Equal(expected, fix.Replacement);
        Assert.Contains("LUNITS 2", fix.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Feet_inches_and_metres_become_millimetres_in_a_metric_drawing()
    {
        Assert.Equal("9144", InputRepair.AfterRejection("30'", "Specify length:", "Requires numeric distance.", DecimalMillimetres, 0)!.Replacement);
        Assert.Equal("@3000,0", InputRepair.AfterRejection("@3m,0", "Specify next point:", "Invalid point.", DecimalMillimetres, 0)!.Replacement);
        Assert.Equal("@300,0", InputRepair.AfterRejection("@30cm,0", "Specify next point:", "Invalid point.", DecimalMillimetres, 0)!.Replacement);
    }

    [Theory]
    [InlineData("@30',5")] // 5 what? inches or millimetres: ambiguous, never guessed.
    [InlineData("@30',ABC")]
    public void Ambiguous_or_odd_coordinates_are_not_converted(string input)
    {
        Assert.Null(InputRepair.AfterRejection(input, "Specify next point:", "Invalid point.", DecimalMillimetres, 0));
    }

    [Fact]
    public void Nothing_is_converted_without_a_known_scale_or_when_the_drawing_takes_feet_inches()
    {
        Assert.Null(InputRepair.AfterRejection("30'", "Specify next point:", "Invalid point.", DecimalUnitless, 0));
        Assert.Null(InputRepair.AfterRejection("30'", "Specify next point:", "Invalid point.", Architectural, 0));
        Assert.Equal("11.811024", InputRepair.AfterRejection("300mm", "Specify offset distance:", "Requires numeric distance.", Architectural, 0)!.Replacement);
    }

    // ----- After rejection: angles -----

    [Theory]
    [InlineData("90°", "90")]
    [InlineData("-90deg", "-90")]
    [InlineData("45 degrees", "45")]
    public void Degree_signs_are_removed_from_angles_in_decimal_degrees(string input, string expected)
    {
        var fix = InputRepair.AfterRejection(input, "Specify included angle:", "Requires valid numeric angle or second point.", DecimalInches, 0);

        Assert.Equal(InputFixKind.AngleSyntax, fix!.Kind);
        Assert.Equal(expected, fix.Replacement);
    }

    [Fact]
    public void Polar_input_gets_both_angle_and_length_fixes_and_other_angle_units_are_left_alone()
    {
        Assert.Equal("@120<90", InputRepair.AfterRejection("@10'<90°", "Specify next point:", "Invalid point.", DecimalInches, 0)!.Replacement);
        Assert.Null(InputRepair.AfterRejection("90°", "Specify included angle:", "Invalid.", DecimalInches, 2));
    }

    // ----- After rejection: option keywords -----

    [Theory]
    [InlineData("Clsoe", "Specify next point or [Close/Undo]:", "C")]
    [InlineData("[C]", "Specify next point or [Close/Undo]:", "C")]
    [InlineData("\"U\"", "Specify next point or [Close/Undo]:", "U")]
    [InlineData("e", "Specify second point or [Array/eXit/Undo]:", "X")]
    [InlineData("Lenght", "Specify next point or [Arc/Halfwidth/Length/Undo/Width]:", "L")]
    [InlineData("Ltpye", "Enter an option [?/Make/Set/New/Rename/ON/OFF/Color/Ltype/LWeight]:", "L")]
    [InlineData("_Clsoe", "Specify next point or [Close/Undo]:", "_C")]
    [InlineData("Length", "Specify end point of arc or [Angle/chord Length]:", "L")]
    public void Option_keyword_typos_case_and_brackets_become_the_capital_letters(string input, string prompt, string expected)
    {
        var fix = InputRepair.AfterRejection(input, prompt, "Invalid option keyword.", DecimalInches, 0);

        Assert.NotNull(fix);
        Assert.Equal(InputFixKind.OptionKeyword, fix!.Kind);
        Assert.Equal(expected, fix.Replacement);
    }

    [Theory]
    [InlineData("C", "Specify next point or [Close/Undo]:")]
    [InlineData("Xyz", "Specify next point or [Close/Undo]:")]
    [InlineData("Co", "Specify next point or [Close/Copy]:")]
    [InlineData("WALLS", "Enter name of layer(s) to turn on:")]
    [InlineData("S", "Specify start point of arc or [Center]:")]
    [InlineData("e", "Select object to offset or [Exit/Undo] <Exit>:")]
    [InlineData("BED ROOM", "Enter text:")]
    public void Unclear_keywords_are_not_guessed(string input, string prompt)
    {
        Assert.Null(InputRepair.AfterRejection(input, prompt, "Invalid option keyword.", DecimalInches, 0));
    }

    [Fact]
    public void Options_are_read_with_their_capital_letters()
    {
        var options = InputRepair.Options("Specify next point or [Arc/Halfwidth/Length/Undo/Width/eXit]:");
        Assert.Equal(["A", "H", "L", "U", "W", "X"], options.Select(o => o.Capitals));
        Assert.Empty(InputRepair.Options("Specify first point:"));
        Assert.Equal(1, InputRepair.EditDistance("Clsoe", "Close"));
        Assert.Equal(2, InputRepair.EditDistance("Lenght", "Length") + 1);
    }

    // ----- Finish prompts -----

    [Theory]
    [InlineData("Specify next point or [Close/Undo]:", "LINE")]
    [InlineData("Specify next point or [Arc/Halfwidth/Length/Undo/Width]:", "PLINE")]
    [InlineData("Enter next point or [end Tangency/toLerance/Undo/Close]:", "SPLINE")]
    [InlineData("Select objects:", "ERASE")]
    [InlineData("Enter text:", "TEXT")]
    [InlineData("Specify internal point or [Properties/Select objects/draW boundary]:", "HATCH")]
    [InlineData("Select object to offset or [Exit/Undo] <Exit>:", "OFFSET")]
    [InlineData("Specify second point or [Array/Exit/Undo] <Exit>:", "COPY")]
    [InlineData("Select object to trim or shift-select to extend or [cuTting edges/Crossing/mOde/Project/eRase]:", "TRIM")]
    [InlineData("Enter an option [?/Make/Set/New/Rename/ON/OFF/Color/Ltype]:", "LAYER")]
    [InlineData("Specify a second extension line origin or [Select/Undo] <Select>:", "DIMCONTINUE")]
    [InlineData("Select continued dimension:", "DIMCONTINUE")]
    public void Finish_prompts_take_the_forgotten_Enter(string prompt, string command)
    {
        Assert.True(InputRepair.IsFinishPrompt(prompt, command));
    }

    [Theory]
    [InlineData("Specify first point:", "LINE")]
    [InlineData("Specify offset distance or [Through/Erase/Layer] <Through>:", "OFFSET")]
    [InlineData("Select objects or <select all>:", "TRIM")]
    [InlineData("Specify base point or [Displacement] <Displacement>:", "MOVE")]
    [InlineData("Specify height <0.2000>:", "TEXT")]
    [InlineData("Enter an option [Yes/No] <N>:", "PURGE")]
    [InlineData("Enter an option [?/Create/Load/Set]:", "STYLE")]
    [InlineData("Command:", "LINE")]
    [InlineData("", "LINE")]
    public void Prompts_where_Enter_would_choose_something_are_not_finish_prompts(string prompt, string command)
    {
        Assert.False(InputRepair.IsFinishPrompt(prompt, command));
    }

    [Fact]
    public void Dash_versions_are_offered_only_for_allowed_plain_commands()
    {
        Assert.Equal("-VIEW", InputRepair.DashVersion("VIEW"));
        Assert.Null(InputRepair.DashVersion("-VIEW"));
        Assert.Null(InputRepair.DashVersion("(command \"LINE\")"));
        Assert.Null(InputRepair.DashVersion("'ZOOM"));
    }

    [Fact]
    public void Fixes_describe_themselves_for_the_result()
    {
        Assert.Equal("pressed Enter at 'Select objects:' (the inputs ended without the finishing \"\"; Enter only ends the command or selection here)",
            InputRepair.FinishEnter("Select objects: ").Describe());
        Assert.StartsWith("'30'' typed as '360' (", InputRepair.AfterRejection("30'", "Specify length:", "Invalid.", DecimalInches, 0)!.Describe(), StringComparison.Ordinal);
        Assert.Equal("typed -HATCH instead of HATCH (dialog)", InputRepair.CommandLineVersion("HATCH", "-HATCH", "dialog").Describe());
    }

    // ----- run_commands options and report -----

    [Fact]
    public void Run_commands_takes_autoFix_and_autoCheckpoint_options()
    {
        var steps = new JsonArray(Enumerable.Range(0, 10).Select(_ => (JsonNode?)new JsonObject { ["command"] = "LINE", ["inputs"] = new JsonArray("0,0", "@1,0", "") }).ToArray());
        var big = new JsonObject { ["title"] = "Outer walls", ["steps"] = steps };
        Assert.Empty(JsonSchemaValidator.Validate(DraftingTools.RunCommandsDefinition().InputSchema, big));
        Assert.True(RunCommandsRequest.TryParse(big, out var request, out _));
        Assert.True(request!.AutoFix);
        Assert.Null(request.AutoCheckpoint);
        Assert.True(request.WantsCheckpoint);

        var small = JsonNode.Parse("""{"title":"x","steps":[{"command":"LINE"}],"autoFix":false}""")!.AsObject();
        Assert.Empty(JsonSchemaValidator.Validate(DraftingTools.RunCommandsDefinition().InputSchema, small));
        Assert.True(RunCommandsRequest.TryParse(small, out var smallRequest, out _));
        Assert.False(smallRequest!.AutoFix);
        Assert.False(smallRequest.WantsCheckpoint);

        var forced = JsonNode.Parse("""{"title":"x","steps":[{"command":"LINE"}],"autoCheckpoint":true}""")!.AsObject();
        Assert.True(RunCommandsRequest.TryParse(forced, out var forcedRequest, out _));
        Assert.True(forcedRequest!.WantsCheckpoint);

        var bad = JsonNode.Parse("""{"title":"x","steps":[{"command":"LINE"}],"autoFix":"yes"}""")!.AsObject();
        Assert.False(RunCommandsRequest.TryParse(bad, out _, out var errors));
        Assert.Contains(errors, e => e.Contains("$.autoFix", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_lists_auto_fixes_and_the_checkpoint_in_the_summary()
    {
        var plan = DraftingPlan.Create(new RunCommandsRequest { Title = "Walls", Steps = [new DraftingStep { Command = "LINE", Inputs = ["0,0", "30'", ""] }] });
        var recorder = new StepRecorder(plan.Steps[0]) { Status = StepStatus.Finished };
        recorder.AutoFixed(InputRepair.AfterRejection("30'", "Specify next point:", "Invalid point.", DecimalInches, 0)!);
        recorder.AutoFixed(InputRepair.FinishEnter("Specify next point or [Close/Undo]:"));
        var report = new RunCommandsReport
        {
            Title = "Walls",
            Outcome = RunOutcome.Completed,
            Pace = "watch",
            StepsTotal = 1,
            StepsRun = 1,
            Steps = [recorder.Build()],
            Checkpoint = "before: Walls",
        };

        var summary = report.Summarize();
        Assert.Contains("Auto-fixed: step 1 LINE: '30'' typed as '360'", summary, StringComparison.Ordinal);
        Assert.Contains("pressed Enter at 'Specify next point or [Close/Undo]:'", summary, StringComparison.Ordinal);
        Assert.Contains("Checkpoint 'before: Walls'", summary, StringComparison.Ordinal);
        Assert.Equal(2, report.AutoFixes().Count);
        Assert.Empty(report.Problems());
    }
}
