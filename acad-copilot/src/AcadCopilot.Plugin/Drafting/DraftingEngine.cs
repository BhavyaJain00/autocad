using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Json;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CommandTypeFlags = Autodesk.AutoCAD.Internal.CommandTypeFlags;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>Options of one run_commands call besides the plan.</summary>
internal sealed record DraftingOptions
{
    /// <summary>Default options: self-healing on, no progress callback, no checkpoint.</summary>
    public static DraftingOptions Default { get; } = new();

    /// <summary>Apply <see cref="InputRepair"/> syntax fixes and press forgotten finishing Enters (run_commands autoFix).</summary>
    public bool AutoFix { get; init; } = true;

    /// <summary>Called on the main thread after each step that was typed through (1-based step number); for the job journal.</summary>
    public Action<int>? StepFinished { get; init; }

    /// <summary>Name of the checkpoint created before the call, reported in the result.</summary>
    public string? Checkpoint { get; init; }
}

/// <summary>What a run_commands call produced: the report plus handle values for <see cref="ToolResult"/>.</summary>
/// <param name="Report">The full report (ToolResult.Data).</param>
/// <param name="Created">Handle values of objects created (capped).</param>
/// <param name="Modified">Handle values of objects modified (capped).</param>
/// <param name="Erased">Handle values of objects erased (capped).</param>
internal sealed record DraftingOutcome(RunCommandsReport Report, IReadOnlyList<long> Created, IReadOnlyList<long> Modified, IReadOnlyList<long> Erased)
{
    /// <summary>The tool result: success when every step was typed; the summary (problems first) as the message.</summary>
    public ToolResult ToToolResult()
    {
        var warnings = new List<string>(Report.Problems());
        warnings.AddRange(Report.Warnings ?? []);
        var completed = Report.Outcome == RunOutcome.Completed;
        return new ToolResult
        {
            Success = completed,
            Message = Report.Summarize(),
            Data = CopilotJson.ToNode(Report),
            CreatedIds = Created,
            ModifiedIds = Modified,
            DeletedIds = Erased,
            Warnings = warnings,
            Errors = completed ? [] : [Report.StoppedReason ?? Report.Summarize()],
        };
    }
}

/// <summary>
/// Types AutoCAD commands and their inputs into the command line VISIBLY, one input at a time, the way a drafter types
/// (the heart of run_commands). Runs on AutoCAD's main thread inside <see cref="AcadDispatcher.InvokeAsync{T}(Func{Task{T}}, CancellationToken)"/>:
/// every await resumes on the main thread in the application context.
/// <para><b>Mechanism (chosen for AutoCAD 2027 after comparing the options).</b> Each command and each input is sent
/// with <c>Document.SendStringToExecute(text + "\n", activate: true, wrapUpInactiveDoc: false, echoCommand: true)</c>,
/// exactly like keyboard input: AutoCAD echoes it in the command line ("Specify first point: 0,0"), validates it like
/// typed input, and draws the result before the next input is sent. After each input the engine yields to AutoCAD's
/// message loop and polls (30 ms) until AutoCAD has taken the input: a new prompt started (Editor.PromptingForXxx),
/// the command ended/cancelled/failed, AutoCAD became quiescent, or the queued input is gone
/// (Internal.Utils.IsInputPending) and the state changed. Only then, after the watch-mode pause
/// (<see cref="DraftingPace"/>), is the next input typed. This keeps every input visible and gives the engine a clean
/// point to check the prompt, the command-line messages and the command state between inputs.</para>
/// <para><b>Why not Editor.CommandAsync inside DocumentCollection.ExecuteInCommandContextAsync.</b> It passes all
/// arguments of a command in one call like AutoLISP's (command), shows them only through CMDECHO, cannot pause between
/// arguments, cannot see which prompt each argument met or which one AutoCAD rejected, and leaves surplus arguments to
/// the Command prompt. SendStringToExecute is the documented way for modeless UI to type into AutoCAD, and the only one
/// that looks like a person typing.</para>
/// <para><b>Safety.</b> The plan was checked by <see cref="CommandPolicy"/> and approved before the engine starts. While
/// typing: an input is never typed when its command has already ended (it would start another command at the Command
/// prompt); an input containing a space or ';' is typed only at a text prompt (elsewhere a space acts like Enter and
/// would split it); TEXTEVAL=0 keeps "(" literal at text prompts; and <see cref="RunWatch"/> vetoes any forbidden command
/// AutoCAD is about to run. Dialogs are switched off (<see cref="SysvarScope"/>); one that still opens is cancelled
/// (<see cref="DialogCloser"/>) and the call stops with its title. The user's Esc stops the call.</para>
/// <para><b>Self-healing</b> (run_commands autoFix, default on; rules in <see cref="InputRepair"/>, pure and unit-tested):
/// before an input is typed, typographic marks and spaces in coordinates are fixed ("10, 20" -&gt; "10,20"); when AutoCAD
/// rejects an input and asks again, one syntax-only retry is typed at the same prompt (feet-inches or mm/m in drawing units,
/// option keyword typos -&gt; capital letters, degree signs removed); a step still waiting at a finish prompt after its
/// inputs gets the forgotten Enter (at most <see cref="InputRepair.MaxFinishEnters"/>); a command that still opens a
/// dialog box is retyped as its '-' version. Every fix is reported (StepReport.AutoFixed, "Auto-fixed: ..." in the
/// summary); nothing that changes the meaning of the geometry is ever guessed.</para>
/// <para><b>Always restored</b> (also on errors, Stop and timeouts): a still-active command is cancelled with Esc, the
/// UNDO Begin/End group is closed, and the system variables are put back.</para>
/// </summary>
internal sealed class DraftingEngine
{
    private const int PollMs = 30;
    private const int MaxIdsReported = 200;
    private const int MaxObjectsSummarizedPerStep = 5000;

