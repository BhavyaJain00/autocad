using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Learning;

/// <summary>One recallable item of the knowledge base.</summary>
/// <param name="Kind">rule, style, recipe or lesson.</param>
/// <param name="Title">Category, style section or recipe name.</param>
/// <param name="Text">The rule / style line / lesson, or the recipe's description.</param>
/// <param name="Date">yyyy-MM-dd when known.</param>
/// <param name="Recipe">The recipe, for kind "recipe".</param>
public sealed record KnowledgeItem(string Kind, string Title, string Text, string? Date, KnowledgeRecipe? Recipe = null);

/// <summary>A recalled item with its score.</summary>
/// <param name="Item">The item.</param>
/// <param name="Score">Keyword score (higher = more relevant).</param>
public sealed record KnowledgeHit(KnowledgeItem Item, double Score);

/// <summary>Keyword search over the knowledge base (recall_knowledge). Pure, unit-tested.</summary>
public static class KnowledgeRecall
{
    /// <summary>Default number of hits.</summary>
    public const int DefaultLimit = 8;

    /// <summary>Most hits per call.</summary>
    public const int MaxLimit = 25;

    /// <summary>Every item of <paramref name="snapshot"/>, in file order (rules, style, recipes, lessons).</summary>
    public static IReadOnlyList<KnowledgeItem> Items(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var items = new List<KnowledgeItem>();
        foreach (var (section, line) in snapshot.Rules.AllBullets)
        {
            items.Add(new KnowledgeItem("rule", section.Title ?? "General", line.Text, line.Date));
        }

        foreach (var section in snapshot.Style.Sections)
        {
            if (string.Equals(section.Title, KnowledgeStore.StyleSourcesSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var line in section.Lines.Where(l => l.IsBullet && l.Text.Length > 0))
            {
                items.Add(new KnowledgeItem("style", section.Title ?? "Style", line.Text, line.Date));
            }
        }

        foreach (var recipe in snapshot.Recipes)
        {
            items.Add(new KnowledgeItem("recipe", recipe.Title, recipe.Description, recipe.Date, recipe));
        }

        foreach (var (_, line) in snapshot.Lessons.AllBullets)
        {
            items.Add(new KnowledgeItem("lesson", "Lesson", line.Text, line.Date));
        }

        return items;
    }

    /// <summary>
    /// The items that best match <paramref name="query"/>: each query word found in the item's title counts 3, in its
    /// text (recipes: also the commands of their steps) 2, a word sharing its first 4+ letters 1; rules get a small bonus
    /// (they must be followed), newer items win ties. Items scoring 0 are left out. <paramref name="kinds"/> filters
    /// (null = all).
    /// </summary>
    public static IReadOnlyList<KnowledgeHit> Search(KnowledgeSnapshot snapshot, string? query, int limit = DefaultLimit, IReadOnlyCollection<string>? kinds = null)
    {
        var words = KnowledgeText.Tokens(query).Distinct(StringComparer.Ordinal).ToList();
        limit = Math.Clamp(limit, 1, MaxLimit);
        var hits = new List<KnowledgeHit>();
        foreach (var item in Items(snapshot))
        {
            if (kinds is { Count: > 0 } && !kinds.Contains(item.Kind))
            {
                continue;
            }

            var score = Score(item, words);
            if (score > 0)
            {
                hits.Add(new KnowledgeHit(item, score));
            }
        }

        return [.. hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Item.Date ?? string.Empty, StringComparer.Ordinal)
            .Take(limit)];
    }

    /// <summary>Score of one item for the query words (0 = no match).</summary>
    public static double Score(KnowledgeItem item, IReadOnlyList<string> queryWords)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(queryWords);
        if (queryWords.Count == 0)
        {
            return 0;
        }

        var title = new HashSet<string>(KnowledgeText.Tokens(item.Title), StringComparer.Ordinal);
        var bodyText = item.Recipe is null ? item.Text : item.Text + " " + RecipeCommands(item.Recipe) + " " + item.Recipe.Notes;
        var body = new HashSet<string>(KnowledgeText.Tokens(bodyText), StringComparer.Ordinal);
        double score = 0;
        foreach (var word in queryWords)
        {
            if (title.Contains(word))
            {
                score += 3;
            }
            else if (body.Contains(word))
            {
                score += 2;
            }
            else if (word.Length >= 4 && title.Concat(body).Any(t => t.Length >= 4 && (t.StartsWith(word[..4], StringComparison.Ordinal) || word.StartsWith(t[..4], StringComparison.Ordinal))))
            {
                score += 1;
            }
        }

        if (score > 0 && item.Kind == "rule")
        {
            score += 0.5;
        }

        return score;
    }

    /// <summary>The command names of a recipe's steps ("PLINE OFFSET -ARRAY").</summary>
    public static string RecipeCommands(KnowledgeRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        try
        {
            if (JsonNode.Parse(recipe.Steps) is JsonArray steps)
            {
                return string.Join(' ', steps.Select(s => s?["command"]?.GetValue<string>()).Where(c => c is not null));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // A hand-edited recipe: no commands to add.
        }

        return string.Empty;
    }
}
