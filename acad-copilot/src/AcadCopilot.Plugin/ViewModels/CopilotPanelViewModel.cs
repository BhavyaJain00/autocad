using System.Collections.ObjectModel;
using System.ComponentModel;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Learning;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// The panel's chat tabs. Each tab is a <see cref="ChatViewModel"/> over its own <see cref="ICopilotSession"/>: its own
/// conversation, provider and model, task progress and Stop, bound to the drawing that was active when it was created.
/// Tabs think and answer in parallel (at most <see cref="ICopilotHub.MaxRunningSessions"/> at once); their drawing
/// actions take turns in AutoCAD. Keeps what is shared in step across the tabs (sign-in states, approval mode, voice
/// settings); the Watch / Fast switch is global already. Switching tabs stops the mic of the tab left (its words stay
/// there). UI thread only.
/// </summary>
public sealed class CopilotPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>Most tabs that can be open (idle ones cost no memory, but the strip has to stay usable).</summary>
    public const int MaxTabs = 12;

    private readonly ICopilotHub _hub;
    private readonly Func<ICopilotSession, ChatViewModel> _createChat;
    private readonly bool _canCreateTabs;
    private ChatViewModel _selectedTab;
    private ChatViewModel? _pendingClose;
    private bool _syncing;
    private bool _disposed;

    /// <summary>Creates the tabs view model: one tab per existing session, or a first tab when there is none.</summary>
    /// <param name="hub">The chat sessions.</param>
    /// <param name="createChat">Creates the view model of one tab (attaches itself to the session).</param>
    /// <param name="canCreateTabs">False for a host with a single fixed conversation (no + button).</param>
    /// <param name="recorder">The "Learn from me" recorder (null hides the Learning bar).</param>
    public CopilotPanelViewModel(ICopilotHub hub, Func<ICopilotSession, ChatViewModel> createChat, bool canCreateTabs = true, IUserActionRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(createChat);
        _hub = hub;
        _createChat = createChat;
        _canCreateTabs = canCreateTabs;

        NewTabCommand = new RelayCommand(() => NewTab(), () => CanAddTab);
        SelectTabCommand = new RelayCommand(p => SelectTab(p as ChatViewModel), p => p is ChatViewModel);
        CloseTabCommand = new RelayCommand(p => RequestClose(p as ChatViewModel), p => p is ChatViewModel && (_canCreateTabs || Tabs.Count > 1));
        ConfirmCloseCommand = new RelayCommand(ConfirmClose);
        CancelCloseCommand = new RelayCommand(() => PendingClose = null);

        var sessions = hub.Sessions.Count > 0 ? hub.Sessions : [hub.CreateSession()];
        foreach (var session in sessions)
        {
            Add(_createChat(session));
        }

        _selectedTab = Tabs.FirstOrDefault(t => ReferenceEquals(t.Backend, hub.ActiveSession)) ?? Tabs[0];
        _selectedTab.IsTabSelected = true;
        hub.ActiveSession = SessionOf(_selectedTab);
        hub.SessionsChanged += OnSessionsChanged;
        Tabs.CollectionChanged += (_, _) => RaiseTabCount();
        if (recorder is not null)
        {
            Learning = new LearningViewModel(recorder, _selectedTab.Dispatcher, OnRecordingSaved, (text, isError) => SelectedTab.ShowNotice(text, isError));
        }
    }

    /// <summary>The "Learn from me" bar (one recorder for every tab), or null when the host has no recorder.</summary>
    public LearningViewModel? Learning { get; }

    /// <summary>True when the Learning bar is shown.</summary>
    public bool HasLearning => Learning is not null;

    /// <summary>Raised after <see cref="SelectedTab"/> changed (the view swaps its DataContext).</summary>
    public event EventHandler? SelectedTabChanged;

    /// <summary>The open tabs, in order.</summary>
    public ObservableCollection<ChatViewModel> Tabs { get; } = [];

    /// <summary>The tab the panel shows.</summary>
    public ChatViewModel SelectedTab
    {
        get => _selectedTab;
        set => SelectTab(value);
    }

    /// <summary>True when another tab can be opened.</summary>
    public bool CanAddTab => _canCreateTabs && Tabs.Count < MaxTabs;

    /// <summary>Chats running a turn now.</summary>
    public int RunningCount => _hub.RunningSessionCount;

    /// <summary>The most chats that may run at once.</summary>
    public int MaxRunning => _hub.MaxRunningSessions;

    /// <summary>"2 of 4 chats working" while more than one tab exists and something runs; otherwise empty.</summary>
    public string RunningText => Tabs.Count > 1 && RunningCount > 0 ? $"{RunningCount} of {MaxRunning} chats working" : string.Empty;

    /// <summary>Tool tip of the + button.</summary>
    public string NewTabToolTip => CanAddTab
        ? "New chat tab on the active drawing (chats work in parallel; their drawing steps take turns in AutoCAD)"
        : _canCreateTabs ? $"At most {MaxTabs} chat tabs; close one first" : "Chat tabs are not available here";

    /// <summary>A working tab the user asked to close (the confirmation bar is shown), or null.</summary>
    public ChatViewModel? PendingClose
    {
        get => _pendingClose;
        private set
        {
            if (Set(ref _pendingClose, value))
            {
                OnPropertiesChanged(nameof(HasPendingClose), nameof(PendingCloseText));
            }
        }
    }

    /// <summary>True while the close confirmation is shown.</summary>
    public bool HasPendingClose => PendingClose is not null;

    /// <summary>The close confirmation text.</summary>
    public string PendingCloseText => PendingClose is { } tab ? $"\"{tab.Title}\" is still working. Stop it and close the tab?" : string.Empty;

    /// <summary>Opens a new tab bound to the active drawing.</summary>
    public RelayCommand NewTabCommand { get; }

    /// <summary>Shows the tab passed as parameter.</summary>
    public RelayCommand SelectTabCommand { get; }

    /// <summary>Closes the tab passed as parameter (asks first when it is working).</summary>
    public RelayCommand CloseTabCommand { get; }

    /// <summary>Stops and closes the tab waiting for confirmation.</summary>
    public RelayCommand ConfirmCloseCommand { get; }

    /// <summary>Keeps the tab waiting for confirmation.</summary>
    public RelayCommand CancelCloseCommand { get; }

    /// <summary>Opens a new tab (bound to the drawing that is active now) and shows it. Null when no tab can be added.</summary>
    public ChatViewModel? NewTab()
    {
        if (!CanAddTab)
        {
            return null;
        }

        ICopilotSession session;
        try
        {
            session = _hub.CreateSession();
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Could not create a chat tab", ex);
            SelectedTab.ShowNotice("Could not open a new chat tab: " + ex.Message, isError: true);
            return null;
        }

        var chat = _createChat(session);
        CopySharedState(SelectedTab, chat);
        Add(chat);
        SelectTab(chat);
        chat.RequestComposerFocus();
        return chat;
    }

    /// <summary>Shows <paramref name="tab"/>.</summary>
    public void SelectTab(ChatViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab) || ReferenceEquals(tab, _selectedTab))
        {
            return;
        }

        if (_selectedTab.Voice is { IsActive: true } voice)
        {
            _ = voice.StopAsync(allowAutoSend: false);
        }

        _selectedTab.IsTabSelected = false;
        tab.IsTabSelected = true;
        Set(ref _selectedTab, tab, nameof(SelectedTab));
        _hub.ActiveSession = SessionOf(tab);
        SelectedTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the tab of session <paramref name="sessionId"/>, when it exists.</summary>
    public void ShowSession(string? sessionId)
    {
        if (sessionId is not null && Tabs.FirstOrDefault(t => SessionOf(t)?.Id == sessionId) is { } tab)
        {
            SelectTab(tab);
        }
    }

    /// <summary>Closes <paramref name="tab"/>; a working tab asks first (<see cref="PendingClose"/>).</summary>
    public void RequestClose(ChatViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        if (tab.IsBusy)
        {
            SelectTab(tab);
            PendingClose = tab;
            return;
        }

        Close(tab);
    }

    /// <summary>Stops the pending tab's turn and closes it.</summary>
    public void ConfirmClose()
    {
        if (PendingClose is { } tab)
        {
            PendingClose = null;
            tab.Stop();
            Close(tab);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.SessionsChanged -= OnSessionsChanged;
        Learning?.Dispose();
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            tab.Dispose();
        }
    }

    private void Close(ChatViewModel tab)
    {
        if (!_canCreateTabs && Tabs.Count <= 1)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (ReferenceEquals(PendingClose, tab))
        {
            PendingClose = null;
        }

        if (Tabs.Count == 1)
        {
            // Never leave the panel without a chat: a fresh tab replaces the last one.
            var fresh = NewTab();
            if (fresh is null)
            {
                return;
            }
        }
        else if (ReferenceEquals(tab, _selectedTab))
        {
            SelectTab(Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1]);
        }

        Tabs.Remove(tab);
        tab.PropertyChanged -= OnTabPropertyChanged;
        foreach (var provider in tab.Providers)
        {
            provider.PropertyChanged -= OnProviderPropertyChanged;
        }

        tab.Dispose();
        if (SessionOf(tab) is { } session)
        {
            _hub.CloseSession(session);
        }
    }

    private void Add(ChatViewModel chat)
    {
        chat.PropertyChanged += OnTabPropertyChanged;
        foreach (var provider in chat.Providers)
        {
            provider.PropertyChanged += OnProviderPropertyChanged;
        }

        Tabs.Add(chat);
    }

    private static ICopilotSession? SessionOf(ChatViewModel chat) => chat.Backend as ICopilotSession;

    // Stop & learn saved a recording: the tab shown asks its AI to learn from it (a visible message), or keeps the
    // request in its message box while it is still working.
    private void OnRecordingSaved(RecordingOutcome outcome)
    {
        if (outcome.FileName is not { } file)
        {
            return;
        }

        var tab = SelectedTab;
        var sent = tab.SendOrDraft(ChatViewModel.LearnFromRecordingPrompt(file));
        var style = outcome.StyleMessage is { Length: > 0 } message ? " Style of your new objects: " + message : string.Empty;
        tab.ShowNotice(
            sent
                ? $"Recording saved ({outcome.Actions} actions): {file}.{style}"
                : $"Recording saved ({outcome.Actions} actions): {file}. This chat is busy, so the request to learn from it waits in the message box: press Send when it is done.{style}",
            isError: false);
    }

    // A new tab starts with what the others know: sign-in states and the approval mode.
    private void CopySharedState(ChatViewModel from, ChatViewModel to)
    {
        _syncing = true;
        try
        {
            foreach (var provider in to.Providers)
            {
                if (from.Providers.FirstOrDefault(p => p.Id == provider.Id) is { Status: { } status })
                {
                    provider.Status = status;
                }
            }

            to.ApprovalMode = from.ApprovalMode;
            to.VoiceAutoSend = from.VoiceAutoSend;
            to.SelectedVoiceLanguage = from.SelectedVoiceLanguage;
        }
        finally
        {
            _syncing = false;
        }
    }

    // Sign-in checks of one tab update every tab (each CLI is installed and signed in once for all).
    private void OnProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || sender is not ProviderViewModel { Status: { } status } changed || e.PropertyName != nameof(ProviderViewModel.Status))
        {
            return;
        }

        _syncing = true;
        try
        {
            foreach (var tab in Tabs)
            {
                foreach (var provider in tab.Providers)
                {
                    if (!ReferenceEquals(provider, changed) && provider.Id == changed.Id)
                    {
                        provider.Status = status;
                    }
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ChatViewModel changed)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.ApprovalMode) when !_syncing:
                _syncing = true;
                try
                {
                    foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, changed)))
                    {
                        tab.ApprovalMode = changed.ApprovalMode;
                    }
                }
                finally
                {
                    _syncing = false;
                }

                break;
            case nameof(ChatViewModel.VoiceAutoSend) or nameof(ChatViewModel.SelectedVoiceLanguage) when !_syncing:
                _syncing = true;
                try
                {
                    foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, changed)))
                    {
                        tab.VoiceAutoSend = changed.VoiceAutoSend;
                        tab.SelectedVoiceLanguage = changed.SelectedVoiceLanguage;
                    }
                }
                finally
                {
                    _syncing = false;
                }

                break;
            case nameof(ChatViewModel.AutoSwitchProvider) or nameof(ChatViewModel.AskBeforeDrawing) or nameof(ChatViewModel.AutoReview) or nameof(ChatViewModel.TutorMode) when !_syncing:
                // The switches are shared by every tab (one backend setting): show the new value everywhere.
                _syncing = true;
                try
                {
                    foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, changed)))
                    {
                        tab.RefreshFeatures();
                    }
                }
                finally
                {
                    _syncing = false;
                }

                break;
            case nameof(ChatViewModel.Title) when ReferenceEquals(changed, PendingClose):
                OnPropertyChanged(nameof(PendingCloseText));
                break;
            case nameof(ChatViewModel.IsBusy):
                if (!changed.IsBusy && ReferenceEquals(changed, PendingClose))
                {
                    // It finished by itself: no need to ask any more.
                    PendingClose = null;
                }

                RaiseRunning();
                break;
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (SelectedTab.Dispatcher.CheckAccess())
        {
            RaiseRunning();
        }
        else
        {
            _ = SelectedTab.Dispatcher.InvokeAsync(RaiseRunning);
        }
    }

    private void RaiseRunning() => OnPropertiesChanged(nameof(RunningCount), nameof(RunningText));

    private void RaiseTabCount()
    {
        OnPropertiesChanged(nameof(CanAddTab), nameof(NewTabToolTip), nameof(RunningText));
        NewTabCommand.NotifyCanExecuteChanged();
        CloseTabCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// Hub for a host that hands the pane a single <see cref="ICopilotBackend"/> (no chat tabs): one fixed session that
/// forwards to it.
/// </summary>
internal sealed class SingleSessionHub : ICopilotHub
{
    private readonly SingleSession _session;

    public SingleSessionHub(ICopilotBackend backend)
    {
        // A backend with the assistant switches keeps them visible through the wrapper.
        _session = backend is ICopilotAssist assist ? new AssistSingleSession(backend, assist) : new SingleSession(backend);
    }

    public event EventHandler? SessionsChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<IAgentRunner> Runners => _session.Runners;

    public IReadOnlyList<ICopilotSession> Sessions => [_session];

    public ICopilotSession? ActiveSession
    {
        get => _session;
        set { }
    }

    public int MaxRunningSessions => 1;

    public int RunningSessionCount => _session.IsBusy ? 1 : 0;

    public ICopilotSession CreateSession() => throw new NotSupportedException("This panel has a single conversation.");

    public void CloseSession(ICopilotSession session)
    {
    }

    private sealed class AssistSingleSession(ICopilotBackend backend, ICopilotAssist assist) : SingleSession(backend), ICopilotAssist
    {
        public CopilotFeatures Features
        {
            get => assist.Features;
            set => assist.Features = value;
        }

        public bool EmitNoticesAsText
        {
            get => assist.EmitNoticesAsText;
            set => assist.EmitNoticesAsText = value;
        }

        public IAsyncEnumerable<AgentEvent> ReviewAsync(string? focus, CancellationToken cancellationToken) => assist.ReviewAsync(focus, cancellationToken);
    }

    private class SingleSession(ICopilotBackend backend) : ICopilotSession
    {
        public event EventHandler? StateChanged
        {
            add => backend.StateChanged += value;
            remove => backend.StateChanged -= value;
        }

        public event EventHandler? ChatReset
        {
            add => backend.ChatReset += value;
            remove => backend.ChatReset -= value;
        }

        public string Id => "main";

        public DrawingRef? Drawing => null;

        public AcadTurnStatus AcadTurn => AcadTurnStatus.Idle;

        public bool CanStartTurn => true;

        public int MaxRunningTurns => 1;

        public IReadOnlyList<IAgentRunner> Runners => backend.Runners;

        public ProviderId SelectedProvider
        {
            get => backend.SelectedProvider;
            set => backend.SelectedProvider = value;
        }

        public bool IsBusy => backend.IsBusy;

        public AcadSessionInfo Acad => backend.Acad;

        public string? GetModel(ProviderId provider) => backend.GetModel(provider);

        public void SetModel(ProviderId provider, string? modelId) => backend.SetModel(provider, modelId);

        public Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default) =>
            backend.GetStatusAsync(provider, cancellationToken);

        public IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, CancellationToken cancellationToken) =>
            backend.SendAsync(text, attachments, cancellationToken);

        public void NewChat() => backend.NewChat();

        public bool SignIn(ProviderId provider) => backend.SignIn(provider);

        public void AttachUi(ICopilotUi ui) => backend.AttachUi(ui);
    }
}
