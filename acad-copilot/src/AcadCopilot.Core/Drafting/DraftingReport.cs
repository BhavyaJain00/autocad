using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Drafting;

/// <summary>How one step ended.</summary>
public enum StepStatus
{
    /// <summary>The command ended after its inputs (normally, by the inputs given).</summary>
    Finished,

    /// <summary>The command was still waiting for input after the last input; the copilot pressed Esc.</summary>
    LeftWaiting,

    /// <summary>The step was cancelled (Stop in the panel, the user pressed Esc, or a timeout).</summary>
    Cancelled,

    /// <summary>The step could not run or AutoCAD refused it (unknown command, dialog opened, error).</summary>
    Failed,

    /// <summary>The step was not started because the call stopped earlier.</summary>
    NotRun,
}

/// <summary>How the whole call ended.</summary>
public enum RunOutcome
{
    /// <summary>Every step was typed.</summary>
    Completed,

    /// <summary>The call stopped early because of a problem (dialog, timeout, error); later steps were not run.</summary>
    Stopped,

    /// <summary>The user stopped it (Stop in the panel, Esc in AutoCAD) or the CLI went away.</summary>
    Cancelled,

    /// <summary>Nothing was typed: the policy blocked a command.</summary>
    Blocked,

    /// <summary>Nothing was typed: the user declined the approval.</summary>
    Declined,

    /// <summary>Nothing was typed: AutoCAD was busy (a command, dialog or no drawing).</summary>
    Busy,

    /// <summary>The call failed with an error.</summary>
    Failed,
}

/// <summary>An input AutoCAD refused (it printed an error and asked again).</summary>
public sealed record RejectedInput
{
    /// <summary>1-based position of the input within its step.</summary>
    public required int Input { get; init; }

    /// <summary>The text typed.</summary>
    public required string Text { get; init; }

    /// <summary>The prompt it was typed at.</summary>
    public string? Prompt { get; init; }

    /// <summary>AutoCAD's message ("Invalid point.").</summary>
    public required string Message { get; init; }

    /// <summary>How to fix it, when known.</summary>
    public string? Hint { get; init; }
}

/// <summary>What happened in one step (one command and its inputs).</summary>
public sealed record StepReport
{
    /// <summary>1-based step number.</summary>
    public required int Step { get; init; }

    /// <summary>The command typed.</summary>
    public required string Command { get; init; }

    /// <summary>The command the LLM asked for, when the copilot typed a command-line version instead.</summary>
    public string? Requested { get; init; }

    /// <summary>The step's note.</summary>
    public string? Note { get; init; }

    /// <summary>How the step ended.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>The command line as it read: each prompt with what was typed at it ("Specify first point: 0,0").</summary>
    public IReadOnlyList<string>? Transcript { get; init; }

    /// <summary>The prompt the command was still waiting at when the copilot pressed Esc.</summary>
    public string? WaitingAt { get; init; }

    /// <summary>Inputs AutoCAD rejected.</summary>
    public IReadOnlyList<RejectedInput>? Rejected { get; init; }

    /// <summary>Inputs that were not typed, and why (the command had already ended, a prompt did not take spaces...).</summary>
    public IReadOnlyList<string>? NotTyped { get; init; }

    /// <summary>Why <see cref="NotTyped"/> inputs were not typed.</summary>
    public string? NotTypedReason { get; init; }

    /// <summary>Other command-line messages ("2 found", "Current settings: ...").</summary>
    public IReadOnlyList<string>? Messages { get; init; }

    /// <summary>Summaries of objects created (capped; see <see cref="CreatedCount"/>).</summary>
    public IReadOnlyList<JsonObject>? Created { get; init; }

    /// <summary>Number of objects created.</summary>
    public int CreatedCount { get; init; }

    /// <summary>Summaries of objects modified (capped).</summary>
    public IReadOnlyList<JsonObject>? Modified { get; init; }

    /// <summary>Number of objects modified.</summary>
    public int ModifiedCount { get; init; }

    /// <summary>Summaries of objects erased (capped).</summary>
    public IReadOnlyList<JsonObject>? Erased { get; init; }

    /// <summary>Number of objects erased.</summary>
    public int ErasedCount { get; init; }

    /// <summary>Error that stopped the step, if any.</summary>
    public string? Error { get; init; }

    /// <summary>Syntax fixes the copilot applied in this step (<see cref="InputFix.Describe"/>), in order.</summary>
    public IReadOnlyList<string>? AutoFixed { get; init; }

