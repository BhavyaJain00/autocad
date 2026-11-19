using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Learning;

/// <summary>
/// The learning tools. remember_rule, save_recipe, save_lesson, recall_knowledge and forget only touch the knowledge
/// files, so the MCP bridge implements them itself (<see cref="Execute"/>): they work while AutoCAD is busy or not
/// connected, never wait for a turn at the command line and never activate a drawing. learn_style_from_drawing reads the
/// drawing, so the plug-in implements it (definition here, analysis in <see cref="StyleAnalyzer"/>).
/// </summary>
public static class KnowledgeTools
{
    /// <summary>Saves a user preference or correction.</summary>
    public const string RememberRule = "remember_rule";

    /// <summary>Saves a command sequence that worked.</summary>
    public const string SaveRecipe = "save_recipe";

    /// <summary>Saves a mistake and its fix.</summary>
    public const string SaveLesson = "save_lesson";

    /// <summary>Searches rules, style, recipes and lessons.</summary>
    public const string RecallKnowledge = "recall_knowledge";

    /// <summary>Removes an item.</summary>
    public const string Forget = "forget";

    /// <summary>Reads the drawing's style into style.md (plug-in tool).</summary>
    public const string LearnStyleFromDrawing = "learn_style_from_drawing";

    /// <summary>Lists the user's "Learn from me" recordings.</summary>
    public const string ListRecordings = "list_recordings";

    /// <summary>Reads one "Learn from me" recording.</summary>
    public const string ReadRecording = "read_recording";

    /// <summary>Names of the tools the bridge implements (reserved: the plug-in may not define them).</summary>
    public static IReadOnlySet<string> BridgeNames { get; } =
        new HashSet<string>(StringComparer.Ordinal) { RememberRule, SaveRecipe, SaveLesson, RecallKnowledge, Forget, ListRecordings, ReadRecording };

    /// <summary>The bridge's learning tools, in listing order.</summary>
    public static IReadOnlyList<ToolDefinition> BridgeDefinitions() =>
        [RecallKnowledgeDefinition(), RememberRuleDefinition(), SaveRecipeDefinition(), SaveLessonDefinition(), ForgetDefinition(), ListRecordingsDefinition(), ReadRecordingDefinition()];

    /// <summary>list_recordings.</summary>
    public static ToolDefinition ListRecordingsDefinition() => new()
    {
        Name = ListRecordings,
        Title = "List my recordings",
        Risk = ToolRisk.Read,
        Description =
            "List the user's \"Learn from me\" recordings, newest first: file name, title (date and drawing) and how many actions. " +
            "A recording is what the user drew by hand in AutoCAD while learning was on (their own commands, typed inputs and the " +
            "objects created; never yours). Read one with read_recording.",
        InputSchema = Schema.Object(
            Schema.Opt("limit", Schema.Integer("Most recordings (default 20).", minimum: 1, maximum: 100))),
    };

    /// <summary>read_recording.</summary>
    public static ToolDefinition ReadRecordingDefinition() => new()
    {
        Name = ReadRecording,
        Title = "Read my recording",
        Risk = ToolRisk.Read,
        Description =
            "Read a \"Learn from me\" recording (Markdown): the user's own commands in order, grouped into steps, with what they " +
            "typed at each prompt (distances also in feet-inches), the current layer and the objects each command created. Learn " +
            "from it: save_recipe for each reusable sequence (describe what the numbers mean, so it adapts to other sizes), " +
            "remember_rule for clear habits (layers, offsets, text heights, dimension style). Long files come in parts: call " +
            "again with offset while more is true. Never re-run the recorded commands.",
        InputSchema = Schema.Object(
            Schema.Opt("name", Schema.String("File name from list_recordings or the user's message (default: the newest).", maxLength: 200)),
            Schema.Opt("offset", Schema.Integer("Character offset to continue from (default 0).", minimum: 0)),
            Schema.Opt("maxCharacters", Schema.Integer($"Most characters (default {RecordingLibrary.DefaultReadCharacters}).", minimum: 1_000, maximum: RecordingLibrary.MaxReadCharacters))),
    };

    /// <summary>remember_rule.</summary>
    public static ToolDefinition RememberRuleDefinition() => new()
    {
        Name = RememberRule,
        Title = "Remember a rule",
        Risk = ToolRisk.Write,
        Description =
            "Save a preference or correction of the user as a permanent rule in rules.md (the user's local knowledge files). " +
            "Call it whenever the user corrects you or states how they want things (\"toilets 5'x7' minimum\", \"dimensions " +
            "outside the walls\", \"always ask before the next floor\"), in one short imperative sentence with the numbers, then " +
            "follow it. Every later reply sees all rules. A rule on the same subject replaces the older one (newest wins); " +
            "identical rules are not duplicated. Returns what was saved and what it replaced.",
        InputSchema = Schema.Object(
            Schema.Req("text", Schema.String("The rule, one sentence with its numbers and units (\"Toilets at least 5'x7' clear.\").", minLength: 3, maxLength: KnowledgeStore.MaxRuleLength)),
            Schema.Opt("category", Schema.String("Where it belongs (default general).", RuleCategories.Keys))),
    };

