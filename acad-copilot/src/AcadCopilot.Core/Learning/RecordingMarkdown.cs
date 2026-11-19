using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Learning;

/// <summary>
/// Writes a <see cref="UserRecording"/> as the readable Markdown file the AI learns from
/// (knowledge\recordings\&lt;yyyy-MM-dd_HHmm&gt;_&lt;drawing&gt;.md): a summary (commands used, layers, objects, units),
/// then the steps (<see cref="RecordingSteps"/>) with every command in order, what the user typed at each prompt
/// (typed distances also in feet-inches) and the objects it created, modified or erased.
/// </summary>
public static class RecordingMarkdown
{
    /// <summary>First line of every recording file.</summary>
    public const string TitlePrefix = "# Recording ";

    /// <summary>The Markdown text of <paramref name="recording"/>.</summary>
    public static string Write(UserRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var text = new StringBuilder();
        var drawing = recording.Drawings.FirstOrDefault() ?? "drawing";
        text.Append(TitlePrefix).Append(recording.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" - ").AppendLine(drawing);
        text.AppendLine();
        text.AppendLine("What the user drew by hand in AutoCAD while \"Learn from me\" was on, in order. The copilot's own commands are not");
        text.AppendLine("included. Learn the user's way of drafting from it: reusable command sequences as recipes, clear habits as rules.");
        text.AppendLine();

        AppendSummary(text, recording);
        foreach (var step in RecordingSteps.Group(recording.Actions))
        {
            AppendStep(text, recording, step);
        }

        return text.ToString();
    }

    private static void AppendSummary(StringBuilder text, UserRecording recording)
    {
        text.AppendLine("## Summary");
        text.AppendLine();
        var started = recording.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var paused = recording.PausedTime >= TimeSpan.FromSeconds(1) ? $" (paused {Duration(recording.PausedTime)})" : string.Empty;
        text.Append("- Started ").Append(started).Append(", recorded ").Append(Duration(recording.Elapsed)).AppendLine(paused);
        foreach (var name in recording.Drawings)
        {
            var units = recording.UnitsOf(name);
            text.Append("- Drawing ").Append(name).AppendLine(units is null ? string.Empty : ": " + units.Describe());
        }

        var layerChanges = recording.Actions.Count(a => a.Kind == RecordedActionKind.LayerChanged);
        text.Append(CultureInfo.InvariantCulture, $"- {recording.ActionCount} actions ({recording.CommandCount} commands, {layerChanges} layer changes); objects: ")
            .AppendLine(CultureInfo.InvariantCulture, $"{recording.TotalCreated} created, {recording.TotalModified} modified, {recording.TotalErased} erased");
        var use = recording.CommandUse();
        if (use.Count > 0)
        {
            text.Append("- Commands used: ").AppendLine(string.Join(", ", use.Take(25).Select(u => u.Count > 1 ? $"{u.Name} x{u.Count}" : u.Name)) + (use.Count > 25 ? ", ..." : string.Empty));
        }

        var layers = recording.Actions.SelectMany(a => a.Objects).Where(o => o.Change == ObjectChange.Created && o.Layer.Length > 0)
            .GroupBy(o => o.Layer, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Layer: g.Key, Count: g.Count()))
            .OrderByDescending(p => p.Count)
            .ToList();
        if (layers.Count > 0)
        {
            text.Append("- New objects by layer: ").AppendLine(string.Join(", ", layers.Take(15).Select(l => $"{l.Layer} ({l.Count})")));
        }

        if (recording.Overflow.Count > 0)
        {
            var more = recording.Overflow.Values.Sum();
            text.Append(CultureInfo.InvariantCulture, $"- Not detailed: {more} more actions beyond the limit of {recording.MaxActions} (")
                .Append(string.Join(", ", recording.Overflow.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key} x{p.Value}")))
                .AppendLine(")");
        }

        if (recording.ViewCommands.Count > 0)
        {
            text.Append("- View changes (not listed): ").AppendLine(string.Join(", ", recording.ViewCommands.OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}")));
        }

        text.AppendLine("- Lengths are in drawing units, with feet-inches in brackets. Points are (x,y) in drawing units.");
        text.AppendLine();
    }

    private static void AppendStep(StringBuilder text, UserRecording recording, RecordedStep step)
    {
        var time = step.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        var end = step.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        text.Append(CultureInfo.InvariantCulture, $"## Step {step.Number} - {step.Title} ({time}").Append(end == time ? string.Empty : "-" + end).AppendLine(")");
        text.AppendLine();
        var number = 0;
        foreach (var action in step.Actions)
        {
            switch (action.Kind)
            {
                case RecordedActionKind.LayerChanged:
                    text.Append("- Current layer set to **").Append(action.Name).AppendLine("**");
                    break;
                case RecordedActionKind.DrawingSwitched:
                    text.Append("- Switched to the drawing **").Append(action.Name).AppendLine("**");
                    break;
                case RecordedActionKind.Paused:
                    text.Append("- Recording paused at ").AppendLine(action.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                case RecordedActionKind.Resumed:
                    text.Append("- Recording resumed at ").AppendLine(action.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                default:
                    AppendCommand(text, recording, action, ++number);
                    break;
            }
        }

        text.AppendLine();
    }

    private static void AppendCommand(StringBuilder text, UserRecording recording, RecordedAction action, int number)
    {
        text.Append(CultureInfo.InvariantCulture, $"{number}. **{action.Name}**");
        if (!string.IsNullOrWhiteSpace(action.Layer))
        {
            text.Append(" on layer ").Append(action.Layer);
        }

        text.Append(" (").Append(action.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(action.Outcome switch
        {
            CommandOutcome.Cancelled => ", cancelled",
            CommandOutcome.Failed => ", failed",
            CommandOutcome.Running => ", still running when the recording stopped",
            _ => string.Empty,
        }).AppendLine(")");

        var units = recording.UnitsOf(action.Drawing);
        foreach (var line in action.Lines)
        {
            var (prompt, answer, message) = RecordingText.SplitPromptLine(line);
            if (message is not null)
            {
                text.Append("   - ").AppendLine(RecordingText.Code(message));
            }
            else if (answer is null)
            {
                text.Append("   - ").Append(RecordingText.Code(prompt ?? line)).AppendLine(" (picked on screen or Enter)");
            }
            else
            {
                text.Append("   - ").Append(RecordingText.Code(prompt!)).Append(' ').Append(RecordingText.Code(answer));
                if (RecordingText.LengthNote(prompt, answer, units) is { } note)
                {
                    text.Append(' ').Append(note);
                }

                text.AppendLine();
            }
        }

        if (action.LinesOmitted > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"   - ({action.LinesOmitted} more command-line lines)");
        }

        foreach (var obj in action.Objects)
        {
            var verb = obj.Change switch { ObjectChange.Created => "Created", ObjectChange.Modified => "Modified", _ => "Erased" };
            text.Append("   - ").Append(verb).Append(' ').Append(obj.Type).Append(" on ").Append(obj.Layer);
            text.AppendLine(obj.Text.Length > 0 ? ": " + obj.Text : string.Empty);
        }

        var described = action.Objects.Count;
        var total = action.Created + action.Modified + action.Erased;
        if (total > described)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"   - ({total - described} more objects: {action.Created} created, {action.Modified} modified, {action.Erased} erased in all)");
        }
    }

    /// <summary>"03:12" or "1:05:09".</summary>
    public static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
