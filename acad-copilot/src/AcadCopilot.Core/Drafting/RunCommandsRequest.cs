using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Drafting;

/// <summary>Limits of one run_commands call.</summary>
public static class DraftingLimits
{
    /// <summary>Most steps (commands) in one call.</summary>
    public const int MaxSteps = 30;

    /// <summary>Most inputs typed for one command.</summary>
    public const int MaxInputsPerStep = 60;

    /// <summary>Longest single input, in characters.</summary>
    public const int MaxInputLength = 300;

    /// <summary>Longest command (with prefixes), in characters.</summary>
    public const int MaxCommandLength = 64;

    /// <summary>Longest title, in characters.</summary>
    public const int MaxTitleLength = 80;

    /// <summary>Longest step note, in characters.</summary>
    public const int MaxNoteLength = 200;
}

/// <summary>One AutoCAD command of a run_commands call and the inputs typed at its prompts.</summary>
public sealed record DraftingStep
{
    /// <summary>The command as it will be typed at the Command prompt ("LINE", "-LAYER", "_.OFFSET").</summary>
    public required string Command { get; init; }

    /// <summary>The inputs, typed one by one, each followed by Enter; "" is Enter alone. Leading and trailing spaces are removed.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>Optional short note shown with the step ("plot boundary").</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Arguments of run_commands: <c>{ "title": "Outer walls", "steps": [ { "command": "LINE", "inputs": ["0,0", "@30'&lt;0", ""], "note": "plot boundary" } ] }</c>.
/// Use <see cref="TryParse"/> to read and validate them; every problem is reported with its JSON path.
/// </summary>
public sealed record RunCommandsRequest
{
    /// <summary>Short title used for the undo group and the panel row ("Outer walls").</summary>
    public required string Title { get; init; }

    /// <summary>The steps, in order (1-30).</summary>
    public IReadOnlyList<DraftingStep> Steps { get; init; } = [];

    /// <summary>Total number of inputs over all steps.</summary>
    public int InputCount => Steps.Sum(s => s.Inputs.Count);

    /// <summary>
    /// Self-healing (default true): safe syntax fixes of rejected inputs and the forgotten finishing Enter
    /// (<see cref="InputRepair"/>). False types everything exactly as given.
    /// </summary>
    public bool AutoFix { get; init; } = true;

    /// <summary>
    /// Checkpoint before typing: true / false as asked, null = automatic (a call of at least
    /// <see cref="AutoCheckpointMinSteps"/> steps, i.e. a whole stage, gets one). See <see cref="WantsCheckpoint"/>.
    /// </summary>
    public bool? AutoCheckpoint { get; init; }

    /// <summary>Calls with at least this many steps get an automatic checkpoint unless autoCheckpoint is false.</summary>
    public const int AutoCheckpointMinSteps = 10;

    /// <summary>True when a checkpoint should be created before this call is typed.</summary>
    public bool WantsCheckpoint => AutoCheckpoint ?? Steps.Count >= AutoCheckpointMinSteps;

    /// <summary>
    /// Reads and validates the arguments. On success <paramref name="request"/> is set and <paramref name="errors"/> is
    /// empty; otherwise <paramref name="errors"/> lists every problem as "$.path: message".
    /// </summary>
    public static bool TryParse(JsonObject? arguments, out RunCommandsRequest? request, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        request = null;
        if (arguments is null)
        {
            errors = ["$: arguments are missing; send {\"title\": \"...\", \"steps\": [{\"command\": \"LINE\", \"inputs\": [\"0,0\", \"@10,0\", \"\"]}]}."];
            return false;
        }

        foreach (var (name, _) in arguments)
        {
            if (name is not ("title" or "steps" or "autoFix" or "autoCheckpoint"))
            {
                problems.Add($"$: unknown property '{name}'. Allowed: title, steps, autoFix, autoCheckpoint.");
            }
        }

        var autoFix = ReadFlag(arguments, "autoFix", problems);
        var autoCheckpoint = ReadFlag(arguments, "autoCheckpoint", problems);

        var title = ReadTitle(arguments["title"], problems);
        var steps = new List<DraftingStep>();
        if (arguments["steps"] is not JsonArray stepArray)
        {
            problems.Add("$.steps: required; an array of 1-30 steps such as {\"command\": \"LINE\", \"inputs\": [\"0,0\", \"@10,0\", \"\"]}.");
        }
        else
        {
            if (stepArray.Count is < 1 or > DraftingLimits.MaxSteps)
            {
                problems.Add($"$.steps: needs 1-{DraftingLimits.MaxSteps} steps, got {stepArray.Count}. Split bigger jobs into several calls.");
            }

            for (var i = 0; i < Math.Min(stepArray.Count, DraftingLimits.MaxSteps); i++)
            {
                if (ReadStep(stepArray[i], $"$.steps[{i}]", problems) is { } step)
                {
                    steps.Add(step);
                }
            }
        }

        errors = problems;
        if (problems.Count > 0 || title is null)
        {
            return false;
        }

        request = new RunCommandsRequest { Title = title, Steps = steps, AutoFix = autoFix ?? true, AutoCheckpoint = autoCheckpoint };
        return true;
    }

    /// <summary>Like <see cref="TryParse"/> but throws a <see cref="ToolException"/> listing every problem.</summary>
    public static RunCommandsRequest Parse(JsonObject? arguments)
    {
        if (TryParse(arguments, out var request, out var errors))
        {
            return request!;
        }

        throw new ToolException("Invalid run_commands arguments; fix them and call again: " + string.Join(" ", errors));
    }

