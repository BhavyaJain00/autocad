using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>State of one tool call row.</summary>
public enum ActivityState
{
    /// <summary>Spinner.</summary>
    Running,

    /// <summary>Green check.</summary>
    Succeeded,

    /// <summary>Red cross.</summary>
    Failed,

    /// <summary>Stopped before it finished.</summary>
    Cancelled,
}

/// <summary>What a "CAD activity" row shows, decided by the tool.</summary>
public enum ActivityKind
{
    /// <summary>Any other tool: a readable title plus the argument summary.</summary>
    Other,

    /// <summary>run_commands: the call's own title and one line per AutoCAD command the AI typed.</summary>
    Commands,

    /// <summary>capture_view: "Looked at the drawing" (with the picture when it reached the panel).</summary>
    Look,
}

/// <summary>
/// Receives the AutoCAD tool calls the plug-in runs for the AI, with their full arguments and results, so the panel can
/// show what was typed (each run_commands command with its inputs and how it ended), what changed (created / erased
/// objects, warnings) and what the AI looked at (the capture_view picture). The CLIs only stream short summaries of the
/// arguments and results (ToolCallStarted.Summary, ToolCallFinished.Summary), so these details can only come from the
/// plug-in. Implemented by <see cref="ChatViewModel"/>; call it from any thread: it returns at once and never throws.
/// Calls made while no panel turn is running (other MCP clients) are ignored.
/// </summary>
public interface IToolActivitySink
{
    /// <summary>An AutoCAD tool call is about to run.</summary>
    /// <param name="toolName">Bare tool name ("run_commands").</param>
    /// <param name="arguments">The call's arguments as received (the panel keeps its own copy).</param>
    void OnToolCallStarting(string toolName, JsonObject? arguments);

    /// <summary>An AutoCAD tool call finished (also when it failed or was rejected).</summary>
    /// <param name="toolName">Bare tool name.</param>
    /// <param name="arguments">The call's arguments (they pair the result with its start).</param>
    /// <param name="result">The result returned to the AI, images included.</param>
    void OnToolCallFinished(string toolName, JsonObject? arguments, ToolResult result);
}

/// <summary>How one typed command ended, when the plug-in reported it.</summary>
public enum CommandStepState
{
    /// <summary>No outcome known (not reported yet): a command-prompt marker.</summary>
    Unknown,

    /// <summary>The command finished.</summary>
    Done,

    /// <summary>The command needed attention: left waiting (Esc pressed), cancelled, or inputs rejected / not typed.</summary>
    Warning,

    /// <summary>The command failed.</summary>
    Failed,

    /// <summary>The command was not started because the call stopped earlier.</summary>
    NotRun,
}

/// <summary>One AutoCAD command of a run_commands call, as typed into the command line.</summary>
public sealed class CommandStepItem : ObservableObject
{
    private string _command;
    private string? _requested;
    private CommandStepState _state;
    private string? _problem;
    private string? _autoFixed;

    /// <summary>Creates the step.</summary>
    /// <param name="number">1-based position in the call.</param>
    /// <param name="command">Command name as asked for ("LINE").</param>
    /// <param name="inputs">Inputs in typing order ("" = Enter).</param>
    /// <param name="note">The AI's note for the step ("plot boundary"), if any.</param>
    public CommandStepItem(int number, string command, IReadOnlyList<string> inputs, string? note)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Number = number;
        _command = CommandEcho.CommandName(command);
        Inputs = inputs;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    /// <summary>1-based position in the call.</summary>
    public int Number { get; }

    /// <summary>Command name as typed ("LINE", "-LAYER"): the requested name until the plug-in reports what it typed.</summary>
    public string Command
    {
        get => _command;
        private set
        {
            if (Set(ref _command, value))
            {
                OnPropertiesChanged(nameof(InputsText), nameof(InputsDisplay), nameof(Text), nameof(ToolTip));
            }
        }
    }

    /// <summary>The inputs as typed.</summary>
    public IReadOnlyList<string> Inputs { get; }

    /// <summary>"0,0 -> @30'&lt;0 -> @40'&lt;90" (shortened when long; empty when the command had no inputs).</summary>
    public string InputsText => CommandEcho.FormatInputs(Inputs, Math.Max(24, CommandEcho.DefaultMaxLength - Command.Length - 2));

    /// <summary>What follows the command name on the row: two spaces and <see cref="InputsText"/> (empty without inputs).</summary>
    public string InputsDisplay => InputsText is { Length: > 0 } inputs ? "  " + inputs : string.Empty;

    /// <summary>"LINE  0,0 -> @30'&lt;0 -> @40'&lt;90".</summary>
    public string Text => Command + InputsDisplay;

    /// <summary>The AI's note for this command, or null.</summary>
    public string? Note { get; }

    /// <summary>Tooltip: the full command line (never shortened), the note, what was asked for and any problem.</summary>
    public string ToolTip
    {
        get
        {
            var all = CommandEcho.FormatInputs(Inputs, int.MaxValue);
            var text = new StringBuilder(all.Length == 0 ? Command : Command + "  " + all);
            if (Note is not null)
            {
                text.AppendLine().Append(Note);
            }

            if (_requested is not null)
            {
                text.AppendLine().Append("Asked for ").Append(_requested).Append("; typed the command-line version ").Append(Command).Append('.');
            }

            if (Problem is not null)
            {
                text.AppendLine().Append(Problem);
            }

            if (AutoFixedText is not null)
            {
                text.AppendLine().Append(AutoFixedText);
            }

            return text.ToString();
        }
    }

