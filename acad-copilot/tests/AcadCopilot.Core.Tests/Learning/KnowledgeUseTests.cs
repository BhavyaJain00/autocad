using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Learning;

public sealed class KnowledgeRecallTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private KnowledgeStore Filled()
    {
        var store = new KnowledgeStore(Path.Combine(_dir.Path, "knowledge"), () => new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));
        store.RememberRule("Toilets at least 5'x7' clear", "planning");
        store.RememberRule("Dimensions outside the walls, two chains", "dimensions");
        store.RememberRule("Room names 9 inch high", "text");
        store.SaveLesson("ARC with the A option asked for an angle, not an end point", "Type C, centre, start, A, -90");
        store.SaveRecipe(
            "Dog-legged stair",
            "Two flights of 9 risers with a landing; first point = bottom-left of the stair hall.",
            JsonNode.Parse("""[{"command":"PLINE","inputs":["0,0","@3'6,0",""]},{"command":"-ARRAY","inputs":["L","","R","9","1","10"]}]""")!.AsArray(),
            null);
        store.MergeStyle(new StyleProfile { Sections = [(StyleProfile.TextSection, ["Style ARCH (arial.ttf, height 0): heights 9\" x40, 6\" x25"])] });
        return store;
    }

    [Fact]
    public void Synonyms_and_plurals_find_the_rule()
    {
        var hits = KnowledgeRecall.Search(Filled().Read(), "bathroom sizes");
        var first = hits[0].Item;
        Assert.Equal("rule", first.Kind);
        Assert.StartsWith("Toilets", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipes_are_found_by_title_and_by_their_commands()
    {
        var snapshot = Filled().Read();
        Assert.Equal("Dog-legged stair", KnowledgeRecall.Search(snapshot, "staircase")[0].Item.Title);
        Assert.Equal("Dog-legged stair", KnowledgeRecall.Search(snapshot, "array")[0].Item.Title);
        Assert.Empty(KnowledgeRecall.Search(snapshot, "elevation hatch"));
    }

    [Fact]
    public void Kinds_filter_and_limit_apply()
    {
        var snapshot = Filled().Read();
        var hits = KnowledgeRecall.Search(snapshot, "text height room names arc", kinds: ["style"]);
        Assert.All(hits, h => Assert.Equal("style", h.Item.Kind));
        Assert.Single(KnowledgeRecall.Search(snapshot, "text height room names arc", limit: 1));
    }

    [Fact]
    public void Title_matches_score_higher_than_body_matches()
    {
        var item = new KnowledgeItem("recipe", "Door with swing", "a leaf and an arc", null);
        var other = new KnowledgeItem("lesson", "Lesson", "the door leaf was drawn twice", null);
        Assert.True(KnowledgeRecall.Score(item, ["door"]) > KnowledgeRecall.Score(other, ["door"]));
        Assert.Equal(0, KnowledgeRecall.Score(item, []));
    }

    [Fact]
    public void Tools_save_and_recall_through_json_arguments()
    {
        var store = new KnowledgeStore(Path.Combine(_dir.Path, "k2"));
        var saved = KnowledgeTools.Execute(store, KnowledgeTools.RememberRule, new JsonObject { ["text"] = "Kitchen on the SE corner", ["category"] = "planning" });
        Assert.True(saved.Success, saved.Message);
        Assert.Contains("Planning and room sizes", saved.Message!, StringComparison.Ordinal);

        var invalid = KnowledgeTools.Execute(store, KnowledgeTools.RememberRule, new JsonObject { ["text"] = "x", ["category"] = "nonsense" });
        Assert.False(invalid.Success);

        var recipe = KnowledgeTools.Execute(store, KnowledgeTools.SaveRecipe, new JsonObject
        {
            ["name"] = "Window in 9 inch wall",
            ["description"] = "4' window, faces y=3' and 3'9.",
            ["steps"] = JsonNode.Parse("""[{"command":"BREAK","inputs":["7'4.5,3'","F","5'4.5,3'","@4',0"]}]"""),
        });
        Assert.True(recipe.Success, recipe.Message);

        var recall = KnowledgeTools.Execute(store, KnowledgeTools.RecallKnowledge, new JsonObject { ["query"] = "window" });
        Assert.True(recall.Success);
        var item = recall.Data!["items"]!.AsArray().Single()!;
        Assert.Equal("recipe", item["kind"]!.GetValue<string>());
        Assert.Equal("BREAK", item["steps"]![0]!["command"]!.GetValue<string>());

        var overview = KnowledgeTools.Execute(store, KnowledgeTools.RecallKnowledge, null);
        Assert.Equal(2, overview.Data!["items"]!.AsArray().Count);

        var forget = KnowledgeTools.Execute(store, KnowledgeTools.Forget, new JsonObject { ["kind"] = "rule", ["text"] = "no such rule" });
        Assert.False(forget.Success);
        Assert.Contains("Kitchen", forget.Message!, StringComparison.Ordinal);

        var lesson = KnowledgeTools.Execute(store, KnowledgeTools.SaveLesson, new JsonObject { ["mistake"] = "SYSMON notices", ["fix"] = "not errors" });
        Assert.True(lesson.Success);
    }

    [Fact]
    public void Recall_on_an_empty_base_says_so()
    {
        var result = KnowledgeTools.Execute(new KnowledgeStore(Path.Combine(_dir.Path, "none")), KnowledgeTools.RecallKnowledge, new JsonObject { ["query"] = "stair" });
        Assert.True(result.Success);
        Assert.StartsWith("Nothing learned yet", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "none")));
    }

    [Fact]
    public void Learning_tool_definitions_are_valid_and_the_bridge_names_are_reserved()
    {
        foreach (var definition in KnowledgeTools.BridgeDefinitions().Append(KnowledgeTools.LearnStyleFromDrawingDefinition()))
        {
            Assert.Empty(definition.Lint());
            Assert.True(definition.Description.Length < 1_200, definition.Name);
        }

        Assert.All(KnowledgeTools.BridgeNames, n => Assert.Contains(n, BridgeTools.ReservedNames));
        Assert.DoesNotContain(KnowledgeTools.LearnStyleFromDrawing, BridgeTools.ReservedNames);
        Assert.Equal(ToolRisk.Read, KnowledgeTools.RecallKnowledgeDefinition().Risk);
        Assert.Equal(ToolRisk.Write, KnowledgeTools.RememberRuleDefinition().Risk);
        Assert.Empty(JsonSchemaValidator.Validate(KnowledgeTools.SaveRecipeDefinition().InputSchema, JsonNode.Parse("""{"name":"Door","description":"door","steps":[{"command":"LINE","inputs":["0,0","@3',0",""],"note":"leaf"}]}""")!));
        Assert.NotEmpty(JsonSchemaValidator.Validate(KnowledgeTools.SaveRecipeDefinition().InputSchema, JsonNode.Parse("""{"name":"Door","description":"d","steps":[]}""")!));
    }
}

