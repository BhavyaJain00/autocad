using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Interaction;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// Tools that involve the user's hands in the drawing (Plugin/Interaction, <see cref="AcadDrawingInteraction"/>):
/// <list type="bullet">
/// <item>pick_point: asks the user to click one or more points (prompt in the command line and the panel).</item>
/// <item>get_selection: the objects the user selected (or selected last), with summaries.</item>
/// <item>select_objects: selects and highlights objects by handle so the user sees them.</item>
/// </list>
/// pick_point and select_objects have risk Write only so that they take a turn in AutoCAD like typing does (one command
/// line); they never change the drawing. Compile-verified only (the handlers need AutoCAD).
/// </summary>
public sealed partial class ToolRegistry
{
    /// <summary>Name of the point-picking tool.</summary>
    public const string PickPointTool = "pick_point";

    /// <summary>Name of the selection-reading tool.</summary>
    public const string GetSelectionTool = "get_selection";

    /// <summary>Name of the selecting / highlighting tool.</summary>
    public const string SelectObjectsTool = "select_objects";

    private const int MaxSelectionSummaries = 500;

    /// <summary>The interaction tools' definitions (pure; also used by tests).</summary>
    public static IReadOnlyList<ToolDefinition> InteractionDefinitions() =>
    [
        new()
        {
            Name = PickPointTool,
            Title = "Ask the user to click a point",
            Risk = ToolRisk.Write,
            Description =
                "Ask the USER to click one or more points in the AutoCAD drawing and wait for the clicks. The prompt is shown " +
                "in AutoCAD's command line and in the AI Copilot panel (\"Click where the kitchen should go\"). Use it when a " +
                "position is easier to show than to describe: where a room, door, text or dimension should go, the corner to " +
                "start from, the wall to break. The user can use object snaps; Enter finishes a multi-point pick, Esc cancels. " +
                "Returns points in world coordinates (drawing units, with feet-inch text): {status, points:[{x, y, ftIn}]}. " +
                "On Esc or timeout it fails: then ask in chat instead and do not call it again at once.",
            InputSchema = Schema.Object(
                Schema.Req("prompt", Schema.String("What the user should click, as a short instruction ('Click where the kitchen should go').", minLength: 1, maxLength: 160)),
                Schema.Opt("count", Schema.Integer("Most points to collect (default 1). With more than 1 the user presses Enter when done.", 1, PickRequest.MaxPointsLimit)),
                Schema.Opt("timeoutSeconds", Schema.Integer("How long to wait for the clicks (default 120).", 5, PickRequest.MaxTimeoutSeconds))),
        },
        new()
        {
            Name = GetSelectionTool,
            Title = "Get the user's selection",
            Risk = ToolRisk.Read,
            Description =
                "The objects the user has selected in the active drawing (grips / pickfirst selection), or, when nothing is " +
                "selected, the last selection they made. Use it when the user says 'these', 'this wall', 'the selected " +
                "lines'. Returns {source: current|last|none, count, byType, byLayer, objects:[{handle, type, layer, geometry " +
                "in drawing units with feet-inches}]}. Use get_entity on a handle for full details; in run_commands select them " +
                "by their coordinates. A message from the panel may already list the selection as '[selected in AutoCAD: ...]'.",
            InputSchema = Schema.Object(
                Schema.Opt("which", Schema.String("current = only what is selected now; last = the last selection; auto (default) = current, else last.", ["auto", "current", "last"])),
                Schema.Opt("limit", Schema.Integer("Most objects to describe (default 100).", 1, MaxSelectionSummaries))),
        },
        new()
        {
            Name = SelectObjectsTool,
            Title = "Select objects for the user",
            Risk = ToolRisk.Write,
            Description =
                "Select and highlight objects by handle in the active drawing (grips shown), replacing the user's selection, so " +
                "the user can see what you mean ('these are the walls I will move'). It does not change the drawing. Pass an " +
                "empty list to clear the selection. Fails while a command is running.",
            InputSchema = Schema.Object(
                Schema.Req("handles", Schema.Array("Hex handles ('2A3F') from list_entities, get_selection or run_commands results.", Schema.String("A handle.", minLength: 1, maxLength: 16), 0, MaxSelectionSummaries))),
        },
    ];

