using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Learning;

/// <summary>What a write to the knowledge base did.</summary>
/// <param name="Message">One line for the AI ("Remembered ... Replaced the older rule ...").</param>
/// <param name="Replaced">Items replaced by the new one (same subject: newest wins) or refreshed duplicates.</param>
/// <param name="Dropped">Oldest items removed to keep the file small.</param>
/// <param name="File">The file written.</param>
public sealed record KnowledgeWrite(string Message, IReadOnlyList<string> Replaced, IReadOnlyList<string> Dropped, string File);

/// <summary>A saved recipe (recipes\&lt;slug&gt;.md).</summary>
/// <param name="Slug">File name without .md (also its id for recall and forget).</param>
/// <param name="Title">Recipe name ("Dog-legged stair").</param>
/// <param name="Description">What it draws and what the numbers mean.</param>
/// <param name="Date">Saved on (yyyy-MM-dd), if known.</param>
/// <param name="Steps">The run_commands steps (JSON array text).</param>
/// <param name="Notes">Optional notes.</param>
public sealed record KnowledgeRecipe(string Slug, string Title, string Description, string? Date, string Steps, string? Notes);

/// <summary>Everything in the knowledge folder, read once.</summary>
/// <param name="Directory">The knowledge folder.</param>
/// <param name="Rules">rules.md.</param>
/// <param name="Style">style.md.</param>
/// <param name="Lessons">lessons.md.</param>
/// <param name="Recipes">recipes\*.md, newest first.</param>
public sealed record KnowledgeSnapshot(
    string Directory,
    KnowledgeDocument Rules,
    KnowledgeDocument Style,
    KnowledgeDocument Lessons,
    IReadOnlyList<KnowledgeRecipe> Recipes)
{
    /// <summary>True when nothing has been learned yet (no "- " items anywhere, no recipes: only the starter text).</summary>
    public bool IsEmpty =>
        !Rules.AllBullets.Any() && !Lessons.AllBullets.Any() && Recipes.Count == 0 && !Style.AllBullets.Any();
}

/// <summary>
/// The local knowledge base the AI reads every turn and writes to (no model can be retrained: this is how the copilot
/// "learns"): %LOCALAPPDATA%\AcadCopilot\knowledge\ with rules.md, style.md, lessons.md and recipes\&lt;name&gt;.md, all
/// plain Markdown the user can open and edit. Writes are small, deduplicated (the newest item on the same subject wins),
/// capped in size, atomic (temp file + rename) and serialised across processes (every chat tab runs its own MCP bridge)
/// with a named mutex.
/// </summary>
public sealed class KnowledgeStore
{
    /// <summary>Longest rule, in characters.</summary>
    public const int MaxRuleLength = 300;

    /// <summary>Most rules kept (oldest dated rules are dropped beyond this).</summary>
    public const int MaxRules = 60;

    /// <summary>Most characters of rule text kept in rules.md (the whole file is injected every turn).</summary>
    public const int MaxRulesCharacters = 3_000;

    /// <summary>Longest lesson part (mistake or fix), in characters.</summary>
    public const int MaxLessonPartLength = 300;

    /// <summary>Most lessons kept.</summary>
    public const int MaxLessons = 40;

    /// <summary>Most recipes kept (the oldest files are deleted beyond this).</summary>
    public const int MaxRecipes = 60;

    /// <summary>Largest recipe steps JSON, in characters.</summary>
    public const int MaxRecipeStepsCharacters = 12_000;

    /// <summary>Longest recipe description, in characters.</summary>
    public const int MaxRecipeDescriptionLength = 600;

    /// <summary>Most characters of learned style kept in style.md.</summary>
    public const int MaxStyleCharacters = 2_500;

    /// <summary>style.md section that learning never overwrites.</summary>
    public const string StyleNotesSection = "My notes";

    /// <summary>style.md section listing the drawings the style was learned from.</summary>
    public const string StyleSourcesSection = "Learned from";

