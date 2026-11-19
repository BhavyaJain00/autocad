using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Interaction;

/// <summary>One clickable option of a <see cref="UserQuestion"/>.</summary>
public sealed record QuestionOption
{
    /// <summary>Short option text shown on the card ("3 bedrooms").</summary>
    public required string Label { get; init; }

    /// <summary>Optional one-line explanation under the label.</summary>
    public string? Description { get; init; }

    /// <summary>True for the option the assistant recommends (highlighted in the panel).</summary>
    public bool Recommended { get; init; }
}

/// <summary>A clarifying question rendered as option cards in the panel.</summary>
public sealed record UserQuestion
{
    /// <summary>Very short label/chip (max ~16 chars), e.g. "Bedrooms".</summary>
    public string? Header { get; init; }

    /// <summary>The full question text.</summary>
    public required string Question { get; init; }

    /// <summary>2-6 options.</summary>
    public IReadOnlyList<QuestionOption> Options { get; init; } = [];

    /// <summary>When true the panel also offers a free-text "Other..." answer.</summary>
    public bool AllowCustom { get; init; } = true;

    /// <summary>When true several options can be picked.</summary>
    public bool MultiSelect { get; init; }
}

/// <summary>Request to ask the user one or more questions (pipe method ui.askUser, MCP tool ask_user).</summary>
public sealed record QuestionRequest
{
    /// <summary>1-4 questions shown together.</summary>
    public IReadOnlyList<UserQuestion> Questions { get; init; } = [];
}

/// <summary>The user's answer to one question.</summary>
public sealed record QuestionAnswer
{
    /// <summary>The question text this answers.</summary>
    public required string Question { get; init; }

    /// <summary>Labels of the selected options (may be empty when a custom answer was typed).</summary>
    public IReadOnlyList<string> Selected { get; init; } = [];

    /// <summary>Free text the user typed instead of / in addition to the options.</summary>
    public string? Custom { get; init; }
}

/// <summary>Answers to a <see cref="QuestionRequest"/> (same order as the questions).</summary>
public sealed record QuestionResponse
{
    /// <summary>One answer per question.</summary>
    public IReadOnlyList<QuestionAnswer> Answers { get; init; } = [];

    /// <summary>True when the user dismissed the questions (Stop / New chat / skipped).</summary>
    public bool Cancelled { get; init; }
}

/// <summary>Status of one plan task.</summary>
public enum PlanTaskStatus
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>Being worked on (spinner).</summary>
    InProgress,

    /// <summary>Done (check).</summary>
    Completed,

    /// <summary>Failed (cross).</summary>
    Failed,

    /// <summary>Skipped / no longer needed.</summary>
    Skipped,
}

/// <summary>One row of the "Task Progress (n/m)" checklist.</summary>
public sealed record PlanTask
{
    /// <summary>Task text ("Create exterior walls").</summary>
    public required string Title { get; init; }

    /// <summary>Current status.</summary>
    public PlanTaskStatus Status { get; init; } = PlanTaskStatus.Pending;
}

/// <summary>Full replacement of the checklist (pipe method ui.updatePlan, MCP tool update_plan).</summary>
public sealed record PlanUpdate
{
    /// <summary>Optional checklist title.</summary>
    public string? Title { get; init; }

    /// <summary>All tasks, in order. An empty list clears the checklist.</summary>
    public IReadOnlyList<PlanTask> Tasks { get; init; } = [];

    /// <summary>Number of completed tasks (n of "n/m").</summary>
    public int CompletedCount => Tasks.Count(t => t.Status == PlanTaskStatus.Completed);
}

/// <summary>Asks the user to approve a destructive tool call before it runs.</summary>
public sealed record ApprovalRequest
{
    /// <summary>Tool name ("delete_elements").</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool title ("Delete elements").</summary>
    public required string ToolTitle { get; init; }

    /// <summary>What will happen, in plain words ("Delete 3 elements: 1234, 1235, 1236").</summary>
    public required string Summary { get; init; }

    /// <summary>Risk class (normally <see cref="ToolRisk.Destructive"/>).</summary>
    public ToolRisk Risk { get; init; } = ToolRisk.Destructive;

    /// <summary>Compact JSON of the arguments, for a "details" expander.</summary>
    public string? ArgumentsJson { get; init; }
}

/// <summary>The user's choice on an <see cref="ApprovalRequest"/>.</summary>
public enum ApprovalDecision
{
    /// <summary>Run it this time.</summary>
    Approve,

    /// <summary>Do not run it; the tool returns an error saying the user declined.</summary>
    Reject,

    /// <summary>Run it and do not ask again for this tool until New chat or AutoCAD restarts.</summary>
    AlwaysApprove,
}

/// <summary>Thrown when a question/approval cannot be shown because no panel UI is attached.</summary>
public sealed class UiUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UiUnavailableException(string message)
        : base(message)
    {
    }
}
