using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Interaction;

namespace AcadCopilot.Core.Recovery;

/// <summary>Why a job is unfinished.</summary>
public enum ResumeReason
{
    /// <summary>The journal belongs to this AutoCAD session: the CLI (or the chat) stopped before the job was done.</summary>
    SameSession,

    /// <summary>The AutoCAD session that wrote it has ended (closed or crashed) before the job was done.</summary>
    AutoCadStopped,
}

/// <summary>A run_commands call that did not finish: what was typed and what was not.</summary>
public sealed record InterruptedRun
{
    /// <summary>The call's title.</summary>
    public required string Title { get; init; }

    /// <summary>"interrupted" (AutoCAD stopped while typing), "stopped", "cancelled", "failed" or "running".</summary>
    public required string State { get; init; }

    /// <summary>Steps of the call.</summary>
    public required int StepsTotal { get; init; }

    /// <summary>1-based numbers of the steps that finished.</summary>
    public required IReadOnlyList<int> CompletedSteps { get; init; }

    /// <summary>The steps that did not finish: "13 LINE", "14 OFFSET"...</summary>
    public required IReadOnlyList<string> RemainingSteps { get; init; }

    /// <summary>Why it stopped, when known.</summary>
    public string? Reason { get; init; }

    /// <summary>The checkpoint created before it, if any (restore_checkpoint throws the half-typed stage away).</summary>
    public string? Checkpoint { get; init; }
}

/// <summary>
/// What a job journal says about an unfinished drafting job: tasks done and remaining, the calls that finished, the
/// call that was interrupted, and a short text for the AI (get_resume_info) and the panel's "Resume previous job" card.
/// </summary>
public sealed record ResumeInfo
{
    /// <summary>Session key of the journal.</summary>
    public required string Session { get; init; }

    /// <summary>Why the job is unfinished.</summary>
    public required ResumeReason Reason { get; init; }

    /// <summary>The drawing (full path).</summary>
    public string? DrawingPath { get; init; }

    /// <summary>The drawing's file name.</summary>
    public string? DrawingTitle { get; init; }

    /// <summary>The plan's title.</summary>
    public string? PlanTitle { get; init; }

    /// <summary>Plan tasks that are done (completed or skipped).</summary>
    public IReadOnlyList<string> CompletedTasks { get; init; } = [];

    /// <summary>Plan tasks still to do, with their status ("Inner walls (in_progress)").</summary>
    public IReadOnlyList<string> RemainingTasks { get; init; } = [];

    /// <summary>Titles of the run_commands calls that completed, oldest first (at most the last 30).</summary>
    public IReadOnlyList<string> CompletedRuns { get; init; } = [];

    /// <summary>The last call, when it did not complete.</summary>
    public InterruptedRun? Interrupted { get; init; }

    /// <summary>When the journal was last written (UTC).</summary>
    public DateTime UpdatedUtc { get; init; }

    /// <summary>True when something is left to do.</summary>
    public bool IsUnfinished => RemainingTasks.Count > 0 || Interrupted is not null;

    /// <summary>Card title for the panel: "Resume previous job".</summary>
    public string CardTitle => "Resume previous job";

    /// <summary>Builds the info from a journal.</summary>
    public static ResumeInfo From(JobJournal journal, ResumeReason reason)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var done = journal.Tasks.Where(t => t.Status is PlanTaskStatus.Completed or PlanTaskStatus.Skipped).Select(t => t.Title).ToList();
        var remaining = journal.Tasks
            .Where(t => t.Status is not (PlanTaskStatus.Completed or PlanTaskStatus.Skipped))
            .Select(t => t.Status == PlanTaskStatus.Pending ? t.Title : $"{t.Title} ({StatusName(t.Status)})")
            .ToList();
        var completedRuns = journal.Runs.Where(r => r.Status == JournalRunStatus.Completed).Select(r => r.Title).TakeLast(30).ToList();

        InterruptedRun? interrupted = null;
        var last = journal.Runs.LastOrDefault(r => r.Status != JournalRunStatus.NotRun);
        var planDone = journal.Tasks.Count > 0 && remaining.Count == 0;
        if (last is not null && last.Status != JournalRunStatus.Completed && !planDone)
        {
            var state = last.Status switch
            {
                JournalRunStatus.Running => reason == ResumeReason.AutoCadStopped ? "interrupted" : "running",
                _ => last.Status.ToString().ToLowerInvariant(),
            };
            var remainingSteps = new List<string>();
            for (var step = 1; step <= last.StepsTotal; step++)
            {
                if (!last.CompletedSteps.Contains(step))
                {
                    var command = step <= last.Commands.Count ? " " + last.Commands[step - 1] : string.Empty;
                    remainingSteps.Add(step.ToString(CultureInfo.InvariantCulture) + command);
                }
            }

            interrupted = new InterruptedRun
            {
                Title = last.Title,
                State = state,
                StepsTotal = last.StepsTotal,
                CompletedSteps = last.CompletedSteps.ToList(),
                RemainingSteps = remainingSteps,
                Reason = last.StoppedReason,
                Checkpoint = last.Checkpoint,
            };
        }

