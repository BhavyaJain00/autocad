namespace AcadCopilot.Core.Learning;

/// <summary>A run of recorded actions that belong together (same layer and drawing, no long break).</summary>
/// <param name="Number">1-based step number.</param>
/// <param name="Title">"On A-DOOR: LINE, ARC x2".</param>
/// <param name="Layer">The current layer of the step, when known.</param>
/// <param name="Drawing">The drawing of the step.</param>
/// <param name="Start">When its first action started.</param>
/// <param name="End">When its last action ended.</param>
/// <param name="Actions">Its actions in time order.</param>
public sealed record RecordedStep(int Number, string Title, string? Layer, string Drawing, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<RecordedAction> Actions);

/// <summary>
/// Groups a recording into steps the AI can turn into recipes: a new step starts when the user makes another layer
/// current (the command that did it moves into the new step), switches drawings, pauses, leaves a break longer than
/// <see cref="BreakGap"/>, or after <see cref="MaxCommandsPerStep"/> commands.
/// </summary>
public static class RecordingSteps
{
    /// <summary>A break between two commands longer than this starts a new step.</summary>
    public static readonly TimeSpan BreakGap = TimeSpan.FromSeconds(90);

    /// <summary>Most commands in one step.</summary>
    public const int MaxCommandsPerStep = 12;

    /// <summary>Groups <paramref name="actions"/> (time order) into steps.</summary>
    public static IReadOnlyList<RecordedStep> Group(IReadOnlyList<RecordedAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var groups = new List<List<RecordedAction>>();
        var current = new List<RecordedAction>();

        void Close()
        {
            if (current.Count > 0)
            {
                groups.Add(current);
                current = [];
            }
        }

        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case RecordedActionKind.LayerChanged:
                {
                    // The command that made the layer current (-LAYER, LAYMCUR, SETVAR ...) opens the new step with it.
                    RecordedAction? carrier = null;
                    if (current.Count > 0 && current[^1] is { Kind: RecordedActionKind.Command } last && last.At <= action.At && (last.EndedAt is null || last.EndedAt > action.At))
                    {
                        carrier = last;
                        current.RemoveAt(current.Count - 1);
                    }

                    Close();
                    if (carrier is not null)
                    {
                        current.Add(carrier);
                    }

                    current.Add(action);
                    break;
                }

                case RecordedActionKind.DrawingSwitched:
                case RecordedActionKind.Resumed:
                    Close();
                    current.Add(action);
                    break;
                case RecordedActionKind.Paused:
                    current.Add(action);
                    Close();
                    break;
                default:
                    var commands = current.Count(a => a.Kind == RecordedActionKind.Command);
                    var previous = current.LastOrDefault(a => a.Kind == RecordedActionKind.Command);
                    if (previous is not null && (action.At - (previous.EndedAt ?? previous.At) > BreakGap || commands >= MaxCommandsPerStep))
                    {
                        Close();
                    }

                    current.Add(action);
                    break;
            }
        }

        Close();
        var steps = new List<RecordedStep>(groups.Count);
        string? layer = null;
        foreach (var group in groups)
        {
            layer = group.LastOrDefault(a => a.Kind == RecordedActionKind.LayerChanged)?.Name
                ?? group.FirstOrDefault(a => a.Kind == RecordedActionKind.Command && a.Layer is not null)?.Layer
                ?? layer;
            var start = group[0].At;
            var end = group.Max(a => a.EndedAt ?? a.At);
            steps.Add(new RecordedStep(steps.Count + 1, Title(group, layer), layer, group.FirstOrDefault(a => a.Drawing.Length > 0)?.Drawing ?? string.Empty, start, end, group));
        }

        return steps;
    }

    /// <summary>"On A-WALL: PLINE, OFFSET x2, TRIM" (at most five command names), or what the step is about.</summary>
    public static string Title(IReadOnlyList<RecordedAction> actions, string? layer)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var names = new List<(string Name, int Count)>();
        foreach (var action in actions.Where(a => a.Kind == RecordedActionKind.Command))
        {
            var index = names.FindIndex(n => string.Equals(n.Name, action.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                names[index] = (names[index].Name, names[index].Count + 1);
            }
            else
            {
                names.Add((action.Name, 1));
            }
        }

        var commands = string.Join(", ", names.Take(5).Select(n => n.Count > 1 ? $"{n.Name} x{n.Count}" : n.Name)) + (names.Count > 5 ? ", ..." : string.Empty);
        if (names.Count == 0)
        {
            return actions.FirstOrDefault()?.Kind switch
            {
                RecordedActionKind.LayerChanged => "Layer " + actions[0].Name + " made current",
                RecordedActionKind.DrawingSwitched => "Switched to " + actions[0].Name,
                RecordedActionKind.Paused => "Paused",
                RecordedActionKind.Resumed => "Resumed",
                _ => "Actions",
            };
        }

        return string.IsNullOrWhiteSpace(layer) ? commands : $"On {layer}: {commands}";
    }
}
