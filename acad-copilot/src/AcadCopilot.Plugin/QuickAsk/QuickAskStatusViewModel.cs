using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.ViewModels;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>What the status bubble next to the floating AI button shows.</summary>
public enum QuickAskState
{
    /// <summary>No bubble.</summary>
    Hidden,

    /// <summary>The request waits in the chat's queue (the chat is still busy).</summary>
    Queued,

    /// <summary>The AI works on it ("Drawing kitchen walls...").</summary>
    Working,

    /// <summary>A question or approval card waits in the chat.</summary>
    NeedsAnswer,

    /// <summary>Finished ("Done - kitchen 10'x20'").</summary>
    Done,

    /// <summary>Stopped by the user.</summary>
    Stopped,

    /// <summary>Failed, or a problem before sending.</summary>
    Failed,

    /// <summary>Undo of the request's drawing steps runs.</summary>
    Undoing,

    /// <summary>The request's drawing steps were undone.</summary>
    Undone,
}

/// <summary>
/// The status bubble of the last Quick Ask: follows the chat tab that got the request (its message, live activity, plan
/// task, question / approval cards and the turn's footer) and offers Undo (the run_commands calls of that request) and
/// Open chat. Finished states hide by themselves after a while (never while the pointer is over the bubble). UI thread.
/// </summary>
public sealed class QuickAskStatusViewModel : ObservableObject, IDisposable
{
    /// <summary>How long "Done" (and "Stopped", "Undone") stays.</summary>
    public static readonly TimeSpan DoneVisibleFor = TimeSpan.FromSeconds(25);

    /// <summary>How long an error stays.</summary>
    public static readonly TimeSpan ErrorVisibleFor = TimeSpan.FromSeconds(12);

    private readonly IQuickAskHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _hideTimer;
    private ChatViewModel? _chat;
    private QuickAskMessage? _message;
    private UserMessageItem? _userItem;
    private bool _queuedCheckPending;
    private bool _shownChatForAnswer;
    private bool _finished;
    private bool _undoStarted;
    private QuickAskState _state;
    private string _text = string.Empty;
    private int _undoCalls;
    private bool _isPointerOver;
    private CancellationTokenSource? _undoCts;
    private bool _disposed;