        return new ResumeInfo
        {
            Session = journal.Session,
            Reason = reason,
            DrawingPath = journal.DrawingPath,
            DrawingTitle = journal.DrawingTitle,
            PlanTitle = journal.PlanTitle,
            CompletedTasks = done,
            RemainingTasks = remaining,
            CompletedRuns = completedRuns,
            Interrupted = interrupted,
            UpdatedUtc = journal.UpdatedUtc,
        };
    }

    /// <summary>
    /// The plain-words summary for the AI and the card: "The job 'G+1 house' on House-G1.dwg stopped because AutoCAD
    /// closed. Done: Setup, Outer walls. Remaining: Inner walls, Doors. 'Inner walls' was interrupted after step 12 of 25..."
    /// </summary>
    public string Summarize()
    {
        var text = new StringBuilder();
        var job = PlanTitle is { Length: > 0 } title ? $"The job '{title}'" : "The drafting job";
        text.Append(job);
        if (DrawingTitle is { Length: > 0 } drawing)
        {
            text.Append(" on ").Append(drawing);
        }

        text.Append(Reason == ResumeReason.AutoCadStopped
            ? " did not finish: AutoCAD closed (or crashed) while it was in progress."
            : " did not finish: the AI stopped before it was done.");
        text.Append(' ').Append(Reason == ResumeReason.AutoCadStopped ? "Last activity " : "Last update ")
            .Append(UpdatedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC.");

        if (CompletedTasks.Count > 0)
        {
            text.Append(" Done: ").Append(string.Join(", ", CompletedTasks)).Append('.');
        }
        else if (CompletedRuns.Count > 0)
        {
            text.Append(" Calls completed: ").Append(string.Join(", ", CompletedRuns.TakeLast(10))).Append('.');
        }

        if (RemainingTasks.Count > 0)
        {
            text.Append(" Remaining: ").Append(string.Join(", ", RemainingTasks)).Append('.');
        }

        if (Interrupted is { } run)
        {
            text.Append(' ').Append(CultureInfo.InvariantCulture, $"'{run.Title}' was {run.State} after {run.CompletedSteps.Count} of {run.StepsTotal} steps");
            text.Append(run.RemainingSteps.Count > 0 ? $"; not typed: steps {string.Join(", ", run.RemainingSteps.Take(12))}{(run.RemainingSteps.Count > 12 ? ", ..." : string.Empty)}." : ".");
            if (run.Reason is { Length: > 0 } why)
            {
                text.Append(" Reason: ").Append(why.TrimEnd('.')).Append('.');
            }

            if (run.Checkpoint is { Length: > 0 } checkpoint)
            {
                text.Append(" Checkpoint '").Append(checkpoint).Append("' holds the drawing from before it.");
            }
        }

        return text.ToString();
    }

    /// <summary>What the AI should do next (part of get_resume_info's answer).</summary>
    public string Advice()
    {
        var text = new StringBuilder("Before continuing, look at the drawing (get_drawing_info, capture_view): ");
        text.Append(Reason == ResumeReason.AutoCadStopped
            ? "work after the user's last save may be missing, and a half-typed stage may be partly drawn. "
            : "a half-typed stage may be partly drawn. ");
        if (Interrupted?.Checkpoint is { Length: > 0 } checkpoint)
        {
            text.Append(CultureInfo.InvariantCulture, $"To redo the interrupted stage cleanly, restore_checkpoint '{checkpoint}' first (it asks the user). ");
        }

        text.Append("Then tell the user in one line what is left, call update_plan with the full list (finished tasks as completed), and continue with the remaining stages.");
        return text.ToString();
    }

    /// <summary>The info as JSON for tool results and the panel.</summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["session"] = Session,
            ["reason"] = Reason == ResumeReason.AutoCadStopped ? "autocad_stopped" : "same_session",
            ["drawingPath"] = DrawingPath,
            ["drawingTitle"] = DrawingTitle,
            ["planTitle"] = PlanTitle,
            ["completedTasks"] = new JsonArray(CompletedTasks.Select(t => (JsonNode?)t).ToArray()),
            ["remainingTasks"] = new JsonArray(RemainingTasks.Select(t => (JsonNode?)t).ToArray()),
            ["completedCalls"] = new JsonArray(CompletedRuns.Select(t => (JsonNode?)t).ToArray()),
            ["updatedUtc"] = UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["summary"] = Summarize(),
        };
        if (Interrupted is { } run)
        {
            node["interruptedCall"] = new JsonObject
            {
                ["title"] = run.Title,
                ["state"] = run.State,
                ["stepsTotal"] = run.StepsTotal,
                ["completedSteps"] = new JsonArray(run.CompletedSteps.Select(s => (JsonNode?)s).ToArray()),
                ["remainingSteps"] = new JsonArray(run.RemainingSteps.Select(s => (JsonNode?)s).ToArray()),
                ["reason"] = run.Reason,
                ["checkpoint"] = run.Checkpoint,
            };
        }

        return node;
    }

    private static string StatusName(PlanTaskStatus status) => status switch
    {
        PlanTaskStatus.InProgress => "in progress",
        PlanTaskStatus.Failed => "failed",
        _ => status.ToString().ToLowerInvariant(),
    };
}