    private const string RulesTitle = "Rules";
    private const string StyleTitle = "Drawing style";
    private const string LessonsTitle = "Lessons";
    private const string LessonsSection = "Lessons";
    private const string FixMarker = " | Fix: ";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly string[] RulesIntro =
    [
        "Your preferences and corrections for AI Copilot. The AI reads this whole file before every reply and follows it.",
        "Edit freely: one rule per line starting with \"- \", under \"## \" headings. The date is when it was learned; when two rules",
        "disagree, the newer one wins. Delete a line to make the AI forget it.",
    ];

    private static readonly string[] StyleIntro =
    [
        "How your drawings look (layers and colours, text, dimensions, walls, doors, windows, stairs). Written by \"Learn my style",
        "from this drawing\" (the learn_style_from_drawing tool) and by you; the AI follows it when it draws. Learning again replaces",
        "the learned sections; the \"## My notes\" section is yours and is never overwritten.",
    ];

    private static readonly string[] LessonsIntro =
    [
        "Mistakes the AI made in AutoCAD and how it fixed them, newest first, so it does not repeat them.",
        "Delete any line that is wrong.",
    ];

    private readonly Func<DateTimeOffset> _clock;
    private readonly string _mutexName;

    /// <summary>Creates a store over <paramref name="directory"/> (created on the first write).</summary>
    /// <param name="directory">The knowledge folder (normally <see cref="AppPaths.Knowledge"/>).</param>
    /// <param name="clock">Current time (tests).</param>
    public KnowledgeStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _clock = clock ?? (() => DateTimeOffset.Now);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Directory.ToUpperInvariant()));
        _mutexName = @"Local\AcadCopilot.Knowledge." + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>The knowledge folder.</summary>
    public string Directory { get; }

    /// <summary>rules.md.</summary>
    public string RulesPath => Path.Combine(Directory, "rules.md");

    /// <summary>style.md.</summary>
    public string StylePath => Path.Combine(Directory, "style.md");

    /// <summary>lessons.md.</summary>
    public string LessonsPath => Path.Combine(Directory, "lessons.md");

    /// <summary>recipes\.</summary>
    public string RecipesDirectory => Path.Combine(Directory, "recipes");

    /// <summary>Creates the folder and the starter files (with their explanations) when missing. Never overwrites.</summary>
    public void EnsureCreated() => WithLock(() =>
    {
        System.IO.Directory.CreateDirectory(RecipesDirectory);
        if (!File.Exists(RulesPath))
        {
            WriteAtomically(RulesPath, NewDocument(RulesTitle, RulesIntro).Render());
        }

        if (!File.Exists(StylePath))
        {
            var style = NewDocument(StyleTitle, StyleIntro);
            style.GetOrAdd(StyleNotesSection).Lines.Add(new KnowledgeLine(false, "(Write your own style notes here, one per line starting with \"- \".)"));
            WriteAtomically(StylePath, style.Render());
        }

        if (!File.Exists(LessonsPath))
        {
            WriteAtomically(LessonsPath, NewDocument(LessonsTitle, LessonsIntro).Render());
        }

        return 0;
    });

    /// <summary>Reads everything (missing files are empty). Safe while another process writes (writes are atomic).</summary>
    public KnowledgeSnapshot Read() => new(
        Directory,
        KnowledgeDocument.Parse(ReadText(RulesPath)),
        KnowledgeDocument.Parse(ReadText(StylePath)),
        KnowledgeDocument.Parse(ReadText(LessonsPath)),
        ReadRecipes());

    /// <summary>
    /// Adds a rule under its category (newest first). An identical rule is refreshed; a rule on the same subject
    /// (same words, other numbers: "toilets 5'x7'" then "toilets 5'x8'") is replaced. The oldest dated rules are dropped
    /// beyond <see cref="MaxRules"/> / <see cref="MaxRulesCharacters"/>.
    /// </summary>
    public KnowledgeWrite RememberRule(string text, string? category)
    {
        var rule = KnowledgeText.Truncate(KnowledgeText.OneLine(text), MaxRuleLength);
        if (rule.Length == 0)
        {
            throw new ArgumentException("The rule is empty.", nameof(text));
        }

        var heading = RuleCategories.Heading(category);
        return WithLock(() =>
        {
            var document = LoadOrNew(RulesPath, RulesTitle, RulesIntro);
            var replaced = RemoveSimilar(document, rule, heading, l => l.Text);
            var section = document.GetOrAdd(heading);
            section.Lines.Insert(FirstBulletIndex(section), new KnowledgeLine(true, rule, Today));
            var dropped = DropOldest(document, MaxRules, MaxRulesCharacters, keep: rule);
            document.RemoveEmptySections();
            WriteAtomically(RulesPath, document.Render());
            var message = $"Remembered under \"{heading}\" in rules.md: {rule}" +
                (replaced.Count > 0 ? $" Replaced: {string.Join(" / ", replaced)}." : string.Empty) +
                (dropped.Count > 0 ? $" Dropped {dropped.Count} oldest rule(s) to keep the file small." : string.Empty);
            return new KnowledgeWrite(message, replaced, dropped, RulesPath);
        });
    }

    /// <summary>Adds a lesson (mistake + fix) at the top of lessons.md; a lesson about the same mistake is replaced.</summary>
    public KnowledgeWrite SaveLesson(string mistake, string fix)
    {
        var what = KnowledgeText.Truncate(KnowledgeText.OneLine(mistake), MaxLessonPartLength);
        var how = KnowledgeText.Truncate(KnowledgeText.OneLine(fix), MaxLessonPartLength);
        if (what.Length == 0 || how.Length == 0)
        {
            throw new ArgumentException("Both the mistake and the fix are needed.", what.Length == 0 ? nameof(mistake) : nameof(fix));
        }

        var text = what + FixMarker + how;
        return WithLock(() =>
        {
            var document = LoadOrNew(LessonsPath, LessonsTitle, LessonsIntro);
            var replaced = RemoveSimilar(document, what, null, l => LessonMistake(l.Text));
            var section = document.Sections.FirstOrDefault() ?? document.GetOrAdd(LessonsSection);
            section.Lines.Insert(FirstBulletIndex(section), new KnowledgeLine(true, text, Today));
            var dropped = DropOldest(document, MaxLessons, int.MaxValue, keep: text);
            document.RemoveEmptySections();
            WriteAtomically(LessonsPath, document.Render());
            var message = $"Lesson saved in lessons.md: {text}" +
                (replaced.Count > 0 ? $" Replaced: {string.Join(" / ", replaced)}." : string.Empty) +
                (dropped.Count > 0 ? $" Dropped {dropped.Count} oldest lesson(s)." : string.Empty);
            return new KnowledgeWrite(message, replaced, dropped, LessonsPath);
        });
    }

    /// <summary>
    /// Saves a recipe as recipes\&lt;slug&gt;.md; the same name overwrites (newest wins). Beyond <see cref="MaxRecipes"/>
    /// the oldest recipe files are deleted.
    /// </summary>
    /// <param name="name">Recipe name ("Dog-legged stair").</param>
    /// <param name="description">What it draws, when to use it and what the numbers mean.</param>
    /// <param name="steps">The run_commands steps that worked (array of {command, inputs, note}).</param>
    /// <param name="notes">Optional notes (pitfalls, variations).</param>
    public KnowledgeWrite SaveRecipe(string name, string description, JsonArray steps, string? notes)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var title = KnowledgeText.Truncate(KnowledgeText.OneLine(name), 80);
        var about = KnowledgeText.Truncate(KnowledgeText.OneLine(description), MaxRecipeDescriptionLength);
        var extra = KnowledgeText.Truncate(KnowledgeText.OneLine(notes), MaxRecipeDescriptionLength);
        if (title.Length == 0)
        {
            throw new ArgumentException("The recipe needs a name.", nameof(name));
        }

        if (steps.Count == 0)
        {
            throw new ArgumentException("The recipe needs at least one step.", nameof(steps));
        }

        var json = FormatSteps(steps);
        if (json.Length > MaxRecipeStepsCharacters)
        {
            throw new ArgumentException(
                $"The recipe's steps are too long ({json.Length} characters, limit {MaxRecipeStepsCharacters}). Save the essential steps only, or split it into two recipes.",
                nameof(steps));
        }

        var slug = KnowledgeText.Slug(title);
        return WithLock(() =>
        {
            System.IO.Directory.CreateDirectory(RecipesDirectory);
            var path = Path.Combine(RecipesDirectory, slug + ".md");
            var existed = File.Exists(path);
            WriteAtomically(path, RenderRecipe(title, about, Today, json, extra.Length == 0 ? null : extra));
            var dropped = new List<string>();
            var files = new DirectoryInfo(RecipesDirectory).GetFiles("*.md").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(MaxRecipes).Where(f => !string.Equals(f.FullName, path, StringComparison.OrdinalIgnoreCase)))
            {
                old.Delete();
                dropped.Add(Path.GetFileNameWithoutExtension(old.Name));
            }

            var message = $"Recipe \"{title}\" {(existed ? "updated" : "saved")} as recipes\\{slug}.md ({steps.Count} steps)." +
                (dropped.Count > 0 ? $" Deleted the oldest recipe(s): {string.Join(", ", dropped)}." : string.Empty);
            return new KnowledgeWrite(message, existed ? [slug] : [], dropped, path);
        });
    }

    /// <summary>
    /// Merges a learned style into style.md: each learned section replaces the old one of the same name (newest wins),
    /// layers are merged (new first), "My notes" and the user's own sections are kept, the drawing is added to
    /// "Learned from". The learned part is capped at <see cref="MaxStyleCharacters"/>.
    /// </summary>
    public KnowledgeWrite MergeStyle(StyleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return WithLock(() =>
        {
            var document = LoadOrNew(StylePath, StyleTitle, StyleIntro);
            var updated = new List<string>();
            foreach (var (title, lines) in profile.Sections)
            {
                if (lines.Count == 0)
                {
                    continue;
                }

                var section = document.GetOrAdd(title);
                var merged = new List<KnowledgeLine>(lines.Select(l => new KnowledgeLine(true, l)));
                if (string.Equals(title, StyleProfile.LayersSection, StringComparison.Ordinal))
                {
                    var names = new HashSet<string>(lines.Select(LayerName), StringComparer.OrdinalIgnoreCase);
                    merged.AddRange(section.Bullets.Where(b => !names.Contains(LayerName(b.Text))));
                    merged = [.. merged.Take(StyleProfile.MaxLayers)];
                }

                section.Lines.Clear();
                section.Lines.AddRange(merged);
                updated.Add(title);
            }

            if (!string.IsNullOrWhiteSpace(profile.Source))
            {
                var sources = document.GetOrAdd(StyleSourcesSection);
                var source = KnowledgeText.Truncate(KnowledgeText.OneLine(profile.Source), 120);
                sources.Lines.RemoveAll(l => l.IsBullet && string.Equals(l.Text, source, StringComparison.OrdinalIgnoreCase));
                sources.Lines.Insert(0, new KnowledgeLine(true, source, Today));
                sources.Lines.RemoveAll(l => !l.IsBullet && string.IsNullOrWhiteSpace(l.Text));
                while (sources.Lines.Count > 5)
                {
                    sources.Lines.RemoveAt(sources.Lines.Count - 1);
                }
            }

            // Keep the user's notes last and the learned part small (long layer lists shrink first).
            if (document.Find(StyleNotesSection) is { } notes)
            {
                document.Sections.Remove(notes);
                document.Sections.Add(notes);
            }

            var trimmed = TrimStyle(document);
            document.RemoveEmptySections();
            WriteAtomically(StylePath, document.Render());
            var message = updated.Count == 0
                ? "Nothing to learn: the drawing has no layers, text, dimensions, walls or blocks to take a style from."
                : $"style.md updated ({string.Join(", ", updated)})" + (trimmed > 0 ? $"; {trimmed} line(s) left out to keep it small." : ".");
            return new KnowledgeWrite(message, updated, [], StylePath);
        });
    }

    /// <summary>
    /// Removes items. <paramref name="kind"/>: rule, lesson, recipe or style. <paramref name="text"/>: the item's text or
    /// a distinctive part of it (recipe: its name or slug; style: a line, or a section name to clear it).
    /// Fails (ArgumentException with the candidates) when nothing or several different items match.
    /// </summary>
    public KnowledgeWrite Forget(string kind, string text)
    {
        var needle = KnowledgeText.OneLine(text);
        if (needle.Length == 0)
        {
            throw new ArgumentException("Say what to forget.", nameof(text));
        }

        return kind switch
        {
            "rule" => WithLock(() => ForgetBullet(RulesPath, needle, "rule")),
            "lesson" => WithLock(() => ForgetBullet(LessonsPath, needle, "lesson")),
            "style" => WithLock(() => ForgetStyle(needle)),
            "recipe" => WithLock(() => ForgetRecipe(needle)),
            _ => throw new ArgumentException($"Unknown kind '{kind}'. Use rule, lesson, recipe or style.", nameof(kind)),
        };
    }

    /// <summary>The mistake part of a lesson line ("ARC with A ... | Fix: ..." gives "ARC with A ...").</summary>
    public static string LessonMistake(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var index = line.IndexOf(FixMarker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? line : line[..index];
    }

    private string Today => KnowledgeText.Date(_clock());

    private static string LayerName(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return (colon < 0 ? line : line[..colon]).Trim();
    }

    private static KnowledgeDocument NewDocument(string title, IEnumerable<string> intro)
    {
        var document = new KnowledgeDocument { Title = title };
        document.Intro.AddRange(intro);
        return document;
    }

    private static KnowledgeDocument LoadOrNew(string path, string title, IEnumerable<string> intro)
    {
        var document = KnowledgeDocument.Parse(ReadText(path));
        if (document.Title is null && document.Intro.Count == 0)
        {
            document.Title = title;
            document.Intro.AddRange(intro);
        }

        return document;
    }

    private static int FirstBulletIndex(KnowledgeSection section)
    {
        var index = section.Lines.FindIndex(l => l.IsBullet);
        return index < 0 ? section.Lines.Count : index;
    }

    // Removes bullets that duplicate or contradict the new item (same fingerprint, or same subject words); newest wins.
    private static List<string> RemoveSimilar(KnowledgeDocument document, string text, string? heading, Func<KnowledgeLine, string> key)
    {
        var fingerprint = KnowledgeText.Fingerprint(text);
        var subject = KnowledgeText.SubjectWords(text);
        var removed = new List<string>();
        foreach (var section in document.Sections)
        {
            var sameSection = heading is null || string.Equals(section.Title, heading, StringComparison.OrdinalIgnoreCase);
            for (var i = section.Lines.Count - 1; i >= 0; i--)
            {
                var line = section.Lines[i];
                if (!line.IsBullet)
                {
                    continue;
                }

                var other = key(line);
                var duplicate = KnowledgeText.Fingerprint(other) == fingerprint;
                var otherSubject = KnowledgeText.SubjectWords(other);
                var similarity = KnowledgeText.Jaccard(subject, otherSubject);
                var sameSubject = subject.Count > 0 && similarity >= (sameSection ? 0.7 : 0.85);
                if (duplicate || sameSubject)
                {
                    section.Lines.RemoveAt(i);
                    removed.Add(line.Text);
                }
            }
        }

        return removed;
    }

    // Drops the oldest dated bullets (undated ones were written by the user and stay) beyond the limits.
    private static List<string> DropOldest(KnowledgeDocument document, int maxItems, int maxCharacters, string keep)
    {
        var dropped = new List<string>();
        while (true)
        {
            var bullets = document.AllBullets.ToList();
            var characters = bullets.Sum(b => b.Line.Text.Length + 16);
            if (bullets.Count <= maxItems && characters <= maxCharacters)
            {
                return dropped;
            }

            var oldest = bullets
                .Select((b, order) => (b.Section, b.Line, order))
                .Where(b => b.Line.Date is not null && !string.Equals(b.Line.Text, keep, StringComparison.Ordinal))
                .OrderBy(b => b.Line.Date, StringComparer.Ordinal)
                .ThenByDescending(b => b.order)
                .FirstOrDefault();
            if (oldest.Section is null)
            {
                return dropped;
            }

            oldest.Section.Lines.Remove(oldest.Line);
            dropped.Add(oldest.Line.Text);
        }
    }

    private static int TrimStyle(KnowledgeDocument document)
    {
        var trimmed = 0;
        int Size() => document.Sections
            .Where(s => !string.Equals(s.Title, StyleNotesSection, StringComparison.OrdinalIgnoreCase))
            .Sum(s => (s.Title?.Length ?? 0) + s.Lines.Sum(l => l.Text.Length + 4));
        while (Size() > MaxStyleCharacters)
        {
            // The longest learned section loses its last line.
            var longest = document.Sections
                .Where(s => !string.Equals(s.Title, StyleNotesSection, StringComparison.OrdinalIgnoreCase) && s.Lines.Count > 1)
                .OrderByDescending(s => s.Lines.Sum(l => l.Text.Length))
                .FirstOrDefault();
            if (longest is null)
            {
                break;
            }

            longest.Lines.RemoveAt(longest.Lines.Count - 1);
            trimmed++;
        }

        return trimmed;
    }

    private KnowledgeWrite ForgetBullet(string path, string needle, string what)
    {
        var document = KnowledgeDocument.Parse(ReadText(path));
        var matches = Match(document.AllBullets.ToList(), needle);
        var removed = new List<string>();
        foreach (var (section, line) in matches)
        {
            section.Lines.Remove(line);
            removed.Add(line.Text);
        }

        document.RemoveEmptySections();
        WriteAtomically(path, document.Render());
        return new KnowledgeWrite($"Forgot the {what}: {string.Join(" / ", removed)}", removed, [], path);

        List<(KnowledgeSection, KnowledgeLine)> Match(List<(KnowledgeSection Section, KnowledgeLine Line)> bullets, string text)
        {
            var exact = bullets.Where(b => string.Equals(b.Line.Text, text, StringComparison.OrdinalIgnoreCase) ||
                                           KnowledgeText.Fingerprint(b.Line.Text) == KnowledgeText.Fingerprint(text)).ToList();
            if (exact.Count > 0)
            {
                return [.. exact.Select(b => (b.Section, b.Line))];
            }

            var containing = bullets.Where(b => b.Line.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (containing.Count == 1)
            {
                return [(containing[0].Section, containing[0].Line)];
            }

            if (containing.Count > 1)
            {
                throw new ArgumentException(
                    $"Several {what}s contain \"{text}\": {string.Join(" / ", containing.Take(6).Select(b => b.Line.Text))}. Give the exact text of the one to forget.");
            }

            var known = bullets.Count == 0 ? "there are none" : "known: " + string.Join(" / ", bullets.Take(8).Select(b => KnowledgeText.Truncate(b.Line.Text, 80)));
            throw new ArgumentException($"No {what} matches \"{text}\" ({known}).");
        }
    }

    private KnowledgeWrite ForgetStyle(string needle)
    {
        var document = KnowledgeDocument.Parse(ReadText(StylePath));
        if (document.Find(needle) is { } section)
        {
            document.Sections.Remove(section);
            WriteAtomically(StylePath, document.Render());
            return new KnowledgeWrite($"Removed the style section \"{section.Title}\".", [section.Title ?? needle], [], StylePath);
        }

        var lines = document.Sections.SelectMany(s => s.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Select(l => (Section: s, Line: l)))
            .Where(x => x.Line.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lines.Count == 0)
        {
            var titles = string.Join(", ", document.Sections.Select(s => s.Title).Where(t => t is not null));
            throw new ArgumentException($"No style line or section matches \"{needle}\" (sections: {(titles.Length == 0 ? "none" : titles)}).");
        }

        if (lines.Count > 1)
        {
            throw new ArgumentException($"Several style lines contain \"{needle}\": {string.Join(" / ", lines.Take(6).Select(x => KnowledgeText.Truncate(x.Line.Text, 80)))}. Be more specific.");
        }

        lines[0].Section.Lines.Remove(lines[0].Line);
        document.RemoveEmptySections();
        WriteAtomically(StylePath, document.Render());
        return new KnowledgeWrite($"Removed from style.md: {lines[0].Line.Text}", [lines[0].Line.Text], [], StylePath);
    }

    private KnowledgeWrite ForgetRecipe(string needle)
    {
        var recipes = ReadRecipes();
        var slug = KnowledgeText.Slug(needle);
        var matches = recipes.Where(r => string.Equals(r.Slug, slug, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(r.Title, needle, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = [.. recipes.Where(r => r.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))];
        }

        if (matches.Count != 1)
        {
            var names = recipes.Count == 0 ? "there are none" : "recipes: " + string.Join(", ", recipes.Select(r => r.Title));
            throw new ArgumentException(matches.Count == 0
                ? $"No recipe matches \"{needle}\" ({names})."
                : $"Several recipes match \"{needle}\": {string.Join(", ", matches.Select(r => r.Title))}. Give the exact name.");
        }

        var path = Path.Combine(RecipesDirectory, matches[0].Slug + ".md");
        File.Delete(path);
        return new KnowledgeWrite($"Deleted the recipe \"{matches[0].Title}\".", [matches[0].Slug], [], path);
    }

    private List<KnowledgeRecipe> ReadRecipes()
    {
        var recipes = new List<KnowledgeRecipe>();
        if (!System.IO.Directory.Exists(RecipesDirectory))
        {
            return recipes;
        }

        foreach (var file in new DirectoryInfo(RecipesDirectory).GetFiles("*.md").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var text = ReadText(file.FullName);
            if (text is not null)
            {
                recipes.Add(ParseRecipe(Path.GetFileNameWithoutExtension(file.Name), text));
            }
        }

        return recipes;
    }

    /// <summary>Parses a recipe file (tolerant: a hand-written recipe without a JSON block keeps its text as steps).</summary>
    public static KnowledgeRecipe ParseRecipe(string slug, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = KnowledgeDocument.Parse(markdown);
        string? date = null;
        var description = new List<string>();
        foreach (var line in document.Intro)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Saved ", StringComparison.Ordinal) && trimmed.Length >= 16)
            {
                date = trimmed.Substring(6, 10);
                continue;
            }

            if (trimmed.Length > 0)
            {
                description.Add(trimmed);
            }
        }

        var stepsSection = document.Find("Steps");
        var stepLines = stepsSection?.Lines.Select(l => l.Render()).ToList() ?? [];
        var start = stepLines.FindIndex(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        var end = start < 0 ? -1 : stepLines.FindIndex(start + 1, l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        var steps = start >= 0 && end > start
            ? string.Join("\n", stepLines.Skip(start + 1).Take(end - start - 1))
            : string.Join("\n", stepLines);
        var notes = document.Find("Notes")?.Lines.Select(l => l.Render()).Where(l => l.Trim().Length > 0).ToList();
        return new KnowledgeRecipe(
            slug,
            document.Title ?? slug,
            string.Join(" ", description),
            date,
            steps.Trim(),
            notes is { Count: > 0 } ? string.Join(" ", notes) : null);
    }

    private static string RenderRecipe(string title, string description, string date, string stepsJson, string? notes)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(title).Append("\r\n\r\n");
        if (description.Length > 0)
        {
            builder.Append(description).Append("\r\n\r\n");
        }

        builder.Append("Saved ").Append(date).Append(". These are run_commands steps that worked; adapt the coordinates to the new drawing.\r\n\r\n");
        builder.Append("## Steps\r\n\r\n```json\r\n").Append(stepsJson.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n```\r\n");
        if (notes is not null)
        {
            builder.Append("\r\n## Notes\r\n\r\n").Append(notes).Append("\r\n");
        }

        return builder.ToString();
    }

    // One step per line: compact enough to stay small, readable enough to edit by hand.
    private static string FormatSteps(JsonArray steps)
    {
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var lines = steps.Select(s => "  " + (s?.ToJsonString(options) ?? "null")).ToList();
        return "[\n" + string.Join(",\n", lines) + "\n]";
    }

    private static string? ReadText(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text, Utf8NoBom);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                // The user's editor or a virus scanner may hold the file for a moment.
                Thread.Sleep(100);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                throw new IOException("The knowledge files are busy (another chat is writing them). Try again in a moment.");
            }

            return action();
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