    /// <summary>Milliseconds the step took.</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>The result data of run_commands.</summary>
public sealed record RunCommandsReport
{
    /// <summary>The call's title.</summary>
    public required string Title { get; init; }

    /// <summary>How the call ended.</summary>
    public required RunOutcome Outcome { get; init; }

    /// <summary>"watch" or "fast".</summary>
    public required string Pace { get; init; }

    /// <summary>Number of steps asked for.</summary>
    public required int StepsTotal { get; init; }

    /// <summary>Number of steps that were started.</summary>
    public int StepsRun { get; init; }

    /// <summary>Per-step reports.</summary>
    public IReadOnlyList<StepReport> Steps { get; init; } = [];

    /// <summary>Objects created, by DXF type.</summary>
    public IReadOnlyDictionary<string, int>? CreatedByType { get; init; }

    /// <summary>Total objects created.</summary>
    public int CreatedCount { get; init; }

    /// <summary>Total objects modified.</summary>
    public int ModifiedCount { get; init; }

    /// <summary>Total objects erased.</summary>
    public int ErasedCount { get; init; }

    /// <summary>True when the whole call is one undo step (UNDO Begin/End worked).</summary>
    public bool OneUndoStep { get; init; }

    /// <summary>The drawing's units, as the copilot read them ("architectural (LUNITS 4, INSUNITS 1): ...").</summary>
    public string? Units { get; init; }

    /// <summary>Why the call stopped early, if it did.</summary>
    public string? StoppedReason { get; init; }

    /// <summary>Notes for the LLM (replaced commands, settings, problems).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>The checkpoint created before this call was typed (restore_checkpoint goes back to it), if any.</summary>
    public string? Checkpoint { get; init; }

    /// <summary>Every syntax fix the copilot applied, as "step N COMMAND: ..." lines (self-healing).</summary>
    public IReadOnlyList<string> AutoFixes() =>
        Steps.SelectMany(s => (s.AutoFixed ?? []).Select(f => $"step {s.Step} {s.Command}: {f}")).ToList();

    /// <summary>Milliseconds the call took.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// One-paragraph summary for the tool result message: what was typed, what was drawn, and every problem first
    /// ("Typed 3 of 3 commands (watch mode). Created 6 objects: LINE 4, TEXT 2. PROBLEMS: step 2 OFFSET: input 'x'
    /// rejected (Invalid point.).").
    /// </summary>
    public string Summarize()
    {
        var text = new StringBuilder();
        var problems = Problems();
        text.Append(Outcome switch
        {
            RunOutcome.Completed => $"Typed {Plural(StepsRun, "command")} ({Pace} mode).",
            RunOutcome.Stopped => $"Stopped after {StepsRun} of {StepsTotal} commands.",
            RunOutcome.Cancelled => $"Cancelled after {StepsRun} of {StepsTotal} commands.",
            RunOutcome.Blocked => "Nothing was typed: a command is not allowed.",
            RunOutcome.Declined => "Nothing was typed: the user declined.",
            RunOutcome.Busy => "Nothing was typed: AutoCAD is busy.",
            _ => $"Failed after {StepsRun} of {StepsTotal} commands.",
        });

        if (StoppedReason is not null)
        {
            text.Append(' ').Append(StoppedReason.TrimEnd('.')).Append('.');
        }

        if (CreatedCount + ModifiedCount + ErasedCount > 0)
        {
            text.Append(' ');
            var parts = new List<string>();
            if (CreatedCount > 0)
            {
                var byType = CreatedByType is { Count: > 0 }
                    ? ": " + string.Join(", ", CreatedByType.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}"))
                    : string.Empty;
                parts.Add($"created {Plural(CreatedCount, "object")}{byType}");
            }

            if (ModifiedCount > 0)
            {
                parts.Add($"modified {ModifiedCount}");
            }

            if (ErasedCount > 0)
            {
                parts.Add($"erased {ErasedCount}");
            }

            var sentence = string.Join("; ", parts);
            text.Append(char.ToUpperInvariant(sentence[0])).Append(sentence, 1, sentence.Length - 1).Append('.');
        }
        else if (StepsRun > 0)
        {
            text.Append(" No objects were created or changed.");
        }

        var fixes = AutoFixes();
        if (fixes.Count > 0)
        {
            text.Append(" Auto-fixed: ").Append(string.Join("; ", fixes.Select(f => f.TrimEnd('.')))).Append('.');
        }

        if (problems.Count > 0)
        {
            text.Append(" PROBLEMS: ").Append(string.Join(" ", problems));
        }

        if (OneUndoStep && CreatedCount + ModifiedCount + ErasedCount > 0)
        {
            text.Append(" The whole call is one undo step.");
        }

        if (Checkpoint is not null)
        {
            text.Append(" Checkpoint '").Append(Checkpoint).Append("' was created before it (restore_checkpoint goes back there).");
        }

        return text.ToString();
    }

