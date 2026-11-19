using System.Text.Json.Nodes;
using AcadCopilot.Core.Learning;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Learning;

public sealed class KnowledgeTextTests
{
    [Fact]
    public void Tokens_are_lower_case_singular_and_map_synonyms()
    {
        Assert.Equal(["toilet", "5", "x7", "door"], KnowledgeText.Tokens("The Bathrooms 5'x7' doors"));
        Assert.Equal(["stair", "landing"], KnowledgeText.Tokens("Staircase landings"));
        Assert.Equal(["glass"], KnowledgeText.Tokens("glass"));
    }

    [Fact]
    public void Subject_words_ignore_numbers_and_stop_words()
    {
        Assert.Equal(KnowledgeText.SubjectWords("Toilets 5'x7' minimum"), KnowledgeText.SubjectWords("toilet 5'x8' min"));
        Assert.Equal(["toilet"], KnowledgeText.SubjectWords("Toilets 5'x7' minimum"));
    }

    [Theory]
    [InlineData("Dog-legged stair (G+1)", "dog-legged-stair-g-1")]
    [InlineData("  ", "recipe")]
    [InlineData("Door with swing / 3'0\"", "door-with-swing-3-0")]
    [InlineData("Café", "cafe")]
    public void Slugs_are_safe_file_names(string name, string slug) => Assert.Equal(slug, KnowledgeText.Slug(name));

    [Fact]
    public void One_line_collapses_whitespace()
    {
        Assert.Equal("a b c", KnowledgeText.OneLine("  a\r\n b\t\tc "));
        Assert.Equal(string.Empty, KnowledgeText.OneLine(null));
    }
}

