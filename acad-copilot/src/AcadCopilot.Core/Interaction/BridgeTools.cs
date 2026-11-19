using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Interaction;

/// <summary>
/// Tools implemented by the MCP bridge itself rather than by the AutoCAD tool registry.
/// Their names are reserved: the add-in registry must not define tools with these names.
/// </summary>
public static class BridgeTools
{
    /// <summary>Name of the clarifying-question tool (panel only, bridge flag --panel).</summary>
    public const string AskUser = "ask_user";

    /// <summary>Name of the checklist tool (panel only, bridge flag --panel).</summary>
    public const string UpdatePlan = "update_plan";

    /// <summary>Name of the connection-status tool (always available).</summary>
    public const string AcadStatus = "acad_status";

    /// <summary>
    /// All reserved names: the bridge's own tools, including the learning tools it implements over the knowledge files
    /// (<see cref="Learning.KnowledgeTools.BridgeNames"/>).
    /// </summary>
    public static IReadOnlySet<string> ReservedNames { get; } =
        new HashSet<string>(Learning.KnowledgeTools.BridgeNames, StringComparer.Ordinal) { AskUser, UpdatePlan, AcadStatus };

    /// <summary>Definition of <see cref="AskUser"/>; its arguments deserialize to <see cref="QuestionRequest"/>.</summary>
    public static ToolDefinition AskUserDefinition() => new()
    {
        Name = AskUser,
        Title = "Ask the user",
        Risk = ToolRisk.Read,
        Description =
            "Ask the user 1-4 clarifying questions in the AI Copilot panel and wait for the answers. " +
            "Each question is shown as clickable option cards (2-6 options) plus an optional free-text answer. " +
            "Use it BEFORE building when key facts are missing (size, number of rooms, levels, wall type, units) " +
            "instead of guessing; mark the option you suggest with recommended=true. " +
            "Returns {answers:[{question, selected:[labels], custom}], cancelled}. If cancelled is true, stop and wait for the user.",
        InputSchema = Schema.Object(
            Schema.Req("questions", Schema.Array(
                "Questions to show together.",
                Schema.Object(
                    Schema.Opt("header", Schema.String("Very short chip label, max 16 characters (e.g. 'Bedrooms').", maxLength: 16)),
                    Schema.Req("question", Schema.String("The full question, ending with '?'.", minLength: 1)),
                    Schema.Req("options", Schema.Array(
                        "Choices shown as cards.",
                        Schema.Object(
                            Schema.Req("label", Schema.String("Short option text (1-6 words).", minLength: 1)),
                            Schema.Opt("description", Schema.String("One-line explanation of the option.")),
                            Schema.Opt("recommended", Schema.Boolean("True for the option you recommend (at most one)."))),
                        minItems: 2,
                        maxItems: 6)),
                    Schema.Opt("allowCustom", Schema.Boolean("Offer a free-text answer too (default true).")),
                    Schema.Opt("multiSelect", Schema.Boolean("Allow picking several options (default false)."))),
                minItems: 1,
                maxItems: 4))),
    };

    /// <summary>Definition of <see cref="UpdatePlan"/>; its arguments deserialize to <see cref="PlanUpdate"/>.</summary>
    public static ToolDefinition UpdatePlanDefinition() => new()
    {
        Name = UpdatePlan,
        Title = "Update task progress",
        Risk = ToolRisk.Read,
        Description =
            "Show or update the 'Task Progress' checklist in the AI Copilot panel. " +
            "For any request needing 3 or more AutoCAD operations, call it first with all steps 'pending', " +
            "then call it again (always with the FULL list) as each step becomes 'in_progress' and then 'completed' " +
            "(or 'failed'/'skipped'). Keep titles short and concrete ('Create exterior walls').",
        InputSchema = Schema.Object(
            Schema.Opt("title", Schema.String("Optional checklist title.")),
            Schema.Req("tasks", Schema.Array(
                "All tasks in order (full replacement).",
                Schema.Object(
                    Schema.Req("title", Schema.String("Task text.", minLength: 1)),
                    Schema.Req("status", Schema.String("Task status.", ["pending", "in_progress", "completed", "failed", "skipped"]))),
                minItems: 0,
                maxItems: 30))),
    };

    /// <summary>Definition of <see cref="AcadStatus"/> (no arguments).</summary>
    public static ToolDefinition AcadStatusDefinition() => new()
    {
        Name = AcadStatus,
        Title = "AutoCAD connection status",
        Risk = ToolRisk.Read,
        Description =
            "Report whether this MCP server is connected to a running AutoCAD with the AI Copilot add-in " +
            "(process id, AutoCAD version, active document) and how many AutoCAD tools are available. " +
            "Call it when AutoCAD tools fail with a connection error.",
        InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
    };
}