    /// <summary>Every problem of the call as short sentences (rejected inputs, commands left waiting, errors, inputs not typed).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var step in Steps)
        {
            var label = $"step {step.Step} {step.Command}:";
            foreach (var rejected in step.Rejected ?? [])
            {
                problems.Add($"{label} input {rejected.Input} '{rejected.Text}' rejected ({rejected.Message.TrimEnd('.')}).");
            }

            if (step.Status == StepStatus.LeftWaiting)
            {
                problems.Add($"{label} still waiting at '{step.WaitingAt}' after its inputs; the copilot pressed Esc.");
            }

            if (step.NotTyped is { Count: > 0 } notTyped)
            {
                problems.Add($"{label} {Plural(notTyped.Count, "input")} not typed ({step.NotTypedReason?.TrimEnd('.') ?? "the command had ended"}).");
            }

            if (step.Error is not null)
            {
                problems.Add($"{label} {step.Error.TrimEnd('.')}.");
            }
        }

        return problems;
    }

    private static string Plural(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : noun + "s");
}

/// <summary>
/// Collects what happens in one step while the drafting engine types it, then builds the <see cref="StepReport"/>.
/// Caps the transcript (first and last lines), messages and entity lists so results stay small. Not thread-safe:
/// used on AutoCAD's main thread only.
/// </summary>
public sealed class StepRecorder
{
    /// <summary>Most transcript lines kept (first half and last half).</summary>
    public const int MaxTranscriptLines = 40;

    /// <summary>Most messages kept.</summary>
    public const int MaxMessages = 12;

    /// <summary>Most entity summaries listed per category (created, modified, erased).</summary>
    public const int MaxEntitiesListed = 15;

    private readonly PlannedStep _step;
    private readonly List<string> _transcript = [];
    private readonly List<RejectedInput> _rejected = [];
    private readonly List<string> _messages = [];
    private readonly List<string> _notTyped = [];
    private readonly List<string> _autoFixed = [];
    private int _omittedMessages;