    /// <summary>save_recipe.</summary>
    public static ToolDefinition SaveRecipeDefinition() => new()
    {
        Name = SaveRecipe,
        Title = "Save a recipe",
        Risk = ToolRisk.Write,
        Description =
            "Save a command sequence that worked well as a reusable recipe (recipes\\<name>.md), after a drawing stage came out " +
            "right (checked with capture_view), e.g. \"Dog-legged stair\", \"Door with swing\", \"Window in a 9 inch wall\". " +
            "Give the run_commands steps exactly as they worked and a description that says what they draw and what the " +
            "coordinates and sizes stand for, so they can be adapted next time. The same name overwrites (newest wins). " +
            "Use recall_knowledge to get a recipe back.",
        InputSchema = Schema.Object(
            Schema.Req("name", Schema.String("Short recipe name (\"Dog-legged stair\").", minLength: 2, maxLength: 80)),
            Schema.Req("description", Schema.String("What it draws, when to use it, and what the numbers mean (reference point, sizes).", minLength: 3, maxLength: KnowledgeStore.MaxRecipeDescriptionLength)),
            Schema.Req("steps", Schema.Array(
                "The run_commands steps that worked, in order.",
                Schema.Object(
                    Schema.Req("command", Schema.String("AutoCAD command (\"PLINE\").", minLength: 1, maxLength: DraftingLimits.MaxCommandLength)),
                    Schema.Opt("inputs", Schema.Array("Inputs typed at its prompts.", Schema.String("One input.", maxLength: DraftingLimits.MaxInputLength), maxItems: DraftingLimits.MaxInputsPerStep)),
                    Schema.Opt("note", Schema.String("What this step does.", maxLength: DraftingLimits.MaxNoteLength))),
                minItems: 1,
                maxItems: DraftingLimits.MaxSteps)),
            Schema.Opt("notes", Schema.String("Pitfalls or variations (\"mirror for a left-hand stair\").", maxLength: KnowledgeStore.MaxRecipeDescriptionLength))),
    };

    /// <summary>save_lesson.</summary>
    public static ToolDefinition SaveLessonDefinition() => new()
    {
        Name = SaveLesson,
        Title = "Save a lesson",
        Risk = ToolRisk.Write,
        Description =
            "Save a mistake you made in AutoCAD and how you fixed it in lessons.md, right after fixing it, so you never repeat " +
            "it (\"ARC with the A option after C and the start point needs the included angle, not an end point\" / " +
            "\"SYSMON notices in the transcript are not errors\"). Only real lessons about commands, prompts or the user's " +
            "drawings, not one-off typos. A lesson about the same mistake replaces the older one.",
        InputSchema = Schema.Object(
            Schema.Req("mistake", Schema.String("What went wrong (command, prompt, what happened).", minLength: 3, maxLength: KnowledgeStore.MaxLessonPartLength)),
            Schema.Req("fix", Schema.String("What works instead.", minLength: 3, maxLength: KnowledgeStore.MaxLessonPartLength))),
    };

    /// <summary>recall_knowledge.</summary>
    public static ToolDefinition RecallKnowledgeDefinition() => new()
    {
        Name = RecallKnowledge,
        Title = "Recall knowledge",
        Risk = ToolRisk.Read,
        Description =
            "Search what you have learned from this user: rules (preferences, corrections), style (layers, text, dimensions, " +
            "walls, blocks), recipes (command sequences that worked, with their run_commands steps) and lessons (mistakes and " +
            "fixes). Call it before drawing something (\"stair\", \"door swing\", \"toilet\", \"dimensions\") and reuse what " +
            "it returns. An empty query lists everything briefly. Returns the best keyword matches, newest first on ties.",
        InputSchema = Schema.Object(
            Schema.Opt("query", Schema.String("Keywords (\"dog-legged stair\", \"toilet size\", \"text height\").", maxLength: 200)),
            Schema.Opt("kinds", Schema.Array("Only these kinds (default all).", Schema.String("Kind.", ["rule", "style", "recipe", "lesson"]), maxItems: 4)),
            Schema.Opt("limit", Schema.Integer($"Most items (default {KnowledgeRecall.DefaultLimit}).", minimum: 1, maximum: KnowledgeRecall.MaxLimit))),
    };

