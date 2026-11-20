namespace AcadCopilot.Plugin.ViewModels;

/// <summary>How a Quick Ask request was handed to a chat tab.</summary>
public enum QuickAskDelivery
{
    /// <summary>The turn started at once.</summary>
    Started,

    /// <summary>The tab is working (or the most chats at once are): the request waits in its queue and runs next.</summary>
    Queued,

    /// <summary>The tab's queue is full: nothing was sent.</summary>
    Rejected,
}

/// <summary>
/// Quick Ask (the floating AI box in the drawing area, Plugin/QuickAsk) sends through the tab bound to the active drawing
/// like a normal message, without touching what the user is typing in the tab's own message box.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>
    /// Sends a Quick Ask request as this tab's next user message: <paramref name="displayText"/> is shown in the chat,
    /// <paramref name="sentText"/> (with its point and selection lines) goes to the AI. Starts at once when the tab is
    /// idle; otherwise the request waits in the tab's queue (Enter-while-busy behaviour; a paused queue is resumed so it
    /// runs). The message box is left alone.
    /// </summary>
    /// <param name="displayText">What the chat shows as the user's message.</param>
    /// <param name="sentText">What the AI receives.</param>
    /// <param name="selectionNote">"5 objects selected" when the selection goes with it.</param>
    public QuickAskDelivery SendQuickAsk(string displayText, string sentText, string? selectionNote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayText);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentText);
        if (_disposed)
        {
            return QuickAskDelivery.Rejected;
        }

        // An idle tab starts at once (also past a paused queue: the user asked for this one now).
        if (!IsBusy && (Queue.Count == 0 || IsQueuePaused) && _session is not { CanStartTurn: false })
        {
            _ = RunTurnAsync(displayText, sentText, [], selectionNote, fromQueue: false);
            return QuickAskDelivery.Started;
        }

        if (Queue.Count >= MaxQueued)
        {
            SetNotice($"At most {MaxQueued} requests can wait in a chat's queue.", isError: true);
            return QuickAskDelivery.Rejected;
        }

        Queue.Add(new QueuedRequest(displayText, sentText, [], selectionNote));
        RenumberQueue();
        if (IsQueuePaused)
        {
            ResumeQueue();
        }
        else if (!IsBusy)
        {
            ScheduleNextQueued();
        }

        return QuickAskDelivery.Queued;
    }
}