    /// <summary>Outcome, when reported.</summary>
    public CommandStepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertiesChanged(nameof(IsUnknown), nameof(IsDone), nameof(IsWarning), nameof(IsFailed), nameof(IsNotRun));
            }
        }
    }

    /// <summary>No outcome known (a neutral command-prompt marker).</summary>
    public bool IsUnknown => State == CommandStepState.Unknown;

    /// <summary>Finished.</summary>
    public bool IsDone => State == CommandStepState.Done;

    /// <summary>Needed attention.</summary>
    public bool IsWarning => State == CommandStepState.Warning;

    /// <summary>Failed.</summary>
    public bool IsFailed => State == CommandStepState.Failed;

    /// <summary>Not started.</summary>
    public bool IsNotRun => State == CommandStepState.NotRun;

    /// <summary>What went wrong, shown under the command, or null.</summary>
    public string? Problem
    {
        get => _problem;
        set
        {
            if (Set(ref _problem, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
            {
                OnPropertiesChanged(nameof(HasProblem), nameof(ToolTip));
            }
        }
    }

    /// <summary>True when <see cref="Problem"/> is shown.</summary>
    public bool HasProblem => Problem is not null;

    /// <summary>
    /// "Auto-fixed: added the missing Enter" when the copilot repaired this command's inputs (run_commands auto-fix:
    /// dialog version retyped as its '-' version, a missing Enter, feet-inches syntax), or null.
    /// </summary>
    public string? AutoFixedText
    {
        get => _autoFixed;
        private set
        {
            if (Set(ref _autoFixed, value))
            {
                OnPropertiesChanged(nameof(HasAutoFixed), nameof(ToolTip));
            }
        }
    }

    /// <summary>True when <see cref="AutoFixedText"/> is shown.</summary>
    public bool HasAutoFixed => AutoFixedText is not null;

    /// <summary>Shows how the command ended, from the run_commands report.</summary>
    public void ApplyReport(StepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var typed = CommandEcho.CommandName(report.Command);
        if (!string.IsNullOrWhiteSpace(report.Command) && typed != Command)
        {
            _requested = Command;
            Command = typed;
        }

        var hasInputProblems = report.Rejected is { Count: > 0 } || report.NotTyped is { Count: > 0 };
        State = report.Status switch
        {
            StepStatus.Finished => hasInputProblems ? CommandStepState.Warning : CommandStepState.Done,
            StepStatus.LeftWaiting or StepStatus.Cancelled => CommandStepState.Warning,
            StepStatus.Failed => CommandStepState.Failed,
            StepStatus.NotRun => CommandStepState.NotRun,
            _ => CommandStepState.Unknown,
        };
        Problem = ProblemText(report);
        AutoFixedText = report.AutoFixed is { Count: > 0 } fixes
            ? "Auto-fixed: " + string.Join("; ", fixes.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().TrimEnd('.'))) + "."
            : null;
        OnPropertyChanged(nameof(ToolTip));
    }

    private static string? ProblemText(StepReport report)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.Error))
        {
            parts.Add(Sentence(report.Error));
        }

        if (report.Rejected is { Count: > 0 } rejected)
        {
            var first = rejected[0];
            var more = rejected.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (+{rejected.Count - 1} more)") : string.Empty;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Input {first.Input} \"{first.Text}\" rejected: {Sentence(first.Message)}{more}"));
        }

        if (report.Status == StepStatus.LeftWaiting)
        {
            parts.Add(string.IsNullOrWhiteSpace(report.WaitingAt)
                ? "Still waiting after its inputs; Esc was pressed."
                : $"Still waiting at \"{report.WaitingAt.Trim()}\"; Esc was pressed.");
        }
        else if (report.Status == StepStatus.Cancelled)
        {
            parts.Add("Cancelled.");
        }

        if (report.NotTyped is { Count: > 0 } notTyped)
        {
            var what = notTyped.Count == 1 ? "1 input was" : string.Create(CultureInfo.InvariantCulture, $"{notTyped.Count} inputs were");
            parts.Add($"{what} not typed ({(report.NotTypedReason ?? "the command had ended").TrimEnd('.')}).");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?') ? trimmed : trimmed + ".";
    }
}

/// <summary>Formats AutoCAD commands and inputs the way they were typed: "LINE  0,0 -> @30'&lt;0 -> @40'&lt;90".</summary>
public static class CommandEcho
{
    /// <summary>Between two inputs.</summary>
    public const string Separator = " -> ";

    /// <summary>Shown for an input that is only Enter ("").</summary>
    public const string EnterSymbol = "↵";

    /// <summary>Longest step line before the inputs are cut.</summary>
    public const int DefaultMaxLength = 96;

    /// <summary>Command name for display: trimmed and upper case ("line" -> "LINE", "-layer" -> "-LAYER").</summary>
    public static string CommandName(string? command)
    {
        var name = (command ?? string.Empty).Trim();
        return name.Length == 0 ? "?" : name.ToUpperInvariant();
    }