    static partial void AddInteractionTools(List<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var definition in InteractionDefinitions())
        {
            tools.Add(new ToolSpec(definition, definition.Name switch
            {
                PickPointTool => PickPointAsync,
                GetSelectionTool => GetSelectionAsync,
                _ => SelectObjectsAsync,
            }));
        }
    }

    /// <summary>Adds the interaction tools (pick_point, get_selection, select_objects).</summary>
    static partial void AddInteractionTools(List<ToolSpec> tools);

    private static async Task<ToolResult> PickPointAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new PickRequest
        {
            Prompt = arguments["prompt"]?.GetValue<string>() ?? string.Empty,
            MaxPoints = arguments["count"] is JsonValue count ? count.GetValue<int>() : 1,
            Timeout = TimeSpan.FromSeconds(arguments["timeoutSeconds"] is JsonValue seconds ? seconds.GetValue<int>() : 120),
            Source = PickSource.Ai,
        };
        var interaction = await context.InvokeAsync(() => AcadDrawingInteraction.GetOrCreate(context.Dispatcher), cancellationToken).ConfigureAwait(false);
        context.Log($"Asking the user to click: {request.Prompt}");
        var result = await interaction.PickInTurnAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (result.Status)
        {
            case PickStatus.Picked:
                var points = new JsonArray();
                foreach (var point in result.Points)
                {
                    var json = new JsonObject { ["x"] = point.X, ["y"] = point.Y };
                    if (Math.Abs(point.Z) > 1e-9)
                    {
                        json["z"] = point.Z;
                    }

                    if (point.XText is { } x && point.YText is { } y)
                    {
                        json["ftIn"] = $"({x}, {y})";
                    }

                    points.Add(json);
                }

                return ToolResult.Ok(
                    result.Message + " " + InteractionText.PointTokens(result.Points),
                    new JsonObject { ["status"] = "picked", ["drawing"] = result.DrawingTitle, ["points"] = points });
            case PickStatus.TimedOut:
                return ToolResult.Fail(result.Message + " The user did not click: ask in chat where it should go instead.");
            case PickStatus.Cancelled:
                return ToolResult.Fail(result.Message + " Ask the user in chat instead; do not ask for a click again right away.");
            default:
                return ToolResult.Fail(result.Message);
        }
    }

    private static Task<ToolResult> GetSelectionAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var which = arguments["which"]?.GetValue<string>() ?? "auto";
        var limit = arguments["limit"] is JsonValue value ? value.GetValue<int>() : 100;
        return context.InvokeAsync(
            () =>
            {
                var document = context.Document;
                var interaction = AcadDrawingInteraction.GetOrCreate(context.Dispatcher);
                var (ids, source) = interaction.ReadSelectionIds(document, allowCurrent: which != "last", allowLast: which != "current");
                if (ids.Length == 0)
                {
                    return ToolResult.Ok(
                        which == "current"
                            ? "Nothing is selected in AutoCAD now. Ask the user to select the objects (click them), then call get_selection again."
                            : "Nothing is selected and there is no earlier selection in this drawing. Ask the user to select the objects first.",
                        new JsonObject { ["source"] = "none", ["count"] = 0, ["objects"] = new JsonArray() });
                }

                var units = ToolContext.GetUnits(document.Database);
                var objects = new JsonArray();
                var byType = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var byLayer = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (var id in ids)
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
                        {
                            continue;
                        }

                        var type = EntitySummary.TypeName(entity);
                        byType[type] = byType.TryGetValue(type, out var t) ? t + 1 : 1;
                        byLayer[entity.Layer] = byLayer.TryGetValue(entity.Layer, out var l) ? l + 1 : 1;
                        if (objects.Count < limit)
                        {
                            objects.Add(EntitySummary.Summarize(entity, units));
                        }
                    }

                    transaction.Commit();
                }

                var data = new JsonObject
                {
                    ["source"] = source,
                    ["count"] = ids.Length,
                    ["drawing"] = Path.GetFileName(document.Name),
                    ["byType"] = Counts(byType),
                    ["byLayer"] = Counts(byLayer),
                    ["objects"] = objects,
                };
                if (ids.Length > objects.Count)
                {
                    data["truncated"] = true;
                }

                var what = source == "current" ? "selected now" : "in the user's last selection";
                return ToolResult.Ok(
                    string.Create(CultureInfo.InvariantCulture, $"{ids.Length} object(s) {what}{(ids.Length > objects.Count ? $"; the first {objects.Count} are described" : string.Empty)}."),
                    data);
            },
            cancellationToken);
    }

    private static Task<ToolResult> SelectObjectsAsync(ToolContext context, JsonObject arguments, CancellationToken cancellationToken)
    {
        var handles = arguments["handles"] is JsonArray array
            ? array.Select(h => h?.GetValue<string>() ?? string.Empty).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return context.InvokeAsync(
            () =>
            {
                var document = context.Document;
                if (Drafting.AcadInput.IsCommandActive())
                {
                    throw new ToolException("A command is running in AutoCAD, so the selection cannot be changed now. Call cancel_command first or wait.");
                }

                var ids = new List<ObjectId>();
                var missing = new List<string>();
                foreach (var handle in handles)
                {
                    if (ToolContext.TryGetObjectId(document.Database, handle, out var id))
                    {
                        ids.Add(id);
                    }
                    else
                    {
                        missing.Add(handle);
                    }
                }

                if (handles.Count > 0 && ids.Count == 0)
                {
                    return ToolResult.Fail($"None of the handles exist in this drawing ({string.Join(", ", missing.Take(10))}). Use list_entities or get_selection for current handles.");
                }

                AcadDrawingInteraction.GetOrCreate(context.Dispatcher).Highlight(document, [.. ids]);
                var message = ids.Count == 0
                    ? "Cleared the selection."
                    : $"Selected and highlighted {ids.Count} object(s) for the user (grips shown).";
                var result = ToolResult.Ok(message, new JsonObject { ["selected"] = ids.Count, ["missing"] = new JsonArray([.. missing.Select(m => (JsonNode)m)]) });
                return missing.Count > 0
                    ? result with { Warnings = [$"Not found (erased or wrong handle): {string.Join(", ", missing.Take(20))}"] }
                    : result;
            },
            cancellationToken);
    }

    private static JsonObject Counts(SortedDictionary<string, int> counts)
    {
        var json = new JsonObject();
        foreach (var (key, value) in counts)
        {
            json[key] = value;
        }

        return json;
    }
}