    /// <summary>forget.</summary>
    public static ToolDefinition ForgetDefinition() => new()
    {
        Name = Forget,
        Title = "Forget",
        Risk = ToolRisk.Write,
        Description =
            "Remove something you learned that is wrong or that the user wants gone: a rule, a lesson, a recipe (by name) or a " +
            "style line (or a whole style section by its name). Give the exact text or a distinctive part of it (copy it from " +
            "recall_knowledge). Fails and lists the candidates when nothing or several items match.",
        InputSchema = Schema.Object(
            Schema.Req("kind", Schema.String("What to remove.", ["rule", "lesson", "recipe", "style"])),
            Schema.Req("text", Schema.String("The item's text, a distinctive part of it, or the recipe name / style section.", minLength: 2, maxLength: 300))),
    };

    /// <summary>learn_style_from_drawing (implemented by the plug-in).</summary>
    public static ToolDefinition LearnStyleFromDrawingDefinition() => new()
    {
        Name = LearnStyleFromDrawing,
        Title = "Learn my style",
        Risk = ToolRisk.Write,
        Description =
            "Read the drawing style of the active drawing and save it in style.md, so you draw like the user from now on: " +
            "units, layers (colour, linetype, lineweight), text styles and the most used text heights, the dimension style " +
            "(DIMSCALE, DIMTXT, arrows/ticks, units ...), wall thicknesses (spacing of parallel lines, polyline widths), door " +
            "swing sizes, block names used for doors, windows and furniture, hatch patterns. Use it on the user's own finished " +
            "drawing (offer \"Learn my style from this drawing?\" when they open one), not on a drawing you are still making. " +
            "It does not change the drawing. Learned sections replace the older ones; the user's notes stay. Returns the " +
            "learned style.",
        InputSchema = Schema.Object(),
    };