    /// <summary>
    /// Inputs joined with " -> ". An input that is only Enter shows as ↵; the Enters that close the command at the end
    /// are left out ("LINE  0,0 -> @30'&lt;0", not "... -> ↵"). Longer than <paramref name="maxLength"/> characters: the
    /// remaining inputs are counted instead ("0,0 -> @30'&lt;0 -> … +5 more").
    /// </summary>
    public static string FormatInputs(IReadOnlyList<string> inputs, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var count = inputs.Count;
        while (count > 0 && IsEnter(inputs[count - 1]))
        {
            count--;
        }

        if (count == 0)
        {
            return string.Empty;
        }

        var shown = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            shown.Add(Show(inputs[i]));
        }

        var full = string.Join(Separator, shown);
        if (full.Length <= maxLength)
        {
            return full;
        }

        var text = new StringBuilder();
        for (var i = 0; i < shown.Count; i++)
        {
            var left = shown.Count - i - 1;
            var piece = (i == 0 ? string.Empty : Separator) + shown[i];
            var tail = left == 0 ? string.Empty : Separator + More(left);
            if (text.Length + piece.Length + tail.Length <= maxLength)
            {
                text.Append(piece);
                continue;
            }

            if (i == 0)
            {
                // Even the first input does not fit: cut it.
                var suffix = left == 0 ? string.Empty : Separator + More(left);
                var room = Math.Max(4, maxLength - suffix.Length);
                return shown[0][..Math.Min(shown[0].Length, room - 1)] + "…" + suffix;
            }

            return text.Append(Separator).Append(More(shown.Count - i)).ToString();
        }

        return text.ToString();
    }

    private static string More(int count) => string.Create(CultureInfo.InvariantCulture, $"… +{count} more");

    private static bool IsEnter(string? input) => string.IsNullOrWhiteSpace(input);

    private static string Show(string? input) =>
        IsEnter(input)
            ? EnterSymbol
            : input!.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>One "CAD activity" row: a tool call the AI made.</summary>
public sealed class ToolActivityItem : ObservableObject
{
    /// <summary>Commands listed before the "Show n more" link.</summary>
    public const int CollapsedStepCount = 4;

    /// <summary>Warnings listed under a row; further ones are counted.</summary>
    public const int MaxWarnings = 3;

    private readonly ActivityGroupItem _group;
    private readonly DateTime _startedUtc;
    private ActivityState _state = ActivityState.Running;
    private string? _resultSummary;
    private string? _error;
    private bool _isExpanded;
    private bool _showAllSteps;
    private TimeSpan? _duration;
    private string? _callTitle;
    private int? _declaredStepCount;
    private IReadOnlyList<CommandStepItem> _steps = [];
    private string? _outcomeText;
    private string? _learningMessage;
    private IReadOnlyList<string> _warnings = [];
    private ImageSource? _picture;
    private string? _checkpoint;
    private string? _exportFile;