public sealed class KnowledgeDigestTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Empty_knowledge_gives_a_short_note_with_the_folder()
    {
        var store = new KnowledgeStore(Path.Combine(_dir.Path, "knowledge"));
        var digest = KnowledgeDigest.Build(store);
        Assert.StartsWith(KnowledgeDigest.Heading, digest, StringComparison.Ordinal);
        Assert.Contains("Nothing learned yet", digest, StringComparison.Ordinal);
        Assert.Contains(store.Directory, digest, StringComparison.Ordinal);
        Assert.True(digest.Length < 500);
    }

    [Fact]
    public void Rules_and_style_in_full_recipes_and_lessons_by_title()
    {
        var store = new KnowledgeStore(Path.Combine(_dir.Path, "knowledge"), () => new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));
        store.RememberRule("Toilets at least 5'x7' clear", "planning");
        store.RememberRule("Dimensions outside the walls", "dimensions");
        store.SaveLesson("ARC with the A option asked for an angle", "SECRET-FIX-TEXT");
        store.SaveRecipe("Dog-legged stair", "two flights", JsonNode.Parse("""[{"command":"PLINE","inputs":["SECRET-STEP"]}]""")!.AsArray(), null);
        store.MergeStyle(new StyleProfile { Sections = [(StyleProfile.LayersSection, ["A-WALL: colour 7, Continuous, 0.5 mm (10 objects)"])] });

        var digest = KnowledgeDigest.Build(store.Read());
        Assert.Contains("[Planning and room sizes] Toilets at least 5'x7' clear (2026-11-19)", digest, StringComparison.Ordinal);
        Assert.Contains("[Dimensions] Dimensions outside the walls", digest, StringComparison.Ordinal);
        Assert.Contains("Layers: A-WALL: colour 7", digest, StringComparison.Ordinal);
        Assert.Contains("Dog-legged stair", digest, StringComparison.Ordinal);
        Assert.Contains("ARC with the A option asked for an angle", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-FIX-TEXT", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-STEP", digest, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_hand_edited_knowledge_base_is_capped()
    {
        var store = new KnowledgeStore(Path.Combine(_dir.Path, "knowledge"));
        Directory.CreateDirectory(store.RecipesDirectory);
        File.WriteAllText(store.RulesPath, "# Rules\n\n## General\n\n" + string.Concat(Enumerable.Range(0, 300).Select(i => $"- Hand rule {i} {new string('r', 80)}\n")));
        File.WriteAllText(store.StylePath, "# Drawing style\n\n## Layers\n\n" + string.Concat(Enumerable.Range(0, 300).Select(i => $"- L{i}: {new string('s', 80)}\n")));
        File.WriteAllText(store.LessonsPath, "# Lessons\n\n" + string.Concat(Enumerable.Range(0, 300).Select(i => $"- Mistake {i} {new string('m', 200)} | Fix: f\n")));
        for (var i = 0; i < 80; i++)
        {
            File.WriteAllText(Path.Combine(store.RecipesDirectory, $"r{i}.md"), $"# Recipe number {i} with a long name\n\nd\n");
        }

        var digest = KnowledgeDigest.Build(store);
        Assert.True(digest.Length <= KnowledgeDigest.MaxCharacters, digest.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("RULES", digest, StringComparison.Ordinal);
        Assert.Contains("STYLE", digest, StringComparison.Ordinal);
        Assert.Contains("RECIPES", digest, StringComparison.Ordinal);
        Assert.Contains("LESSONS", digest, StringComparison.Ordinal);
        Assert.Contains("recall_knowledge", digest, StringComparison.Ordinal);
    }
}

public sealed class InstructionTests
{
    [Fact]
    public void Default_instructions_are_for_plain_AutoCAD_and_leave_room_for_the_learned_section()
    {
        Assert.True(CopilotInstructions.Default.Length < 15_500, $"{CopilotInstructions.Default.Length} characters");
        Assert.Contains("AutoCAD 2027", CopilotInstructions.Default, StringComparison.Ordinal);
        Assert.DoesNotContain("Architecture", CopilotInstructions.Default, StringComparison.Ordinal);
        foreach (var command in new[] { "WALLADD", "DOORADD", "WINDOWADD" })
        {
            Assert.DoesNotContain(command, CopilotInstructions.Default, StringComparison.Ordinal);
            Assert.DoesNotContain(command, CopilotInstructions.Compose("AutoCAD 2027", null), StringComparison.Ordinal);
            Assert.DoesNotContain(command, AcadCopilot.Core.Drafting.DraftingTools.RunCommandsDescription, StringComparison.Ordinal);
        }

        foreach (var tool in new[] { "recall_knowledge", "remember_rule", "save_recipe", "save_lesson", "learn_style_from_drawing", "forget", "Learn my style from this drawing?" })
        {
            Assert.Contains(tool, CopilotInstructions.Default, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Architecture_note_only_for_AutoCAD_Architecture()
    {
        Assert.Contains(CopilotInstructions.ArchitectureNote, CopilotInstructions.Compose("AutoCAD Architecture 2027", null), StringComparison.Ordinal);
        Assert.Contains(CopilotInstructions.ArchitectureNote, CopilotInstructions.Compose("AutoCAD MEP 2027", null), StringComparison.Ordinal);
        Assert.Equal(CopilotInstructions.Default, CopilotInstructions.Compose(null, null));
        Assert.Equal(CopilotInstructions.Default, CopilotInstructions.Compose("Civil 3D 2027", " "));
    }

    [Fact]
    public void Composed_instructions_stay_under_the_cap()
    {
        var learned = KnowledgeDigest.Heading + "\n" + new string('k', CopilotInstructions.MaxComposedLength); // more than ever fits
        var composed = CopilotInstructions.Compose("AutoCAD Architecture 2027", learned);
        Assert.True(composed.Length <= CopilotInstructions.MaxComposedLength);
        Assert.EndsWith("...", composed, StringComparison.Ordinal);
        Assert.Contains(KnowledgeDigest.Heading, composed, StringComparison.Ordinal);
        Assert.True(CopilotInstructions.Default.Length + KnowledgeDigest.MaxCharacters + CopilotInstructions.ArchitectureNote.Length + 4 <= CopilotInstructions.MaxComposedLength);
    }

    [Theory]
    [InlineData(new string[0], null, false, "AutoCAD 2027")]
    [InlineData(new[] { "acad.exe", "/product", "ACA", "/language", "en-US" }, null, false, "AutoCAD Architecture 2027")]
    [InlineData(new[] { "acad.exe", "/product", "\"MEP\"" }, null, false, "AutoCAD MEP 2027")]
    [InlineData(new[] { "acad.exe", "/product", "C3D" }, null, false, "Civil 3D 2027")]
    [InlineData(new[] { "acad.exe" }, "AutoCAD", false, "AutoCAD 2027")]
    [InlineData(new[] { "acad.exe" }, "AutoCAD Architecture", false, "AutoCAD Architecture 2027")]
    [InlineData(new[] { "acad.exe" }, null, true, "AutoCAD Architecture 2027")]
    [InlineData(new[] { "acad.exe", "/product", "XYZ" }, null, false, "AutoCAD (XYZ) 2027")]
    public void Product_is_plain_AutoCAD_unless_a_vertical_is_seen(string[] args, string? host, bool aec, string expected)
    {
        var name = AcadProduct.Describe(args, host, aec, "2027");
        Assert.Equal(expected, name);
        Assert.Equal(expected.StartsWith("AutoCAD Architecture", StringComparison.Ordinal) || expected.StartsWith("AutoCAD MEP", StringComparison.Ordinal), AcadProduct.HasArchitectureObjects(name));
    }
}
