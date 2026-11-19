using System.IO;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;

namespace AcadCopilot.Plugin.Recovery;

/// <summary>
/// Which chat session the current tool call belongs to, for the job journal. The pipe handler sets
/// <see cref="SessionId"/> around a tool handler (it flows into the main-thread items the handler posts); calls without
/// a session (the user's own MCP clients, or before the pipe handler sets it) journal under one key per drawing.
/// </summary>
public static class RecoveryContext
{
    private static readonly AsyncLocal<string?> Session = new();

    /// <summary>The chat session (tab) id of the running tool call, or null.</summary>
    public static string? SessionId
    {
        get => Session.Value;
        set => Session.Value = value;
    }
}

/// <summary>
/// Hooks for the rest of the plug-in (pipe handler, panel) to feed and use the job journal. Every method is safe to call
/// from any thread and never throws: the journal must never break drafting.
/// </summary>
public static class RecoveryHooks
{
    /// <summary>
    /// Records a plan update (update_plan) of <paramref name="sessionId"/> in its journal. The drawing is the one the
    /// session's journal already names, else <paramref name="drawingId"/> (a <see cref="DrawingDirectory"/> id), else the
    /// active drawing, resolved on AutoCAD's main thread. Fire and forget.
    /// </summary>
    public static void PlanUpdated(AcadDispatcher dispatcher, string? sessionId, string? drawingId, PlanUpdate plan)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(plan);
        _ = RecordPlanAsync(dispatcher, sessionId, drawingId, plan);
    }

    private static async Task RecordPlanAsync(AcadDispatcher dispatcher, string? sessionId, string? drawingId, PlanUpdate plan)
    {
        try
        {
            var drawing = await dispatcher.InvokeAsync(
                () =>
                {
                    var document = drawingId is null ? null : DrawingDirectory.Find(drawingId);
                    document ??= Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                    return document is null ? null : RecoveryJournal.DrawingOf(document);
                }).ConfigureAwait(false);
            var key = sessionId is { Length: > 0 } id ? id : drawing is null ? null : JobJournalStore.DrawingSessionKey(drawing);
            if (key is null)
            {
                return;
            }

            var existing = RecoveryJournal.Store.Load(key);
            var target = existing?.DrawingPath is { } path
                ? new JournalDrawing(path, existing.DrawingTitle ?? Path.GetFileName(path), existing.DrawingIsNamed)
                : drawing;
            RecoveryJournal.Store.RecordPlan(key, target, plan);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Job journal: recording the plan failed", ex);
        }
    }
}

/// <summary>
/// The plug-in side of the job journal (crash recovery): which journal a call writes to, one journaled run_commands call,
/// and the once-per-session cleaning of old journals and checkpoint copies. Failures are logged, never thrown.
/// </summary>
internal static class RecoveryJournal
{
    private static readonly Lazy<JobJournalStore> LazyStore = new(JobJournalStore.CreateDefault);
    private static int _cleaned;

    /// <summary>The journal store of this AutoCAD session.</summary>
    public static JobJournalStore Store => LazyStore.Value;

    /// <summary>The drawing of <paramref name="document"/> as the journal names it. Main thread.</summary>
    public static JournalDrawing DrawingOf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var name = document.Name ?? "Drawing.dwg";
        var title = Path.GetFileName(name);
        return new JournalDrawing(name, string.IsNullOrEmpty(title) ? name : title, document.IsNamedDrawing);
    }

    /// <summary>The journal key: the chat session when known, else one per drawing.</summary>
    public static string SessionKey(string? sessionId, JournalDrawing drawing) =>
        sessionId is { Length: > 0 } id ? id : JobJournalStore.DrawingSessionKey(drawing);

    /// <summary>Deletes journals and checkpoint copies older than 7 days, once per AutoCAD session (in the background).</summary>
    public static void CleanOnce()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var journals = Store.CleanOld(JobJournalStore.MaxAge);
                var checkpoints = CheckpointStore.CreateDefault().CleanOld(CheckpointStore.MaxAge, DateTime.UtcNow);
                if (journals + checkpoints > 0)
                {
                    CopilotLog.Info($"Recovery: deleted {journals} old job journal(s) and {checkpoints} old checkpoint folder(s).");
                }
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Recovery: cleaning old journals failed", ex);
            }
        });
    }

    /// <summary>Starts journaling one run_commands call; null when the journal cannot be written. Main thread.</summary>
    public static JournaledRun? BeginRun(string? sessionId, Document document, DraftingPlan plan, string? checkpoint)
    {
        try
        {
            CleanOnce();
            var drawing = DrawingOf(document);
            var key = SessionKey(sessionId, drawing);
            var id = Store.BeginRun(key, drawing, plan.Title, plan.Steps.Select(s => s.Command).ToList(), checkpoint);
            return new JournaledRun(key, id);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Job journal: could not record the start of a run", ex);
            return null;
        }
    }

    /// <summary>The unfinished jobs of <paramref name="document"/>'s drawing (also this session's). Main thread.</summary>
    public static IReadOnlyList<ResumeInfo> FindUnfinished(Document document, bool otherSessionsOnly)
    {
        try
        {
            CleanOnce();
            return Store.FindUnfinished(DrawingOf(document), otherSessionsOnly);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Job journal: reading journals failed", ex);
            return [];
        }
    }

    /// <summary>The journal status of a finished call.</summary>
    public static JournalRunStatus StatusOf(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Completed => JournalRunStatus.Completed,
        RunOutcome.Stopped => JournalRunStatus.Stopped,
        RunOutcome.Cancelled => JournalRunStatus.Cancelled,
        RunOutcome.Failed => JournalRunStatus.Failed,
        _ => JournalRunStatus.NotRun,
    };
}

/// <summary>One run_commands call being journaled.</summary>
/// <param name="Session">Journal key.</param>
/// <param name="RunId">Run id in that journal.</param>
internal sealed record JournaledRun(string Session, int RunId)
{
    /// <summary>Records a finished step (1-based). Never throws.</summary>
    public void StepFinished(int step)
    {
        try
        {
            RecoveryJournal.Store.StepFinished(Session, RunId, step);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Job journal: could not record a step", ex);
        }
    }

    /// <summary>Records how the call ended. Never throws.</summary>
    public void End(RunCommandsReport report)
    {
        try
        {
            var completed = report.Steps.Where(s => s.Status is StepStatus.Finished or StepStatus.LeftWaiting).Select(s => s.Step);
            RecoveryJournal.Store.EndRun(Session, RunId, RecoveryJournal.StatusOf(report.Outcome), completed, report.StoppedReason);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Job journal: could not record the end of a run", ex);
        }
    }
}