    /// <summary>Creates the bubble's view model.</summary>
    public QuickAskStatusViewModel(IQuickAskHost host, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _host = host;
        _dispatcher = dispatcher;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _hideTimer.Tick += (_, _) => OnHideTimer();
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => CanUndo);
        OpenChatCommand = new RelayCommand(OpenChat, () => _chat is not null);
        DismissCommand = new RelayCommand(Dismiss);
    }

    /// <summary>Raised when <see cref="IsVisible"/> or the bubble's size may have changed (the window re-anchors).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>What the bubble shows.</summary>
    public QuickAskState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertiesChanged(
                    nameof(IsVisible), nameof(IsBusy), nameof(IsDone), nameof(IsProblem), nameof(IsNeedsAnswer),
                    nameof(CanUndo), nameof(ShowOpenChat), nameof(ShowDismiss));
                UndoCommand.NotifyCanExecuteChanged();
                OpenChatCommand.NotifyCanExecuteChanged();
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The bubble's line.</summary>
    public string Text
    {
        get => _text;
        private set
        {
            if (Set(ref _text, value))
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Tool tip: the full request and where it went.</summary>
    public string ToolTip => _message is null
        ? "Quick Ask"
        : $"{_message.DisplayText}\nChat: {_chat?.Title} ({_chat?.DrawingTitle})";

    /// <summary>True while the bubble is shown.</summary>
    public bool IsVisible => State != QuickAskState.Hidden;

    /// <summary>True while the spinner turns (queued, working, undoing).</summary>
    public bool IsBusy => State is QuickAskState.Queued or QuickAskState.Working or QuickAskState.Undoing;

    /// <summary>True for the green check (done, undone).</summary>
    public bool IsDone => State is QuickAskState.Done or QuickAskState.Undone;

    /// <summary>True for the warning icon (failed, stopped).</summary>
    public bool IsProblem => State is QuickAskState.Failed or QuickAskState.Stopped;

    /// <summary>True while a question or approval waits in the chat.</summary>
    public bool IsNeedsAnswer => State == QuickAskState.NeedsAnswer;

    /// <summary>The run_commands calls of the request (what Undo takes back).</summary>
    public int UndoCalls
    {
        get => _undoCalls;
        private set
        {
            if (Set(ref _undoCalls, value))
            {
                OnPropertyChanged(nameof(CanUndo));
                UndoCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True when [Undo] is offered: done, and the request drew something.</summary>
    public bool CanUndo => State == QuickAskState.Done && UndoCalls > 0;

    /// <summary>True when [Open chat] is offered.</summary>
    public bool ShowOpenChat => _chat is not null && State is not QuickAskState.Hidden and not QuickAskState.Undoing;

    /// <summary>True when the small x is offered (finished states; a running request keeps its bubble).</summary>
    public bool ShowDismiss => State is QuickAskState.Done or QuickAskState.Stopped or QuickAskState.Failed or QuickAskState.Undone;

    /// <summary>The chat that got the request (null before the first request).</summary>
    public ChatViewModel? Chat => _chat;

    /// <summary>The view sets this while the pointer is over the bubble (it then does not hide).</summary>
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set => Set(ref _isPointerOver, value);
    }

    /// <summary>Takes back the drawing steps of the request (the copilot's undo of its run_commands calls).</summary>
    public RelayCommand UndoCommand { get; }

    /// <summary>Shows the side panel on the chat of the request.</summary>
    public RelayCommand OpenChatCommand { get; }

    /// <summary>Hides the bubble.</summary>
    public RelayCommand DismissCommand { get; }

    /// <summary>Starts following <paramref name="chat"/> for <paramref name="message"/> (the previous request is let go).</summary>
    public void Track(ChatViewModel chat, QuickAskMessage message, QuickAskDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(message);
        Detach();
        _chat = chat;
        _message = message;
        _shownChatForAnswer = false;
        _finished = false;
        _undoStarted = false;
        UndoCalls = 0;
        _userItem = delivery == QuickAskDelivery.Started
            ? chat.Items.OfType<UserMessageItem>().LastOrDefault(i => string.Equals(i.Text, message.DisplayText, StringComparison.Ordinal))
            : null;
        chat.PropertyChanged += OnChatPropertyChanged;
        chat.Items.CollectionChanged += OnItemsChanged;
        chat.Queue.CollectionChanged += OnQueueChanged;
        OnPropertiesChanged(nameof(Chat), nameof(ToolTip), nameof(ShowOpenChat));
        OpenChatCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    /// <summary>Shows a problem that happened before anything was sent (no chat, queue full ...).</summary>
    public void ShowProblem(string text, ChatViewModel? chat = null)
    {
        Detach();
        _finished = true;
        _chat = chat;
        _message = null;
        OnPropertiesChanged(nameof(Chat), nameof(ToolTip), nameof(ShowOpenChat));
        OpenChatCommand.NotifyCanExecuteChanged();
        Text = text;
        State = QuickAskState.Failed;
        StartHideTimer(ErrorVisibleFor);
    }

    /// <summary>Recomputes the bubble from the chat (public for tests; the chat's events call it).</summary>
    public void Refresh()
    {
        if (_disposed || _undoStarted || _chat is not { } chat || _message is not { } message)
        {
            return;
        }

        if (_userItem is null)
        {
            var position = chat.Queue.FirstOrDefault(q => string.Equals(q.DisplayText, message.DisplayText, StringComparison.Ordinal))?.Position ?? 0;
            Text = QuickAskText.Queued(position);
            State = QuickAskState.Queued;
            return;
        }

        var start = chat.Items.IndexOf(_userItem);
        if (start < 0)
        {
            // New chat, or the tab was cleared: nothing left to follow.
            Hide();
            return;
        }

        TurnFooterItem? footer = null;
        ErrorItem? error = null;
        var calls = 0;
        for (var i = start + 1; i < chat.Items.Count && footer is null; i++)
        {
            switch (chat.Items[i])
            {
                case TurnFooterItem f:
                    footer = f;
                    break;
                case ErrorItem e:
                    error ??= e;
                    break;
                case ActivityGroupItem group:
                    calls += group.Rows.Count(r => r.Kind == ActivityKind.Commands && r.IsSucceeded);
                    break;
                case UserMessageItem:
                    // The next request started without a footer for ours (should not happen): stop here.
                    i = chat.Items.Count;
                    break;
            }
        }

        UndoCalls = calls;
        if (footer is not null)
        {
            Finish(footer.Outcome, error);
            return;
        }

        if (chat.IsWaitingForUser)
        {
            Text = "The AI needs your answer";
            State = QuickAskState.NeedsAnswer;
            if (!_shownChatForAnswer)
            {
                // Open the side panel on that chat once; the user answers there.
                _shownChatForAnswer = true;
                ShowChat(chat);
            }

            return;
        }

        _shownChatForAnswer = false;
        Text = QuickAskText.Working(message.Subject, chat.Plan.CurrentTaskText, chat.ShowActivity ? chat.ActivityText : null);
        State = QuickAskState.Working;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hideTimer.Stop();
        _undoCts?.Cancel();
        Detach();
    }

    private void Finish(TurnOutcome outcome, ErrorItem? error)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        var subject = _message?.Subject ?? "your request";
        switch (outcome)
        {
            case TurnOutcome.Completed:
                Text = QuickAskText.Done(subject);
                State = QuickAskState.Done;
                StartHideTimer(DoneVisibleFor);
                break;
            case TurnOutcome.Stopped:
                Text = QuickAskText.Stopped(subject);
                State = QuickAskState.Stopped;
                StartHideTimer(DoneVisibleFor);
                break;
            default:
                Text = QuickAskText.Failed(error is null ? null : error.Title + ": " + error.Message);
                State = QuickAskState.Failed;
                StartHideTimer(ErrorVisibleFor);
                break;
        }
    }

    private async Task UndoAsync()
    {
        if (!CanUndo || _chat is not { } chat)
        {
            return;
        }

        var calls = UndoCalls;
        _undoStarted = true;
        _hideTimer.Stop();
        Text = "Undoing...";
        State = QuickAskState.Undoing;
        _undoCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        QuickAskUndoResult result;
        try
        {
            result = await _host.UndoAsync(chat, calls, _undoCts.Token);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: undo failed", ex);
            result = new QuickAskUndoResult(false, ex.Message);
        }
        finally
        {
            _undoCts.Dispose();
            _undoCts = null;
        }

        if (_disposed || !ReferenceEquals(chat, _chat))
        {
            return;
        }

        if (result.Success)
        {
            Text = QuickAskText.Undone(calls);
            State = QuickAskState.Undone;
            StartHideTimer(DoneVisibleFor);
        }
        else
        {
            Text = QuickAskText.Shorten("Could not undo: " + result.Message, QuickAskText.MaxStatusLength + 30);
            State = QuickAskState.Failed;
            StartHideTimer(ErrorVisibleFor);
        }
    }

    private void OpenChat()
    {
        if (_chat is { } chat)
        {
            ShowChat(chat);
        }
    }

    private void ShowChat(ChatViewModel chat)
    {
        try
        {
            _host.ShowChat(chat);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: could not open the chat panel", ex);
        }
    }

    private void Dismiss() => Hide();

    private void Hide()
    {
        _hideTimer.Stop();
        Detach();
        State = QuickAskState.Hidden;
    }

    private void StartHideTimer(TimeSpan after)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = after;
        _hideTimer.Start();
    }

    private void OnHideTimer()
    {
        if (IsPointerOver)
        {
            // Keep it while the user looks at it; try again a little later.
            _hideTimer.Interval = TimeSpan.FromSeconds(3);
            return;
        }

        if (State is QuickAskState.Done or QuickAskState.Stopped or QuickAskState.Failed or QuickAskState.Undone)
        {
            Hide();
        }
        else
        {
            _hideTimer.Stop();
        }
    }

    private void Detach()
    {
        if (_chat is { } chat)
        {
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.Items.CollectionChanged -= OnItemsChanged;
            chat.Queue.CollectionChanged -= OnQueueChanged;
        }

        _chat = null;
        _userItem = null;
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.ActivityText) or nameof(ChatViewModel.ActivityElapsed) or nameof(ChatViewModel.ShowActivity)
                or nameof(ChatViewModel.IsWaitingForUser) or nameof(ChatViewModel.IsBusy) or nameof(ChatViewModel.IsPlanPinned):
                Refresh();
                break;
            case nameof(ChatViewModel.Title) or nameof(ChatViewModel.DrawingTitle):
                OnPropertyChanged(nameof(ToolTip));
                break;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_userItem is null && _message is { } message && e.NewItems is { } added)
        {
            // A queued request starts: its message appears in the chat.
            _userItem = added.OfType<UserMessageItem>().FirstOrDefault(i => i.FromQueue && string.Equals(i.Text, message.DisplayText, StringComparison.Ordinal));
        }

        Refresh();
    }

    // The queue changed: our request may have moved, started (its message follows in the same dispatcher pass) or been
    // removed by the user. Check once things settled.
    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_queuedCheckPending || _userItem is not null)
        {
            return;
        }

        _queuedCheckPending = true;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queuedCheckPending = false;
            if (_disposed || _userItem is not null || _chat is not { } chat || _message is not { } message)
            {
                return;
            }

            if (!chat.Queue.Any(q => string.Equals(q.DisplayText, message.DisplayText, StringComparison.Ordinal)))
            {
                // Removed from the queue without running.
                Hide();
                return;
            }

            Refresh();
        }));
    }
}
