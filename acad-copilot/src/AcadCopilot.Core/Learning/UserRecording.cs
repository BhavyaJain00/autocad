using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Learning;

/// <summary>State of the "Learn from me" recorder.</summary>
public enum RecorderState
{
    /// <summary>Not recording (nothing is captured).</summary>
    Off,

    /// <summary>Capturing the user's own work.</summary>
    Recording,

    /// <summary>Paused: nothing new is captured, what was recorded is kept.</summary>
    Paused,
}

/// <summary>What one recorded action is.</summary>
public enum RecordedActionKind
{
    /// <summary>A command the user ran.</summary>
    Command,

    /// <summary>The user made another layer current (CLAYER).</summary>
    LayerChanged,

    /// <summary>The user switched to another drawing.</summary>
    DrawingSwitched,

    /// <summary>The user paused the recording.</summary>
    Paused,

    /// <summary>The user resumed the recording.</summary>
    Resumed,
}

/// <summary>How a recorded command ended.</summary>
public enum CommandOutcome
{
    /// <summary>Still running (when the recording stopped).</summary>
    Running,

    /// <summary>Finished normally.</summary>
    Ended,

    /// <summary>Cancelled (Esc).</summary>
    Cancelled,

    /// <summary>Failed.</summary>
    Failed,
}

/// <summary>How a command touched an object.</summary>
public enum ObjectChange
{
    /// <summary>New object.</summary>
    Created,

    /// <summary>Existing object changed.</summary>
    Modified,

    /// <summary>Object erased.</summary>
    Erased,
}

/// <summary>One object a recorded command created, modified or erased, described in one line.</summary>
/// <param name="Change">Created, modified or erased.</param>
/// <param name="Type">DXF type ("LINE", "LWPOLYLINE", "INSERT").</param>
/// <param name="Layer">Its layer.</param>
/// <param name="Text">Compact geometry ("from (0,0) to (120,0), length 120 (10'-0")").</param>
public sealed record RecordedObject(ObjectChange Change, string Type, string Layer, string Text);

/// <summary>One thing the user did while recording: a command with its typed answers and objects, or a layer change.</summary>
public sealed class RecordedAction
{
    private readonly List<string> _lines = [];
    private readonly List<RecordedObject> _objects = [];

    /// <summary>What it is.</summary>
    public required RecordedActionKind Kind { get; init; }

    /// <summary>1-based position among the stored actions.</summary>
    public required int Number { get; init; }

    /// <summary>When it started.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The drawing it happened in (file name).</summary>
    public required string Drawing { get; init; }

    /// <summary>The command name, the new current layer, or the drawing switched to.</summary>
    public required string Name { get; init; }

    /// <summary>The current layer when the command started (null when unknown).</summary>
    public string? Layer { get; init; }

    /// <summary>When the command ended (null while running).</summary>
    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>How the command ended.</summary>
    public CommandOutcome Outcome { get; internal set; } = CommandOutcome.Running;

    /// <summary>Command-line lines of the command in time order (prompts with the user's answers, messages).</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Command-line lines left out beyond <see cref="UserRecording.MaxLinesPerCommand"/>.</summary>
    public int LinesOmitted { get; internal set; }

    /// <summary>Objects described (at most <see cref="UserRecording.MaxObjectsPerCommand"/>).</summary>
    public IReadOnlyList<RecordedObject> Objects => _objects;

    /// <summary>Objects created by the command (all, also those not described).</summary>
    public int Created { get; internal set; }

    /// <summary>Objects modified by the command.</summary>
    public int Modified { get; internal set; }

    /// <summary>Objects erased by the command.</summary>
    public int Erased { get; internal set; }

    /// <summary>False for a command beyond the action limit: it is only counted, not stored.</summary>
    public bool IsStored { get; init; } = true;

    internal List<string> MutableLines => _lines;

    internal List<RecordedObject> MutableObjects => _objects;
}