    /// <summary>Starts recording <paramref name="step"/>.</summary>
    public StepRecorder(PlannedStep step)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        Status = StepStatus.NotRun;
    }

    /// <summary>The step being recorded.</summary>
    public PlannedStep Step => _step;

    /// <summary>How the step ended (set by the engine).</summary>
    public StepStatus Status { get; set; }

    /// <summary>The prompt the command was left waiting at.</summary>
    public string? WaitingAt { get; set; }

    /// <summary>An error that stopped the step.</summary>
    public string? Error { get; set; }

    /// <summary>Why inputs were not typed.</summary>
    public string? NotTypedReason { get; set; }

    /// <summary>Milliseconds the step took.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Number of inputs typed so far.</summary>
    public int TypedCount { get; private set; }

    /// <summary>Number of rejected inputs so far.</summary>
    public int RejectedCount => _rejected.Count;

    /// <summary>Records the command line after typing the command.</summary>
    public void CommandTyped(string command) => _transcript.Add("Command: " + command);

    /// <summary>Records an input typed at <paramref name="prompt"/> ("" = Enter).</summary>
    public void Typed(string? prompt, string input)
    {
        TypedCount++;
        var shown = input.Length == 0 ? "<Enter>" : input;
        var promptText = CommandLineText.Clean(prompt);
        _transcript.Add(promptText.Length == 0 ? shown : promptText + " " + shown);
    }

    /// <summary>Records the prompt shown at the end of the step (or "Command:" when the command finished).</summary>
    public void FinalPrompt(string? prompt)
    {
        var text = CommandLineText.Clean(prompt);
        if (text.Length > 0)
        {
            _transcript.Add(text);
        }
    }

    /// <summary>Records a rejected input (1-based index within the step).</summary>
    public void Rejected(int inputNumber, string input, string? prompt, string message, string? hint)
    {
        if (_rejected.Count < 20)
        {
            _rejected.Add(new RejectedInput
            {
                Input = inputNumber,
                Text = input,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? null : CommandLineText.Clean(prompt),
                Message = CommandLineText.Clean(message),
                Hint = hint,
            });
        }
    }

    /// <summary>Records a syntax fix the copilot applied (self-healing).</summary>
    public void AutoFixed(InputFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        if (_autoFixed.Count < 20)
        {
            _autoFixed.Add(fix.Describe());
        }
    }

    /// <summary>Number of fixes recorded so far.</summary>
    public int AutoFixCount => _autoFixed.Count;

    /// <summary>Records informative command-line messages (duplicates in a row are dropped).</summary>
    public void Messages(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var text = CommandLineText.Clean(line);
            if (text.Length == 0 || (_messages.Count > 0 && _messages[^1] == text))
            {
                continue;
            }

            if (_messages.Count >= MaxMessages)
            {
                _omittedMessages++;
                continue;
            }

            _messages.Add(text.Length > 200 ? text[..200] + "..." : text);
        }
    }

    /// <summary>Records inputs that were not typed.</summary>
    public void NotTyped(IEnumerable<string> inputs, string reason)
    {
        _notTyped.AddRange(inputs.Select(i => i.Length == 0 ? "<Enter>" : i));
        NotTypedReason ??= reason;
    }

    /// <summary>
    /// Records the inputs left over when the command ended before all of them were typed. They are never typed
    /// (at the Command prompt they would start other commands). Leftover Enters ("") alone are harmless extra
    /// finishing Enters and are not reported as a problem.
    /// </summary>
    public void CommandEndedEarly(IReadOnlyList<string> remaining)
    {
        ArgumentNullException.ThrowIfNull(remaining);
        if (remaining.All(i => i.Length == 0))
        {
            IgnoredEnters += remaining.Count;
            return;
        }

        NotTyped(remaining, "the command had already ended, and at the Command prompt they would have started other commands");
    }

    /// <summary>Number of leftover Enters that were not needed because the command had already ended.</summary>
    public int IgnoredEnters { get; private set; }

    /// <summary>Builds the report with the entity summaries of this step (lists are capped, counts are exact).</summary>
    public StepReport Build(
        IReadOnlyList<JsonObject>? created = null,
        int createdCount = 0,
        IReadOnlyList<JsonObject>? modified = null,
        int modifiedCount = 0,
        IReadOnlyList<JsonObject>? erased = null,
        int erasedCount = 0)
    {
        var messages = _messages.ToList();
        if (_omittedMessages > 0)
        {
            messages.Add($"... {_omittedMessages} more messages");
        }

        return new StepReport
        {
            Step = _step.Number,
            Command = _step.Command,
            Requested = _step.RequestedCommand == _step.Command ? null : _step.RequestedCommand,
            Note = _step.Note,
            Status = Status,
            Transcript = NullIfEmpty(CapTranscript(_transcript)),
            WaitingAt = WaitingAt,
            Rejected = NullIfEmpty(_rejected.ToList()),
            NotTyped = NullIfEmpty(_notTyped.ToList()),
            NotTypedReason = _notTyped.Count > 0 ? NotTypedReason : null,
            Messages = NullIfEmpty(messages),
            Created = NullIfEmpty(created?.Take(MaxEntitiesListed).ToList()),
            CreatedCount = createdCount,
            Modified = NullIfEmpty(modified?.Take(MaxEntitiesListed).ToList()),
            ModifiedCount = modifiedCount,
            Erased = NullIfEmpty(erased?.Take(MaxEntitiesListed).ToList()),
            ErasedCount = erasedCount,
            Error = Error,
            AutoFixed = NullIfEmpty(_autoFixed.ToList()),
            ElapsedMs = ElapsedMs,
        };
    }

    /// <summary>Keeps the first and last <see cref="MaxTranscriptLines"/>/2 lines with a marker in between.</summary>
    public static IReadOnlyList<string> CapTranscript(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count <= MaxTranscriptLines)
        {
            return lines.ToList();
        }

        const int half = MaxTranscriptLines / 2;
        var capped = new List<string>(MaxTranscriptLines + 1);
        capped.AddRange(lines.Take(half));
        capped.Add($"... {lines.Count - MaxTranscriptLines} lines omitted ...");
        capped.AddRange(lines.Skip(lines.Count - half));
        return capped;
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T>? list) => list is { Count: > 0 } ? list : null;
}
