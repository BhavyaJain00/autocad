using System.Collections.ObjectModel;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Interaction;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// The composer's helpers of a chat tab: "click to point" (📍 Point), "select and ask" (the selection chip), the task
/// queue (several requests run one after another) and the ⚡ quick prompts.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Most requests waiting in one tab's queue.</summary>
    public const int MaxQueued = 20;

    private static readonly TimeSpan QueueRetryInterval = TimeSpan.FromSeconds(3);

    private IDrawingInteraction? _interaction;
    private QuickPromptsViewModel? _quickPrompts;
    private CancellationTokenSource? _pickCts;
    private bool _isPickingPoint;
    private bool _isWaitingForPickTurn;
    private string? _dismissedSelectionKey;
    private bool _isQueuePaused;
    private bool _isQueueExpanded = true;
    private string? _queuePauseReason;
    private bool _isQuickMenuOpen;
    private DispatcherTimer? _queueRetryTimer;
    private TurnOutcome? _lastOutcome;
    private RelayCommand? _pickPointCommand;
    private RelayCommand? _cancelPickCommand;
    private RelayCommand? _dismissSelectionCommand;
    private RelayCommand? _addToQueueCommand;
    private RelayCommand? _removeQueuedCommand;
    private RelayCommand? _moveQueuedUpCommand;
    private RelayCommand? _moveQueuedDownCommand;
    private RelayCommand? _pauseQueueCommand;
    private RelayCommand? _resumeQueueCommand;
    private RelayCommand? _clearQueueCommand;
    private RelayCommand? _toggleQueueCommand;
    private RelayCommand? _toggleQuickMenuCommand;
    private RelayCommand? _useQuickPromptCommand;
    private RelayCommand? _saveQuickPromptCommand;

    /// <summary>
    /// The drawing link for 📍 Point and the selection chip (null hides both: no AutoCAD, or tests without it). Set once,
    /// right after construction.
    /// </summary>
    public IDrawingInteraction? Interaction
    {
        get => _interaction;
        init
        {
            _interaction = value;
            if (value is not null)
            {
                value.SelectionChanged += OnSelectionChanged;
                value.PickStateChanged += OnPickStateChanged;
            }
        }
    }

    /// <summary>The ⚡ quick prompts shared by the panel's tabs (null hides the button).</summary>
    public QuickPromptsViewModel? QuickPrompts
    {
        get => _quickPrompts;
        init => _quickPrompts = value;
    }

    /// <summary>
    /// The Focus button and the drawing-area settings, shared by the panel's tabs (null: no Focus button and no
    /// DRAWING AREA settings).
    /// </summary>
    public FocusLayoutViewModel? Layout { get; init; }

    /// <summary>
    /// Where the caret was in the message box (the view keeps it up to date), so a picked point is inserted there.
    /// </summary>
    public int ComposerCaret { get; set; }

    /// <summary>Where the view should put the caret on the next focus request (null = at the end).</summary>
    public int? PendingComposerCaret { get; private set; }

    // ---------------------------------------------------------------- 📍 Point

    /// <summary>True when the 📍 Point button is shown.</summary>
    public bool CanPickPoint => _interaction is not null;

    /// <summary>True while this tab waits for the user's clicks (or for AutoCAD's turn to ask for them).</summary>
    public bool IsPickingPoint
    {
        get => _isPickingPoint;
        private set
        {
            if (Set(ref _isPickingPoint, value))
            {
                RaisePickBanner();
                PickPointCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True while a pick (this tab's, or the AI's pick_point) waits for clicks in the drawing.</summary>
    public bool ShowPickBanner => _interaction?.ActivePick is not null || IsPickingPoint;

    /// <summary>"Click a point in the drawing · Enter when done · Esc cancels" / "The AI asks: Click where ...".</summary>
    public string PickBannerText
    {
        get
        {
            if (_interaction?.ActivePick is { } pick)
            {
                var ask = pick.Source == PickSource.Ai ? "The AI asks: " + pick.Prompt : pick.Prompt;
                var progress = pick.MaxPoints > 1 && pick.PointsSoFar > 0 ? $" ({pick.PointsSoFar} picked)" : string.Empty;
                return ask + progress;
            }

            return _isWaitingForPickTurn ? "Waiting for AutoCAD (another chat is drawing)..." : "Click in the drawing...";
        }
    }

    /// <summary>Smaller line under the banner text.</summary>
    public string PickBannerHint
    {
        get
        {
            var keys = _interaction?.ActivePick is { MaxPoints: > 1 } ? "Enter when done, Esc cancels" : "Esc cancels";
            return _interaction?.ActivePick is { Source: PickSource.Ai }
                ? $"Click in the drawing (object snaps work); the point goes back to the AI. {keys}."
                : $"Click in the drawing (object snaps work); the coordinates go into your message. {keys}.";
        }
    }

    /// <summary>Starts a pick: the user clicks one or more points; their coordinates go into the message at the caret.</summary>
    public RelayCommand PickPointCommand => _pickPointCommand ??= new RelayCommand(() => _ = PickPointAsync(), () => CanPickPoint && !IsPickingPoint);

    /// <summary>Ends the waiting pick (like Esc in AutoCAD).</summary>
    public RelayCommand CancelPickCommand => _cancelPickCommand ??= new RelayCommand(CancelPick);

    /// <summary>Asks the user for points and inserts "[point 12'-6\",30'-0\" (150,360)]" tokens into the message.</summary>
    public async Task PickPointAsync()
    {
        if (_interaction is not { } interaction || IsPickingPoint)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pickCts = cts;
        IsPickingPoint = true;
        _isWaitingForPickTurn = true;
        RaisePickBanner();
        PickResult result;
        try
        {
            result = await interaction.PickPointsAsync(
                new PickRequest { Prompt = "Click a point for the AI (Enter when done)", MaxPoints = PickRequest.MaxPointsLimit, Source = PickSource.User },
                cts.Token);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Point pick failed", ex);
            result = PickResult.Fail("Could not ask AutoCAD for a point: " + ex.Message);
        }
        finally
        {
            _pickCts = null;
            _isWaitingForPickTurn = false;
            IsPickingPoint = false;
        }

        if (_disposed)
        {
            return;
        }

        switch (result.Status)
        {
            case PickStatus.Picked when result.Points.Count > 0:
                InsertIntoComposer(InteractionText.PointTokens(result.Points));
                break;
            case PickStatus.TimedOut:
                SetNotice("No point was clicked in time. Click 📍 Point again when you are ready.");
                break;
            case PickStatus.Failed:
                SetNotice(result.Message, isError: true);
                break;
        }
    }

    /// <summary>Inserts <paramref name="token"/> into the message at the caret (with spaces around it) and focuses the box.</summary>
    public void InsertIntoComposer(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var (text, caret) = InteractionText.Insert(ComposerText, ComposerCaret, token);
        ComposerText = text;
        ComposerCaret = caret;
        PendingComposerCaret = caret;
        RequestComposerFocus();
        PendingComposerCaret = null;
    }

    private void CancelPick()
    {
        try
        {
            _pickCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pick just ended.
        }

        _interaction?.CancelPick();
    }

    private void OnPickStateChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_interaction?.ActivePick is not null)
        {
            _isWaitingForPickTurn = false;
        }

        RaisePickBanner();
    });

    private void RaisePickBanner() => OnPropertiesChanged(nameof(ShowPickBanner), nameof(PickBannerText), nameof(PickBannerHint));

    // ---------------------------------------------------------------- selection chip

    /// <summary>The user's selection in the active drawing, or null.</summary>
    public SelectionSnapshot? Selection => _interaction?.CurrentSelection is { IsEmpty: false } selection ? selection : null;

    /// <summary>True when the chip "5 objects selected" is shown (the next message carries the selection).</summary>
    public bool HasSelectionChip => Selection is { } selection && !string.Equals(selection.Key, _dismissedSelectionKey, StringComparison.Ordinal);

    /// <summary>"5 objects selected".</summary>
    public string SelectionChipText => Selection is { } selection ? InteractionText.SelectionChip(selection) : string.Empty;

    /// <summary>The chip's tool tip (what is selected, in which drawing).</summary>
    public string SelectionChipToolTip => Selection is { } selection ? InteractionText.SelectionToolTip(selection) : string.Empty;

    /// <summary>Leaves the current selection out of the next message (until the selection changes).</summary>
    public RelayCommand DismissSelectionCommand => _dismissSelectionCommand ??= new RelayCommand(() =>
    {
        _dismissedSelectionKey = Selection?.Key;
        RaiseSelection();
    });

    private void OnSelectionChanged(object? sender, EventArgs e) => RunOnUi(RaiseSelection);

    private void RaiseSelection() => OnPropertiesChanged(nameof(Selection), nameof(HasSelectionChip), nameof(SelectionChipText), nameof(SelectionChipToolTip));

    /// <summary>The text sent for <paramref name="text"/>: with the selection block while the chip is shown.</summary>
    private (string Sent, string? Note) WithSelection(string text)
    {
        if (!HasSelectionChip || Selection is not { } selection)
        {
            return (text, null);
        }

        var block = InteractionText.SelectionBlock(selection);
        return (text.Length == 0 ? block : text + "\n\n" + block, InteractionText.SelectionChip(selection));
    }

    // ---------------------------------------------------------------- task queue

    /// <summary>Requests waiting to run in this tab, in order.</summary>
    public ObservableCollection<QueuedRequest> Queue { get; } = [];

    /// <summary>True when requests wait.</summary>
    public bool HasQueue => Queue.Count > 0;

    /// <summary>"Queue (3)".</summary>
    public string QueueHeader => $"Queue ({Queue.Count})";

    /// <summary>True when the queue does not start the next request by itself (Stop, a failure, or Pause).</summary>
    public bool IsQueuePaused
    {
        get => _isQueuePaused;
        private set
        {
            if (Set(ref _isQueuePaused, value))
            {
                RaiseQueue();
            }
        }
    }

    /// <summary>True when the queue's list is shown.</summary>
    public bool IsQueueExpanded
    {
        get => _isQueueExpanded;
        set => Set(ref _isQueueExpanded, value);
    }

    /// <summary>What the queue will do: "Next starts when this answer is done", "Paused: Stopped. Resume to continue".</summary>
    public string QueueStatusText
    {
        get
        {
            if (Queue.Count == 0)
            {
                return string.Empty;
            }

            if (IsQueuePaused)
            {
                return (_queuePauseReason is { Length: > 0 } reason ? "Paused: " + reason + "." : "Paused.") + " Resume to continue.";
            }

            if (_queueRetryTimer?.IsEnabled == true)
            {
                return "Waiting: the most chats at once are working.";
            }

            return IsBusy ? "Next starts when this answer is done." : "Starting...";
        }
    }

    /// <summary>True when "Add to queue" is possible (something typed or attached; the queue is not full).</summary>
    public bool CanAddToQueue => (!string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0) && Queue.Count < MaxQueued;

    /// <summary>Puts the message box's text (and attachments, and the selection) into the queue.</summary>
    public RelayCommand AddToQueueCommand => _addToQueueCommand ??= new RelayCommand(() => AddToQueue(), () => CanAddToQueue);

    /// <summary>Removes the queued request passed as parameter.</summary>
    public RelayCommand RemoveQueuedCommand => _removeQueuedCommand ??= new RelayCommand(p =>
    {
        if (p is QueuedRequest request && Queue.Remove(request))
        {
            RenumberQueue();
        }
    });

    /// <summary>Moves the queued request passed as parameter one up.</summary>
    public RelayCommand MoveQueuedUpCommand => _moveQueuedUpCommand ??= new RelayCommand(p =>
    {
        if (p is QueuedRequest request)
        {
            MoveQueued(request, Queue.IndexOf(request) - 1);
        }
    });

    /// <summary>Moves the queued request passed as parameter one down.</summary>
    public RelayCommand MoveQueuedDownCommand => _moveQueuedDownCommand ??= new RelayCommand(p =>
    {
        if (p is QueuedRequest request)
        {
            MoveQueued(request, Queue.IndexOf(request) + 1);
        }
    });

    /// <summary>Stops the queue from starting the next request.</summary>
    public RelayCommand PauseQueueCommand => _pauseQueueCommand ??= new RelayCommand(() => PauseQueue("paused by you"));

    /// <summary>Lets the queue continue (starts the next request at once when idle).</summary>
    public RelayCommand ResumeQueueCommand => _resumeQueueCommand ??= new RelayCommand(ResumeQueue);

    /// <summary>Removes every queued request.</summary>
    public RelayCommand ClearQueueCommand => _clearQueueCommand ??= new RelayCommand(ClearQueue);

    /// <summary>Shows / hides the queue's list.</summary>
    public RelayCommand ToggleQueueCommand => _toggleQueueCommand ??= new RelayCommand(() => IsQueueExpanded = !IsQueueExpanded);

    /// <summary>
    /// Adds the message box's content to the queue (Enter while the tab is working does this too). When the tab is idle
    /// and the queue is running, it starts at once. False when there was nothing to add or the queue is full.
    /// </summary>
    public bool AddToQueue()
    {
        if (!CanAddToQueue)
        {
            if (Queue.Count >= MaxQueued)
            {
                SetNotice($"At most {MaxQueued} requests can wait in a chat's queue.", isError: true);
            }

            return false;
        }

        var text = ComposerText.Trim();
        var (sent, note) = WithSelection(text);
        var request = new QueuedRequest(text, sent, [.. Attachments.Select(a => a.Path)], note);
        ComposerText = string.Empty;
        Attachments.Clear();
        Queue.Add(request);
        RenumberQueue();
        IsQueueExpanded = true;
        if (!IsBusy && !IsQueuePaused)
        {
            ScheduleNextQueued();
        }

        return true;
    }

    /// <summary>Moves <paramref name="request"/> to <paramref name="index"/> (drag and drop, up / down).</summary>
    public void MoveQueued(QueuedRequest request, int index)
    {
        var from = Queue.IndexOf(request);
        if (from < 0 || Queue.Count < 2)
        {
            return;
        }

        index = Math.Clamp(index, 0, Queue.Count - 1);
        if (index != from)
        {
            Queue.Move(from, index);
            RenumberQueue();
        }
    }

    /// <summary>Lets the queue run again; starts the next request now when the tab is idle.</summary>
    public void ResumeQueue()
    {
        _queuePauseReason = null;
        IsQueuePaused = false;
        ScheduleNextQueued();
    }

    private void PauseQueue(string reason)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        _queueRetryTimer?.Stop();
        _queuePauseReason = reason;
        IsQueuePaused = true;
        RaiseQueue();
    }

    private void ClearQueue()
    {
        Queue.Clear();
        _queueRetryTimer?.Stop();
        _queuePauseReason = null;
        IsQueuePaused = false;
        RaiseQueue();
    }

    // After a turn: a completed answer starts the next request; a failure or Stop pauses the queue.
    private void OnTurnEndedForQueue(TurnOutcome? outcome)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        switch (outcome)
        {
            case TurnOutcome.Completed:
                ScheduleNextQueued();
                break;
            case TurnOutcome.Stopped:
                PauseQueue("you pressed Stop");
                break;
            default:
                PauseQueue("the last request failed");
                break;
        }
    }

    private void ScheduleNextQueued()
    {
        if (_disposed || Queue.Count == 0)
        {
            return;
        }

        // Let the finished turn's bookkeeping settle first.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TryRunNextQueued));
    }

    /// <summary>Starts the next queued request when the tab is idle and the queue runs (public for tests).</summary>
    public void TryRunNextQueued()
    {
        if (_disposed || IsBusy || IsQueuePaused || Queue.Count == 0)
        {
            RaiseQueue();
            return;
        }

        if (_session is { CanStartTurn: false })
        {
            // The most chats at once are working: try again in a moment.
            _queueRetryTimer ??= CreateQueueRetryTimer();
            _queueRetryTimer.Start();
            RaiseQueue();
            return;
        }

        _queueRetryTimer?.Stop();
        var next = Queue[0];
        Queue.RemoveAt(0);
        RenumberQueue();
        _ = RunTurnAsync(next.DisplayText, next.SentText, next.Attachments, next.SelectionNote, fromQueue: true);
    }

    private DispatcherTimer CreateQueueRetryTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = QueueRetryInterval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TryRunNextQueued();
        };
        return timer;
    }

    private void RenumberQueue()
    {
        for (var i = 0; i < Queue.Count; i++)
        {
            Queue[i].Position = i + 1;
        }

        RaiseQueue();
    }

    private void RaiseQueue()
    {
        OnPropertiesChanged(nameof(HasQueue), nameof(QueueHeader), nameof(QueueStatusText), nameof(CanAddToQueue));
        AddToQueueCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- ⚡ quick prompts

    /// <summary>True when the ⚡ button is shown.</summary>
    public bool HasQuickPrompts => _quickPrompts is not null;

    /// <summary>True while the ⚡ menu is open.</summary>
    public bool IsQuickMenuOpen
    {
        get => _isQuickMenuOpen;
        set
        {
            if (Set(ref _isQuickMenuOpen, value) && !value)
            {
                _quickPrompts?.CancelEdit();
            }
        }
    }

    /// <summary>Opens / closes the ⚡ menu.</summary>
    public RelayCommand ToggleQuickMenuCommand => _toggleQuickMenuCommand ??= new RelayCommand(() => IsQuickMenuOpen = !IsQuickMenuOpen, () => HasQuickPrompts);

    /// <summary>Puts the quick prompt passed as parameter into the message box (added below what is typed).</summary>
    public RelayCommand UseQuickPromptCommand => _useQuickPromptCommand ??= new RelayCommand(p =>
    {
        if (p is QuickPromptItem item)
        {
            UseQuickPrompt(item);
        }
    });

    /// <summary>Opens the menu's editor to save the current message as a quick prompt.</summary>
    public RelayCommand SaveQuickPromptCommand => _saveQuickPromptCommand ??= new RelayCommand(
        () =>
        {
            _quickPrompts?.BeginSave(ComposerText);
            IsQuickMenuOpen = true;
        },
        () => HasQuickPrompts && !string.IsNullOrWhiteSpace(ComposerText));

    /// <summary>Puts <paramref name="item"/>'s text into the message box and closes the menu.</summary>
    public void UseQuickPrompt(QuickPromptItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ComposerText = string.IsNullOrWhiteSpace(ComposerText) ? item.Text : ComposerText.TrimEnd() + Environment.NewLine + item.Text;
        IsQuickMenuOpen = false;
        RequestComposerFocus();
    }

    // ---------------------------------------------------------------- shared

    private void RefreshComposerCommands()
    {
        _addToQueueCommand?.NotifyCanExecuteChanged();
        _saveQuickPromptCommand?.NotifyCanExecuteChanged();
        OnPropertiesChanged(nameof(CanAddToQueue), nameof(QueueStatusText));
    }

    private void DisposeComposer()
    {
        _queueRetryTimer?.Stop();
        CancelPickOnDispose();
        if (_interaction is { } interaction)
        {
            interaction.SelectionChanged -= OnSelectionChanged;
            interaction.PickStateChanged -= OnPickStateChanged;
        }
    }

    private void CancelPickOnDispose()
    {
        try
        {
            _pickCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ended.
        }
    }
}