/// <summary>
/// One "Learn from me" recording: the user's own commands (with what they typed at each prompt and the objects each
/// created, modified or erased), current-layer changes and drawing switches, in time order. Pure and AutoCAD-free: the
/// plug-in feeds it from AutoCAD's events and says for every event whether the copilot caused it (then it is ignored).
/// Memory is capped: at most <see cref="MaxActions"/> actions are kept in detail, later ones are only counted by name.
/// Pausing stops capturing without losing anything. Not thread-safe (AutoCAD's main thread only); the panel reads the
/// counters from the same thread.
/// </summary>
public sealed class UserRecording
{
    /// <summary>Default most actions kept in detail.</summary>
    public const int DefaultMaxActions = 2_000;

    /// <summary>Most command-line lines kept per command.</summary>
    public const int MaxLinesPerCommand = 40;

    /// <summary>Most objects described per command (all are counted).</summary>
    public const int MaxObjectsPerCommand = 30;

    /// <summary>Longest text kept per line.</summary>
    public const int MaxTextLength = 240;

    private static readonly HashSet<string> ViewCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ZOOM", "PAN", "REGEN", "REGENALL", "REDRAW", "REDRAWALL", "3DORBIT", "ORBIT", "VIEW", "NAVSWHEEL", "NAVVCUBE",
        "DSVIEWER", "VIEWRES", "VPORTS",
    };

    // The copilot's own commands and the panel: never part of what the user teaches.
    private static readonly HashSet<string> IgnoredCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "COPILOT", "COPILOTSTATUS",
    };

    private readonly Func<DateTimeOffset> _clock;
    private readonly List<RecordedAction> _actions = [];
    private readonly Dictionary<string, int> _overflow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _viewCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DrawingUnits> _units = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _drawings = [];
    private TimeSpan _pausedTotal;
    private DateTimeOffset? _pausedAt;
    private string? _lastLayer;
    private int _storedCount;

    /// <summary>Starts a recording now.</summary>
    /// <param name="clock">Current time (tests).</param>
    /// <param name="maxActions">Most actions kept in detail.</param>
    public UserRecording(Func<DateTimeOffset>? clock = null, int maxActions = DefaultMaxActions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxActions, 1);
        _clock = clock ?? (() => DateTimeOffset.Now);
        MaxActions = maxActions;
        Started = _clock();
        State = RecorderState.Recording;
    }

    /// <summary>Most actions kept in detail.</summary>
    public int MaxActions { get; }

    /// <summary>When the recording started.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>When it was stopped (null while it runs).</summary>
    public DateTimeOffset? Stopped { get; private set; }

    /// <summary>Recording, paused, or off (stopped).</summary>
    public RecorderState State { get; private set; }

    /// <summary>True while new work is captured.</summary>
    public bool IsCapturing => State == RecorderState.Recording;

    /// <summary>Actions kept in detail, in time order.</summary>
    public IReadOnlyList<RecordedAction> Actions => _actions;

    /// <summary>The user's actions: commands and layer changes, including those beyond the limit (only counted).</summary>
    public int ActionCount { get; private set; }

    /// <summary>Commands recorded (stored or only counted).</summary>
    public int CommandCount { get; private set; }

    /// <summary>Actions beyond <see cref="MaxActions"/>, counted by command name (layer changes as "layer change").</summary>
    public IReadOnlyDictionary<string, int> Overflow => _overflow;

    /// <summary>Zoom / pan / regen commands, counted by name and not listed.</summary>
    public IReadOnlyDictionary<string, int> ViewCommands => _viewCommands;

    /// <summary>The drawings worked in, in the order first seen.</summary>
    public IReadOnlyList<string> Drawings => _drawings;

    /// <summary>Objects created by the recorded commands.</summary>
    public int TotalCreated { get; private set; }

    /// <summary>Objects modified by the recorded commands.</summary>
    public int TotalModified { get; private set; }

    /// <summary>Objects erased by the recorded commands.</summary>
    public int TotalErased { get; private set; }

    /// <summary>Time spent paused.</summary>
    public TimeSpan PausedTime => _pausedTotal + (_pausedAt is { } since ? (Stopped ?? _clock()) - since : TimeSpan.Zero);

    /// <summary>Time recorded (from the start to now or the stop, without pauses).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            var span = (Stopped ?? _clock()) - Started - PausedTime;
            return span < TimeSpan.Zero ? TimeSpan.Zero : span;
        }
    }

    /// <summary>True when <paramref name="command"/> only changes the view (zoom, pan, regen).</summary>
    public static bool IsViewCommand(string? command) => ViewCommandNames.Contains(BareName(command));

    /// <summary>The command name without AutoCAD's prefixes ("_.-LAYER" -> "LAYER").</summary>
    public static string BareName(string? command) => (command ?? string.Empty).Trim().TrimStart('_', '.', '-', '\'', '+', '#').ToUpperInvariant();

    /// <summary>Stops capturing; everything recorded stays. No effect unless recording.</summary>
    public void Pause()
    {
        if (State != RecorderState.Recording)
        {
            return;
        }

        State = RecorderState.Paused;
        _pausedAt = _clock();
        AddMarker(RecordedActionKind.Paused, _drawings.LastOrDefault() ?? string.Empty, "Paused");
    }

    /// <summary>Captures again after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        if (State != RecorderState.Paused)
        {
            return;
        }

        if (_pausedAt is { } since)
        {
            _pausedTotal += _clock() - since;
        }

        _pausedAt = null;
        State = RecorderState.Recording;
        AddMarker(RecordedActionKind.Resumed, _drawings.LastOrDefault() ?? string.Empty, "Resumed");
    }

    /// <summary>Ends the recording (nothing more is captured).</summary>
    public void Stop()
    {
        if (State == RecorderState.Off)
        {
            return;
        }

        var now = _clock();
        if (_pausedAt is { } since)
        {
            _pausedTotal += now - since;
            _pausedAt = null;
        }

        Stopped = now;
        State = RecorderState.Off;
    }

    /// <summary>The units of <paramref name="drawing"/> (for feet-inch notes in the recording file).</summary>
    public void SetUnits(string drawing, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(units);
        _units[drawing ?? string.Empty] = units;
        NoteDrawing(drawing);
    }

    /// <summary>The units set for <paramref name="drawing"/>, or null.</summary>
    public DrawingUnits? UnitsOf(string? drawing) => _units.TryGetValue(drawing ?? string.Empty, out var units) ? units : null;

    /// <summary>
    /// A command started. Returns the action to finish with <see cref="CommandFinished"/>, or null when it is not
    /// recorded: paused or stopped, caused by the copilot (<paramref name="byCopilot"/>), a view command (only counted),
    /// or the copilot's own COPILOT commands. Beyond <see cref="MaxActions"/> the returned action is not stored
    /// (<see cref="RecordedAction.IsStored"/> false) but its objects are still counted.
    /// </summary>
    public RecordedAction? CommandStarted(string drawing, string command, string? layer, bool byCopilot)
    {
        var name = (command ?? string.Empty).Trim();
        if (!IsCapturing || byCopilot || name.Length == 0 || IgnoredCommandNames.Contains(BareName(name)))
        {
            return null;
        }

        NoteDrawing(drawing);
        if (IsViewCommand(name))
        {
            var bare = BareName(name);
            _viewCommands[bare] = _viewCommands.TryGetValue(bare, out var views) ? views + 1 : 1;
            return null;
        }

        ActionCount++;
        CommandCount++;
        var stored = StoredActionCount() < MaxActions;
        var action = new RecordedAction
        {
            Kind = RecordedActionKind.Command,
            Number = stored ? _actions.Count + 1 : 0,
            At = _clock(),
            Drawing = drawing ?? string.Empty,
            Name = name.ToUpperInvariant(),
            Layer = string.IsNullOrWhiteSpace(layer) ? _lastLayer : layer,
            IsStored = stored,
        };
        if (stored)
        {
            _actions.Add(action);
            _storedCount++;
        }
        else
        {
            var key = action.Name;
            _overflow[key] = _overflow.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        if (!string.IsNullOrWhiteSpace(layer))
        {
            _lastLayer = layer;
        }

        return action;
    }

    /// <summary>
    /// A command returned by <see cref="CommandStarted"/> ended: keeps its command-line lines (cleaned, capped) and
    /// objects (all counted, at most <see cref="MaxObjectsPerCommand"/> described). Null is ignored. Also works after a
    /// pause or stop (a command that started while recording is completed).
    /// </summary>
    public void CommandFinished(RecordedAction? action, CommandOutcome outcome, IEnumerable<string>? lines, IEnumerable<RecordedObject>? objects)
    {
        if (action is null || action.Outcome != CommandOutcome.Running)
        {
            return;
        }

        action.Outcome = outcome == CommandOutcome.Running ? CommandOutcome.Ended : outcome;
        action.EndedAt = _clock();
        foreach (var obj in objects ?? [])
        {
            switch (obj.Change)
            {
                case ObjectChange.Created:
                    action.Created++;
                    TotalCreated++;
                    break;
                case ObjectChange.Modified:
                    action.Modified++;
                    TotalModified++;
                    break;
                default:
                    action.Erased++;
                    TotalErased++;
                    break;
            }

            if (action.IsStored && action.MutableObjects.Count < MaxObjectsPerCommand)
            {
                action.MutableObjects.Add(obj with { Text = KnowledgeText.Truncate(KnowledgeText.OneLine(obj.Text), MaxTextLength) });
            }
        }

        if (!action.IsStored || lines is null)
        {
            return;
        }

        var kept = RecordingText.CommandLines(lines, action.Name);
        action.MutableLines.AddRange(kept.Take(MaxLinesPerCommand));
        action.LinesOmitted = Math.Max(0, kept.Count - MaxLinesPerCommand);
    }

    /// <summary>The user made <paramref name="layer"/> current. Ignored while not capturing, for the copilot, or when unchanged.</summary>
    public void LayerChanged(string drawing, string layer, bool byCopilot)
    {
        var name = (layer ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (!IsCapturing || byCopilot)
        {
            // The copilot's (or a paused) change still moves the current layer.
            _lastLayer = name;
            return;
        }

        if (string.Equals(_lastLayer, name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLayer = name;
        NoteDrawing(drawing);
        ActionCount++;
        if (StoredActionCount() < MaxActions)
        {
            _actions.Add(new RecordedAction { Kind = RecordedActionKind.LayerChanged, Number = _actions.Count + 1, At = _clock(), Drawing = drawing ?? string.Empty, Name = name, Layer = name });
            _storedCount++;
        }
        else
        {
            _overflow["layer change"] = _overflow.TryGetValue("layer change", out var n) ? n + 1 : 1;
        }
    }

    /// <summary>The current layer as known now (start value or last change).</summary>
    public void SetCurrentLayer(string? layer)
    {
        if (!string.IsNullOrWhiteSpace(layer))
        {
            _lastLayer = layer.Trim();
        }
    }

    /// <summary>The user switched to <paramref name="drawing"/> (not counted as an action). Ignored for the copilot.</summary>
    public void DrawingSwitched(string drawing, bool byCopilot)
    {
        if (!IsCapturing || byCopilot || string.IsNullOrWhiteSpace(drawing))
        {
            return;
        }

        var current = _actions.LastOrDefault(a => a.Drawing.Length > 0)?.Drawing ?? _drawings.LastOrDefault();
        if (current is null || string.Equals(current, drawing, StringComparison.OrdinalIgnoreCase))
        {
            NoteDrawing(drawing);
            return;
        }

        NoteDrawing(drawing);
        AddMarker(RecordedActionKind.DrawingSwitched, drawing, drawing);
    }

    /// <summary>Commands still running (they started while recording and did not end before the stop).</summary>
    public IEnumerable<RecordedAction> RunningCommands() => _actions.Where(a => a.Kind == RecordedActionKind.Command && a.Outcome == CommandOutcome.Running);

    /// <summary>Stored commands by name with their counts, most used first.</summary>
    public IReadOnlyList<(string Name, int Count)> CommandUse() =>
        _actions.Where(a => a.Kind == RecordedActionKind.Command)
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(p => p.Item2)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

    private int StoredActionCount() => _storedCount;

    private void AddMarker(RecordedActionKind kind, string drawing, string name)
    {
        // Markers are few (pauses and drawing switches); they are kept beyond the limit only while there is room.
        if (_actions.Count < MaxActions + 200)
        {
            _actions.Add(new RecordedAction { Kind = kind, Number = _actions.Count + 1, At = _clock(), Drawing = drawing, Name = name });
        }
    }

    private void NoteDrawing(string? drawing)
    {
        if (!string.IsNullOrWhiteSpace(drawing) && !_drawings.Contains(drawing, StringComparer.OrdinalIgnoreCase))
        {
            _drawings.Add(drawing);
        }
    }
}
