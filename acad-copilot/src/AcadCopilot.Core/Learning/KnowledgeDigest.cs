using System.Text;

namespace AcadCopilot.Core.Learning;

/// <summary>
/// The compact "WHAT YOU HAVE LEARNED" section appended to the system instructions of every CLI turn: all of rules.md,
/// the learned style, and the titles of the recipes and lessons (their details come from recall_knowledge). Capped at
/// <see cref="MaxCharacters"/>, because Codex receives the instructions on its command line (Windows limit 32,767).
/// </summary>
public static class KnowledgeDigest
{
    /// <summary>Hard limit of the whole section, in characters.</summary>
    public const int MaxCharacters = 4_000;

    /// <summary>Heading of the section.</summary>
    public const string Heading = "WHAT YOU HAVE LEARNED";

    // Budgets (sum 3,500) leave about 500 characters for the heading lines and the folder path.
    private const int MaxRulesCharacters = 2_000;
    private const int MaxStyleCharacters = 800;
    private const int MaxRecipesCharacters = 300;
    private const int MaxLessonsCharacters = 400;
    private const string MoreRules = "(Not all rules fit here: call recall_knowledge with the subject before drawing it.)\n";
    private const int MaxLessonTitle = 110;

    /// <summary>Reads <paramref name="store"/> and builds the section; never throws (a broken folder gives a short note).</summary>
    public static string Build(KnowledgeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return Build(store.Read());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Heading + "\nThe knowledge files in " + store.Directory + " could not be read (" + ex.Message + "). Use recall_knowledge.";
        }
    }

    /// <summary>Builds the section from <paramref name="snapshot"/> (at most <see cref="MaxCharacters"/>).</summary>
    public static string Build(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        text.Append(Heading).Append('\n');
        text.Append("Local knowledge files in ").Append(snapshot.Directory)
            .Append(" (rules.md, style.md, lessons.md, recipes\\), kept by you with remember_rule, save_recipe, save_lesson and learn_style_from_drawing; the user can open and edit them. ");
        if (snapshot.IsEmpty)
        {
            text.Append("Nothing learned yet.");
            return text.ToString();
        }

        text.Append("Follow these rules and this style unless the user asks otherwise now; when two rules disagree, the newer date wins.\n");

        var rules = snapshot.Rules.AllBullets.ToList();
        if (rules.Count > 0)
        {
            var block = new StringBuilder("RULES (rules.md)\n");
            var shown = 0;
            foreach (var group in rules.GroupBy(r => r.Section.Title ?? "General"))
            {
                var line = new StringBuilder("[").Append(group.Key).Append("] ");
                line.Append(string.Join(" | ", group.Select(r => r.Line.Text + (r.Line.Date is null ? string.Empty : " (" + r.Line.Date + ")"))));
                var candidate = line.ToString();
                if (block.Length + candidate.Length + 1 > MaxRulesCharacters - MoreRules.Length)
                {
                    // Keep the start of this group.
                    var room = MaxRulesCharacters - MoreRules.Length - block.Length - 1;
                    if (room > 80)
                    {
                        block.Append(KnowledgeText.Truncate(candidate, room)).Append('\n');
                    }

                    break;
                }

                block.Append(candidate).Append('\n');
                shown += group.Count();
            }

            if (shown < rules.Count)
            {
                block.Append(MoreRules);
            }

            text.Append(block);
        }

        var style = snapshot.Style.Sections
            .Where(s => s.Title is not null && !string.Equals(s.Title, KnowledgeStore.StyleSourcesSection, StringComparison.OrdinalIgnoreCase))
            .Select(s => (s.Title, Lines: s.Lines.Where(l => l.IsBullet && l.Text.Length > 0).Select(l => l.Text).ToList()))
            .Where(s => s.Lines.Count > 0)
            .ToList();
        if (style.Count > 0)
        {
            var block = new StringBuilder("STYLE (style.md)\n");
            foreach (var (title, lines) in style)
            {
                block.Append(title).Append(": ").Append(string.Join("; ", lines)).Append('\n');
            }

            text.Append(Cap(block.ToString(), MaxStyleCharacters, "(style.md is longer: recall_knowledge \"style\")\n"));
        }

        if (snapshot.Recipes.Count > 0)
        {
            var titles = string.Join("; ", snapshot.Recipes.Select(r => r.Title));
            text.Append(Cap("RECIPES (recall_knowledge \"<name>\" gives the steps): " + titles + "\n", MaxRecipesCharacters, "...\n"));
        }

        var lessons = snapshot.Lessons.AllBullets.ToList();
        if (lessons.Count > 0)
        {
            var titles = string.Join("; ", lessons.Select(l => KnowledgeText.Truncate(KnowledgeStore.LessonMistake(l.Line.Text), MaxLessonTitle)));
            text.Append(Cap("LESSONS (mistakes not to repeat; recall_knowledge gives the fixes): " + titles + "\n", MaxLessonsCharacters, "...\n"));
        }

        var result = text.ToString().TrimEnd();
        return result.Length <= MaxCharacters ? result : KnowledgeText.Truncate(result, MaxCharacters);
    }

    private static string Cap(string block, int max, string more) =>
        block.Length <= max ? block : KnowledgeText.Truncate(block.TrimEnd(), max - more.Length) + "\n" + more;
}