public sealed class KnowledgeDocumentTests
{
    [Fact]
    public void Round_trip_keeps_the_users_own_lines()
    {
        var text = "# Rules\n\nIntro line.\n\n## Planning\n\n- Toilets 5'x7' minimum. (2026-11-19)\n- A long rule\n  continued here\nA note the user wrote.\n\n## Mine\n\n* star bullet\n";
        var document = KnowledgeDocument.Parse(text);
        Assert.Equal("Rules", document.Title);
        Assert.Equal(["Intro line."], document.Intro);
        var planning = document.Find("planning")!;
        Assert.Equal(["Toilets 5'x7' minimum.", "A long rule continued here"], planning.Bullets.Select(b => b.Text));
        Assert.Equal("2026-11-19", planning.Bullets.First().Date);
        Assert.Contains(planning.Lines, l => !l.IsBullet && l.Text == "A note the user wrote.");
        Assert.Equal("star bullet", Assert.Single(document.Find("Mine")!.Bullets).Text);

        var again = KnowledgeDocument.Parse(document.Render());
        Assert.Equal(document.Render(), again.Render());
        Assert.Contains("- Toilets 5'x7' minimum. (2026-11-19)", document.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bullets_before_any_heading_form_an_untitled_section()
    {
        var document = KnowledgeDocument.Parse("# Lessons\n\nExplanation.\n\n- one (2026-01-02)\n- two\n");
        var section = Assert.Single(document.Sections);
        Assert.Null(section.Title);
        Assert.Equal(2, section.Bullets.Count());
    }
}

public sealed class KnowledgeStoreTests : IDisposable
{
    private readonly TestDirectory _dir = new();
    private DateTimeOffset _now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private KnowledgeStore Store() => new(Path.Combine(_dir.Path, "knowledge"), () => _now);

    // Rules on unrelated subjects (letters only, so no two share subject words): 12 -> "Use bcq bcr bcs".
    private static string UniqueRule(int i)
    {
        var letters = new string([.. i.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => (char)('b' + (c - '0')))]);
        return $"Use {letters}q {letters}r {letters}s";
    }

    [Fact]
    public void Starter_files_explain_themselves_and_are_never_overwritten()
    {
        var store = Store();
        store.EnsureCreated();
        Assert.True(Directory.Exists(store.RecipesDirectory));
        Assert.Contains("Edit freely", File.ReadAllText(store.RulesPath), StringComparison.Ordinal);
        Assert.Contains("## My notes", File.ReadAllText(store.StylePath), StringComparison.Ordinal);
        Assert.True(store.Read().IsEmpty);

        File.WriteAllText(store.RulesPath, "# Rules\n\n- my own rule\n");
        store.EnsureCreated();
        Assert.Equal("# Rules\n\n- my own rule\n", File.ReadAllText(store.RulesPath));
        Assert.False(store.Read().IsEmpty);
    }

    [Fact]
    public void Rules_go_under_their_category_newest_first_with_a_date()
    {
        var store = Store();
        store.RememberRule("Dimensions outside the walls.", "dimensions");
        _now = _now.AddDays(1);
        store.RememberRule("Two dimension chains on the road sides.", "dimensions");
        store.RememberRule("Toilets at least 5'x7'.", "planning");

        var rules = store.Read().Rules;
        Assert.Equal(["Two dimension chains on the road sides.", "Dimensions outside the walls."], rules.Find("Dimensions")!.Bullets.Select(b => b.Text));
        Assert.Equal("2026-11-20", rules.Find("Dimensions")!.Bullets.First().Date);
        Assert.Single(rules.Find("Planning and room sizes")!.Bullets);
        Assert.Contains("- Toilets at least 5'x7'. (2026-11-20)", File.ReadAllText(store.RulesPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_rules_are_not_duplicated_and_the_newest_value_wins()
    {
        var store = Store();
        store.RememberRule("Toilets 5'x7' minimum", "planning");
        var again = store.RememberRule("toilets 5'x7' minimum", "planning");
        Assert.Single(again.Replaced);
        Assert.Single(store.Read().Rules.AllBullets);

        var changed = store.RememberRule("Toilets 5'x8' minimum", "planning");
        Assert.Equal(["toilets 5'x7' minimum"], changed.Replaced);
        Assert.Contains("Replaced", changed.Message, StringComparison.Ordinal);
        Assert.Equal("Toilets 5'x8' minimum", Assert.Single(store.Read().Rules.AllBullets).Line.Text);

        // A different subject is kept.
        store.RememberRule("Toilets on outside walls with a ventilator", "planning");
        Assert.Equal(2, store.Read().Rules.AllBullets.Count());
    }

    [Fact]
    public void Rules_are_capped_by_dropping_the_oldest_dated_ones_but_never_the_users_own()
    {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.RulesPath, "# Rules\n\n## General\n\n- Hand written rule without a date\n");
        for (var i = 0; i < KnowledgeStore.MaxRules + 10; i++)
        {
            _now = _now.AddDays(1);
            store.RememberRule(UniqueRule(i), "general");
        }

        var bullets = store.Read().Rules.AllBullets.Select(b => b.Line.Text).ToList();
        Assert.True(bullets.Count <= KnowledgeStore.MaxRules);
        Assert.True(bullets.Sum(b => b.Length + 16) <= KnowledgeStore.MaxRulesCharacters);
        Assert.Contains("Hand written rule without a date", bullets);
        Assert.Contains(UniqueRule(KnowledgeStore.MaxRules + 9), bullets);
        Assert.DoesNotContain(UniqueRule(0), bullets);
    }

    [Fact]
    public void Rules_are_shortened_and_kept_on_one_line()
    {
        var store = Store();
        store.RememberRule(new string('x', 50) + "\n" + new string('y', 400), null);
        var rule = Assert.Single(store.Read().Rules.AllBullets);
        Assert.Equal("General", rule.Section.Title);
        Assert.Equal(KnowledgeStore.MaxRuleLength, rule.Line.Text.Length);
        Assert.DoesNotContain('\n', rule.Line.Text);
        Assert.Throws<ArgumentException>(() => store.RememberRule("   ", null));
    }

    [Fact]
    public void Lessons_are_newest_first_and_the_same_mistake_is_replaced()
    {
        var store = Store();
        store.SaveLesson("ARC with A option after the centre asked for an end point", "Type the included angle after A");
        _now = _now.AddDays(1);
        store.SaveLesson("SYSMON notices in the transcript looked like errors", "They are not errors; ignore them");
        var write = store.SaveLesson("ARC with A option after the centre point asked for an end point", "Give C, centre, start point, then A and the angle");

        var lessons = store.Read().Lessons.AllBullets.Select(b => b.Line.Text).ToList();
        Assert.Equal(2, lessons.Count);
        Assert.StartsWith("ARC with A option after the centre point", lessons[0], StringComparison.Ordinal);
        Assert.Contains(" | Fix: Give C, centre", lessons[0], StringComparison.Ordinal);
        Assert.Single(write.Replaced);
        Assert.Equal("ARC with A option after the centre point asked for an end point", KnowledgeStore.LessonMistake(lessons[0]));
    }

    [Fact]
    public void Recipes_are_saved_as_markdown_with_their_steps_and_read_back()
    {
        var store = Store();
        var steps = JsonNode.Parse("""[{"command":"LINE","inputs":["3'1.5,10'4.5","@0,3'",""],"note":"leaf"},{"command":"ARC","inputs":["C","3'1.5,10'4.5","@0,3'","A","-90"]}]""")!.AsArray();
        var write = store.SaveRecipe("Door with swing", "3' door, hinge at the first point.", steps, "Mirror for the other hand.");
        Assert.EndsWith("door-with-swing.md", write.File, StringComparison.Ordinal);
        var text = File.ReadAllText(write.File);
        Assert.Contains("# Door with swing", text, StringComparison.Ordinal);
        Assert.Contains("```json", text, StringComparison.Ordinal);

        var recipe = Assert.Single(store.Read().Recipes);
        Assert.Equal("Door with swing", recipe.Title);
        Assert.Equal("3' door, hinge at the first point.", recipe.Description);
        Assert.Equal("2026-11-19", recipe.Date);
        Assert.Equal("Mirror for the other hand.", recipe.Notes);
        var parsed = JsonNode.Parse(recipe.Steps)!.AsArray();
        Assert.Equal(2, parsed.Count);
        Assert.Equal("3'1.5,10'4.5", parsed[0]!["inputs"]![0]!.GetValue<string>());

        // Same name overwrites (newest wins).
        var update = store.SaveRecipe("door with swing", "Updated.", steps, null);
        Assert.Contains("updated", update.Message, StringComparison.Ordinal);
        Assert.Equal("Updated.", Assert.Single(store.Read().Recipes).Description);
    }

    [Fact]
    public void Oversized_recipes_are_refused_and_old_recipes_are_capped()
    {
        var store = Store();
        var big = new JsonArray([.. Enumerable.Range(0, 30).Select(_ => (JsonNode)new JsonObject { ["command"] = "LINE", ["inputs"] = new JsonArray([.. Enumerable.Range(0, 60).Select(_ => (JsonNode)new string('1', 20))]) })]);
        Assert.Throws<ArgumentException>(() => store.SaveRecipe("Huge", "too big", big, null));

        var small = JsonNode.Parse("""[{"command":"LINE"}]""")!.AsArray();
        for (var i = 0; i < KnowledgeStore.MaxRecipes + 2; i++)
        {
            store.SaveRecipe("Recipe " + i, "d", small, null);
            File.SetLastWriteTimeUtc(Path.Combine(store.RecipesDirectory, "recipe-" + i + ".md"), DateTime.UtcNow.AddMinutes(i - 100));
        }

        var recipes = store.Read().Recipes;
        Assert.Equal(KnowledgeStore.MaxRecipes, recipes.Count);
        Assert.DoesNotContain(recipes, r => r.Slug == "recipe-0");
    }

    [Fact]
    public void Forget_removes_one_matching_item_and_explains_ambiguity()
    {
        var store = Store();
        store.RememberRule("Toilets 5'x7' minimum", "planning");
        store.RememberRule("Dimensions outside the walls", "dimensions");
        store.RememberRule("Room names 9 inch high", "text");
        store.SaveLesson("ARC A option", "angle");
        store.SaveRecipe("Dog-legged stair", "stair", JsonNode.Parse("""[{"command":"PLINE"}]""")!.AsArray(), null);

        var ambiguous = Assert.Throws<ArgumentException>(() => store.Forget("rule", "o"));
        Assert.Contains("Several", ambiguous.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => store.Forget("rule", "nothing like this"));

        store.Forget("rule", "outside the walls");
        Assert.DoesNotContain(store.Read().Rules.AllBullets, b => b.Line.Text.Contains("outside", StringComparison.Ordinal));
        Assert.Null(store.Read().Rules.Find("Dimensions"));

        store.Forget("lesson", "ARC A option");
        Assert.Empty(store.Read().Lessons.AllBullets);
        store.Forget("recipe", "dog-legged stair");
        Assert.Empty(store.Read().Recipes);
        Assert.Throws<ArgumentException>(() => store.Forget("bogus", "x"));
    }

    [Fact]
    public void Learned_style_replaces_learned_sections_merges_layers_and_keeps_my_notes()
    {
        var store = Store();
        store.EnsureCreated();
        var notes = File.ReadAllText(store.StylePath).Replace("(Write your own style notes here, one per line starting with \"- \".)", "- Door tags in circles", StringComparison.Ordinal);
        File.WriteAllText(store.StylePath, notes);

        store.MergeStyle(new StyleProfile
        {
            Source = "Old.dwg",
            Sections =
            [
                (StyleProfile.LayersSection, ["A-WALL: colour 7, Continuous, 0.5 mm (10 objects)", "A-DOOR: colour 4, Continuous, default weight (4 objects)"]),
                (StyleProfile.TextSection, ["Style ARCH: heights 9\" x4"]),
            ],
        });
        _now = _now.AddDays(1);
        var write = store.MergeStyle(new StyleProfile
        {
            Source = "New.dwg",
            Sections =
            [
                (StyleProfile.LayersSection, ["A-WALL: colour 1, Continuous, 0.7 mm (20 objects)"]),
                (StyleProfile.TextSection, ["Style ROMANS: heights 6\" x9"]),
            ],
        });

        Assert.Contains("Layers", write.Replaced);
        var style = store.Read().Style;
        Assert.Equal(["A-WALL: colour 1, Continuous, 0.7 mm (20 objects)", "A-DOOR: colour 4, Continuous, default weight (4 objects)"], style.Find("Layers")!.Bullets.Select(b => b.Text));
        Assert.Equal(["Style ROMANS: heights 6\" x9"], style.Find("Text")!.Bullets.Select(b => b.Text));
        Assert.Equal(["New.dwg", "Old.dwg"], style.Find(KnowledgeStore.StyleSourcesSection)!.Bullets.Select(b => b.Text));
        Assert.Equal("- Door tags in circles", style.Sections[^1].Lines.Single().Render());
        Assert.Equal(KnowledgeStore.StyleNotesSection, style.Sections[^1].Title);
    }

    [Fact]
    public void Learned_style_is_capped()
    {
        var store = Store();
        var lines = Enumerable.Range(0, 200).Select(i => $"Line {i} " + new string('z', 60)).ToList();
        var write = store.MergeStyle(new StyleProfile { Sections = [(StyleProfile.BlocksSection, lines)] });
        Assert.Contains("left out", write.Message, StringComparison.Ordinal);
        var learned = store.Read().Style.Sections.Where(s => s.Title != KnowledgeStore.StyleNotesSection).Sum(s => (s.Title?.Length ?? 0) + s.Lines.Sum(l => l.Text.Length + 4));
        Assert.True(learned <= KnowledgeStore.MaxStyleCharacters, learned.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Parallel_writers_do_not_lose_rules()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => new KnowledgeStore(store.Directory).RememberRule(UniqueRule(i), "general"))));
        Assert.Equal(12, store.Read().Rules.AllBullets.Count());
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));
    }
}
