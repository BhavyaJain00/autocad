namespace AcadCopilot.Core.Interaction;

/// <summary>
/// What the chat panel (its view model) provides to the backend for things that come from the
/// add-in side rather than from the CLI's output stream: clarifying questions (ask_user), the task
/// checklist (update_plan) and approvals for destructive tools.
/// <para>
/// Implemented by the panel's view model (ChatViewModel) and attached with <c>ICopilotBackend.AttachUi</c>.
/// All members are called from background threads: implementations must marshal to the WPF
/// dispatcher themselves and must never block the calling thread waiting for the UI thread
/// synchronously (AutoCAD's main thread is the WPF thread).
/// </para>
/// </summary>
public interface ICopilotUi
{
    /// <summary>
    /// Shows the questions as option cards and completes when the user answers.
    /// Complete with <c>Cancelled = true</c> when the user presses Stop / New chat, and honour
    /// <paramref name="cancellationToken"/> (the CLI may abandon the call).
    /// </summary>
    Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken);

    /// <summary>Replaces the "Task Progress (n/m)" checklist. Must return quickly.</summary>
    void UpdatePlan(PlanUpdate plan);

    /// <summary>
    /// Shows an approval prompt (Approve / Reject / Always) and completes with the choice.
    /// Return <see cref="ApprovalDecision.Reject"/> when cancelled.
    /// </summary>
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
