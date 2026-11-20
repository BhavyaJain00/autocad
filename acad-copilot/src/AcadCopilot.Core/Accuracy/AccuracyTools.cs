using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Accuracy;

/// <summary>
/// The parts of the accuracy tools that need no AutoCAD: running validate_plan on the arguments with the user's rules,
/// choosing the floor and origin for check_drawing, and turning reports into tool results.
/// </summary>
public static class AccuracyTools
{
    /// <summary>Default most findings returned by check_drawing.</summary>
    public const int DefaultMaxFindings = 40;

    /// <summary>The user's rules (rules.md bullets, without dates) from the knowledge folder; empty when unreadable.</summary>
    public static IReadOnlyList<string> RuleLines(string knowledgeDirectory)
    {
        try
        {
            return [.. new KnowledgeStore(knowledgeDirectory).Read().Rules.AllBullets.Select(b => b.Line.Text)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Runs validate_plan on its <paramref name="arguments"/> with the user's <paramref name="ruleLines"/>.</summary>
    public static (PlanValidation Validation, ToolResult Result, PlanRules Rules) ValidatePlan(JsonObject arguments, IEnumerable<string> ruleLines)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var rules = PlanRules.FromRules(ruleLines);
        if (arguments["vastu"] is JsonValue v && v.TryGetValue<bool>(out var vastu))
        {
            rules.Vastu = vastu;
        }

        if (arguments["plan"] is not JsonObject plan)
        {
            throw new ToolException("plan is missing: give the plot, setbacks, outline and the floors with rooms, walls, doors, windows and stair.");
        }

        var validation = PlanValidator.Validate(plan, rules);
        if (validation.Plan is null)
        {
            var errors = validation.Findings.Select(f => f.Message).ToList();
            return (validation, ToolResult.Fail($"The plan could not be read ({errors.Count} problem{(errors.Count == 1 ? string.Empty : "s")}): fix them and call validate_plan again.", errors), rules);
        }

        var data = new JsonObject
        {
            ["ready"] = validation.IsReady,
            ["errors"] = Texts(validation.Findings, Severity.Error),
            ["warnings"] = Texts(validation.Findings, Severity.Warning),
            ["hints"] = Texts(validation.Findings, Severity.Hint),
            ["summary"] = validation.Summary,
            ["schedule"] = validation.Schedule,
            ["rulesUsed"] = new JsonArray([.. rules.Applied.Select(r => (JsonNode)r)]),
            ["vastu"] = rules.Vastu,
        };
        var message = validation.IsReady
            ? string.Create(CultureInfo.InvariantCulture, $"Plan OK: 0 errors, {validation.Warnings} warning{(validation.Warnings == 1 ? string.Empty : "s")}. Fix or mention the warnings, show the user data.summary (room table, areas) and ask \"Shall I draw this?\". Draw exactly these numbers; run check_drawing after each stage.")
            : string.Create(CultureInfo.InvariantCulture, $"NOT READY: {validation.Errors} error{(validation.Errors == 1 ? string.Empty : "s")} ({validation.Warnings} warnings). Do not draw yet: fix every error in the plan (sizes, walls, doors) and call validate_plan again.");
        return (validation, ToolResult.Ok(message, data), rules);
    }

    /// <summary>Picks the floor (name or 1-based number) and its origin in the drawing.</summary>
    /// <exception cref="ToolException">Unknown floor or a bad origin.</exception>
    public static (HousePlan Plan, PlanFloor Floor, Pt Origin) ResolveFloor(JsonObject storedPlan, string? floorText, string? originText)
    {
        ArgumentNullException.ThrowIfNull(storedPlan);
        var parsed = PlanParser.Parse(storedPlan);
        if (parsed.Plan is null)
        {
            throw new ToolException("The saved plan can no longer be read (" + string.Join(" ", parsed.Errors.Take(3)) + "); call validate_plan again.");
        }

        var plan = parsed.Plan;
        PlanFloor? floor;
        if (string.IsNullOrWhiteSpace(floorText))
        {
            floor = plan.Floors[0];
        }
        else if (int.TryParse(floorText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            floor = number >= 1 && number <= plan.Floors.Count ? plan.Floors[number - 1] : null;
        }
        else
        {
            floor = plan.Floors.FirstOrDefault(f => string.Equals(f.Name, floorText.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (floor is null)
        {
            throw new ToolException($"There is no floor '{floorText}' in the saved plan; floors: {string.Join(", ", plan.Floors.Select((f, i) => $"{i + 1} = {f.Name}"))}.");
        }

        Pt origin;
        if (!string.IsNullOrWhiteSpace(originText))
        {
            origin = PlanParser.Point(originText) ?? throw new ToolException($"origin '{originText}' is not a point; write \"x,y\" in feet-inches, e.g. \"60',0\".");
        }
        else if (floor.Origin is { } given)
        {
            origin = given;
        }
        else if (floor.Index == 0)
        {
            origin = new Pt(0, 0);
        }
        else
        {
            throw new ToolException($"Where is {floor.Name} drawn? Pass origin (the drawing point of its plot corner 0,0, e.g. \"60',0\"), or give each floor an origin in validate_plan.");
        }

        return (plan, floor, origin);
    }

    /// <summary>The check_drawing result.</summary>
    public static ToolResult CheckResult(DrawingCheckReport report, PlanFloor floor, Pt origin, StoredPlan stored, int maxFindings)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(stored);
        var shown = report.Findings.Take(Math.Max(1, maxFindings)).ToList();
        var data = new JsonObject
        {
            ["floor"] = floor.Name,
            ["origin"] = origin.Input(),
            ["plan"] = stored.Plan["name"]?.ToString() ?? "House plan",
            ["planSaved"] = stored.SavedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            ["errors"] = report.Errors,
            ["warnings"] = report.Warnings,
            ["findings"] = new JsonArray([.. shown.Select(f => (JsonNode)f.Text())]),
            ["rooms"] = report.Rooms.DeepClone(),
            ["read"] = report.Read.DeepClone(),
        };
        if (report.Findings.Count > shown.Count)
        {
            data["moreFindings"] = report.Findings.Count - shown.Count;
        }

        var message = report.Errors == 0 && report.Warnings == 0
            ? $"{floor.Name} matches the plan: no problems found."
            : string.Create(CultureInfo.InvariantCulture, $"{floor.Name}: {report.Errors} error{(report.Errors == 1 ? string.Empty : "s")}, {report.Warnings} warning{(report.Warnings == 1 ? string.Empty : "s")}. Fix the errors (undo and redraw the stage if it is easier), then run check_drawing again.");
        return ToolResult.Ok(message, data);
    }

    private static JsonArray Texts(IEnumerable<PlanFinding> findings, string severity) =>
        new([.. findings.Where(f => f.Severity == severity).Select(f => (JsonNode)((f.Floor.Length > 0 ? f.Floor + ": " : string.Empty) + f.Message))]);
}
