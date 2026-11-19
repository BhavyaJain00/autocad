using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class CommandLineTextTests
{
    [Fact]
    public void NewLines_finds_lines_appended_after_a_shift()
    {
        string[] before = ["Command: LINE", "Specify first point:"];
        string[] after = ["Specify first point: 0,0", "Specify next point or [Undo]:"];
        Assert.Equal(["Specify first point: 0,0", "Specify next point or [Undo]:"], CommandLineText.NewLines(before, after));
    }

    [Fact]
    public void NewLines_when_the_window_scrolled()
    {
        string[] before = ["a", "b", "Specify next point or [Undo]:"];
        string[] after = ["Specify next point or [Undo]: @10,0", "Invalid point.", "Specify next point or [Undo]:"];
        Assert.Equal(["Specify next point or [Undo]: @10,0", "Invalid point.", "Specify next point or [Undo]:"], CommandLineText.NewLines(before, after));
    }

    [Fact]
    public void NewLines_with_a_full_overlap_and_new_tail()
    {
        string[] before = ["x", "y", "Select objects:"];
        string[] after = ["y", "Select objects: W", "Specify first corner:"];
        Assert.Equal(["Select objects: W", "Specify first corner:"], CommandLineText.NewLines(before, after));
    }

    [Fact]
    public void NewLines_is_empty_when_nothing_changed()
    {
        string[] lines = ["Command:", "Command: LINE", "Specify first point:"];
        Assert.Empty(CommandLineText.NewLines(lines, lines));
    }

    [Fact]
    public void NewLines_without_overlap_returns_everything()
    {
        Assert.Equal(["p", "q"], CommandLineText.NewLines(["a", "b"], ["p", "q"]));
        Assert.Equal(["p"], CommandLineText.NewLines([], ["p"]));
    }

    [Fact]
    public void NewLines_never_rereads_old_lines_when_a_line_further_up_changed()
    {
        // A notice re-wrapped/inserted above breaks the clean overlap; the old notice must not count as new output.
        string[] before = ["**** System Variable Changed ****", "1 of the monitored system variables has changed.", "Command:"];
        string[] after = ["**** System Variable Changed ****", "(re-wrapped)", "Command: SETVAR", "Enter variable name or [?]:"];
        Assert.Equal(["Command: SETVAR", "Enter variable name or [?]:"], CommandLineText.NewLines(before, after));
    }

    [Fact]
    public void Chronological_reverses_the_newest_first_history_of_autocad_2027()
    {
        // As GetLastCommandLines returned it in the first real run: newest line first.
        string[] newestFirst = ["Enter new value for DIMZIN <0>: 3", "Enter new value for DIMFRAC <0>: 2", "Enter new value for DIMDEC <4>: 1"];
        Assert.Equal(
            ["Enter new value for DIMDEC <4>: 1", "Enter new value for DIMFRAC <0>: 2", "Enter new value for DIMZIN <0>: 3"],
            CommandLineText.Chronological(newestFirst, "Command:"));

        string[] withCurrentFirst = ["Command:", "Enter new value for DIMZIN <0>: 3", "Enter new value for DIMFRAC <0>: 2"];
        Assert.Equal("Command:", CommandLineText.Chronological(withCurrentFirst, "Command:")[^1]);
    }

    [Fact]
    public void Chronological_keeps_an_oldest_first_history()
    {
        string[] oldestFirst = ["Command: LINE", "Specify first point: 0,0", "Specify next point or [Undo]:"];
        Assert.Equal(oldestFirst, CommandLineText.Chronological(oldestFirst, "Specify next point or [Undo]:"));
        Assert.Equal(["x"], CommandLineText.Chronological(["x"], null));
    }

    [Fact]
    public void Stale_history_read_newest_first_no_longer_looks_new()
    {
        // The bug: the snapshots were compared newest-first, never overlapped, and every old line (with the SYSMON
        // notice) was read as output of the input just typed.
        string[] beforeRaw = ["Command:", "Enter new value for SYSMON <1>: 0", "**** System Variable Changed ****", "Regenerating model."];
        string[] afterRaw = ["Enter new value for INSUNITS <4>:", "Command: INSUNITS", "Command:", "Enter new value for SYSMON <1>: 0", "**** System Variable Changed ****"];
        var before = CommandLineText.Chronological(beforeRaw, "Command:");
        var after = CommandLineText.Chronological(afterRaw, "Enter new value for INSUNITS <4>:");
        var lines = CommandLineText.NewLines(before, after);
        Assert.Equal(["Command: INSUNITS", "Enter new value for INSUNITS <4>:"], lines);
        Assert.Null(CommandLineText.FindRejection(lines));
    }

    [Theory]
    [InlineData("**** System Variable Changed ****")]
    [InlineData("**** No System Variable Changed ****")]
    [InlineData("3 of the monitored system variables have changed from the preferred value. Use SYSVARMONITOR command to view changes.")]
    [InlineData("1 of the monitored system variables has changed from the preferred value. Use SYSVARMONITOR command to view changes.")]
    public void System_variable_monitor_notices_are_never_rejections(string notice)
    {
        Assert.True(CommandLineText.IsSystemNotice(notice));
        Assert.Null(CommandLineText.FindRejection(["Enter new value for LUNITS <2>: 4", notice, "Command:"]));
        Assert.Empty(CommandLineText.Messages([notice], "Enter new value for LUNITS <2>:"));
        Assert.Equal("Invalid point.", CommandLineText.FindRejection([notice, "Invalid point."]));
    }

    [Theory]
    [InlineData("** That command may not be invoked transparently **")]
    [InlineData("Invalid point.")]
    public void Real_errors_are_not_system_notices(string line)
    {
        Assert.False(CommandLineText.IsSystemNotice(line));
    }

    [Fact]
    public void An_input_still_echoed_on_the_current_line_is_not_settled()
    {
        // The ARC of the first real run: the included angle was typed and AutoCAD was still drawing the arc.
        const string echo = "Specify included angle (hold Ctrl to switch direction): -90";
        Assert.False(CommandLineText.IsSettled(reacted: true, quiet: true, inputPending: false, commandActive: true, echo));
        Assert.False(CommandLineText.IsWaitingForInput(commandActive: true, echo));

        // Done: back at the Command prompt, or a new prompt waiting for the next input.
        Assert.True(CommandLineText.IsSettled(true, true, false, commandActive: false, echo));
        Assert.True(CommandLineText.IsSettled(true, true, null, true, "Specify included angle (hold Ctrl to switch direction):"));
        Assert.True(CommandLineText.IsWaitingForInput(true, "Specify next point or [Close/Undo]:"));

        // Not yet: no reaction, a burst of events still arriving, input still queued.
        Assert.False(CommandLineText.IsSettled(false, true, false, false, "Command:"));
        Assert.False(CommandLineText.IsSettled(true, false, false, false, "Command:"));
        Assert.False(CommandLineText.IsSettled(true, true, true, false, "Command:"));
        Assert.False(CommandLineText.IsWaitingForInput(false, "Command:"));
    }

    [Theory]
    [InlineData("Specify next point or [Undo]:", true)]
    [InlineData("Select objects:  ", true)]
    [InlineData("Command:", true)]
    [InlineData("Specify first point: 0,0", false)]
    [InlineData("1 found", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPrompt_needs_a_trailing_colon(string? line, bool expected)
    {
        Assert.Equal(expected, CommandLineText.IsPrompt(line));
    }

    [Theory]
    [InlineData("Invalid point.")]
    [InlineData("Point or option keyword required.")]
    [InlineData("Requires numeric distance or two points.")]
    [InlineData("Requires valid numeric angle or second point.")]
    [InlineData("Invalid option keyword.")]
    [InlineData("*Invalid selection*")]
    [InlineData("Value must be positive and nonzero.")]
    [InlineData("Unknown command \"LIEN\".  Press F1 for help.")]
    [InlineData("Layer \"WALLS\" not found.")]
    [InlineData("Cannot find block \"DOOR\".")]
    [InlineData("** That command may not be invoked transparently **")]
    [InlineData("Unable to offset.")]
    [InlineData("Ambiguous response, please clarify...")]
    public void FindRejection_knows_autocad_error_messages(string message)
    {
        Assert.Equal(CommandLineText.Clean(message), CommandLineText.FindRejection(["Specify next point or [Undo]: @x", message, "Specify next point or [Undo]:"]));
    }

    [Theory]
    [InlineData("1 found")]
    [InlineData("Current settings: Offset distance = 9\"  Erase source=No  Layer=Source  OFFSETGAPTYPE=0")]
    [InlineData("Specify next point or [Undo]:")]
    [InlineData("Area = 172800.0000, Perimeter = 1680.0000")]
    public void FindRejection_ignores_normal_lines(string line)
    {
        Assert.Null(CommandLineText.FindRejection([line]));
    }

    [Fact]
    public void Messages_skip_prompts_echoes_and_command_lines()
    {
        var messages = CommandLineText.Messages(
            ["Select objects: L", "1 found", "Select objects:", "Command: _.ZOOM", "Current settings: Offset distance = 9\""],
            "Select objects:");
        Assert.Equal(["1 found", "Current settings: Offset distance = 9\""], messages);
    }

    [Fact]
    public void ContainsCancel_finds_the_cancel_mark()
    {
        Assert.True(CommandLineText.ContainsCancel(["Specify next point or [Undo]: *Cancel*"]));
        Assert.False(CommandLineText.ContainsCancel(["Specify next point or [Undo]:"]));
    }

    [Theory]
    [InlineData("Enter text:", true)]
    [InlineData("MText:", true)]
    [InlineData("Text:", true)]
    [InlineData("Enter attribute prompt:", true)]
    [InlineData("Enter default attribute value:", true)]
    [InlineData("Enter new string:", true)]
    [InlineData("Enter new text <OLD>:", true)]
    [InlineData("Specify height of text <0'-6\">:", false)]
    [InlineData("Specify rotation angle of text <0>:", false)]
    [InlineData("Specify start point of text or [Justify/Style]:", false)]
    [InlineData("Enter name for new layer (becomes the current layer) <0>:", false)]
    [InlineData("Select objects:", false)]
    [InlineData(null, false)]
    public void IsTextPrompt_only_for_text_prompts(string? prompt, bool expected)
    {
        Assert.Equal(expected, CommandLineText.IsTextPrompt(prompt));
    }

    [Theory]
    [InlineData("BED ROOM", true)]
    [InlineData("0,0;SHELL", true)]
    [InlineData("A\tB", true)]
    [InlineData("@30'<0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasSeparator_spots_spaces_and_semicolons(string? input, bool expected)
    {
        Assert.Equal(expected, CommandLineText.HasSeparator(input));
    }

    [Fact]
    public void Hint_for_feet_inches_in_a_decimal_drawing()
    {
        var hint = CommandLineText.HintForRejection("@30'<0", "Specify next point or [Undo]:", "Invalid point.", new DrawingUnits { LUnits = 2, InsUnits = 1 });
        Assert.NotNull(hint);
        Assert.Contains("LUNITS 2", hint);
        Assert.Contains("30' = 360", hint);
    }

    [Fact]
    public void Hint_for_a_bare_number_at_a_point_prompt()
    {
        var hint = CommandLineText.HintForRejection("360", "Specify next point or [Undo]:", "Invalid point.", new DrawingUnits { LUnits = 4, InsUnits = 1 });
        Assert.Contains("@dx,dy", hint);
    }

    [Fact]
    public void Hints_for_keywords_unknown_commands_and_missing_names()
    {
        Assert.Contains("capital letters", CommandLineText.HintForRejection("X", "Enter an option [Close/Undo]:", "Invalid option keyword.", null));
        Assert.Contains("does not exist", CommandLineText.HintForRejection("LIEN", "Command:", "Unknown command \"LIEN\".  Press F1 for help.", null));
        Assert.Contains("create or load it first", CommandLineText.HintForRejection("WALLS", "Enter layer name:", "Layer \"WALLS\" not found.", null));
        Assert.Null(CommandLineText.HintForRejection("0,0", "Specify first point:", "Something else.", new DrawingUnits { LUnits = 4 }));
    }
}
