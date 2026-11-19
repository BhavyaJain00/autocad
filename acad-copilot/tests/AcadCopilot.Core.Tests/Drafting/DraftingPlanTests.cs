using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Drafting;

public sealed class DraftingPlanTests
{
    private static RunCommandsRequest Request(params DraftingStep[] steps) => new() { Title = "Ground floor", Steps = steps };

    private static DraftingStep Step(string command, params string[] inputs) => new() { Command = command, Inputs = inputs };

    [Fact]
    public void Allowed_steps_are_planned_as_given()
    {
        var plan = DraftingPlan.Create(Request(Step("LINE", "0,0", "@360,0", ""), Step("OFFSET", "9", "0,0", "10,10", "")));
        Assert.False(plan.IsBlocked);
        Assert.False(plan.NeedsApproval);
        Assert.Empty(plan.RewriteNotes);
        Assert.Empty(plan.BlockedErrors);
        Assert.Equal(1, plan.Steps[0].Number);
        Assert.Equal(2, plan.Steps[1].Number);
        Assert.Equal("LINE", plan.Steps[0].Command);
        Assert.Equal("LINE", plan.Steps[0].CanonicalName);
        Assert.Equal(string.Empty, plan.ApprovalSummary());
    }

    [Fact]
    public void Dialog_commands_are_replaced_by_their_command_line_version()
    {
        var plan = DraftingPlan.Create(Request(Step("LAYER", "M", "WALLS", ""), Step("bh", "P", "ANSI31", "1", "0", "10,10", "")));
        Assert.False(plan.IsBlocked);
        Assert.Equal("-LAYER", plan.Steps[0].Command);
        Assert.Equal("LAYER", plan.Steps[0].RequestedCommand);
        Assert.Equal("-HATCH", plan.Steps[1].Command);
        Assert.Equal(2, plan.RewriteNotes.Count);
        Assert.Contains("Step 1: typed -LAYER instead of LAYER", plan.RewriteNotes[0]);
    }

    [Fact]
    public void A_blocked_step_blocks_the_whole_plan()
    {
        var plan = DraftingPlan.Create(Request(Step("LINE", "0,0", "@10,0", ""), Step("SHELL"), Step("TEXT", "(alert \"x\")")));
        Assert.True(plan.IsBlocked);
        Assert.False(plan.NeedsApproval);
        Assert.Equal(2, plan.BlockedErrors.Count);
        Assert.StartsWith("Step 2 (SHELL): SHELL: ", plan.BlockedErrors[0]);
        Assert.StartsWith("Step 3 (TEXT): AutoLISP input", plan.BlockedErrors[1]);
    }

    [Fact]
    public void Approval_summary_lists_the_exact_commands()
    {
        var plan = DraftingPlan.Create(Request(Step("LINE", "0,0", "@10,0", ""), Step("QSAVE"), Step("E", "ALL", "")));
        Assert.False(plan.IsBlocked);
        Assert.True(plan.NeedsApproval);
        Assert.Equal(2, plan.ApprovalSteps.Count);
        var summary = plan.ApprovalSummary();
        Assert.StartsWith("Ground floor: the AI wants to run 2 commands that need your OK: ", summary);
        Assert.Contains("step 2 QSAVE (saves the drawing file)", summary);
        Assert.Contains("step 3 E ALL <Enter> (erases every object in the drawing", summary);
    }

    [Fact]
    public void Plot_is_rewritten_and_still_needs_approval()
    {
        var plan = DraftingPlan.Create(Request(Step("PLOT", "Y")));
        Assert.Equal("-PLOT", plan.Steps[0].Command);
        Assert.True(plan.NeedsApproval);
    }

    [Fact]
    public void Describe_shortens_long_inputs()
    {
        var inputs = Enumerable.Range(0, 10).Select(i => i == 0 ? new string('X', 50) : "@1,0").ToArray();
        var step = DraftingPlan.Create(Request(Step("PLINE", inputs))).Steps[0];
        var text = step.Describe();
        Assert.StartsWith("PLINE " + new string('X', 30) + "...", text);
        Assert.EndsWith("(4 more inputs)", text);
    }

    [Fact]
    public void User_pgp_aliases_are_used()
    {
        var aliases = CommandAliases.FromPgp("XX, *SHELL\nLL, *LAYER\n");
        var plan = DraftingPlan.Create(Request(Step("LL", "M", "A", ""), Step("XX")), aliases);
        Assert.Equal("-LAYER", plan.Steps[0].Command);
        Assert.True(plan.Steps[1].IsBlocked);
    }
}