    /// <summary>
    /// Runs a bridge learning tool against <paramref name="store"/>. Validates the arguments; problems the AI can fix
    /// become failed results.
    /// </summary>
    public static ToolResult Execute(KnowledgeStore store, string name, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(store);
        var definition = BridgeDefinitions().FirstOrDefault(d => d.Name == name)
            ?? throw new ArgumentException($"'{name}' is not a learning tool.", nameof(name));
        arguments ??= [];
        var errors = JsonSchemaValidator.Validate(definition.InputSchema, arguments);
        if (errors.Count > 0)
        {
            return ToolResult.Fail($"Invalid arguments for {name}. Fix them and call again.", errors);
        }

        var args = new ToolArguments(arguments);
        try
        {
            switch (name)
            {
                case RememberRule:
                    return Written(store.RememberRule(args.GetString("text"), args.GetStringOrNull("category")));
                case SaveLesson:
                    return Written(store.SaveLesson(args.GetString("mistake"), args.GetString("fix")));
                case SaveRecipe:
                    return Written(store.SaveRecipe(args.GetString("name"), args.GetString("description"), args.GetArray("steps"), args.GetStringOrNull("notes")));
                case Forget:
                    return Written(store.Forget(args.GetString("kind"), args.GetString("text")));
                case ListRecordings:
                    return ListRecordingFiles(new RecordingLibrary(store.Directory), (int)args.GetLong("limit", 20));
                case ReadRecording:
                    return ReadRecordingFile(new RecordingLibrary(store.Directory), args);
                default:
                    return Recall(store, args);
            }
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail("Could not use the knowledge files: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail("The knowledge files are read-only or locked: " + ex.Message);
        }
    }

    private static ToolResult ListRecordingFiles(RecordingLibrary library, int limit)
    {
        var all = library.List();
        if (all.Count == 0)
        {
            return ToolResult.Ok(
                "No recordings yet. The user records one with \"Start learning\" in the panel, draws, then presses \"Stop & learn\".",
                new JsonObject { ["recordings"] = new JsonArray(), ["folder"] = library.Directory });
        }

        var items = new JsonArray();
        foreach (var recording in all.Take(limit))
        {
            var item = new JsonObject
            {
                ["name"] = recording.Name,
                ["title"] = recording.Title,
                ["modified"] = recording.Modified.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                ["characters"] = recording.Bytes,
            };
            if (recording.Summary is not null)
            {
                item["summary"] = recording.Summary;
            }

            items.Add(item);
        }

        return ToolResult.Ok(
            $"{all.Count} recording(s){(all.Count > limit ? $" (newest {limit} shown)" : string.Empty)}. Read one with read_recording {{name}}.",
            new JsonObject { ["recordings"] = items, ["folder"] = library.Directory });
    }

    private static ToolResult ReadRecordingFile(RecordingLibrary library, ToolArguments args)
    {
        var name = args.GetStringOrNull("name");
        RecordingPart part;
        try
        {
            part = library.Read(name, (int)args.GetLong("offset", 0), (int)args.GetLong("maxCharacters", RecordingLibrary.DefaultReadCharacters));
        }
        catch (FileNotFoundException)
        {
            var names = library.List().Take(10).Select(r => r.Name).ToList();
            return ToolResult.Fail(names.Count == 0
                ? "There are no recordings yet."
                : $"No single recording matches \"{name}\". Recordings: {string.Join(", ", names)}.");
        }

        var data = new JsonObject
        {
            ["name"] = part.Name,
            ["text"] = part.Text,
            ["offset"] = part.Offset,
            ["totalCharacters"] = part.TotalCharacters,
            ["more"] = part.HasMore,
        };
        if (part.HasMore)
        {
            data["nextOffset"] = part.Offset + part.Text.Length;
        }

        var message = part.HasMore
            ? $"Part of {part.Name} (characters {part.Offset}-{part.Offset + part.Text.Length} of {part.TotalCharacters}); call read_recording again with offset {part.Offset + part.Text.Length} for the rest."
            : $"{part.Name} ({part.TotalCharacters} characters). Save what the user does as recipes and rules, then tell them briefly what you learned.";
        return ToolResult.Ok(message, data);
    }

    private static ToolResult Written(KnowledgeWrite write)
    {
        var data = new JsonObject { ["file"] = write.File };
        if (write.Replaced.Count > 0)
        {
            data["replaced"] = new JsonArray([.. write.Replaced.Select(r => (JsonNode)r)]);
        }

        if (write.Dropped.Count > 0)
        {
            data["dropped"] = new JsonArray([.. write.Dropped.Select(r => (JsonNode)r)]);
        }

        return ToolResult.Ok(write.Message, data);
    }

    private static ToolResult Recall(KnowledgeStore store, ToolArguments args)
    {
        var snapshot = store.Read();
        var query = args.GetStringOrNull("query");
        var kinds = args.Has("kinds") ? args.GetStringList("kinds") : null;
        var limit = (int)args.GetLong("limit", KnowledgeRecall.DefaultLimit);
        if (snapshot.IsEmpty)
        {
            return ToolResult.Ok($"Nothing learned yet (knowledge folder {snapshot.Directory}).", new JsonObject { ["items"] = new JsonArray(), ["folder"] = snapshot.Directory });
        }

        if (KnowledgeText.Tokens(query).Count == 0)
        {
            // Overview: everything, briefly.
            var all = KnowledgeRecall.Items(snapshot).Where(i => kinds is null || kinds.Count == 0 || kinds.Contains(i.Kind)).ToList();
            var overview = new JsonArray([.. all.Take(60).Select(i => (JsonNode)Brief(i))]);
            return ToolResult.Ok(
                $"{all.Count} items learned{(all.Count > 60 ? " (first 60 shown)" : string.Empty)}. Ask with a query for details (recipes come with their steps).",
                new JsonObject { ["items"] = overview, ["folder"] = snapshot.Directory });
        }

        var hits = KnowledgeRecall.Search(snapshot, query, limit, kinds);
        var items = new JsonArray();
        foreach (var hit in hits)
        {
            var item = Brief(hit.Item);
            if (hit.Item.Recipe is { } recipe)
            {
                item["steps"] = ParseSteps(recipe.Steps);
                if (recipe.Notes is not null)
                {
                    item["notes"] = recipe.Notes;
                }
            }

            items.Add(item);
        }

        var message = hits.Count == 0
            ? $"Nothing learned matches \"{query}\". Use your general knowledge (and save what works)."
            : $"{hits.Count} item(s) match \"{query}\": " + string.Join(", ", hits.GroupBy(h => h.Item.Kind).Select(g => $"{g.Count()} {g.Key}(s)")) + ". Follow rules and style; adapt recipe coordinates to this drawing.";
        return ToolResult.Ok(message, new JsonObject { ["items"] = items, ["folder"] = snapshot.Directory });
    }

    private static JsonObject Brief(KnowledgeItem item)
    {
        var node = new JsonObject { ["kind"] = item.Kind, ["title"] = item.Title, ["text"] = item.Text };
        if (item.Date is not null)
        {
            node["date"] = item.Date;
        }

        return node;
    }

    private static JsonNode ParseSteps(string steps)
    {
        try
        {
            return JsonNode.Parse(steps) ?? JsonValue.Create(steps);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(steps);
        }
    }
}