/// <summary>One request waiting in a chat's queue.</summary>
public sealed class QueuedRequest : ObservableObject
{
    private int _position;

    /// <summary>Creates the request.</summary>
    /// <param name="displayText">What the user typed (shown in the queue and in the chat).</param>
    /// <param name="sentText">What is sent (with the selection block, when there was one).</param>
    /// <param name="attachments">Attached files.</param>
    /// <param name="selectionNote">"5 objects selected" when the selection goes with it.</param>
    public QueuedRequest(string displayText, string sentText, IReadOnlyList<string> attachments, string? selectionNote = null)
    {
        DisplayText = displayText;
        SentText = sentText;
        Attachments = attachments;
        SelectionNote = selectionNote;
        Title = ChatViewModel.TitleFrom(displayText, attachments.Count);
    }

    /// <summary>What the user typed.</summary>
    public string DisplayText { get; }

    /// <summary>What is sent to the AI.</summary>
    public string SentText { get; }

    /// <summary>Attached files.</summary>
    public IReadOnlyList<string> Attachments { get; }

    /// <summary>"5 objects selected", or null.</summary>
    public string? SelectionNote { get; }

    /// <summary>One-line title.</summary>
    public string Title { get; }

    /// <summary>Tool tip: the full request and what goes with it.</summary>
    public string ToolTip
    {
        get
        {
            var extra = new List<string>();
            if (Attachments.Count > 0)
            {
                extra.Add(Attachments.Count == 1 ? "1 file attached" : $"{Attachments.Count} files attached");
            }

            if (SelectionNote is { } note)
            {
                extra.Add(note);
            }

            return extra.Count == 0 ? DisplayText : DisplayText + "\n(" + string.Join(", ", extra) + ")";
        }
    }

    /// <summary>1-based place in the queue.</summary>
    public int Position
    {
        get => _position;
        internal set => Set(ref _position, value);
    }
}
