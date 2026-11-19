using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Tools;

/// <summary>How dangerous a tool is. Drives transactions and approval prompts.</summary>
public enum ToolRisk
{
    /// <summary>Only reads the model. Runs without a transaction and without approval.</summary>
    Read,

    /// <summary>Creates or changes elements. Runs in one transaction (one undo step), no approval.</summary>
    Write,

    /// <summary>Deletes or overwrites things (delete elements, save over a file). Needs the user's approval in the panel first.</summary>
    Destructive,
}

/// <summary>
/// Describes one tool the LLM can call (exposed through the MCP bridge).
/// Wire shape (camelCase JSON): { name, title, description, risk, inputSchema }.
/// </summary>
public sealed partial record ToolDefinition
{
    /// <summary>Unique snake_case name, 2-64 chars, [a-z0-9_], starting with a letter (e.g. "run_commands").</summary>
    public required string Name { get; init; }

    /// <summary>Short human title shown in the panel and used in the undo name ("Create wall").</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Description written for an LLM: what it does, when to use it, units, and what it returns.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>Risk class of the tool.</summary>
    public ToolRisk Risk { get; init; } = ToolRisk.Read;

    /// <summary>
    /// Strict JSON Schema of the arguments: root "type":"object" with "additionalProperties": false.
    /// Build it with <see cref="Schema"/>. Use only keywords <see cref="JsonSchemaValidator"/> supports.
    /// JsonNodes can have only one parent: call DeepClone() before embedding it in another node.
    /// </summary>
    public required JsonObject InputSchema { get; init; }

    /// <summary>Returns true when <paramref name="name"/> is a valid tool name.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>
    /// Checks the definition is well formed (valid name, non-empty title/description, object root schema
    /// that forbids additional properties). Returns the problems found; empty when valid.
    /// </summary>
    public IReadOnlyList<string> Lint()
    {
        var problems = new List<string>();
        if (!IsValidName(Name))
        {
            problems.Add($"Tool name '{Name}' must match ^[a-z][a-z0-9_]{{1,63}}$.");
        }

        if (string.IsNullOrWhiteSpace(Title))
        {
            problems.Add($"Tool '{Name}' needs a title.");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            problems.Add($"Tool '{Name}' needs a description.");
        }

        if (InputSchema["type"]?.GetValue<string>() != "object")
        {
            problems.Add($"Tool '{Name}' input schema root must be \"type\": \"object\".");
        }

        if (InputSchema["additionalProperties"] is not JsonValue additional || additional.GetValueKind() != System.Text.Json.JsonValueKind.False)
        {
            problems.Add($"Tool '{Name}' input schema root must set \"additionalProperties\": false.");
        }

        return problems;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