    /// <summary>True when <paramref name="text"/> contains a control character (Enter, Tab, Esc...) or a line/paragraph separator.</summary>
    public static bool HasControlCharacters(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029 || c == (char)0x85)
            {
                return true;
            }
        }

        return false;
    }

    private static bool? ReadFlag(JsonObject arguments, string name, List<string> problems)
    {
        if (!arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (CopilotJson.TryGetBool(node, out var value))
        {
            return value;
        }

        problems.Add($"$.{name}: must be true or false.");
        return null;
    }

    private static string? ReadTitle(JsonNode? node, List<string> problems)
    {
        var raw = CopilotJson.TryGetString(node);
        if (raw is null)
        {
            problems.Add("$.title: required; a short name for this batch such as \"Outer walls\".");
            return null;
        }

        var title = raw.Trim();
        if (title.Length == 0)
        {
            problems.Add("$.title: must not be empty.");
            return null;
        }

        if (title.Length > DraftingLimits.MaxTitleLength)
        {
            problems.Add($"$.title: at most {DraftingLimits.MaxTitleLength} characters.");
            return null;
        }

        if (HasControlCharacters(title))
        {
            problems.Add("$.title: must be one line without control characters.");
            return null;
        }

        return title;
    }

    private static DraftingStep? ReadStep(JsonNode? node, string path, List<string> problems)
    {
        if (node is not JsonObject step)
        {
            problems.Add($"{path}: must be an object {{\"command\": \"...\", \"inputs\": [...]}}.");
            return null;
        }

        var before = problems.Count;
        foreach (var (name, _) in step)
        {
            if (name is not ("command" or "inputs" or "note"))
            {
                problems.Add($"{path}: unknown property '{name}'. Allowed: command, inputs, note.");
            }
        }

        var command = ReadCommand(step["command"], path + ".command", problems);
        var inputs = ReadInputs(step["inputs"], path + ".inputs", problems);
        string? note = null;
        if (step["note"] is { } noteNode)
        {
            note = CopilotJson.TryGetString(noteNode)?.Trim();
            if (note is null)
            {
                problems.Add($"{path}.note: must be a string.");
            }
            else if (note.Length > DraftingLimits.MaxNoteLength || HasControlCharacters(note))
            {
                problems.Add($"{path}.note: one line of at most {DraftingLimits.MaxNoteLength} characters.");
            }
            else if (note.Length == 0)
            {
                note = null;
            }
        }

        if (problems.Count > before || command is null)
        {
            return null;
        }

        return new DraftingStep { Command = command, Inputs = inputs, Note = note };
    }

    private static string? ReadCommand(JsonNode? node, string path, List<string> problems)
    {
        var raw = CopilotJson.TryGetString(node);
        if (raw is null)
        {
            problems.Add($"{path}: required; one AutoCAD command such as \"LINE\", \"-LAYER\" or \"DIMLINEAR\".");
            return null;
        }

        var command = raw.Trim();
        if (command.Length == 0)
        {
            problems.Add($"{path}: must not be empty; put the command here and its answers in inputs.");
            return null;
        }

        if (command.Length > DraftingLimits.MaxCommandLength)
        {
            problems.Add($"{path}: at most {DraftingLimits.MaxCommandLength} characters.");
            return null;
        }

        if (CommandPolicy.IsAutoLisp(command))
        {
            // Reported by the policy check with a clear reason; still a single word.
            return command;
        }

        if (command.Any(char.IsWhiteSpace) || command.Contains(';', StringComparison.Ordinal) || HasControlCharacters(command))
        {
            problems.Add($"{path}: must be ONE command name without spaces (\"{Shorten(command)}\"); put the answers to its prompts in inputs, one per item.");
            return null;
        }

        if (!CommandPolicy.IsCommandNameText(command.TrimStart('_', '.', '-', '+', '\'')))
        {
            problems.Add($"{path}: \"{Shorten(command)}\" is not an AutoCAD command name; use letters and digits with optional prefixes _ . - ' (\"-LAYER\", \"_.LINE\").");
            return null;
        }

        return command;
    }

    private static List<string> ReadInputs(JsonNode? node, string path, List<string> problems)
    {
        var inputs = new List<string>();
        if (node is null)
        {
            return inputs;
        }

        if (node is not JsonArray array)
        {
            problems.Add($"{path}: must be an array of strings, one per prompt (\"\" = Enter).");
            return inputs;
        }

        if (array.Count > DraftingLimits.MaxInputsPerStep)
        {
            problems.Add($"{path}: at most {DraftingLimits.MaxInputsPerStep} inputs per step, got {array.Count}; split the command into several steps.");
            return inputs;
        }

        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var item = array[i];
            string? text = CopilotJson.TryGetString(item);
            if (text is null && CopilotJson.TryGetDouble(item, out var number))
            {
                problems.Add($"{itemPath}: must be a string; write \"{number.ToString(CultureInfo.InvariantCulture)}\" in quotes.");
                continue;
            }

            if (text is null)
            {
                problems.Add($"{itemPath}: must be a string (\"\" = Enter).");
                continue;
            }

            if (HasControlCharacters(text))
            {
                problems.Add($"{itemPath}: must not contain control characters (Enter, Tab, Esc); use a separate input \"\" for Enter.");
                continue;
            }

            text = text.Trim();
            if (text.Length > DraftingLimits.MaxInputLength)
            {
                problems.Add($"{itemPath}: at most {DraftingLimits.MaxInputLength} characters.");
                continue;
            }

            inputs.Add(text);
        }

        return inputs;
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "...";
}
