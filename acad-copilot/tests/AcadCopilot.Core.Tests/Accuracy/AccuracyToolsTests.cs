using System.Text.Json.Nodes;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;
using static AcadCopilot.Core.Tests.Accuracy.PlanBriefs;

namespace AcadCopilot.Core.Tests.Accuracy;

public sealed class AccuracyToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Definitions_LintCleanAndAcceptTheBriefs()
    {
        var definitions = new[]
        {
            AccuracyToolDefinitions.ValidatePlanDefinition(),
            AccuracyToolDefinitions.CheckDrawingDefinition(),
            AccuracyToolDefinitions.ApplyDrawingTemplateDefinition(),
        };
        foreach (var definition in definitions)
        {
            Assert.Empty(definition.Lint());
        }

        Assert.Equal(ToolRisk.Read, definitions[0].Risk);
        Assert.Equal(ToolRisk.Read, definitions[1].Risk);
        Assert.Equal(ToolRisk.Write, definitions[2].Risk);

        foreach (var plan in new[] { ThirtyForty(), ThirtyForty(3), ThirtyFifty() })
        {
            var errors = JsonSchemaValidator.Validate(definitions[0].InputSchema, plan.Args(vastu: true));
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }

        Assert.Empty(JsonSchemaValidator.Validate(definitions[1].InputSchema, new JsonObject { ["floor"] = "2", ["origin"] = "60',0", ["stage"] = "walls" }));
        Assert.Empty(JsonSchemaValidator.Validate(definitions[2].InputSchema, new JsonObject { ["scale"] = "1/8", ["overwrite"] = true }));
    }

    [Fact]
    public void Definitions_RejectWrongArguments()
    {
        var validate = AccuracyToolDefinitions.ValidatePlanDefinition();
        var plan = ThirtyForty().Args();
        plan["plan"]!["floors"]![0]!["rooms"]![0]!["colour"] = "red";
        Assert.NotEmpty(JsonSchemaValidator.Validate(validate.InputSchema, plan));

        var noPlot = ThirtyForty().Args();
        noPlot["plan"]!.AsObject().Remove("plot");
        Assert.NotEmpty(JsonSchemaValidator.Validate(validate.InputSchema, noPlot));

        var bad = ThirtyForty().Args();
        bad["plan"]!["floors"]![0]!["rooms"]![0]!["type"] = "ballroom";
        Assert.NotEmpty(JsonSchemaValidator.Validate(validate.InputSchema, bad));

        Assert.NotEmpty(JsonSchemaValidator.Validate(AccuracyToolDefinitions.CheckDrawingDefinition().InputSchema, new JsonObject { ["stage"] = "doors" }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(AccuracyToolDefinitions.ApplyDrawingTemplateDefinition().InputSchema, new JsonObject { ["scale"] = "1/2" }));
    }

    [Fact]
    public void ValidatePlan_ReadyResultHasSummaryScheduleAndAdvice()
    {
        var (validation, result, rules) = AccuracyTools.ValidatePlan(ThirtyForty().Args(), []);
        Assert.True(result.Success);
        Assert.True(validation.IsReady);
        Assert.StartsWith("Plan OK: 0 errors", result.Message, StringComparison.Ordinal);
        Assert.Contains("Shall I draw this?", result.Message, StringComparison.Ordinal);
        var data = result.Data!.AsObject();
        Assert.True(data["ready"]!.GetValue<bool>());
        Assert.Empty(data["errors"]!.AsArray());
        Assert.Contains("| Living |", data["summary"]!.ToString(), StringComparison.Ordinal);
        Assert.NotNull(data["schedule"]!["floors"]);
        Assert.False(rules.Vastu);
    }

    [Fact]
    public void ValidatePlan_NotReadyAndUnreadablePlans()
    {
        var plan = ThirtyForty(floors: 1);
        Item(plan, 0, "rooms", "name", "Living")["width"] = "12'";
        var (_, result, _) = AccuracyTools.ValidatePlan(plan.Args(), []);
        Assert.True(result.Success);
        Assert.StartsWith("NOT READY:", result.Message, StringComparison.Ordinal);
        Assert.NotEmpty(result.Data!["errors"]!.AsArray());

        var broken = ThirtyForty(floors: 1);
        broken.Root.Remove("floors");
        var (_, failed, _) = AccuracyTools.ValidatePlan(broken.Args(), []);
        Assert.False(failed.Success);
        Assert.Contains(failed.Errors, e => e.StartsWith("plan.floors", StringComparison.Ordinal));

        Assert.Throws<ToolException>(() => AccuracyTools.ValidatePlan(new JsonObject(), []));
    }

    [Fact]
    public void ValidatePlan_ReadsVastuFromArgumentsAndRules()
    {
        Assert.True(AccuracyTools.ValidatePlan(ThirtyForty().Args(vastu: true), []).Rules.Vastu);
        Assert.True(AccuracyTools.ValidatePlan(ThirtyForty().Args(), ["Follow Vastu"]).Rules.Vastu);
        Assert.False(AccuracyTools.ValidatePlan(ThirtyForty().Args(vastu: false), ["Follow Vastu"]).Rules.Vastu);
    }

    [Fact]
    public void RuleLines_ComeFromTheKnowledgeFolder()
    {
        var store = new KnowledgeStore(_dir);
        store.RememberRule("Toilets at least 5'x8' clear", "planning");
        store.RememberRule("Dimensions outside the walls", "dimensions");
        var lines = AccuracyTools.RuleLines(_dir);
        Assert.Contains("Toilets at least 5'x8' clear", lines);
        var rules = PlanRules.FromRules(lines);
        Assert.Equal(new RoomMinimum(60, 96), rules.RoomMinimums["toilet"]);
        Assert.Single(rules.Applied);
        Assert.Empty(AccuracyTools.RuleLines(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void ResolveFloor_ByNumberNameAndOrigin()
    {
        var json = ThirtyForty().Root;
        var (_, ground, origin) = AccuracyTools.ResolveFloor(json, null, null);
        Assert.Equal("Ground floor", ground.Name);
        Assert.Equal(new Pt(0, 0), origin);

        var (_, first, firstOrigin) = AccuracyTools.ResolveFloor(json, "2", null);
        Assert.Equal("First floor", first.Name);
        Assert.Equal(new Pt(720, 0), firstOrigin);

        var (_, named, given) = AccuracyTools.ResolveFloor(json, "first floor", "100',10'");
        Assert.Equal("First floor", named.Name);
        Assert.Equal(new Pt(1200, 120), given);

        Assert.Throws<ToolException>(() => AccuracyTools.ResolveFloor(json, "7", null));
        Assert.Throws<ToolException>(() => AccuracyTools.ResolveFloor(json, null, "here"));

        var noOrigin = ThirtyForty();
        noOrigin.Floor(1).Remove("origin");
        var ex = Assert.Throws<ToolException>(() => AccuracyTools.ResolveFloor(noOrigin.Root, "2", null));
        Assert.Contains("Pass origin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_SavesPerDrawingAndFindsTheLatestOfTheSession()
    {
        var store = new PlanStore(_dir);
        Assert.Null(store.Load("house_1"));
        var t0 = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        store.Save("house_1", "House.dwg", ThirtyForty().Root, 4242, vastu: true, t0);
        store.Save("other_2", "Other.dwg", ThirtyFifty().Root, 4242, vastu: false, t0.AddMinutes(5));
        store.Save("old_3", "Old.dwg", ThirtyFifty().Root, 1111, vastu: false, t0.AddMinutes(9));

        var loaded = store.Load("house_1")!;
        Assert.Equal("House.dwg", loaded.Drawing);
        Assert.True(loaded.Vastu);
        Assert.Equal(4242, loaded.ProcessId);
        Assert.Equal("30x40 south road G+1", loaded.Plan["name"]!.ToString());
        Assert.Equal("Other.dwg", store.LatestOfProcess(4242)!.Drawing);
        Assert.Null(store.LatestOfProcess(9999));

        File.WriteAllText(store.PathOf("bad_4"), "{ not json");
        Assert.Null(store.Load("bad_4"));
    }

    [Fact]
    public void CheckResult_SummarisesTheReport()
    {
        var plan = Parse(ThirtyForty());
        var floor = plan.Floors[0];
        var snapshot = SyntheticDrawing.Draw(plan, floor, new Pt(0, 0));
        var stored = new StoredPlan(ThirtyForty().Root, "House.dwg", DateTimeOffset.UnixEpoch, 1, false, "x.json");
        var clean = AccuracyTools.CheckResult(DrawingChecker.Check(plan, floor, new Pt(0, 0), snapshot, new CheckLayers()), floor, new Pt(0, 0), stored, 40);
        Assert.Equal("Ground floor matches the plan: no problems found.", clean.Message);

        snapshot.Arcs.Clear();
        var report = DrawingChecker.Check(plan, floor, new Pt(0, 0), snapshot, new CheckLayers());
        var result = AccuracyTools.CheckResult(report, floor, new Pt(0, 0), stored, 2);
        Assert.StartsWith("Ground floor: 4 errors", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, result.Data!["findings"]!.AsArray().Count);
        Assert.Equal(2, result.Data!["moreFindings"]!.GetValue<int>());
    }

    [Fact]
    public void Instructions_TellTheAiToValidateCheckAndUseTheTemplate()
    {
        var text = CopilotInstructions.Default;
        Assert.Contains("validate_plan", text, StringComparison.Ordinal);
        Assert.Contains("check_drawing", text, StringComparison.Ordinal);
        Assert.Contains("apply_drawing_template", text, StringComparison.Ordinal);
        Assert.True(text.Length < 15_500, $"{text.Length} characters");
        Assert.Contains(DraftingGuides.Match("drawing setup layers"), g => g.Name == "Drawing setup");
        Assert.Contains(DraftingGuides.Match("room sizes"), g => g.Name == "Room sizes (India)");
    }
}
