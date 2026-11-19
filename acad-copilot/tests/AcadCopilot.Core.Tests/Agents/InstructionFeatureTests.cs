using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

/// <summary>Fewer questions, planning ahead, recipe reuse, tutor mode, built-in guides and the reviewer's answer format.</summary>
public sealed class InstructionFeatureTests
{
    [Fact]
    public void Default_asks_less_plans_ahead_and_reuses_recipes_within_the_length_limit()
    {
        var text = CopilotInstructions.Default;
        Assert.True(text.Length < 15_500, $"{text.Length} characters");
        Assert.Contains("state your assumptions in one line", text, StringComparison.Ordinal);
        Assert.Contains("ask_user only for blocking facts", text, StringComparison.Ordinal);
        Assert.Contains("\"just draw\"", text, StringComparison.Ordinal);
        Assert.Contains("Shall I draw this?", text, StringComparison.Ordinal);
        Assert.Contains("work out every coordinate of every stage", text, StringComparison.Ordinal);
        Assert.Contains("queued calls run in order", text, StringComparison.Ordinal);
        Assert.Contains("if a recipe matches, reuse it", text, StringComparison.Ordinal);
        Assert.Contains("HANDOVER", text, StringComparison.Ordinal);
        Assert.Contains("recall_knowledge with the drawing kind", text, StringComparison.Ordinal);
        Assert.DoesNotContain("never in parallel", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Turn_options_add_only_what_differs_from_the_defaults()
    {
        Assert.Equal(CopilotInstructions.Default, CopilotInstructions.Compose(null, null, TurnOptions.Default));
        Assert.Equal(CopilotInstructions.Compose(null, null), CopilotInstructions.Compose(null, null, null));

        var tutor = CopilotInstructions.Compose(null, null, new TurnOptions { TutorMode = true });
        Assert.Contains("TURN SETTINGS", tutor, StringComparison.Ordinal);
        Assert.Contains("OFFSET copies a line at a distance: used here for the 9", tutor, StringComparison.Ordinal);
        Assert.DoesNotContain(CopilotInstructions.NoApprovalNote, tutor, StringComparison.Ordinal);

        var direct = CopilotInstructions.Compose(null, "WHAT YOU HAVE LEARNED\n- rule", new TurnOptions { AskBeforeDrawing = false });
        Assert.Contains(CopilotInstructions.NoApprovalNote, direct, StringComparison.Ordinal);
        Assert.True(direct.IndexOf("TURN SETTINGS", StringComparison.Ordinal) < direct.LastIndexOf("WHAT YOU HAVE LEARNED\n- rule", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_option_with_the_largest_learned_section_stays_under_the_cap_and_fits_the_codex_command_line()
    {
        using var dir = new TestDirectory();
        var learned = KnowledgeDigest.Heading + "\n" + string.Concat(Enumerable.Repeat("- 9\" walls, C:\\x \"q\"\n", KnowledgeDigest.MaxCharacters / 20));
        learned = learned[..KnowledgeDigest.MaxCharacters];
        var instructions = CopilotInstructions.Compose("AutoCAD Architecture 2027", learned, new TurnOptions { AskBeforeDrawing = false, TutorMode = true });
        Assert.True(instructions.Length <= CopilotInstructions.MaxComposedLength, $"{instructions.Length} characters");
        Assert.Contains(CopilotInstructions.TutorNote, instructions, StringComparison.Ordinal);

        var arguments = CodexRunner.BuildArguments(Request(dir.Path) with { Instructions = instructions }, "5b1f2c3d-0000-4000-8000-000000000000", [], new CodexUserDefaults("gpt-5", "high"));
        var length = arguments.Sum(a => a.Length + 3 + a.Count(c => c is '"' or '\\')) + 200;
        Assert.True(length < 32_000, $"Codex command line is {length} characters");
    }

    [Fact]
    public void Reviewer_instructions_fit_and_name_only_read_tools()
    {
        Assert.True(CopilotInstructions.Reviewer.Length < 4_000, $"{CopilotInstructions.Reviewer.Length} characters");
        foreach (var check in new[] { "Proportions", "Circulation", "Doors", "window", "Stair", "Toilet and kitchen", "dimensions complete", "Layers", "SUMMARY:", "[major]", "[minor]" })
        {
            Assert.Contains(check, CopilotInstructions.Reviewer, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("run_commands", CopilotInstructions.Reviewer, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_in_guides_come_back_with_recall_knowledge()
    {
        Assert.Equal("Elevation", DraftingGuides.Match("front elevation")[0].Name);
        Assert.Equal("Dog-legged stair", DraftingGuides.Match("dog-legged stair")[0].Name);
        Assert.Equal("Site plan", DraftingGuides.Match("site plan setbacks")[0].Name);
        Assert.Empty(DraftingGuides.Match("door swing"));
        Assert.Empty(DraftingGuides.Match(null));
        Assert.All(DraftingGuides.All, g => Assert.True(g.Text.Length < 1_200, g.Name));

        var nothingLearned = ToolResult.Ok("Nothing learned yet.", new JsonObject { ["items"] = new JsonArray() });
        var withGuide = DraftingGuides.AddTo(nothingLearned, "section through the stair");
        var names = withGuide.Data!["guides"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Section", names);
        Assert.Contains("Built-in guide", withGuide.Message, StringComparison.Ordinal);
        Assert.Equal(DraftingGuides.All.Count, DraftingGuides.AddTo(nothingLearned, " ").Data!["builtInGuides"]!.AsArray().Count);
        var failed = ToolResult.Fail("x");
        Assert.Same(failed, DraftingGuides.AddTo(failed, "stair"));
    }

    [Fact]
    public void Review_answers_are_parsed_into_findings()
    {
        var report = ReviewReport.Parse(ProviderId.Codex, "ChatGPT", """
            Here is my review.
            SUMMARY: Good start; fix the stair.
            - [minor] Room names on layer 0 -> move them to A-ANNO-TEXT.
            - [major] Stair risers are 8" -> use 18 risers of 7".
            * **Major:** Toilet has no ventilator -> add a 2' ventilator on the north wall.
            1. Low ceiling note missing -> add it.
            """);
        Assert.Equal("Good start; fix the stair.", report.Summary);
        Assert.Equal(4, report.Findings.Count);
        Assert.Equal(2, report.MajorCount);
        Assert.Equal(ReviewSeverity.Major, report.Findings[0].Severity);
        Assert.Equal("Low ceiling note missing -> add it.", report.Findings[^1].Text);
        Assert.Contains("**Second opinion (ChatGPT)**: Good start; fix the stair.", report.Markdown, StringComparison.Ordinal);
        Assert.Contains("3. [minor] Room names on layer 0", report.FixPrompt, StringComparison.Ordinal);

        var clean = ReviewReport.Parse(ProviderId.Claude, "Claude", "SUMMARY: No problems found.");
        Assert.True(clean.IsClean);
        Assert.Equal(string.Empty, clean.FixPrompt);
    }

    [Theory]
    [InlineData("review this plan", true)]
    [InlineData("Please review the drawing.", true)]
    [InlineData("can you give me a second opinion", true)]
    [InlineData("recheck it", true)]
    [InlineData("review the plan and make the kitchen 10 feet wide", false)]
    [InlineData("draw the ground floor plan", false)]
    public void Review_requests_are_recognised(string text, bool expected) =>
        Assert.Equal(expected, ReviewRequests.IsReviewRequest(text));

    [Fact]
    public void Fix_requests_and_review_focus_are_recognised()
    {
        Assert.True(ReviewRequests.IsFixRequest("fix these"));
        Assert.True(ReviewRequests.IsFixRequest("OK, fix them."));
        Assert.True(ReviewRequests.IsFixRequest("please fix the findings"));
        Assert.False(ReviewRequests.IsFixRequest("fix the kitchen door so it opens inwards"));
        Assert.Null(ReviewRequests.FocusOf("review this plan"));
        Assert.Equal("the first floor plan", ReviewRequests.FocusOf("review the first floor plan"));
    }

    [Fact]
    public void Provider_order_is_completed_and_deduplicated()
    {
        Assert.Equal([ProviderId.Gemini, ProviderId.Claude, ProviderId.Codex], CopilotFeatures.NormalizeOrder([ProviderId.Gemini, ProviderId.Gemini]));
        Assert.Equal([ProviderId.Claude, ProviderId.Codex, ProviderId.Gemini], CopilotFeatures.NormalizeOrder(null));
        Assert.True(new CopilotFeatures().SameAs(new CopilotFeatures { ProviderOrder = [ProviderId.Claude] }));
    }
}