    private static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HeavyInputTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private static readonly HashSet<string> HeavyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "HATCH", "BHATCH", "GRADIENT", "BOUNDARY", "REGEN", "REGENALL", "AUDIT", "PURGE", "OVERKILL", "ARRAY", "ARRAYRECT",
        "ARRAYPOLAR", "ARRAYPATH", "QSAVE", "SAVE", "SAVEAS", "PLOT", "PUBLISH", "INSERT", "XREF", "WBLOCK", "EXPORT",
        "EXPORTPDF", "ZOOM", "EXTRUDE", "PRESSPULL", "UNION", "SUBTRACT", "INTERSECT", "FLATSHOT", "HIDE", "IMAGE",
        "PDFATTACH", "PDFIMPORT", "IMPORT", "BURST", "EXPLODE", "COPY", "MOVE", "MIRROR", "SCALE", "ROTATE", "STRETCH",
    };

    private readonly Document _document;
    private readonly Database _database;
    private readonly DraftingPlan _plan;
    private readonly CommandAliases _aliases;
    private readonly AcadDispatcher _dispatcher;
    private readonly CancellationToken _cancellationToken;
    private readonly DraftingOptions _options;
    private readonly List<string> _warnings = [];
    private bool _selectionWarningLogged;
    private int? _angleUnits;

    private DraftingEngine(Document document, DraftingPlan plan, CommandAliases aliases, AcadDispatcher dispatcher, DraftingOptions options, CancellationToken cancellationToken)
    {
        _document = document;
        _database = document.Database;
        _plan = plan;
        _aliases = aliases;
        _dispatcher = dispatcher;
        _options = options;
        _cancellationToken = cancellationToken;
    }

    private enum WaitResult
    {
        Taken,
        Dialog,
        TextEditor,
        Timeout,
        UserCancelled,
        CancelledByAutoCad,
        Vetoed,
        Cancelled,
        DrawingChanged,
    }

    /// <summary>
    /// Commands of the plan that are AutoLISP-defined or action macros (Express Tools, the user's LISP files, .actm
    /// recordings): the copilot only types AutoCAD's built-in and ObjectARX/.NET commands. Main thread.
    /// </summary>
    public static IReadOnlyList<string> FindScriptedCommands(DraftingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = new List<string>();
        foreach (var step in plan.Steps)
        {
            var names = new List<string> { step.CanonicalName, CommandPolicy.NormalizeName(step.Command) };
            names.AddRange(step.Inputs.Where(i => i.TrimStart().StartsWith('\'')).Select(CommandPolicy.NormalizeName));
            foreach (var name in names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var kind = AcadInput.CommandType(name);
                if (kind is CommandTypeFlags.LispCmd or CommandTypeFlags.ActionMacroCmd)
                {
                    var what = kind == CommandTypeFlags.LispCmd ? "an AutoLISP command (Express Tools or a LISP file)" : "an action macro";
                    problems.Add($"Step {step.Number} ({step.Command}): {name} is {what}; the copilot only types AutoCAD's own commands. Use built-in commands instead.");
                }
            }
        }

        return problems;
    }

    /// <summary>The report for a call that typed nothing (blocked, declined or busy).</summary>
    public static DraftingOutcome NotRun(DraftingPlan plan, RunOutcome outcome, string reason, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var report = new RunCommandsReport
        {
            Title = plan.Title,
            Outcome = outcome,
            Pace = DraftingPace.ModeName,
            StepsTotal = plan.Steps.Count,
            StoppedReason = reason,
            Steps = plan.Steps.Select(s => new StepRecorder(s).Build()).ToList(),
            Warnings = warnings?.ToList() is { Count: > 0 } list ? list : null,
        };
        return new DraftingOutcome(report, [], [], []);
    }

    /// <summary>Runs <paramref name="plan"/> in the active drawing. Main thread (inside the dispatcher).</summary>
    public static Task<DraftingOutcome> RunAsync(DraftingPlan plan, CommandAliases aliases, AcadDispatcher dispatcher, CancellationToken cancellationToken) =>
        RunAsync(plan, aliases, dispatcher, DraftingOptions.Default, cancellationToken);

    /// <summary>Runs <paramref name="plan"/> in the active drawing with <paramref name="options"/>. Main thread (inside the dispatcher).</summary>
    public static Task<DraftingOutcome> RunAsync(DraftingPlan plan, CommandAliases aliases, AcadDispatcher dispatcher, DraftingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        var document = AcadDispatcher.RequireActiveDocument();
        return new DraftingEngine(document, plan, aliases, dispatcher, options, cancellationToken).RunCoreAsync();
    }

    /// <summary>
    /// Why AutoCAD cannot take typed commands right now (a command, script, dialog or text editor is active), or null
    /// when it is idle at the Command prompt. Main thread.
    /// </summary>
    public static string? BusyReason(Document document, AcadDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher.IsModal)
        {
            return "a dialog box is open in AutoCAD. Ask the user to close it, then try again.";
        }

        if (AcadInput.IsTextEditorActive() == true)
        {
            return "a text editor is open in AutoCAD. Ask the user to finish or close it (Esc), then try again.";
        }

        var active = AcadInput.CommandActive();
        var command = document.CommandInProgress;
        if (active == 0 && string.IsNullOrEmpty(command))
        {
            return null;
        }

        var name = string.IsNullOrEmpty(command) ? "a command" : "the command " + command;
        var prompt = AcadInput.LastPrompt();
        return $"{name} is running in AutoCAD{(prompt.Length > 0 ? $" (waiting at '{prompt}')" : string.Empty)}. The user may be in the middle of " +
               "something: ask them to finish it or press Esc, or call cancel_command if they agree; then try again.";
    }

    private async Task<DraftingOutcome> RunCoreAsync()
    {
        var total = Stopwatch.StartNew();
        var units = ToolContext.GetUnits(_database);
        var recorders = _plan.Steps.Select(s => new StepRecorder(s)).ToList();
        foreach (var recorder in recorders.Where(r => r.Step.RewriteNote is not null))
        {
            // Self-healing at plan time: the dialog command was replaced by its command-line version.
            var step = recorder.Step;
            recorder.AutoFixed(InputRepair.CommandLineVersion(
                step.RequestedCommand,
                step.Command,
                $"{CommandPolicy.NormalizeName(step.RequestedCommand)} opens a dialog box, palette or interactive mode; use {step.Command} yourself next time"));
        }

        try
        {
            _angleUnits = _database.Aunits;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            _angleUnits = null;
        }

        if (BusyReason(_document, _dispatcher) is { } busy)
        {
            return NotRun(_plan, RunOutcome.Busy, "AutoCAD is busy: " + busy, _warnings);
        }

        if (AcadInput.GetInt("WORLDUCS") == 0)
        {
            _warnings.Add("The current UCS is not the World UCS: typed coordinates are in the current UCS while reported geometry is in World coordinates.");
        }

        if (!_database.TileMode)
        {
            _warnings.Add("A layout tab (paper space) is active, so the commands work on that layout unless a viewport is active.");
        }

        CopilotLog.Info($"run_commands '{_plan.Title}': {_plan.Steps.Count} steps, {DraftingPace.ModeName} mode.");
        var approved = new HashSet<string>(_plan.ApprovalSteps.Select(s => s.CanonicalName), StringComparer.OrdinalIgnoreCase);
        var outcome = RunOutcome.Completed;
        string? stoppedReason = null;
        var stepsRun = 0;
        var oneUndoStep = false;

        DraftingHistory.Instance.Watch(_document);
        using var ownChanges = DraftingHistory.Instance.Suspend();

        // The watch first: if subscribing fails, nothing has been changed yet. The system variables are applied last
        // before the try, so the finally below always restores them.
        using var watch = new RunWatch(_document, _aliases, approved);
        var sysvars = SysvarScope.Apply(_document);
        _warnings.AddRange(sysvars.Problems.Select(p => "Setting not applied: " + p + "."));
        var groupOpenAtStart = (AcadInput.GetInt("UNDOCTL") & 8) != 0;
        try
        {
            ClearSelection();
            AcadInput.FocusDrawing();
            oneUndoStep = await BeginUndoGroupAsync(watch).ConfigureAwait(true);
            for (var index = 0; index < recorders.Count; index++)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    outcome = RunOutcome.Cancelled;
                    stoppedReason = "Stopped by the user (Stop in the panel)";
                    break;
                }

                if (total.Elapsed > MaxRunTime)
                {
                    outcome = RunOutcome.Stopped;
                    stoppedReason = $"The call ran longer than {MaxRunTime.TotalMinutes:0} minutes; split the work into smaller calls";
                    break;
                }

                watch.CurrentStep = index + 1;
                stepsRun++;
                var stop = await RunStepAsync(recorders[index], watch, units).ConfigureAwait(true);
                if (recorders[index].Status is StepStatus.Finished or StepStatus.LeftWaiting)
                {
                    ReportStepFinished(index + 1);
                }

                if (stop is not null)
                {
                    outcome = stop.Value.Outcome;
                    stoppedReason = stop.Value.Reason;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CopilotLog.Error($"run_commands '{_plan.Title}' failed", ex);
            outcome = RunOutcome.Failed;
            stoppedReason = "Unexpected error while typing: " + ex.Message;
        }
        finally
        {
            await CleanupAsync(watch, oneUndoStep, groupOpenAtStart).ConfigureAwait(true);
            sysvars.Restore(_document);
        }

        if (watch.LispStarts.Count > 0)
        {
            _warnings.Add("AutoLISP code ran during this call (" + string.Join("; ", watch.LispStarts) + "); it came from AutoCAD or the user's LISP setup, not from the copilot.");
        }

        if (!oneUndoStep)
        {
            _warnings.Add("This call is not one undo step (UNDO grouping was not available), so the undo tool cannot take it back.");
        }

        var collected = CollectChanges(watch, recorders, units);

        // "Follow the AI's drawing": show what this call drew when it is off screen or too small (never mid-command).
        if (collected.CreatedIds.Count > 0 && IsActiveDocument() && ViewFollow.IsEnabled() &&
            ViewFollow.FrameCreated(_document, collected.CreatedIds, _dispatcher) is { } framed)
        {
            CopilotLog.Info($"run_commands '{_plan.Title}': {framed}.");
        }

        if (oneUndoStep && collected.CreatedIds.Count + collected.ErasedIds.Count + collected.ModifiedIds.Count > 0)
        {
            DraftingHistory.Instance.Add(new RunRecord
            {
                Database = _database.UnmanagedObject,
                Title = _plan.Title,
                ForeignChangesAtEnd = DraftingHistory.Instance.ForeignChanges(_database),
                Created = collected.CreatedIds,
                Erased = collected.ErasedIds,
                Modified = collected.ModifiedIds,
                ModifiedCount = collected.ModifiedIds.Count,
                OneUndoStep = true,
            });
        }

        var report = new RunCommandsReport
        {
            Title = _plan.Title,
            Outcome = outcome,
            Pace = DraftingPace.ModeName,
            StepsTotal = _plan.Steps.Count,
            StepsRun = stepsRun,
            Steps = collected.Steps,
            CreatedByType = collected.CreatedByType.Count > 0 ? collected.CreatedByType : null,
            CreatedCount = collected.CreatedIds.Count,
            ModifiedCount = collected.ModifiedIds.Count,
            ErasedCount = collected.ErasedIds.Count,
            OneUndoStep = oneUndoStep,
            Units = units.Describe(),
            StoppedReason = stoppedReason,
            Warnings = _warnings.Count > 0 ? _warnings.Distinct().ToList() : null,
            Checkpoint = _options.Checkpoint,
            ElapsedMs = total.ElapsedMilliseconds,
        };
        CopilotLog.Info($"run_commands '{_plan.Title}': {outcome}, {stepsRun}/{_plan.Steps.Count} steps, {report.CreatedCount} created, " +
                        $"{report.ModifiedCount} modified, {report.ErasedCount} erased in {total.ElapsedMilliseconds} ms." +
                        (stoppedReason is null ? string.Empty : " " + stoppedReason));
        return new DraftingOutcome(
            report,
            Handles(collected.CreatedIds),
            Handles(collected.ModifiedIds),
            Handles(collected.ErasedIds));
    }

    private async Task<(RunOutcome Outcome, string Reason)?> RunStepAsync(StepRecorder recorder, RunWatch watch, DrawingUnits units)
    {
        var step = recorder.Step;
        var stopwatch = Stopwatch.StartNew();
        var timeout = HeavyCommands.Contains(step.CanonicalName) || step.CanonicalName.StartsWith("AEC", StringComparison.OrdinalIgnoreCase) || step.CanonicalName.StartsWith("EXPORT", StringComparison.OrdinalIgnoreCase)
            ? HeavyInputTimeout
            : InputTimeout;
        try
        {
            ClearSelection();
            await PauseAsync(DraftingPace.CommandDelayMs).ConfigureAwait(true);
            if (AcadInput.IsCommandActive())
            {
                // Something is still active (an earlier command did not end): Esc first, like a drafter would.
                await EscapeAsync(watch).ConfigureAwait(true);
            }

            if (!IsActiveDocument())
            {
                return Stop(recorder, WaitResult.DrawingChanged, null);
            }

            watch.UnknownCommand = null;
            var historyBefore = AcadInput.History();
            var started = watch.CommandsStarted;
            var sequenceBeforeType = watch.Sequence;
            AcadInput.Type(_document, step.Command + "\n", echo: true);
            recorder.CommandTyped(step.Command);
            recorder.Status = StepStatus.Finished;
            var result = await WaitAsync(watch, timeout, historyBefore).ConfigureAwait(true);
            if (result == WaitResult.Dialog && _options.AutoFix && DashRetry(step) is { } dash)
            {
                // Self-healing: the command opened a dialog box although dialogs are off. Close it and type the
                // command-line version instead (only before any input was typed, so nothing can be answered twice).
                var closed = await CloseDialogAsync(watch).ConfigureAwait(true);
                if (_dispatcher.IsModal || watch.ModalDepth > 0)
                {
                    recorder.Status = StepStatus.Failed;
                    recorder.Error = closed;
                    return (RunOutcome.Stopped, $"Step {step.Number} ({step.Command}): {closed}");
                }

                if (AcadInput.IsCommandActive())
                {
                    await EscapeAsync(watch).ConfigureAwait(true);
                }

                CopilotLog.Info($"Drafting: {step.Command} opened a dialog ({closed}); retrying as {dash}.");
                recorder.AutoFixed(InputRepair.CommandLineVersion(step.Command, dash, $"{step.Command} opened a dialog box, which the copilot closed; use {dash} yourself next time"));
                historyBefore = AcadInput.History();
                started = watch.CommandsStarted;
                watch.UnknownCommand = null;
                sequenceBeforeType = watch.Sequence;
                AcadInput.Type(_document, dash + "\n", echo: true);
                recorder.CommandTyped(dash);
                result = await WaitAsync(watch, timeout, historyBefore).ConfigureAwait(true);
            }

            var commandLines = NewLines(historyBefore);
            recorder.Messages(CommandLineText.Messages(commandLines, "Command:").Where(l => !l.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase)));
            if (result != WaitResult.Taken)
            {
                return await StopAsync(recorder, watch, result, step.Command, timeout).ConfigureAwait(true);
            }

            var unknown = watch.UnknownCommand is not null
                || commandLines.Any(l => CommandLineText.Clean(l).StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase));
            if (unknown && watch.CommandsStarted == started && !AcadInput.IsCommandActive())
            {
                recorder.Status = StepStatus.Failed;
                recorder.Error = $"AutoCAD does not know the command {step.Command}";
                if (step.Inputs.Any(i => i.Length > 0))
                {
                    recorder.NotTyped(step.Inputs, "the command did not start");
                }

                return null;
            }

            for (var i = 0; i < step.Inputs.Count; i++)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    recorder.NotTyped(step.Inputs.Skip(i), "the call was stopped");
                    return await StopAsync(recorder, watch, WaitResult.Cancelled, step.Command, timeout).ConfigureAwait(true);
                }

                if (!AcadInput.IsCommandActive())
                {
                    recorder.CommandEndedEarly(step.Inputs.Skip(i).ToList());
                    break;
                }

                var input = step.Inputs[i];
                await PauseAsync(DraftingPace.InputDelayMs).ConfigureAwait(true);
                if (!IsActiveDocument())
                {
                    return Stop(recorder, WaitResult.DrawingChanged, null);
                }

                var prompt = AcadInput.LastPrompt();
                if (_options.AutoFix && !AcceptsSpaces(prompt, watch, sequenceBeforeType) && InputRepair.BeforeTyping(input, prompt) is { } syntax)
                {
                    recorder.AutoFixed(syntax);
                    input = syntax.Replacement;
                }

                if (CommandLineText.HasSeparator(input) && !AcceptsSpaces(prompt, watch, sequenceBeforeType))
                {
                    recorder.Rejected(
                        i + 1,
                        input,
                        prompt,
                        "Not typed: this prompt does not take spaces or ';' (in AutoCAD they act like Enter here and would split the input).",
                        "Put each answer in its own input; only text prompts (TEXT's 'Enter text:', attribute values) take words with spaces.");
                    recorder.NotTyped(step.Inputs.Skip(i + 1), "an earlier input of this command was refused");
                    recorder.Status = StepStatus.Failed;
                    await EscapeAsync(watch).ConfigureAwait(true);
                    return (RunOutcome.Stopped, $"Step {step.Number} ({step.Command}): input {i + 1} '{input}' was not typed because '{prompt}' does not take spaces");
                }

                var before = AcadInput.History();
                sequenceBeforeType = watch.Sequence;
                AcadInput.Type(_document, input + "\n", echo: true);
                recorder.Typed(prompt, input);
                result = await WaitAsync(watch, timeout, before).ConfigureAwait(true);
                var lines = NewLines(before);
                var rejection = lines.Count > 0 ? CommandLineText.FindRejection(lines) : null;
                recorder.Messages(CommandLineText.Messages(lines, prompt).Where(l => !string.Equals(l, rejection, StringComparison.Ordinal)));
                if (result != WaitResult.Taken)
                {
                    recorder.NotTyped(step.Inputs.Skip(i + 1), "the call stopped");
                    return await StopAsync(recorder, watch, result, input, timeout).ConfigureAwait(true);
                }

                if (rejection is not null && _options.AutoFix)
                {
                    var retry = await RetryRejectedAsync(recorder, watch, i, input, prompt, rejection, units, timeout).ConfigureAwait(true);
                    if (retry.Stop is { } stopped)
                    {
                        recorder.NotTyped(step.Inputs.Skip(i + 1), "the call stopped");
                        return stopped;
                    }

                    if (retry.Fixed)
                    {
                        continue;
                    }

                    rejection = retry.Rejection ?? rejection;
                }

                if (rejection is not null)
                {
                    recorder.Rejected(i + 1, input, prompt, rejection, CommandLineText.HintForRejection(input, prompt, rejection, units));
                    recorder.NotTyped(step.Inputs.Skip(i + 1), "AutoCAD rejected an earlier input of this command, so the rest would have gone to the wrong prompts");
                    recorder.Status = StepStatus.Failed;
                    if (AcadInput.IsCommandActive())
                    {
                        recorder.WaitingAt = AcadInput.LastPrompt();
                        await EscapeAsync(watch).ConfigureAwait(true);
                    }

                    return (RunOutcome.Stopped, $"Step {step.Number} ({step.Command}): AutoCAD rejected input {i + 1} '{input}' ({rejection.TrimEnd('.')})");
                }
            }

            if (AcadInput.IsCommandActive())
            {
                // Give AutoCAD the time to finish the last input before judging: an ARC whose included angle was just
                // typed is still active for a moment while it draws.
                var settle = await SettleAfterInputsAsync(watch).ConfigureAwait(true);
                if (settle != WaitResult.Taken)
                {
                    return await StopAsync(recorder, watch, settle, step.Inputs.Count > 0 ? step.Inputs[^1] : step.Command, timeout).ConfigureAwait(true);
                }
            }

            for (var enters = 0; _options.AutoFix && enters < InputRepair.MaxFinishEnters && AcadInput.IsCommandActive(); enters++)
            {
                // Self-healing: the inputs ended without the finishing "" at a prompt where Enter only ends the command
                // or the selection (LINE's "Specify next point", "Select objects:", TEXT's "Enter text:", -HATCH...).
                var waitingAt = AcadInput.LastPrompt();
                if (!InputRepair.IsFinishPrompt(waitingAt, step.CanonicalName))
                {
                    break;
                }

                var before = AcadInput.History();
                AcadInput.Type(_document, "\n", echo: true);
                recorder.Typed(waitingAt, string.Empty);
                var entered = await WaitAsync(watch, timeout, before).ConfigureAwait(true);
                recorder.Messages(CommandLineText.Messages(NewLines(before), waitingAt));
                if (entered != WaitResult.Taken)
                {
                    return await StopAsync(recorder, watch, entered, string.Empty, timeout).ConfigureAwait(true);
                }

                recorder.AutoFixed(InputRepair.FinishEnter(waitingAt));
                if (AcadInput.IsCommandActive())
                {
                    var settled = await SettleAfterInputsAsync(watch).ConfigureAwait(true);
                    if (settled != WaitResult.Taken)
                    {
                        return await StopAsync(recorder, watch, settled, string.Empty, timeout).ConfigureAwait(true);
                    }
                }
            }

            if (AcadInput.IsCommandActive())
            {
                recorder.WaitingAt = AcadInput.LastPrompt();
                recorder.Status = StepStatus.LeftWaiting;
                await EscapeAsync(watch).ConfigureAwait(true);
            }

            recorder.FinalPrompt(AcadInput.IsCommandActive() ? AcadInput.LastPrompt() : "Command:");
            return null;
        }
        finally
        {
            recorder.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
    }

    // Self-healing of a rejected input: when AutoCAD asks again (the command still waits at a prompt) and a pure syntax
    // fix is known (InputRepair.AfterRejection), types the fixed input once at that prompt. Fixed = the retry was taken
    // without a new rejection; Rejection = the combined message when the retry was rejected too; Stop = the wait failed.
    private async Task<(bool Fixed, string? Rejection, (RunOutcome Outcome, string Reason)? Stop)> RetryRejectedAsync(
        StepRecorder recorder,
        RunWatch watch,
        int inputIndex,
        string input,
        string prompt,
        string rejection,
        DrawingUnits units,
        TimeSpan timeout)
    {
        if (!AcadInput.IsCommandActive() || InputRepair.AfterRejection(input, prompt, rejection, units, _angleUnits) is not { } fix)
        {
            return (false, null, null);
        }

        var promptNow = AcadInput.LastPrompt();
        if (!CommandLineText.IsWaitingForInput(true, promptNow) || CommandLineText.HasSeparator(fix.Replacement))
        {
            return (false, null, null);
        }

        await PauseAsync(DraftingPace.InputDelayMs).ConfigureAwait(true);
        var before = AcadInput.History();
        AcadInput.Type(_document, fix.Replacement + "\n", echo: true);
        recorder.Typed(promptNow, fix.Replacement);
        var result = await WaitAsync(watch, timeout, before).ConfigureAwait(true);
        var lines = NewLines(before);
        var again = lines.Count > 0 ? CommandLineText.FindRejection(lines) : null;
        recorder.Messages(CommandLineText.Messages(lines, promptNow).Where(l => !string.Equals(l, again, StringComparison.Ordinal)));
        if (result != WaitResult.Taken)
        {
            return (false, null, await StopAsync(recorder, watch, result, fix.Replacement, timeout).ConfigureAwait(true));
        }

        if (again is null)
        {
            recorder.AutoFixed(fix with { Reason = $"AutoCAD rejected it ({rejection.TrimEnd('.')}); {fix.Reason}" });
            CopilotLog.Info($"Drafting: step {recorder.Step.Number} input {inputIndex + 1} '{input}' rejected, retyped as '{fix.Replacement}'.");
            return (true, null, null);
        }

        return (false, $"{rejection.TrimEnd('.')}; retried as '{fix.Replacement}', which AutoCAD also rejected ({again.TrimEnd('.')})", null);
    }

    // The command-line version to retype when a step's command opened a dialog box: InputRepair.DashVersion, and only
    // when AutoCAD knows that command.
    private string? DashRetry(PlannedStep step)
    {
        if (InputRepair.DashVersion(step.Command, _aliases) is not { } dash)
        {
            return null;
        }

        return AcadInput.CommandType(dash) is CommandTypeFlags.CoreCmd or CommandTypeFlags.ARXCmd ? dash : null;
    }

    private void ReportStepFinished(int step)
    {
        try
        {
            _options.StepFinished?.Invoke(step);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CopilotLog.Warn("Drafting: recording step progress failed", ex);
        }
    }

    // Waits until AutoCAD has taken the input just typed and waits for the next one (or reached a state the engine
    // must handle). Polls on the main thread; every await gives AutoCAD's message loop the chance to process the input.
    // "Taken" needs evidence: a prompt or command event, LASTPROMPT or CMDACTIVE changed, or new command-line history;
    // then a settled state (CommandLineText.IsSettled): one quiet poll (no new events, nothing queued) AND AutoCAD back
    // at the Command prompt or showing a prompt. While LASTPROMPT still shows the echo of the input ("Specify included
    // angle: -90") AutoCAD is still working on it; judging the command then raced AutoCAD (an ARC reported as "still
    // waiting" with its last value in the prompt). With evidence but no settled state the input counts as taken after
    // 2 s. Without any evidence it counts as taken once AutoCAD reports nothing queued (Internal.Utils.IsInputPending)
    // for 400 ms, or, when that helper is unavailable, after 1.2 s.
    private async Task<WaitResult> WaitAsync(RunWatch watch, TimeSpan timeout, IReadOnlyList<string>? historyBefore)
    {
        var sequence = watch.Sequence;
        var prompt = AcadInput.LastPrompt();
        var active = AcadInput.CommandActive();
        var lastLine = historyBefore is { Count: > 0 } ? historyBefore[^1] : null;
        var stopwatch = Stopwatch.StartNew();
        var woke = false;
        var focused = false;
        var reacted = false;
        var lastSequence = -1;
        while (true)
        {
            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
            if (_cancellationToken.IsCancellationRequested)
            {
                return WaitResult.Cancelled;
            }

            if (watch.Vetoed is not null)
            {
                return WaitResult.Vetoed;
            }

            if (watch.UserCancelledCommand is not null)
            {
                return WaitResult.UserCancelled;
            }

            if (watch.UnexplainedCancel is not null)
            {
                return WaitResult.CancelledByAutoCad;
            }

            if (_dispatcher.IsModal || watch.ModalDepth > 0)
            {
                // Some operations disable the main window for a moment; a dialog stays.
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(true);
                if (_dispatcher.IsModal || watch.ModalDepth > 0)
                {
                    return WaitResult.Dialog;
                }
            }

            if (AcadInput.IsTextEditorActive() == true)
            {
                return WaitResult.TextEditor;
            }

            if (!IsActiveDocument())
            {
                return WaitResult.DrawingChanged;
            }

            var elapsed = stopwatch.ElapsedMilliseconds;
            var pending = AcadInput.IsInputPending();
            var lastPromptNow = AcadInput.LastPrompt();
            var activeNow = AcadInput.CommandActive();
            var historyChanged = HistoryChanged(historyBefore, lastLine);
            reacted |= watch.Sequence != sequence
                || lastPromptNow != prompt
                || activeNow != active
                || historyChanged;
            if (reacted)
            {
                // AutoCAD reacted. Settle: one more poll without new events, nothing queued, and a NEW prompt (or the
                // Command prompt) on the current line rather than the echo of the input still being processed. The
                // prompt answered may come back with the same text (LINE's "Specify next point"): new history or a new
                // prompt event shows that it is a new one.
                var quiet = lastSequence == watch.Sequence;
                lastSequence = watch.Sequence;
                var freshPrompt = lastPromptNow != prompt || historyChanged || (watch.LastPrompt?.Sequence ?? int.MinValue) > sequence;
                if (CommandLineText.IsSettled(reacted, quiet, pending, (activeNow & (1 | 2 | 8)) != 0, freshPrompt ? lastPromptNow : null))
                {
                    return WaitResult.Taken;
                }

                if (elapsed >= 2000)
                {
                    return WaitResult.Taken;
                }
            }
            else if ((pending == false && elapsed >= 400) || (pending is null && elapsed >= 1200))
            {
                return WaitResult.Taken;
            }

            if (!woke && elapsed > 400)
            {
                woke = true;
                AcadInput.WakeUp();
            }

            if (!focused && elapsed > 1500)
            {
                focused = true;
                AcadInput.FocusDrawing();
            }

            if (stopwatch.Elapsed > timeout)
            {
                return WaitResult.Timeout;
            }
        }
    }

    // After a step's last input with the command still active: waits (up to SettleTimeout) until the command ended or
    // really waits at a prompt (CommandLineText.IsWaitingForInput) with nothing queued, so "left waiting" is judged on
    // AutoCAD's settled state and not while it is still processing the last value.
    private async Task<WaitResult> SettleAfterInputsAsync(RunWatch watch)
    {
        var stopwatch = Stopwatch.StartNew();
        var quietPolls = 0;
        var lastSequence = watch.Sequence;
        while (stopwatch.Elapsed < SettleTimeout)
        {
            if (!AcadInput.IsCommandActive())
            {
                return WaitResult.Taken;
            }

            if (_cancellationToken.IsCancellationRequested)
            {
                return WaitResult.Cancelled;
            }

            if (watch.UserCancelledCommand is not null)
            {
                return WaitResult.UserCancelled;
            }

            if (watch.UnexplainedCancel is not null)
            {
                return WaitResult.CancelledByAutoCad;
            }

            if (_dispatcher.IsModal || watch.ModalDepth > 0)
            {
                return WaitResult.Dialog;
            }

            if (AcadInput.IsTextEditorActive() == true)
            {
                return WaitResult.TextEditor;
            }

            quietPolls = watch.Sequence == lastSequence ? quietPolls + 1 : 0;
            lastSequence = watch.Sequence;
            if (quietPolls >= 2 && AcadInput.IsInputPending() != true && CommandLineText.IsWaitingForInput(true, AcadInput.LastPrompt()))
            {
                return WaitResult.Taken;
            }

            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
        }

        return WaitResult.Taken;
    }

    private async Task<(RunOutcome Outcome, string Reason)> StopAsync(StepRecorder recorder, RunWatch watch, WaitResult result, string typed, TimeSpan timeout)
    {
        var step = recorder.Step;
        var label = $"Step {step.Number} ({step.Command})";
        switch (result)
        {
            case WaitResult.Dialog:
                var dialog = await CloseDialogAsync(watch).ConfigureAwait(true);
                recorder.Status = StepStatus.Failed;
                recorder.Error = dialog;
                if (!_dispatcher.IsModal && AcadInput.IsCommandActive())
                {
                    await EscapeAsync(watch).ConfigureAwait(true);
                }

                return (RunOutcome.Stopped, $"{label}: {dialog}");

            case WaitResult.TextEditor:
                await EscapeAsync(watch).ConfigureAwait(true);
                recorder.Status = StepStatus.Failed;
                recorder.Error = "AutoCAD opened its in-place text editor, which the copilot cannot type into; the copilot closed it with Esc. " +
                                 "Use TEXT for words (or -MTEXT, or QLEADER for leader notes) instead";
                return (RunOutcome.Stopped, $"{label}: {recorder.Error}");

            case WaitResult.Timeout:
                await EscapeAsync(watch).ConfigureAwait(true);
                recorder.Status = StepStatus.Failed;
                recorder.Error = $"AutoCAD did not take '{(typed.Length == 0 ? "<Enter>" : typed)}' within {timeout.TotalSeconds:0} s; the copilot pressed Esc. " +
                                 "If a message or dialog box is open in AutoCAD, ask the user to close it; if AutoCAD is still busy, wait and look with capture_view";
                return (RunOutcome.Stopped, $"{label}: {recorder.Error}");

            case WaitResult.UserCancelled:
                recorder.Status = StepStatus.Cancelled;
                recorder.Error = $"the user pressed Esc in AutoCAD ({watch.UserCancelledCommand} was cancelled)";
                return (RunOutcome.Cancelled, "The user pressed Esc in AutoCAD, so the copilot stopped; ask what they want before continuing");

            case WaitResult.CancelledByAutoCad:
                recorder.Status = StepStatus.Failed;
                recorder.Error = $"AutoCAD cancelled {watch.UnexplainedCancel} (*Cancel*) although neither the copilot nor the user pressed Esc. " +
                                 "Look at the drawing (capture_view or list_entities) and type this step again";
                return (RunOutcome.Stopped, $"{label}: {recorder.Error}");

            case WaitResult.Vetoed:
                await EscapeAsync(watch).ConfigureAwait(true);
                recorder.Status = StepStatus.Failed;
                recorder.Error = $"AutoCAD was about to run a command that is not allowed and the copilot stopped it ({watch.Vetoed})";
                return (RunOutcome.Stopped, $"{label}: {recorder.Error}");

            case WaitResult.Cancelled:
                await EscapeAsync(watch).ConfigureAwait(true);
                recorder.Status = StepStatus.Cancelled;
                return (RunOutcome.Cancelled, "Stopped by the user (Stop in the panel)");

            default:
                return Stop(recorder, result, null);
        }
    }

    private static (RunOutcome Outcome, string Reason) Stop(StepRecorder recorder, WaitResult result, string? detail)
    {
        recorder.Status = StepStatus.Failed;
        recorder.Error = detail ?? (result == WaitResult.DrawingChanged
            ? "the active drawing changed (another drawing was activated or this one is closing)"
            : "the call stopped");
        return (RunOutcome.Stopped, $"Step {recorder.Step.Number} ({recorder.Step.Command}): {recorder.Error}");
    }

    private async Task<string> CloseDialogAsync(RunWatch watch)
    {
        var title = DialogCloser.ActiveDialogTitle();
        var name = string.IsNullOrWhiteSpace(title) ? "a dialog box" : $"the dialog box '{title}'";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!DialogCloser.TryCancel(attempt))
            {
                break;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 1000)
            {
                await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
                if (!_dispatcher.IsModal && watch.ModalDepth == 0)
                {
                    return $"AutoCAD opened {name}, which the copilot cannot type into; the copilot closed it (Cancel). Use the command-line version of the command (usually its name with '-' in front) or another command";
                }
            }
        }

        return $"AutoCAD opened {name} and the copilot could not close it. Ask the user to close it; then use the command-line version of the command (usually its name with '-' in front)";
    }

    private async Task EscapeAsync(RunWatch watch)
    {
        watch.ExpectingCancel = true;
        try
        {
            AcadInput.Escape(_document);
            var stopwatch = Stopwatch.StartNew();
            var keyPressed = false;
            while (stopwatch.Elapsed < CleanupTimeout)
            {
                await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
                if (_dispatcher.IsModal)
                {
                    break;
                }

                var editorOpen = AcadInput.IsTextEditorActive() == true;
                if (!AcadInput.IsCommandActive() && !editorOpen)
                {
                    break;
                }

                if (!keyPressed && stopwatch.ElapsedMilliseconds > 800)
                {
                    // Typed Esc did not end it (an in-place editor reads the keyboard): press the Esc key itself.
                    keyPressed = true;
                    DialogCloser.PressEscapeKey();
                }
            }

            // Let the cancel notifications arrive while they are still expected.
            await Task.Delay(PollMs * 2, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            watch.ExpectingCancel = false;
        }
    }

    private async Task<bool> BeginUndoGroupAsync(RunWatch watch)
    {
        var undoctl = AcadInput.GetInt("UNDOCTL");
        if ((undoctl & 1) == 0)
        {
            _warnings.Add("UNDO is turned off in AutoCAD (UNDOCTL 0), so this call cannot be undone.");
            return false;
        }

        if ((undoctl & 2) != 0)
        {
            _warnings.Add("UNDO is limited to one step in AutoCAD (UNDO Control One), so this call is not grouped into one undo step.");
            return false;
        }

        await TypeQuietlyAsync(watch, "_.UNDO _BEgin\n").ConfigureAwait(true);
        if ((AcadInput.GetInt("UNDOCTL") & 8) == 0)
        {
            _warnings.Add("UNDO Begin did not start an undo group, so this call may need several undo steps.");
            return false;
        }

        return true;
    }

    // Ends what the call left behind: an active command (Esc) and the undo group. The group is also closed when UNDO
    // Begin took effect only after the engine gave up waiting for it (a group open now that was not open at the start).
    private async Task CleanupAsync(RunWatch watch, bool undoGroup, bool groupOpenAtStart)
    {
        try
        {
            if (!IsActiveDocument())
            {
                // Typing now would go to (and re-activate) the copilot's drawing, and the command state read here
                // belongs to the drawing the user switched to. Leave that drawing alone and say so.
                if (undoGroup)
                {
                    _warnings.Add("The user switched drawings during this call, so its undo group in that drawing was left open; " +
                                  "it closes with the next UNDO there, and the undo tool may not take this call back as one step.");
                    CopilotLog.Warn("Drafting: the active drawing changed; UNDO End was not typed into the copilot's drawing.");
                }

                return;
            }

            if (AcadInput.IsCommandActive() || AcadInput.IsTextEditorActive() == true)
            {
                await EscapeAsync(watch).ConfigureAwait(true);
            }

            if (undoGroup ||(!groupOpenAtStart && (AcadInput.GetInt("UNDOCTL") & 8) != 0))
            {
                await TypeQuietlyAsync(watch, "_.UNDO _End\n").ConfigureAwait(true);
                if ((AcadInput.GetInt("UNDOCTL") & 8) != 0)
                {
                    CopilotLog.Warn("Drafting: the undo group is still open after UNDO End (AutoCAD busy?); it will close when AutoCAD takes the queued input.");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CopilotLog.Error("Drafting: cleanup after run_commands failed", ex);
        }
    }

    // Types the copilot's own bookkeeping (UNDO Begin/End) without echo and with prompts muted, then waits for it.
    private async Task TypeQuietlyAsync(RunWatch watch, string text)
    {
        var nomutt = AcadInput.Get("NOMUTT");
        if (nomutt is not null)
        {
            AcadInput.Set("NOMUTT", Convert.ChangeType(1, nomutt.GetType(), CultureInfo.InvariantCulture));
        }

        try
        {
            AcadInput.Type(_document, text, echo: false);
            var stopwatch = Stopwatch.StartNew();
            var sequence = watch.Sequence;
            while (stopwatch.Elapsed < CleanupTimeout)
            {
                await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
                if (_dispatcher.IsModal)
                {
                    break;
                }

                var pending = AcadInput.IsInputPending();
                if (!AcadInput.IsCommandActive() && pending != true && (watch.Sequence != sequence || stopwatch.ElapsedMilliseconds > 300))
                {
                    break;
                }
            }
        }
        finally
        {
            if (nomutt is not null)
            {
                AcadInput.Set("NOMUTT", nomutt);
            }
        }
    }

    private static bool AcceptsSpaces(string prompt, RunWatch watch, int sequenceAtLastType)
    {
        if (CommandLineText.IsTextPrompt(prompt))
        {
            return true;
        }

        // A string prompt that started after the previous input and allows spaces (Editor.PromptingForString).
        return watch.LastPrompt is { Kind: PromptKind.String, AllowSpaces: true } last && last.Sequence > sequenceAtLastType;
    }

    private bool IsActiveDocument()
    {
        try
        {
            return AcadApp.DocumentManager.MdiActiveDocument is { } active && active.Equals(_document);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private void ClearSelection()
    {
        try
        {
            _document.Editor.SetImpliedSelection(Array.Empty<ObjectId>());
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
        {
            if (!_selectionWarningLogged)
            {
                _selectionWarningLogged = true;
                CopilotLog.Warn("Drafting: could not clear the pickfirst selection", ex);
            }
        }
    }

    private async Task PauseAsync(int milliseconds)
    {
        if (milliseconds > 0 && !_cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(milliseconds, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static bool HistoryChanged(IReadOnlyList<string>? before, string? lastLine)
    {
        if (before is null || AcadInput.History() is not { Count: > 0 } now)
        {
            return false;
        }

        return !string.Equals(now[^1], lastLine, StringComparison.Ordinal) || !now.SequenceEqual(before, StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> NewLines(IReadOnlyList<string>? before)
    {
        if (before is null || AcadInput.History() is not { } after)
        {
            return [];
        }

        return CommandLineText.NewLines(before, after);
    }

    private static IReadOnlyList<long> Handles(IReadOnlyList<ObjectId> ids) =>
        ids.Take(MaxIdsReported).Select(id => id.Handle.Value).ToList();

    private sealed record Collected(
        IReadOnlyList<StepReport> Steps,
        IReadOnlyList<ObjectId> CreatedIds,
        IReadOnlyList<ObjectId> ModifiedIds,
        IReadOnlyList<ObjectId> ErasedIds,
        IReadOnlyDictionary<string, int> CreatedByType);

    // Turns the recorded object ids into per-step summaries: created = appended in the step and still there; erased =
    // existed before the call and erased in the step; modified = existed before, changed in the step and still there.
    // Only drawing content is listed: entities in model or paper space and table records (layers, styles, blocks...).
    private Collected CollectChanges(RunWatch watch, List<StepRecorder> recorders, DrawingUnits units)
    {
        var steps = new List<StepReport>(recorders.Count);
        var createdAll = new List<ObjectId>();
        var modifiedAll = new List<ObjectId>();
        var erasedAll = new List<ObjectId>();
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<ObjectId>();
        var appendedInRun = watch.AppendedInRun;
        try
        {
            using var transaction = _database.TransactionManager.StartOpenCloseTransaction();
            var layoutOwners = new Dictionary<ObjectId, bool>();
            foreach (var recorder in recorders)
            {
                var changes = watch.Changes(recorder.Step.Number);
                var created = Describe(changes.Appended.Where(id => !IsGone(id)), transaction, layoutOwners, units, seen, createdAll, byType);
                var erased = Describe(changes.Erased.Where(id => !appendedInRun.Contains(id) && IsGone(id)), transaction, layoutOwners, units, seen, erasedAll, null);
                var modified = Describe(changes.Modified.Where(id => !appendedInRun.Contains(id) && !IsGone(id)), transaction, layoutOwners, units, seen, modifiedAll, null);
                steps.Add(recorder.Build(created.Listed, created.Count, modified.Listed, modified.Count, erased.Listed, erased.Count));
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Drafting: reading the changed objects failed", ex);
            steps = recorders.Select(r => r.Build()).ToList();
        }

        return new Collected(steps, createdAll, modifiedAll, erasedAll, byType);
    }

    private static bool IsGone(ObjectId id) => id.IsNull || !id.IsValid || id.IsErased;

    private static (List<JsonObject> Listed, int Count) Describe(
        IEnumerable<ObjectId> ids,
        Transaction transaction,
        Dictionary<ObjectId, bool> layoutOwners,
        DrawingUnits units,
        HashSet<ObjectId> seen,
        List<ObjectId> all,
        SortedDictionary<string, int>? byType)
    {
        var listed = new List<JsonObject>();
        var count = 0;
        var examined = 0;
        foreach (var id in ids)
        {
            if (!seen.Add(id) || ++examined > MaxObjectsSummarizedPerStep)
            {
                continue;
            }

            JsonObject? summary;
            try
            {
                summary = Summarize(id, transaction, layoutOwners, units, detailed: listed.Count < StepRecorder.MaxEntitiesListed);
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
            {
                continue;
            }

            if (summary is null)
            {
                continue;
            }

            count++;
            all.Add(id);
            if (byType is not null && summary["type"]?.GetValue<string>() is { } type)
            {
                byType[type] = byType.TryGetValue(type, out var n) ? n + 1 : 1;
            }

            if (listed.Count < StepRecorder.MaxEntitiesListed)
            {
                listed.Add(summary);
            }
        }

        return (listed, count);
    }

    // The summary of a listable object, or null for objects that are not drawing content (block-definition contents,
    // attributes inside a block reference, dictionaries, view records...). With detailed = false only handle, type and
    // layer/name are read (enough for counting).
    private static JsonObject? Summarize(ObjectId id, Transaction transaction, Dictionary<ObjectId, bool> layoutOwners, DrawingUnits units, bool detailed)
    {
        if (id.IsNull || !id.IsValid)
        {
            return null;
        }

        var dbObject = transaction.GetObject(id, OpenMode.ForRead, true);
        switch (dbObject)
        {
            case Entity entity:
                if (!IsInLayout(entity.OwnerId, transaction, layoutOwners))
                {
                    return null;
                }

                return detailed
                    ? EntitySummary.Summarize(entity, units)
                    : new JsonObject
                    {
                        ["handle"] = id.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                        ["type"] = EntitySummary.TypeName(entity),
                        ["layer"] = entity.Layer,
                    };

            case ViewportTableRecord or RegAppTableRecord:
                return null;

            case BlockTableRecord block when block.IsLayout || block.IsAnonymous:
                return null;

            case SymbolTableRecord record:
                return new JsonObject
                {
                    ["handle"] = id.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                    ["type"] = TableName(record),
                    ["name"] = record.Name,
                };

            default:
                return null;
        }
    }

    private static bool IsInLayout(ObjectId ownerId, Transaction transaction, Dictionary<ObjectId, bool> cache)
    {
        if (cache.TryGetValue(ownerId, out var known))
        {
            return known;
        }

        var result = !ownerId.IsNull && ownerId.IsValid && transaction.GetObject(ownerId, OpenMode.ForRead, true) is BlockTableRecord { IsLayout: true };
        cache[ownerId] = result;
        return result;
    }

    private static string TableName(SymbolTableRecord record) => record switch
    {
        LayerTableRecord => "LAYER",
        TextStyleTableRecord => "STYLE",
        DimStyleTableRecord => "DIMSTYLE",
        LinetypeTableRecord => "LTYPE",
        BlockTableRecord => "BLOCK",
        UcsTableRecord => "UCS",
        ViewTableRecord => "VIEW",
        _ => record.GetRXClass().DxfName is { Length: > 0 } dxf ? dxf : record.GetType().Name,
    };
}
