using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Plugin.Tools;

/// <summary>
/// One AutoCAD tool: its definition (name, title, LLM description, risk, strict input schema) and its handler.
/// The handler receives schema-validated arguments, runs on a thread-pool thread (use <see cref="ToolContext"/> to reach
/// AutoCAD's main thread), returns a <see cref="ToolResult"/> (failures as results; throw <see cref="ToolException"/> for
/// problems the AI can fix) and honours the cancellation token (Stop in the panel, or the bridge abandoning the call).
/// </summary>
/// <param name="Definition">What the LLM sees.</param>
/// <param name="Handler">The implementation.</param>
public sealed record ToolSpec(
    ToolDefinition Definition,
    Func<ToolContext, JsonObject, CancellationToken, Task<ToolResult>> Handler);

/// <summary>
/// All AutoCAD tools of the plug-in, exposed over the pipe (tools.list / tools.call) and through the MCP bridge.
/// The tools are contributed by partial methods in the other ToolRegistry.*.cs files:
/// <list type="bullet">
/// <item>ToolRegistry.Drafting.cs (drafting agent): run_commands, cancel_command, undo.</item>
/// <item>ToolRegistry.Inspect.cs (vision agent): get_drawing_info, capture_view, list_layers, list_entities, get_entity, get_sysvars.</item>
/// <item>ToolRegistry.Learning.cs: learn_style_from_drawing (the other learning tools are answered by the MCP bridge).</item>
/// </list>
/// Names must be unique, valid (<see cref="ToolDefinition.IsValidName"/>) and must not use the bridge's reserved names
/// (ask_user, update_plan, acad_status); definitions must pass <see cref="ToolDefinition.Lint"/>.
/// </summary>
public sealed partial class ToolRegistry
{
    private readonly Dictionary<string, ToolSpec> _byName;

    /// <summary>Creates a registry from explicit specs (tests); validates them.</summary>
    /// <exception cref="ArgumentException">Invalid, duplicate or reserved tool definitions.</exception>
    public ToolRegistry(IEnumerable<ToolSpec> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools = [.. tools];
        var problems = new List<string>();
        _byName = new Dictionary<string, ToolSpec>(StringComparer.Ordinal);
        foreach (var spec in Tools)
        {
            problems.AddRange(spec.Definition.Lint());
            if (BridgeTools.ReservedNames.Contains(spec.Definition.Name))
            {
                problems.Add($"Tool name '{spec.Definition.Name}' is reserved for the MCP bridge.");
            }

            if (!_byName.TryAdd(spec.Definition.Name, spec))
            {
                problems.Add($"Tool '{spec.Definition.Name}' is defined twice.");
            }
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException("Invalid tool registry: " + string.Join(" ", problems), nameof(tools));
        }

        Definitions = [.. Tools.Select(t => t.Definition)];
    }

    /// <summary>All tools in registration order.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; }

    /// <summary>All tool definitions in registration order (the tools.list answer).</summary>
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Number of tools.</summary>
    public int Count => Tools.Count;

    /// <summary>The plug-in's registry: drafting tools, inspection tools, then learning tools.</summary>
    public static ToolRegistry CreateDefault()
    {
        var tools = new List<ToolSpec>();
        AddDraftingTools(tools);
        AddInspectTools(tools);
        AddLearningTools(tools);
        AddInteractionTools(tools); // ToolRegistry.Interaction.cs: pick_point, get_selection, select_objects
        AddServicesTools(tools); // ToolRegistry.Services.cs: symbol library, services legend, presentation fills, PNG export
        AddSheetsTools(tools); // ToolRegistry.Sheets.cs: plan_full_set, arrange_drawings, create_sheets, plot_to_pdf
        return new ToolRegistry(tools);
    }

    /// <summary>Looks a tool up by name.</summary>
    public bool TryGet(string name, out ToolSpec spec)
    {
        if (name is not null && _byName.TryGetValue(name, out var found))
        {
            spec = found;
            return true;
        }

        spec = null!;
        return false;
    }

    /// <summary>Adds the drafting tools (implemented in ToolRegistry.Drafting.cs).</summary>
    static partial void AddDraftingTools(List<ToolSpec> tools);

    /// <summary>Adds the inspection tools (implemented in ToolRegistry.Inspect.cs).</summary>
    static partial void AddInspectTools(List<ToolSpec> tools);

    /// <summary>Adds the learning tools (implemented in ToolRegistry.Learning.cs).</summary>
    static partial void AddLearningTools(List<ToolSpec> tools);
}
