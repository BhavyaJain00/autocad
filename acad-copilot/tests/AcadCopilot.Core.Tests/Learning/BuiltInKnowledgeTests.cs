using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Services;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests.Learning;

public sealed class BuiltInKnowledgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", "builtin-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_recipes_are_embedded_and_parse_as_recipes_with_valid_run_commands_steps()
    {
        var slugs = BuiltInKnowledge.Recipes.Select(r => r.Slug).ToList();
        Assert.Equal(new[] { "colour-presentation-elevation", "electrical-layout-first-draft", "plumbing-layout-first-draft" }, slugs);
        var runCommands = DraftingTools.RunCommandsDefinition();
        foreach (var builtIn in BuiltInKnowledge.Recipes)
        {
            var recipe = KnowledgeStore.ParseRecipe(builtIn.Slug, builtIn.Markdown);
            Assert.Equal("2026-11-19", recipe.Date);
            Assert.False(string.IsNullOrWhiteSpace(recipe.Description));
            Assert.False(string.IsNullOrWhiteSpace(recipe.Notes));
            var steps = Assert.IsType<JsonArray>(JsonNode.Parse(recipe.Steps));
            Assert.NotEmpty(steps);
            Assert.Empty(JsonSchemaValidator.Validate(runCommands.InputSchema, new JsonObject { ["title"] = recipe.Title, ["steps"] = steps.DeepClone() }));
            foreach (var step in steps)
            {
                var command = step!["command"]!.GetValue<string>();
                var inputs = step["inputs"]?.AsArray().Select(i => i!.GetValue<string>()).ToList() ?? [];
                var verdict = CommandPolicy.ClassifyStep(command, inputs);
                Assert.True(verdict.IsAllowed, $"{builtIn.Slug}: {command} {string.Join(" ", inputs)} -> {verdict.Describe()}");
            }
        }

        var electrical = BuiltInKnowledge.Recipes.Single(r => r.Slug.StartsWith("electrical", StringComparison.Ordinal)).Markdown;
        var plumbing = BuiltInKnowledge.Recipes.Single(r => r.Slug.StartsWith("plumbing", StringComparison.Ordinal)).Markdown;
        Assert.Contains(SymbolLibrary.DraftNote(ServiceDiscipline.Electrical), electrical, StringComparison.Ordinal);
        Assert.Contains(SymbolLibrary.DraftNote(ServiceDiscipline.Plumbing), plumbing, StringComparison.Ordinal);
        Assert.Contains("4'-0\"", electrical, StringComparison.Ordinal);
        Assert.Contains("1'-0\"", electrical, StringComparison.Ordinal);

        // Every block a recipe inserts exists in the library.
        foreach (var markdown in new[] { electrical, plumbing })
        {
            var steps = JsonNode.Parse(KnowledgeStore.ParseRecipe("x", markdown).Steps)!.AsArray();
            foreach (var step in steps.Where(s => s!["command"]!.GetValue<string>() == "-INSERT"))
            {
                Assert.NotNull(SymbolLibrary.Find(step!["inputs"]![0]!.GetValue<string>()));
            }
        }
    }

    [Fact]
    public void Seeding_writes_once_and_respects_edits_and_deletions()
    {
        var first = BuiltInKnowledge.Seed(_directory);
        Assert.Equal(3, first.Written.Count);
        var recipes = Path.Combine(_directory, "recipes");
        Assert.True(File.Exists(Path.Combine(recipes, BuiltInKnowledge.MarkerFileName)));

        var again = BuiltInKnowledge.Seed(_directory);
        Assert.Empty(again.Written);
        Assert.Equal(3, again.Kept.Count);

        var edited = Path.Combine(recipes, "electrical-layout-first-draft.md");
        File.AppendAllText(edited, "\r\n- My own rule: sockets at 1'-6\".\r\n");
        var deleted = Path.Combine(recipes, "plumbing-layout-first-draft.md");
        File.Delete(deleted);

        var third = BuiltInKnowledge.Seed(_directory);
        Assert.Empty(third.Written);
        Assert.Contains("My own rule", File.ReadAllText(edited), StringComparison.Ordinal);
        Assert.False(File.Exists(deleted));
    }

    [Fact]
    public void An_unedited_older_version_is_updated_but_a_users_own_file_is_never_overwritten()
    {
        var recipes = Path.Combine(_directory, "recipes");
        Directory.CreateDirectory(recipes);

        // The user already has a recipe with the same name: it stays.
        var own = Path.Combine(recipes, "colour-presentation-elevation.md");
        File.WriteAllText(own, "# My presentation\r\n");
        BuiltInKnowledge.Seed(_directory);
        Assert.Equal("# My presentation\r\n", File.ReadAllText(own));

        // An unedited copy of an older built-in version is replaced by the current one.
        var electrical = Path.Combine(recipes, "electrical-layout-first-draft.md");
        File.WriteAllText(electrical, "# Old version\r\n");
        var marker = Path.Combine(recipes, BuiltInKnowledge.MarkerFileName);
        var map = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
        map["electrical-layout-first-draft"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("# Old version\n")));
        File.WriteAllText(marker, map.ToJsonString());
        var result = BuiltInKnowledge.Seed(_directory);
        Assert.Contains("electrical-layout-first-draft", result.Written);
        Assert.StartsWith("# Electrical layout (first draft)", File.ReadAllText(electrical), StringComparison.Ordinal);
    }

    [Fact]
    public void Recall_finds_the_seeded_recipes_by_what_the_user_asks()
    {
        BuiltInKnowledge.Seed(_directory);
        var store = new KnowledgeStore(_directory);
        var snapshot = store.Read();
        Assert.Equal(3, snapshot.Recipes.Count);

        Assert.Equal("Electrical layout (first draft)", KnowledgeRecall.Search(snapshot, "electrical layout")[0].Item.Title);
        Assert.Equal("Plumbing layout (first draft)", KnowledgeRecall.Search(snapshot, "plumbing layout for the toilets")[0].Item.Title);
        Assert.Equal("Colour presentation elevation", KnowledgeRecall.Search(snapshot, "colour presentation elevation")[0].Item.Title);

        var digest = KnowledgeDigest.Build(snapshot);
        Assert.Contains("Electrical layout (first draft)", digest, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing written.
        }
    }
}