    /// <summary>Creates a running row.</summary>
    /// <param name="group">The group the row belongs to.</param>
    /// <param name="callId">CLI tool-call id.</param>
    /// <param name="name">Bare tool name.</param>
    /// <param name="summary">Argument summary from the CLI.</param>
    /// <param name="server">MCP server name, or null for the CLI's own tools.</param>
    public ToolActivityItem(ActivityGroupItem group, string callId, string name, string summary, string? server)
    {
        _group = group;
        CallId = callId;
        Name = name;
        Summary = summary;
        Server = server;
        Kind = ToolActivityText.KindOf(name);
        if (Kind == ActivityKind.Commands && RunCommandsSummary.TryParse(summary, out var title, out var count))
        {
            _callTitle = title;
            _declaredStepCount = count;
        }

        _startedUtc = DateTime.UtcNow;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded, () => HasDetails);
        ToggleStepsCommand = new RelayCommand(() => ShowAllSteps = !ShowAllSteps);
    }

    /// <summary>CLI tool-call id.</summary>
    public string CallId { get; }

    /// <summary>Bare tool name ("run_commands").</summary>
    public string Name { get; }

    /// <summary>What the row shows for this tool.</summary>
    public ActivityKind Kind { get; }

    /// <summary>
    /// Row title: the call's own title for run_commands ("Outer walls"), "Looked at the drawing" for capture_view,
    /// otherwise a readable tool name ("Listed layers").
    /// </summary>
    public string Title => Kind == ActivityKind.Commands
        ? _callTitle ?? (AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase) ? "Blender 5.3 commands" : "AutoCAD commands")
        : ToolActivityText.WebTitle(Name, Summary, State) ?? ToolActivityText.Title(Name, State);

    /// <summary>Muted text after the title: "3 commands" for run_commands, nothing for capture_view, else the argument summary.</summary>
    public string SubtitleText => Kind switch
    {
        ActivityKind.Commands => StepCountText ?? string.Empty,
        ActivityKind.Look => string.Empty,
        _ when ToolActivityText.IsWebTool(Name) => string.Empty,
        _ => Summary,
    };

    /// <summary>"1 command" / "4 commands" for run_commands (null when the count is not known).</summary>
    public string? StepCountText
    {
        get
        {
            var count = _steps.Count > 0 ? _steps.Count : _declaredStepCount;
            return count switch
            {
                null => null,
                1 => "1 command",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count} commands"),
            };
        }
    }

    /// <summary>Argument summary from the CLI.</summary>
    public string Summary { get; }

    /// <summary>True when there is an argument summary.</summary>
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    /// <summary>MCP server name, or null for the CLI's own tools.</summary>
    public string? Server { get; }

    /// <summary>True for AutoCAD tools (served by the AutoCAD MCP server), false for the CLI's own tools.</summary>
    public bool IsAcadTool => Server is not null;

    /// <summary>Current state.</summary>
    public ActivityState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertiesChanged(nameof(IsRunning), nameof(IsSucceeded), nameof(IsFailed), nameof(IsCancelled), nameof(Title), nameof(ShowResultLine));
                _group.Refresh();
            }
        }
    }

    /// <summary>True while running (spinner).</summary>
    public bool IsRunning => State == ActivityState.Running;

    /// <summary>True when it succeeded (check).</summary>
    public bool IsSucceeded => State == ActivityState.Succeeded;

    /// <summary>True when it failed (cross).</summary>
    public bool IsFailed => State == ActivityState.Failed;

    /// <summary>True when stopped before finishing.</summary>
    public bool IsCancelled => State == ActivityState.Cancelled;

    /// <summary>Result summary from the CLI (for the learning tools the plug-in's message, when it reported one).</summary>
    public string? ResultSummary
    {
        get => _learningMessage ?? _resultSummary;
        private set => Set(ref _resultSummary, value);
    }

    /// <summary>
    /// True when the CLI's result summary is shown under a run_commands or capture_view row. Hidden once the plug-in
    /// reported the full result (counts and warnings say more) and for other tools (it is in the details). The learning
    /// tools always show it ("Remembered: walls are 9\" thick").
    /// </summary>
    public bool ShowResultLine =>
        IsSucceeded
        && (ToolActivityText.IsLearning(Name) || (Kind != ActivityKind.Other && !HasResultDetails))
        && !string.IsNullOrWhiteSpace(ResultSummary);

    /// <summary>Error text when it failed.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>True when an error is shown under the row.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>Elapsed time once finished (hidden for instant calls, whose timing the CLI did not stream).</summary>
    public string DurationText => _duration is { TotalSeconds: >= 0.1 } d ? ToolNames.FormatDuration(d) : string.Empty;

    /// <summary>The commands of a run_commands call (empty until the plug-in reported the call).</summary>
    public IReadOnlyList<CommandStepItem> Steps => _steps;

    /// <summary>True when commands are listed.</summary>
    public bool HasSteps => _steps.Count > 0;

    /// <summary>The commands shown: the first <see cref="CollapsedStepCount"/>, or all after "Show more".</summary>
    public IReadOnlyList<CommandStepItem> VisibleSteps =>
        ShowAllSteps || _steps.Count <= CollapsedStepCount ? _steps : [.. _steps.Take(CollapsedStepCount)];

    /// <summary>True when some commands are hidden behind "Show n more".</summary>
    public bool HasHiddenSteps => _steps.Count > CollapsedStepCount;

    /// <summary>"Show 3 more" / "Show fewer".</summary>
    public string MoreStepsText => ShowAllSteps
        ? "Show fewer"
        : string.Create(CultureInfo.InvariantCulture, $"Show {_steps.Count - CollapsedStepCount} more");

    /// <summary>True when every command is listed.</summary>
    public bool ShowAllSteps
    {
        get => _showAllSteps;
        set
        {
            if (Set(ref _showAllSteps, value))
            {
                OnPropertiesChanged(nameof(VisibleSteps), nameof(MoreStepsText));
            }
        }
    }

    /// <summary>"4 created · 1 erased" from the plug-in's result, or null.</summary>
    public string? OutcomeText
    {
        get => _outcomeText;
        private set
        {
            if (Set(ref _outcomeText, value))
            {
                OnPropertyChanged(nameof(HasOutcome));
            }
        }
    }

    /// <summary>True when <see cref="OutcomeText"/> is shown.</summary>
    public bool HasOutcome => OutcomeText is not null;

    /// <summary>AutoCAD warnings and tool notes from the plug-in's result (at most <see cref="MaxWarnings"/> plus a count).</summary>
    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set
        {
            _warnings = value;
            OnPropertiesChanged(nameof(Warnings), nameof(HasWarnings));
        }
    }

    /// <summary>True when warnings are listed.</summary>
    public bool HasWarnings => _warnings.Count > 0;

    /// <summary>The picture capture_view returned, when it reached the panel (null otherwise).</summary>
    public ImageSource? Picture
    {
        get => _picture;
        private set
        {
            if (Set(ref _picture, value))
            {
                OnPropertyChanged(nameof(HasPicture));
            }
        }
    }

    /// <summary>True when the picture is shown.</summary>
    public bool HasPicture => Picture is not null;

    /// <summary>
    /// "Checkpoint 'before Outer walls' saved first (restore_checkpoint goes back)": the checkpoint run_commands created
    /// before typing (from its report), or null.
    /// </summary>
    public string? CheckpointText => _checkpoint is null ? null : $"Checkpoint '{_checkpoint}' saved first (the AI can go back to it).";

    /// <summary>True when <see cref="CheckpointText"/> is shown.</summary>
    public bool HasCheckpoint => _checkpoint is not null;

    /// <summary>The PNG export_view_png wrote (full path, from its result data), or null.</summary>
    public string? ExportFile
    {
        get => _exportFile;
        private set
        {
            if (Set(ref _exportFile, value))
            {
                OnPropertiesChanged(nameof(HasExportFile), nameof(ExportFileName));
            }
        }
    }

    /// <summary>True when the "Open exports folder" link is shown.</summary>
    public bool HasExportFile => ExportFile is not null;

    /// <summary>The exported picture's file name ("House-front_20261120-101500.png").</summary>
    public string ExportFileName => ExportFile is { } file ? Path.GetFileName(file) : string.Empty;

    /// <summary>The call's full arguments, once the plug-in reported them (a private copy).</summary>
    public JsonObject? Arguments { get; private set; }

    /// <summary>True once the plug-in reported the call's arguments.</summary>
    public bool HasCallDetails { get; private set; }

    /// <summary>True once the plug-in reported the call's result.</summary>
    public bool HasResultDetails { get; private set; }

    /// <summary>True when the row can be expanded.</summary>
    public bool HasDetails => HasSummary || !string.IsNullOrWhiteSpace(ResultSummary);

    /// <summary>True when the details are shown.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Text shown when expanded.</summary>
    public string DetailsText
    {
        get
        {
            var text = new StringBuilder();
            if (HasSummary)
            {
                text.Append("Input: ").Append(Summary);
            }

            if (!string.IsNullOrWhiteSpace(ResultSummary))
            {
                if (text.Length > 0)
                {
                    text.AppendLine();
                }

                text.Append("Result: ").Append(ResultSummary);
            }

            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append("Tool: ").Append(Server is null ? Name : $"{Server} / {Name}");
            return text.ToString();
        }
    }

    /// <summary>Expands/collapses the details.</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>Shows all commands / the first few.</summary>
    public RelayCommand ToggleStepsCommand { get; }

    /// <summary>Marks the call finished (from the CLI's stream).</summary>
    public void Finish(bool success, string summary, string? error)
    {
        _duration = DateTime.UtcNow - _startedUtc;
        ResultSummary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        Error = success ? null : (string.IsNullOrWhiteSpace(error) ? (string.IsNullOrWhiteSpace(summary) ? "The tool failed." : summary) : error);
        State = success ? ActivityState.Succeeded : ActivityState.Failed;
        OnPropertiesChanged(nameof(DurationText), nameof(HasDetails), nameof(DetailsText), nameof(ShowResultLine));
        ToggleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Marks a still-running call as stopped.</summary>
    public void Cancel()
    {
        if (State == ActivityState.Running)
        {
            _duration = DateTime.UtcNow - _startedUtc;
            State = ActivityState.Cancelled;
            OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>Shows the call's full arguments reported by the plug-in (run_commands: its title and commands).</summary>
    public void ApplyArguments(JsonObject? arguments)
    {
        Arguments = arguments;
        HasCallDetails = true;
        if (Kind == ActivityKind.Commands)
        {
            if (RunCommandsArguments.Title(arguments) is { } title)
            {
                _callTitle = title;
            }

            _steps = RunCommandsArguments.Steps(arguments);
            OnPropertiesChanged(nameof(Title), nameof(Steps), nameof(HasSteps), nameof(VisibleSteps), nameof(HasHiddenSteps), nameof(MoreStepsText), nameof(StepCountText), nameof(SubtitleText));
        }
    }

    /// <summary>
    /// Shows the result reported by the plug-in: for run_commands how each command ended (from its
    /// <see cref="RunCommandsReport"/>), the created / changed / erased counts, warnings, and capture_view's picture.
    /// </summary>
    public void ApplyResult(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        HasResultDetails = true;
        if (ToolActivityText.IsLearning(Name) && !string.IsNullOrWhiteSpace(result.Message))
        {
            _learningMessage = result.Message.Trim();
            OnPropertiesChanged(nameof(ResultSummary), nameof(HasDetails), nameof(DetailsText));
        }

        var warnings = new List<string>();
        var report = Kind == ActivityKind.Commands ? RunCommandsResult.TryRead(result.Data) : null;
        if (report is not null)
        {
            ApplyReport(report);
            OutcomeText = ToolActivityText.Outcome(report.CreatedCount, report.ModifiedCount, report.ErasedCount);
            if (report.Outcome != RunOutcome.Completed && !string.IsNullOrWhiteSpace(report.StoppedReason))
            {
                warnings.Add(report.StoppedReason);
            }

            warnings.AddRange(report.Warnings ?? []);
            if (!string.IsNullOrWhiteSpace(report.Checkpoint) && !string.Equals(_checkpoint, report.Checkpoint.Trim(), StringComparison.Ordinal))
            {
                _checkpoint = report.Checkpoint.Trim();
                OnPropertiesChanged(nameof(CheckpointText), nameof(HasCheckpoint));
            }
        }
        else
        {
            OutcomeText = ToolActivityText.Outcome(result);
        }

        if (result.Success && string.Equals(Name, ExportViewPngTool, StringComparison.Ordinal))
        {
            ExportFile = ExportedFile(result.Data);
        }

        warnings.AddRange(result.Warnings);
        var distinct = warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.Ordinal).ToList();
        Warnings = distinct.Count <= MaxWarnings
            ? distinct
            : [.. distinct.Take(MaxWarnings), string.Create(CultureInfo.InvariantCulture, $"{distinct.Count - MaxWarnings} more warnings")];
        if (result.Images.Count > 0)
        {
            Picture = ActivityPicture.Decode(result.Images);
        }

        OnPropertyChanged(nameof(ShowResultLine));
    }

    // export_view_png (AcadCopilot.Core.Presentation.PresentationToolDefinitions.ExportViewPng).
    private const string ExportViewPngTool = "export_view_png";

    private static string? ExportedFile(JsonNode? data)
    {
        try
        {
            return data is JsonObject json && json["file"] is JsonValue value && value.TryGetValue<string>(out var file) && Path.IsPathFullyQualified(file)
                ? file
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ApplyReport(RunCommandsReport report)
    {
        if (_steps.Count == 0 && report.Steps.Count > 0)
        {
            // No arguments were reported: list the commands from the report (their inputs are unknown here).
            _steps = [.. report.Steps.Select(s => new CommandStepItem(s.Step, s.Requested ?? s.Command, [], s.Note))];
            if (_callTitle is null && !string.IsNullOrWhiteSpace(report.Title))
            {
                _callTitle = report.Title.Trim();
            }

            OnPropertiesChanged(nameof(Title), nameof(Steps), nameof(HasSteps), nameof(VisibleSteps), nameof(HasHiddenSteps), nameof(MoreStepsText), nameof(StepCountText), nameof(SubtitleText));
        }

        foreach (var stepReport in report.Steps)
        {
            _steps.FirstOrDefault(s => s.Number == stepReport.Step)?.ApplyReport(stepReport);
        }
    }
}

/// <summary>A "CAD activity" group: consecutive tool calls between assistant messages.</summary>
public sealed class ActivityGroupItem : ChatItem
{
    private bool _isExpanded = true;

    /// <summary>Creates an empty group.</summary>
    public ActivityGroupItem()
    {
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>Rows, in call order.</summary>
    public ObservableCollection<ToolActivityItem> Rows { get; } = [];

    /// <summary>True once assistant text followed this group (new calls start a new group).</summary>
    public bool IsClosed { get; set; }

    /// <summary>True when rows are shown.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>True while any row runs.</summary>
    public bool HasRunning => Rows.Any(r => r.IsRunning);

    /// <summary>True when any row failed.</summary>
    public bool HasFailures => Rows.Any(r => r.IsFailed);

    /// <summary>Header title ("3D Scene activity" in Blender, "CAD activity" in AutoCAD).</summary>
    public string GroupTitle => AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase)
        ? "3D Scene activity"
        : "CAD activity";

    /// <summary>Right-hand count text ("3 actions", "3 actions · 1 failed").</summary>
    public string CountText
    {
        get
        {
            var total = Rows.Count;
            var failed = Rows.Count(r => r.IsFailed);
            var text = total == 1 ? "1 action" : string.Create(CultureInfo.InvariantCulture, $"{total} actions");
            return failed > 0 ? string.Create(CultureInfo.InvariantCulture, $"{text} · {failed} failed") : text;
        }
    }

    /// <summary>Expands/collapses the rows.</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>Adds a row.</summary>
    public ToolActivityItem Add(string callId, string name, string summary, string? server)
    {
        var row = new ToolActivityItem(this, callId, name, summary, server);
        Rows.Add(row);
        Refresh();
        return row;
    }

    /// <summary>Recomputes the summary properties.</summary>
    public void Refresh() => OnPropertiesChanged(nameof(HasRunning), nameof(HasFailures), nameof(CountText));
}

/// <summary>Titles and outcome texts of "CAD activity" rows.</summary>
public static partial class ToolActivityText
{
    /// <summary>Name of the tool that returns a picture of the drawing view.</summary>
    public const string CaptureView = "capture_view";

    // (running, done, plain) titles of the AutoCAD tools; plain is used for failed or stopped calls.
    private static readonly Dictionary<string, (string Running, string Done, string Plain)> Titles = new(StringComparer.Ordinal)
    {
        [CaptureView] = ("Looking at the drawing", "Looked at the drawing", "Look at the drawing"),
        ["get_drawing_info"] = ("Reading the drawing info", "Read the drawing info", "Read the drawing info"),
        ["list_layers"] = ("Listing layers", "Listed layers", "List layers"),
        ["list_entities"] = ("Listing objects", "Listed objects", "List objects"),
        ["get_entity"] = ("Reading an object", "Read an object", "Read an object"),
        ["get_sysvars"] = ("Reading system variables", "Read system variables", "Read system variables"),
        [DraftingTools.CancelCommand] = ("Pressing Esc", "Cancelled the active command", "Cancel the active command"),
        [DraftingTools.Undo] = ("Undoing", "Undid the last drawing step", "Undo"),
        ["acad_status"] = ("Checking the AutoCAD connection", "Checked the AutoCAD connection", "Check the AutoCAD connection"),
        [KnowledgeTools.RecallKnowledge] = ("Checking what I've learned", "Checked what I've learned", "Check what I've learned"),
        [KnowledgeTools.RememberRule] = ("Remembering your rule", "Remembered your rule", "Remember your rule"),
        [KnowledgeTools.SaveRecipe] = ("Saving a recipe", "Saved a recipe", "Save a recipe"),
        [KnowledgeTools.SaveLesson] = ("Saving a lesson", "Saved a lesson", "Save a lesson"),
        [KnowledgeTools.Forget] = ("Forgetting an item", "Forgot an item", "Forget an item"),
        [KnowledgeTools.LearnStyleFromDrawing] = ("Learning your style from this drawing", "Learned your style from this drawing", "Learn your style from this drawing"),
        [KnowledgeTools.ListRecordings] = ("Looking for your recordings", "Looked at your recordings", "Look at your recordings"),
        [KnowledgeTools.ReadRecording] = ("Reading your recording", "Read your recording", "Read your recording"),
        ["pick_point"] = ("Waiting for your click in the drawing", "Got your click", "Ask you to click a point"),
        ["get_selection"] = ("Reading your selection", "Read your selection", "Read your selection"),
        ["select_objects"] = ("Selecting objects to show you", "Selected objects to show you", "Select objects to show you"),
    };

    /// <summary>The CLIs' web search tools (Claude Code, Codex, Gemini CLI).</summary>
    public static bool IsWebSearch(string name) => name is "WebSearch" or "web_search" or "google_web_search";

    /// <summary>The CLIs' read-a-web-page tools (Claude Code, Gemini CLI).</summary>
    public static bool IsWebFetch(string name) => name is "WebFetch" or "web_fetch";

    /// <summary>True for any of the CLIs' web tools (Web research).</summary>
    public static bool IsWebTool(string name) => IsWebSearch(name) || IsWebFetch(name);

    /// <summary>
    /// Title of a web tool row: "Searching the web: 30x50 east facing plan" / "Searched the web: ..." and "Reading
    /// houseplans.com" / "Read houseplans.com", from the CLI's argument summary. Null for other tools.
    /// </summary>
    public static string? WebTitle(string name, string? summary, ActivityState state)
    {
        if (IsWebSearch(name))
        {
            var query = WebQuery(summary);
            var verb = state switch
            {
                ActivityState.Running => "Searching the web",
                ActivityState.Succeeded => "Searched the web",
                _ => "Search the web",
            };
            return query is null ? verb : $"{verb}: {query}";
        }

        if (IsWebFetch(name))
        {
            var site = WebSite(summary) ?? "a web page";
            return state switch
            {
                ActivityState.Running => "Reading " + site,
                ActivityState.Succeeded => "Read " + site,
                _ => "Read " + site,
            };
        }

        return null;
    }

    // "query: 30x50 east facing plan, allowed_domains: 1 item" (Claude / Gemini) or the bare query (Codex).
    private static string? WebQuery(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var match = WebQueryPattern().Match(summary);
        var query = (match.Success ? match.Groups[1].Value : summary).Trim();
        return query.Length == 0 ? null : query;
    }

    // The host of the first http(s) address in the summary, without "www.".
    private static string? WebSite(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var match = WebHostPattern().Match(summary);
        if (!match.Success)
        {
            return null;
        }

        var host = match.Groups[1].Value.TrimEnd('.', '…');
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    [GeneratedRegex(@"^\s*query:\s*(.*?)(?:,\s*[a-z_]+:\s.*)?$", RegexOptions.Singleline)]
    private static partial Regex WebQueryPattern();

    [GeneratedRegex(@"https?://([^/\s,?#…]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WebHostPattern();

    /// <summary>True for the learning tools (their result message is shown under the row).</summary>
    public static bool IsLearning(string name) => name is KnowledgeTools.RecallKnowledge or KnowledgeTools.RememberRule
        or KnowledgeTools.SaveRecipe or KnowledgeTools.SaveLesson or KnowledgeTools.Forget or KnowledgeTools.LearnStyleFromDrawing
        or KnowledgeTools.ListRecordings or KnowledgeTools.ReadRecording;

    /// <summary>The row kind for a tool.</summary>
    public static ActivityKind KindOf(string name) => name switch
    {
        DraftingTools.RunCommands => ActivityKind.Commands,
        CaptureView => ActivityKind.Look,
        _ => ActivityKind.Other,
    };

    /// <summary>Readable title of a tool call in a state ("Looking at the drawing" / "Looked at the drawing").</summary>
    public static string Title(string name, ActivityState state)
    {
        var isBlender = AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase);
        if (isBlender && BlenderTitles.TryGetValue(name, out var blenderTitles))
        {
            return state switch
            {
                ActivityState.Running => blenderTitles.Running,
                ActivityState.Succeeded => blenderTitles.Done,
                _ => blenderTitles.Plain,
            };
        }

        if (!Titles.TryGetValue(name, out var titles))
        {
            return ToolNames.Humanize(name);
        }

        return state switch
        {
            ActivityState.Running => titles.Running,
            ActivityState.Succeeded => titles.Done,
            _ => titles.Plain,
        };
    }

    private static readonly Dictionary<string, (string Running, string Done, string Plain)> BlenderTitles = new(StringComparer.Ordinal)
    {
        [CaptureView] = ("Looking at the 3D viewport", "Looked at the 3D viewport", "Look at the 3D viewport"),
        ["get_drawing_info"] = ("Reading the Blender scene info", "Read the Blender scene info", "Read the Blender scene info"),
        ["list_layers"] = ("Listing collections & materials", "Listed collections & materials", "List collections & materials"),
        ["list_entities"] = ("Listing 3D scene objects", "Listed 3D scene objects", "List 3D scene objects"),
        ["get_entity"] = ("Reading a 3D object", "Read a 3D object", "Read a 3D object"),
        ["get_sysvars"] = ("Reading render & scene settings", "Read render & scene settings", "Read render & scene settings"),
        [DraftingTools.Undo] = ("Undoing", "Undid the last 3D step", "Undo"),
        ["acad_status"] = ("Checking the Blender connection", "Checked the Blender connection", "Check the Blender connection"),
        [KnowledgeTools.LearnStyleFromDrawing] = ("Learning your style from this 3D scene", "Learned your style from this 3D scene", "Learn your style from this 3D scene"),
        ["pick_point"] = ("Reading the 3D cursor location", "Got the 3D cursor location", "Read the 3D cursor location"),
    };

    /// <summary>Outcome of a result from its created / modified / deleted ids (see <see cref="Outcome(int, int, int)"/>).</summary>
    public static string? Outcome(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Outcome(result.CreatedIds.Count, result.ModifiedIds.Count, result.DeletedIds.Count);
    }

    /// <summary>"4 created · 2 changed · 1 erased" (zero counts left out), or null when nothing changed.</summary>
    public static string? Outcome(int created, int modified, int erased)
    {
        var parts = new List<string>(3);
        if (created > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{created} created"));
        }

        if (modified > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{modified} changed"));
        }

        if (erased > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{erased} erased"));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>Reads the <see cref="RunCommandsReport"/> that run_commands returns as its result data.</summary>
public static class RunCommandsResult
{
    /// <summary>The report in <paramref name="data"/>, or null when the data is not a run_commands report.</summary>
    public static RunCommandsReport? TryRead(JsonNode? data)
    {
        if (data is not JsonObject)
        {
            return null;
        }

        try
        {
            return CopilotJson.FromNode<RunCommandsReport>(data);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            CopilotLog.Info("The run_commands result data is not a run_commands report: " + ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Reads the title and command count out of the argument summary the CLI runners stream for run_commands
/// ("title: Outer walls, steps: 3 items", see CliText.SummarizeArguments). Best effort: anything else is left alone.
/// </summary>
public static partial class RunCommandsSummary
{
    /// <summary>Parses <paramref name="summary"/>; false when neither the title nor the count was found.</summary>
    public static bool TryParse(string? summary, out string? title, out int? stepCount)
    {
        title = null;
        stepCount = null;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        var titleMatch = TitleRegex().Match(summary);
        if (titleMatch.Success && titleMatch.Groups["title"].Value.Trim() is { Length: > 0 } found)
        {
            title = found;
        }

        var stepsMatch = StepsRegex().Match(summary);
        if (stepsMatch.Success && int.TryParse(stepsMatch.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            stepCount = count;
        }

        return title is not null || stepCount is not null;
    }

    [GeneratedRegex(@"(?:^|, )title: (?<title>.+?)(?=, [A-Za-z_][A-Za-z0-9_]*: |, …$|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"(?:^|, )steps: (?<n>\d{1,4}) items?(?=, |$)", RegexOptions.CultureInvariant)]
    private static partial Regex StepsRegex();
}

/// <summary>Reads run_commands arguments: { title, steps: [ { command, inputs: [..], note } ] }.</summary>
public static class RunCommandsArguments
{
    /// <summary>The call's title, or null.</summary>
    public static string? Title(JsonObject? arguments) =>
        CopilotJson.TryGetString(arguments?["title"])?.Trim() is { Length: > 0 } title ? title : null;

    /// <summary>The commands with their inputs, in order (steps without a command name are skipped).</summary>
    public static IReadOnlyList<CommandStepItem> Steps(JsonObject? arguments)
    {
        if (arguments?["steps"] is not JsonArray steps)
        {
            return [];
        }

        var items = new List<CommandStepItem>(steps.Count);
        foreach (var node in steps)
        {
            if (node is not JsonObject step || CopilotJson.TryGetString(step["command"]) is not { } command || string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var inputs = step["inputs"] is JsonArray array ? array.Select(InputText).ToList() : [];
            items.Add(new CommandStepItem(items.Count + 1, command, inputs, CopilotJson.TryGetString(step["note"])));
        }

        return items;
    }

    /// <summary>Parses the JSON text of run_commands arguments (approval cards carry them as text); null when it is not JSON.</summary>
    public static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string InputText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => node.ToJsonString(),
    };
}

/// <summary>Turns a tool result image (capture_view's PNG) into a small frozen bitmap for the row.</summary>
public static class ActivityPicture
{
    /// <summary>Widest picture decoded, in pixels (the row shows it at most ~360 px wide).</summary>
    public const int DecodeWidth = 720;

    /// <summary>The first image that decodes, or null.</summary>
    public static ImageSource? Decode(IReadOnlyList<ToolImage> images)
    {
        foreach (var image in images)
        {
            if (image.Validate() is not null)
            {
                continue;
            }

            try
            {
                using var stream = new MemoryStream(image.GetBytes(), writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (image.Width is { } width && width > DecodeWidth)
                {
                    bitmap.DecodePixelWidth = DecodeWidth;
                }

                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                CopilotLog.Warn("The drawing picture could not be shown in the panel", ex);
            }
        }

        return null;
    }
}
