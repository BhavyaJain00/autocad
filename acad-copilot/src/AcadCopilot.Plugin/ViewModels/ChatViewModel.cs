using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// View model of one AI Copilot chat (one tab of the panel; <see cref="CopilotPanelViewModel"/> holds the tabs). Talks to
/// its <see cref="ICopilotBackend"/> (with chat tabs: the tab's <see cref="ICopilotSession"/>) (provider picker, sign-in status,
/// sending messages and rendering the <see cref="AgentEvent"/> stream), implements <see cref="ICopilotUi"/> (question
/// cards, the "Task Progress" checklist and approval cards) and <see cref="IToolActivitySink"/> (the commands the AI
/// typed, what they changed and the pictures it looked at), and drives the Watch / Fast switch
/// (<see cref="DraftingPace.Watch"/>, remembered in settings.json). Free of AutoCAD types; every member must be used on
/// the WPF dispatcher thread except the <see cref="ICopilotUi"/> and <see cref="IToolActivitySink"/> methods, which
/// marshal themselves.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, ICopilotUi, IToolActivitySink, IVoiceTarget, IDisposable
{
    /// <summary>Most files that can be attached to one message.</summary>
    public const int MaxAttachments = 10;

    /// <summary>Largest attachment accepted, in bytes.</summary>
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    /// <summary>Plug-in tool reports kept while the CLI's row for them has not appeared yet.</summary>
    private const int MaxPendingReports = 32;

    private static readonly TimeSpan SignInPollInterval = TimeSpan.FromSeconds(5);
    private static readonly int SignInPollAttempts = 36;

    /// <summary>Longest tab title taken from the first message.</summary>
    public const int MaxTitleLength = 30;

    private readonly ICopilotBackend _backend;
    private readonly ICopilotSession? _session;
    private readonly Dispatcher _dispatcher;
    private readonly IFilePicker? _filePicker;
    private readonly PanelSettingsStore _settingsStore;
    private readonly CopilotSettingsStore _copilotSettings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ToolActivityItem> _activities = new(StringComparer.Ordinal);
    private readonly List<ToolActivityItem> _turnRows = [];
    private readonly List<ToolReport> _pendingReports = [];
    private readonly List<QuestionCardItem> _pendingQuestions = [];
    private readonly List<ApprovalCardItem> _pendingApprovals = [];
    private readonly Dictionary<ProviderId, CancellationTokenSource> _signInPolls = [];
    private volatile PanelSettings _settings;
    private ProviderViewModel _selectedProvider;
    private AssistantMessageItem? _currentAssistant;
    private CancellationTokenSource? _turnCts;
    private TurnInfo? _turn;
    private int _generation;
    private string _composerText = string.Empty;
    private bool _isBusy;
    private bool _isSettingsOpen;
    private string? _notice;
    private bool _noticeIsError;
    private string _documentText = string.Empty;
    private bool _paceChanged;
    private bool _disposed;
    private DispatcherTimer? _activityTimer;
    private long _lastTextDelta;
    private string? _activityLabel;
    private long _activitySince;
    private bool _showActivity;
    private string _activityText = string.Empty;
    private string _activityElapsed = string.Empty;
    private string _title = NewChatTitle;
    private string _drawingTitle = string.Empty;
    private bool _isDrawingClosed;
    private bool _isTabSelected;
    private ChatTabState _tabState;
    private string? _tabStatusText;
    private bool _isWaitingForUser;
    private RelayCommand? _openKnowledgeFolder;
    private RelayCommand? _learnStyle;

    /// <summary>Creates the view model and attaches it to <paramref name="backend"/> as the panel UI.</summary>
    /// <param name="backend">The copilot backend.</param>
    /// <param name="dispatcher">The WPF dispatcher of the view (AutoCAD's main thread inside AutoCAD).</param>
    /// <param name="filePicker">File picker for attachments (null hides attaching).</param>
    /// <param name="settingsStore">Panel settings persistence (panel.json: approval mode).</param>
    /// <param name="copilotSettings">
    /// The settings file the backend uses too (settings.json; null = the default one): the Watch / Fast choice is kept
    /// there and applied to <see cref="DraftingPace.Watch"/> here.
    /// </param>
    /// <param name="voice">Voice input shared by the panel's tabs (null hides the mic button).</param>
    public ChatViewModel(ICopilotBackend backend, Dispatcher dispatcher, IFilePicker? filePicker, PanelSettingsStore settingsStore, CopilotSettingsStore? copilotSettings = null, VoiceInputService? voice = null)
    {
        _backend = backend;
        _session = backend as ICopilotSession;
        _dispatcher = dispatcher;
        _filePicker = filePicker;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
        _copilotSettings = copilotSettings ?? new CopilotSettingsStore();
        DraftingPace.Watch = _copilotSettings.Load().DraftingWatch;
        DraftingPace.Changed += OnPaceChanged;

        Providers = [.. backend.Runners.Select(r => new ProviderViewModel(r))];
        _selectedProvider = Providers.FirstOrDefault(p => p.Id == backend.SelectedProvider) ?? Providers[0];
        _selectedProvider.IsSelected = true;
        foreach (var provider in Providers)
        {
            provider.PropertyChanged += (_, _) => RaiseStatusDependents();
        }

        SendCommand = new RelayCommand(() => _ = SendAsync(), () => CanSend);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        NewChatCommand = new RelayCommand(NewChat);
        SelectProviderCommand = new RelayCommand(p => SelectProvider(p as ProviderViewModel), p => !IsBusy && p is ProviderViewModel);
        SignInCommand = new RelayCommand(p => SignIn(p as ProviderViewModel ?? SelectedProvider));
        RefreshStatusCommand = new RelayCommand(() => _ = RefreshStatusesAsync());
        AttachCommand = new RelayCommand(AttachFiles, () => _filePicker is not null && Attachments.Count < MaxAttachments);
        RemoveAttachmentCommand = new RelayCommand(p =>
        {
            if (p is AttachmentViewModel attachment)
            {
                Attachments.Remove(attachment);
            }
        });
        UseExampleCommand = new RelayCommand(p =>
        {
            if (p is string example)
            {
                ComposerText = example;
                ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        DismissNoticeCommand = new RelayCommand(() => SetNotice(null));
        WatchPaceCommand = new RelayCommand(() => IsWatchPace = true);
        FastPaceCommand = new RelayCommand(() => IsFastPace = true);

        Items.CollectionChanged += (_, _) =>
        {
            RaiseEmptyState();
            UpdateTitle();
        };
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            RefreshCommands();
        };

        Voice = voice is null ? null : new VoiceInputViewModel(voice, dispatcher, this);
        if (Voice is not null)
        {
            Voice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VoiceInputViewModel.State))
                {
                    OnPropertyChanged(nameof(ComposerPlaceholder));
                }
                else if (e.PropertyName == nameof(VoiceInputViewModel.IsActive) && !Voice.IsActive && !_disposed)
                {
                    // Finished speaking: the words are ready to edit or send with Enter.
                    RequestComposerFocus();
                }
            };
        }

        UpdateDocumentText();
        InitAssist();
        backend.StateChanged += OnBackendStateChanged;
        backend.AttachUi(this);
    }

    /// <summary>Tab title before the first message.</summary>
    public const string NewChatTitle = "New chat";

    /// <summary>Raised when the view should move keyboard focus to the composer.</summary>
    public event EventHandler? ComposerFocusRequested;

    /// <summary>The backend (with chat tabs: this tab's session).</summary>
    public ICopilotBackend Backend => _backend;

    /// <summary>The WPF dispatcher this view model lives on.</summary>
    public Dispatcher Dispatcher => _dispatcher;

    /// <summary>Asks the view to focus the composer.</summary>
    public void RequestComposerFocus() => ComposerFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows <paramref name="text"/> in the notice banner of this chat.</summary>
    public void ShowNotice(string text, bool isError = false) => SetNotice(text, isError);

    /// <summary>Tab title: the first message of the conversation ("New chat" before it).</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value))
            {
                OnPropertyChanged(nameof(TabToolTip));
            }
        }
    }

    /// <summary>The drawing this chat works on ("Drawing2.dwg"), shown as a chip in its tab.</summary>
    public string DrawingTitle
    {
        get => _drawingTitle;
        private set
        {
            if (Set(ref _drawingTitle, value))
            {
                OnPropertyChanged(nameof(TabToolTip));
            }
        }
    }

    /// <summary>True when the chat's drawing was closed in AutoCAD.</summary>
    public bool IsDrawingClosed
    {
        get => _isDrawingClosed;
        private set => Set(ref _isDrawingClosed, value);
    }

    /// <summary>True for the tab the panel shows.</summary>
    public bool IsTabSelected
    {
        get => _isTabSelected;
        set => Set(ref _isTabSelected, value);
    }

    /// <summary>This chat's place in the AutoCAD turn queue (idle without chat tabs).</summary>
    public AcadTurnStatus AcadTurn => _session?.AcadTurn ?? AcadTurnStatus.Idle;

    /// <summary>True while a question or approval card of this chat waits for the user.</summary>
    public bool IsWaitingForUser
    {
        get => _isWaitingForUser;
        private set => Set(ref _isWaitingForUser, value);
    }

    /// <summary>What the tab shows next to its title.</summary>
    public ChatTabState TabState
    {
        get => _tabState;
        private set => Set(ref _tabState, value);
    }

    /// <summary>Short status in the tab ("Drawing", "2nd in line", "Needs your answer", "Working"), or null when idle.</summary>
    public string? TabStatusText
    {
        get => _tabStatusText;
        private set
        {
            if (Set(ref _tabStatusText, value))
            {
                OnPropertiesChanged(nameof(HasTabStatus), nameof(TabToolTip));
            }
        }
    }

    /// <summary>True when <see cref="TabStatusText"/> is shown.</summary>
    public bool HasTabStatus => TabStatusText is not null;

    /// <summary>Tool tip of the tab: title, drawing and status.</summary>
    public string TabToolTip =>
        TabStatusText is { } status ? $"{Title}\nDrawing: {DrawingTitle}\n{status}" : $"{Title}\nDrawing: {DrawingTitle}";

    /// <summary>Providers in display order.</summary>
    public IReadOnlyList<ProviderViewModel> Providers { get; }

    /// <summary>Selected provider.</summary>
    public ProviderViewModel SelectedProvider
    {
        get => _selectedProvider;
        set => SelectProvider(value);
    }

    /// <summary>Model choices of the selected provider.</summary>
    public IReadOnlyList<ModelChoice> Models => SelectedProvider.Runner.Models;

    /// <summary>True when the selected CLI offers a model choice.</summary>
    public bool HasModelChoice => Models.Count > 1;

    /// <summary>Selected model (first entry = CLI default).</summary>
    public ModelChoice? SelectedModel
    {
        get
        {
            var id = _backend.GetModel(SelectedProvider.Id);
            return Models.FirstOrDefault(m => m.Id == id) ?? Models.FirstOrDefault();
        }

        set
        {
            if (value is null || value == SelectedModel)
            {
                return;
            }

            _backend.SetModel(SelectedProvider.Id, value.Id);
            OnPropertyChanged();
        }
    }

    /// <summary>The conversation.</summary>
    public ObservableCollection<ChatItem> Items { get; } = [];

    /// <summary>The live "Task Progress" checklist (pinned above the composer while a turn runs).</summary>
    public PlanViewModel Plan { get; } = new();

    /// <summary>True when the checklist is pinned.</summary>
    public bool IsPlanPinned => IsBusy && Plan.HasTasks;

    /// <summary>Text being typed.</summary>
    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (Set(ref _composerText, value ?? string.Empty))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>Files attached to the next message.</summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    /// <summary>True when files are attached.</summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>True when attaching is available.</summary>
    public bool CanAttach => _filePicker is not null;

    /// <summary>Placeholder of the composer.</summary>
    public string ComposerPlaceholder => Voice?.State switch
    {
        VoiceState.Starting => "One moment: starting the microphone...",
        VoiceState.Listening or VoiceState.Stopping => "Speak now: your words appear here...",
        _ => $"Ask {SelectedProvider.DisplayName} to draw or check the drawing...",
    };

    /// <summary>True while a turn runs (Send becomes Stop).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertiesChanged(nameof(IsIdle), nameof(IsPlanPinned));
                RaiseEmptyState();
                RefreshCommands();
                UpdateActivityTimer();
                RefreshActivity();
            }
        }
    }

    /// <summary>Inverse of <see cref="IsBusy"/>.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// True while a turn runs and no answer text is streaming: the live status line ("Thinking... 0:42",
    /// "Typing AutoCAD commands: Outer walls... 0:07", "Waiting for your answer...") with a spinner, so a long silent
    /// think never looks like a hang.
    /// </summary>
    public bool ShowActivity
    {
        get => _showActivity;
        private set => Set(ref _showActivity, value);
    }

    /// <summary>What the AI is doing now ("Thinking...").</summary>
    public string ActivityText
    {
        get => _activityText;
        private set => Set(ref _activityText, value);
    }

    /// <summary>How long it has been doing it ("0:42").</summary>
    public string ActivityElapsed
    {
        get => _activityElapsed;
        private set => Set(ref _activityElapsed, value);
    }

    /// <summary>True when Send is possible.</summary>
    public bool CanSend => !IsBusy && (!string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0);

    /// <summary>True when the settings section is open.</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => Set(ref _isSettingsOpen, value);
    }

    /// <summary>Approval behaviour for destructive tools (persisted).</summary>
    public ApprovalMode ApprovalMode
    {
        get => _settings.ApprovalMode;
        set
        {
            if (_settings.ApprovalMode == value)
            {
                return;
            }

            UpdateSettings(settings => settings with { ApprovalMode = value });
            OnPropertiesChanged(nameof(ApprovalMode), nameof(IsAskDestructive), nameof(IsAutoApprove));
        }
    }

    /// <summary>The mic of this tab, or null when voice input is not available.</summary>
    public VoiceInputViewModel? Voice { get; }

    /// <summary>True when the mic button is shown.</summary>
    public bool HasVoice => Voice is not null;

    /// <summary>Send the message as soon as the speaker stops (persisted; off by default).</summary>
    public bool VoiceAutoSend
    {
        get => _settings.VoiceAutoSend;
        set
        {
            if (_settings.VoiceAutoSend != value)
            {
                UpdateSettings(settings => settings with { VoiceAutoSend = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The voice languages offered in the settings.</summary>
    public IReadOnlyList<VoiceLanguageChoice> VoiceLanguageChoices => VoiceLanguages.Choices;

    /// <summary>The voice language (persisted; automatic = the Windows language).</summary>
    public VoiceLanguageChoice SelectedVoiceLanguage
    {
        get => VoiceLanguages.Find(_settings.VoiceLanguage);
        set
        {
            var tag = value?.Tag;
            if (!string.Equals(_settings.VoiceLanguage, tag, StringComparison.OrdinalIgnoreCase))
            {
                UpdateSettings(settings => settings with { VoiceLanguage = tag });
                OnPropertyChanged();
            }
        }
    }

    /// <inheritdoc />
    string? IVoiceTarget.VoiceLanguageTag => _settings.VoiceLanguage;

    /// <inheritdoc />
    void IVoiceTarget.SendFromVoice()
    {
        if (CanSend)
        {
            _ = SendAsync();
        }
    }

    /// <summary>Radio binding for <see cref="ApprovalMode.AskDestructive"/>.</summary>
    public bool IsAskDestructive
    {
        get => ApprovalMode == ApprovalMode.AskDestructive;
        set
        {
            if (value)
            {
                ApprovalMode = ApprovalMode.AskDestructive;
            }
        }
    }

    /// <summary>Radio binding for <see cref="ApprovalMode.AutoApprove"/>.</summary>
    public bool IsAutoApprove
    {
        get => ApprovalMode == ApprovalMode.AutoApprove;
        set
        {
            if (value)
            {
                ApprovalMode = ApprovalMode.AutoApprove;
            }
        }
    }

    /// <summary>
    /// Watch mode (the default): AutoCAD types each input with a short pause so the user can follow every command
    /// (<see cref="DraftingPace.Watch"/>). Takes effect at the very next typed input, also during a running call;
    /// remembered in settings.json.
    /// </summary>
    public bool IsWatchPace
    {
        get => DraftingPace.Watch;
        set
        {
            if (value)
            {
                DraftingPace.Watch = true;
            }
        }
    }

    /// <summary>Fast mode: commands are typed without pauses (radio binding, the inverse of <see cref="IsWatchPace"/>).</summary>
    public bool IsFastPace
    {
        get => !DraftingPace.Watch;
        set
        {
            if (value)
            {
                DraftingPace.Watch = false;
            }
        }
    }

    /// <summary>Banner text under the header (sign-in hints, attachment problems), or null.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    /// <summary>True when the banner is shown.</summary>
    public bool HasNotice => Notice is not null;

    /// <summary>True when the banner is a problem rather than information.</summary>
    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => Set(ref _noticeIsError, value);
    }

    /// <summary>"AutoCAD 2027 · House.dwg" or "AutoCAD 2027 · No drawing open".</summary>
    public string DocumentText
    {
        get => _documentText;
        private set => Set(ref _documentText, value);
    }

    /// <summary>True once every provider's status has been checked.</summary>
    public bool StatusesKnown => Providers.All(p => p.Status is not null);

    /// <summary>True when no provider can be used (setup empty state).</summary>
    public bool NoProviderUsable => StatusesKnown && !Providers.Any(p => p.IsUsable);

    /// <summary>Shows the "connect an AI account" empty state.</summary>
    public bool ShowSetup => Items.Count == 0 && !IsBusy && NoProviderUsable;

    /// <summary>Shows the welcome empty state with example prompts.</summary>
    public bool ShowWelcome => Items.Count == 0 && !IsBusy && !NoProviderUsable;

    /// <summary>True when the conversation has entries.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Example prompts on the welcome screen.</summary>
    public IReadOnlyList<string> ExamplePrompts { get; } =
    [
        "Draw the ground floor plan for a 30' x 40' east-facing plot: car parking, living, kitchen, 2 bedrooms, 1 toilet, staircase",
        "Draw the front elevation of this plan",
        "Add dimensions to the plan",
        "What is in this drawing?",
        LearnStylePrompt,
    ];

    /// <summary>The message the "Learn my style" button sends.</summary>
    public const string LearnStylePrompt = "Learn my style from this drawing";

    /// <summary>
    /// The folder with what the AI has learned (rules, recipes, lessons, style; %LOCALAPPDATA%\AcadCopilot\knowledge),
    /// or null when unknown (the link is hidden).
    /// </summary>
    public string? KnowledgeDirectory { get; init; }

    /// <summary>True when the "Open knowledge folder" link is shown.</summary>
    public bool HasKnowledgeDirectory => !string.IsNullOrWhiteSpace(KnowledgeDirectory);

    /// <summary>Opens a folder; null = Windows Explorer (tests replace it).</summary>
    public Action<string>? FolderOpener { get; init; }

    /// <summary>Opens <see cref="KnowledgeDirectory"/> in Windows Explorer (only when clicked).</summary>
    public RelayCommand OpenKnowledgeFolderCommand => _openKnowledgeFolder ??= new RelayCommand(OpenKnowledgeFolder, () => HasKnowledgeDirectory);

    /// <summary>Sends "Learn my style from this drawing" (or puts it in the message box while a turn runs).</summary>
    public RelayCommand LearnStyleCommand => _learnStyle ??= new RelayCommand(LearnStyle);

    /// <summary>Sends the composer text.</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>Stops the running turn.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Starts a new conversation.</summary>
    public RelayCommand NewChatCommand { get; }

    /// <summary>Selects the provider passed as parameter.</summary>
    public RelayCommand SelectProviderCommand { get; }

    /// <summary>Opens the sign-in terminal for the provider passed as parameter (default: selected).</summary>
    public RelayCommand SignInCommand { get; }

    /// <summary>Re-checks every provider's status.</summary>
    public RelayCommand RefreshStatusCommand { get; }

    /// <summary>Opens the file picker.</summary>
    public RelayCommand AttachCommand { get; }

    /// <summary>Removes the attachment passed as parameter.</summary>
    public RelayCommand RemoveAttachmentCommand { get; }

    /// <summary>Puts the example prompt passed as parameter into the composer.</summary>
    public RelayCommand UseExampleCommand { get; }

    /// <summary>Opens/closes settings.</summary>
    public RelayCommand ToggleSettingsCommand { get; }

    /// <summary>Hides the notice banner.</summary>
    public RelayCommand DismissNoticeCommand { get; }

    /// <summary>Switches to watch mode.</summary>
    public RelayCommand WatchPaceCommand { get; }

    /// <summary>Switches to fast mode.</summary>
    public RelayCommand FastPaceCommand { get; }

    /// <summary>The running turn's bookkeeping, or null between turns (tests and diagnostics).</summary>
    internal TurnInfo? CurrentTurn => _turn is { Finished: false } turn ? turn : null;

    /// <summary>When the statuses were last refreshed (UTC).</summary>
    public DateTime? LastStatusRefreshUtc { get; private set; }

    /// <summary>Checks every provider's install / sign-in state (read-only, off the UI thread).</summary>
    public Task RefreshStatusesAsync()
    {
        LastStatusRefreshUtc = DateTime.UtcNow;
        return Task.WhenAll(Providers.Select(RefreshStatusAsync));
    }

    /// <summary>Checks one provider's state.</summary>
    public async Task RefreshStatusAsync(ProviderViewModel provider)
    {
        provider.IsChecking = true;
        try
        {
            var token = _lifetime.Token;
            var status = await Task.Run(() => _backend.GetStatusAsync(provider.Id, token), token);
            provider.Status = status;
        }
        catch (OperationCanceledException)
        {
            // Panel closing.
        }
        catch (Exception ex)
        {
            CopilotLog.Warn($"Status check of {provider.DisplayName} failed", ex);
            provider.Status = new ProviderStatus
            {
                Provider = provider.Id,
                Installed = provider.Status?.Installed ?? false,
                SignIn = SignInState.Unknown,
                Detail = "Status check failed: " + ex.Message,
            };
        }
        finally
        {
            provider.IsChecking = false;
        }
    }

    /// <summary>Sends <see cref="ComposerText"/> and the attachments and renders the turn until it ends.</summary>
    public async Task SendAsync()
    {
        if (Voice is { IsActive: true } voice)
        {
            // Enter / Send while listening: take the words heard so far, then send them.
            await voice.StopAsync(allowAutoSend: false);
        }

        if (!CanSend)
        {
            return;
        }

        if (_session is { CanStartTurn: false } limited)
        {
            // The message stays in the composer; it can be sent as soon as another chat finishes.
            SetNotice(
                $"{limited.MaxRunningTurns} chats are already working, the most at once (each runs its own AI app and memory is limited). Wait for one to finish or press Stop in one of them, then send again.",
                isError: true);
            return;
        }

        var text = ComposerText.Trim();
        var attachments = Attachments.Select(a => a.Path).ToList();
        var (sent, selectionNote) = WithSelection(text);
        ComposerText = string.Empty;
        Attachments.Clear();
        await RunTurnAsync(text, sent, attachments, selectionNote, fromQueue: false);
    }

    /// <summary>
    /// Shows <paramref name="displayText"/> as the user's message, sends <paramref name="sentText"/> (with the selection
    /// block, when there is one) and renders the turn until it ends; then the queue may start its next request.
    /// </summary>
    private async Task RunTurnAsync(string displayText, string sentText, IReadOnlyList<string> attachments, string? selectionNote, bool fromQueue)
    {
        var text = sentText;
        SetNotice(null);
        IsSettingsOpen = false;
        Items.Add(new UserMessageItem(displayText, attachments) { SelectionNote = selectionNote, FromQueue = fromQueue });

        var provider = SelectedProvider;
        var model = SelectedModel;
        var generation = ++_generation;
        var turn = new TurnInfo(generation, provider, model is { Id: not null } ? model.DisplayName : null);
        _turn = turn;
        _currentAssistant = null;
        _activities.Clear();
        _turnRows.Clear();
        _pendingReports.Clear();
        Plan.Clear();
        _lastOutcome = null;
        using var cts = new CancellationTokenSource();
        _turnCts = cts;
        IsBusy = true;
        try
        {
            await foreach (var agentEvent in _backend.SendAsync(text, attachments, cts.Token))
            {
                if (generation != _generation)
                {
                    continue;
                }

                Apply(agentEvent, turn);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Apply(new TurnFailed("Stopped.", AgentFailureKind.Cancelled), turn);
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Panel turn failed", ex);
            Apply(new TurnFailed(ex.Message, AgentFailureKind.Other), turn);
        }
        finally
        {
            if (!turn.Finished && generation == _generation)
            {
                Apply(cts.IsCancellationRequested
                    ? new TurnFailed("Stopped.", AgentFailureKind.Cancelled)
                    : new TurnFailed($"{provider.DisplayName} ended without a result.", AgentFailureKind.Other), turn);
            }

            if (ReferenceEquals(_turnCts, cts))
            {
                _turnCts = null;
            }

            if (generation == _generation)
            {
                IsBusy = false;
                OnTurnEndedForQueue(_lastOutcome);
            }
        }
    }

    /// <summary>Renders one event of the running turn (public for tests and diagnostics).</summary>
    public void Apply(AgentEvent agentEvent, TurnInfo turn)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        if (turn.Finished)
        {
            return;
        }

        try
        {
            ApplyCore(agentEvent, turn);
        }
        finally
        {
            RefreshActivity();
        }
    }

    private void ApplyCore(AgentEvent agentEvent, TurnInfo turn)
    {
        if (ApplyAssistEvent(agentEvent))
        {
            return;
        }

        switch (agentEvent)
        {
            case TextDelta delta:
                if (delta.Text.Length > 0)
                {
                    _lastTextDelta = Stopwatch.GetTimestamp();
                }

                if (_currentAssistant is null)
                {
                    if (delta.Text.Length == 0)
                    {
                        break;
                    }

                    _currentAssistant = new AssistantMessageItem(string.Empty, isStreaming: true);
                    CloseActivityGroup();
                    Items.Add(_currentAssistant);
                }

                _currentAssistant.Append(delta.Text);
                break;
            case TextDone done:
                if (_currentAssistant is null)
                {
                    if (!string.IsNullOrWhiteSpace(done.Text))
                    {
                        CloseActivityGroup();
                        Items.Add(new AssistantMessageItem(done.Text, isStreaming: false));
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(done.Text))
                    {
                        _currentAssistant.Text = done.Text;
                    }

                    _currentAssistant.IsStreaming = false;
                    _currentAssistant = null;
                }

                break;
            case ToolCallStarted started:
                if (_activities.ContainsKey(started.CallId))
                {
                    break;
                }

                CloseAssistant();
                var group = Items.LastOrDefault() as ActivityGroupItem;
                if (group is null || group.IsClosed)
                {
                    group = new ActivityGroupItem();
                    Items.Add(group);
                }

                var row = group.Add(started.CallId, started.Name, started.Summary, started.Server);
                _activities[started.CallId] = row;

                // Every row can receive the plug-in's report of the same tool (some CLIs report AutoCAD tools without
                // their server prefix); reports name AutoCAD tools only, so other rows never match.
                _turnRows.Add(row);
                AttachPendingReports();
                break;
            case ToolCallFinished finished:
                if (_activities.TryGetValue(finished.CallId, out var finishedRow))
                {
                    finishedRow.Finish(finished.Success, finished.Summary, finished.Error);
                }

                break;
            case Usage usage:
                turn.InputTokens += usage.InputTokens;
                turn.OutputTokens += usage.OutputTokens;
                if (!string.IsNullOrWhiteSpace(usage.Model))
                {
                    turn.ReportedModel = usage.Model;
                }

                break;
            case TurnCompleted:
                FinishTurn(turn, TurnOutcome.Completed, null);
                break;
            case TurnFailed failed:
                FinishTurn(turn, failed.Kind == AgentFailureKind.Cancelled ? TurnOutcome.Stopped : TurnOutcome.Failed, failed);
                break;
        }
    }

    /// <summary>Stops the running turn; pending questions and approvals are cancelled.</summary>
    public void Stop()
    {
        // Stop also pauses the queue: nothing else starts until the user resumes it.
        PauseQueue("you pressed Stop");
        CancelPendingCards("Stopped");
        try
        {
            _turnCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn just ended.
        }
    }

    /// <summary>Clears the conversation and starts a new CLI session.</summary>
    public void NewChat()
    {
        if (IsBusy)
        {
            Stop();
        }

        _generation++;
        _turn = null;
        ClearQueue();
        _backend.NewChat();
        CancelPendingCards("Cancelled");
        _currentAssistant = null;
        _activities.Clear();
        _turnRows.Clear();
        _pendingReports.Clear();
        Items.Clear();
        Plan.Clear();
        IsBusy = false;
        SetNotice(null);
        ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds files to the next message (picker or drag and drop); explains skipped files in the notice.</summary>
    public void AddAttachments(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (var path in paths)
        {
            if (Attachments.Count >= MaxAttachments)
            {
                problems.Add($"at most {MaxAttachments} files per message");
                break;
            }

            if (Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    problems.Add($"'{Path.GetFileName(path)}' was not found");
                    continue;
                }

                if (info.Length > MaxAttachmentBytes)
                {
                    problems.Add($"'{info.Name}' is larger than {MaxAttachmentBytes / (1024 * 1024)} MB");
                    continue;
                }

                Attachments.Add(new AttachmentViewModel(info.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problems.Add($"'{Path.GetFileName(path)}' cannot be read");
            }
        }

        if (problems.Count > 0)
        {
            SetNotice("Not attached: " + string.Join("; ", problems) + ".", isError: true);
        }
    }

    /// <summary>Opens the provider CLI's own login in a terminal window (user click only) and watches for the result.</summary>
    public void SignIn(ProviderViewModel provider)
    {
        bool opened;
        try
        {
            opened = _backend.SignIn(provider.Id);
        }
        catch (Exception ex)
        {
            CopilotLog.Error($"Sign-in terminal for {provider.DisplayName} failed", ex);
            SetNotice($"Could not open the {provider.CliName} sign-in window: {ex.Message}", isError: true);
            return;
        }

        if (!opened)
        {
            SetNotice($"{provider.CliName} was not found. Install it with \"{provider.InstallCommand}\", then click Check again.", isError: true);
            return;
        }

        SetNotice($"A terminal window opened for {provider.DisplayName}. Sign in with your {provider.AccountKind} there; this panel updates by itself when you are done.");
        _ = PollSignInAsync(provider);
    }

    /// <inheritdoc />
    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Questions.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new QuestionResponse { Cancelled = true });
        }

        var completion = new TaskCompletionSource<QuestionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        QuestionCardItem? card = null;
        var shown = RunOnUi(() =>
        {
            card = new QuestionCardItem(request, response =>
            {
                _pendingQuestions.Remove(card!);
                RefreshActivity();
                completion.TrySetResult(response);
            });
            _pendingQuestions.Add(card);
            RefreshActivity();
            CloseAssistant();
            CloseActivityGroup();
            Items.Add(card);
        });
        if (!shown)
        {
            return Task.FromResult(new QuestionResponse { Cancelled = true });
        }

        var registration = cancellationToken.Register(() => RunOnUi(() => card?.Cancel()));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    /// <inheritdoc />
    public void UpdatePlan(PlanUpdate plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RunOnUi(() =>
        {
            Plan.Update(plan);
            OnPropertyChanged(nameof(IsPlanPinned));
        });
    }

    /// <inheritdoc />
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_settings.ApprovalMode == ApprovalMode.AutoApprove)
        {
            CopilotLog.Info($"Auto-approved {request.ToolName} (approval mode: auto).");
            return Task.FromResult(ApprovalDecision.Approve);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(ApprovalDecision.Reject);
        }

        var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApprovalCardItem? card = null;
        var shown = RunOnUi(() =>
        {
            card = new ApprovalCardItem(request, decision =>
            {
                _pendingApprovals.Remove(card!);
                RefreshActivity();
                completion.TrySetResult(decision);
            });
            _pendingApprovals.Add(card);
            RefreshActivity();
            CloseAssistant();
            CloseActivityGroup();
            Items.Add(card);
        });
        if (!shown)
        {
            return Task.FromResult(ApprovalDecision.Reject);
        }

        var registration = cancellationToken.Register(() => RunOnUi(() => card?.Cancel("Cancelled")));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    /// <inheritdoc />
    public void OnToolCallStarting(string toolName, JsonObject? arguments) => Report(toolName, arguments, null);

    /// <inheritdoc />
    public void OnToolCallFinished(string toolName, JsonObject? arguments, ToolResult result)
    {
        if (result is not null)
        {
            Report(toolName, arguments, result);
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
        DisposeComposer();
        Voice?.Dispose();
        _activityTimer?.Stop();
        DraftingPace.Changed -= OnPaceChanged;
        _backend.StateChanged -= OnBackendStateChanged;
        Stop();
        _lifetime.Cancel();
        foreach (var poll in _signInPolls.Values)
        {
            poll.Cancel();
        }

        _signInPolls.Clear();
        _lifetime.Dispose();
    }

    private void OpenKnowledgeFolder()
    {
        if (KnowledgeDirectory is not { Length: > 0 } folder)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            (FolderOpener ?? OpenInExplorer)(folder);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn($"Could not open the knowledge folder '{folder}'", ex);
            SetNotice("Could not open the knowledge folder: " + ex.Message, isError: true);
        }
    }

    private static void OpenInExplorer(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();

    private void LearnStyle() => SendOrDraft(LearnStylePrompt);

    /// <summary>
    /// Sends <paramref name="text"/> as the user's message (visible in the chat), or, while this tab is working or the
    /// running limit is reached, puts it in the message box to send later. True when it was sent.
    /// </summary>
    public bool SendOrDraft(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        IsSettingsOpen = false;
        if (IsBusy)
        {
            // Never replace what the user is typing during a turn: add the request below it.
            ComposerText = string.IsNullOrWhiteSpace(ComposerText) ? text : ComposerText.TrimEnd() + Environment.NewLine + text;
            RequestComposerFocus();
            return false;
        }

        ComposerText = text;
        if (CanSend && _session is not { CanStartTurn: false })
        {
            _ = SendAsync();
            return true;
        }

        RequestComposerFocus();
        return false;
    }

    /// <summary>The message Stop &amp; learn sends for the recording <paramref name="fileName"/>.</summary>
    public static string LearnFromRecordingPrompt(string fileName) =>
        $"Learn from my recording {fileName}: read it, save how I draw as recipes and rules, and tell me briefly what you learned.";

    // Read-modify-write of panel.json, so a tab never writes back another tab's older choices.
    private void UpdateSettings(Func<PanelSettings, PanelSettings> change)
    {
        _settings = change(_settingsStore.Load());
        _settingsStore.Save(_settings);
    }

    private void SelectProvider(ProviderViewModel? provider)
    {
        if (provider is null || IsBusy || ReferenceEquals(provider, _selectedProvider))
        {
            return;
        }

        _backend.SelectedProvider = provider.Id;
        SyncSelectedProvider();
    }

    private void SyncSelectedProvider()
    {
        var selected = Providers.FirstOrDefault(p => p.Id == _backend.SelectedProvider) ?? Providers[0];
        foreach (var provider in Providers)
        {
            provider.IsSelected = ReferenceEquals(provider, selected);
        }

        if (Set(ref _selectedProvider, selected, nameof(SelectedProvider)))
        {
            OnPropertiesChanged(nameof(Models), nameof(HasModelChoice), nameof(SelectedModel), nameof(ComposerPlaceholder));
        }
    }

    private void OnBackendStateChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed)
        {
            return;
        }

        SyncSelectedProvider();
        UpdateDocumentText();
        OnPropertyChanged(nameof(AcadTurn));
        RefreshActivity();
        OnPropertyChanged(nameof(SelectedModel));
        RefreshFeatures();
        if (!IsBusy && !IsQueuePaused && Queue.Count > 0)
        {
            // A chat elsewhere finished: a request waiting for a free slot can start now.
            TryRunNextQueued();
        }
        if (_paceChanged)
        {
            // The backend rewrites settings.json from the copy it loaded at start-up when the provider or model changes;
            // put the Watch / Fast choice made since then back.
            SavePace();
        }
    });

    private void OnPaceChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed)
        {
            return;
        }

        _paceChanged = true;
        OnPropertiesChanged(nameof(IsWatchPace), nameof(IsFastPace));
        SavePace();
    });

    private void SavePace()
    {
        var watch = DraftingPace.Watch;
        _copilotSettings.Update(settings => settings.DraftingWatch == watch ? settings : settings with { DraftingWatch = watch });
    }

    private void Report(string toolName, JsonObject? arguments, ToolResult? result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(toolName) || _disposed)
            {
                return;
            }

            // Copy now: the caller may reuse or change its arguments object after returning.
            var copy = arguments?.DeepClone() as JsonObject;
            RunOnUi(() => AcceptReport(new ToolReport(toolName, copy, result)));
        }
        catch (Exception ex)
        {
            CopilotLog.Warn($"Could not show the {toolName} call in the panel", ex);
        }
    }

    private void AcceptReport(ToolReport report)
    {
        if (CurrentTurn is null)
        {
            // Not a panel turn: another MCP client (Claude Desktop, a terminal session) is using the bridge.
            return;
        }

        if (!TryAttach(report))
        {
            _pendingReports.Add(report);
            if (_pendingReports.Count > MaxPendingReports)
            {
                _pendingReports.RemoveAt(0);
            }
        }
    }

    private void AttachPendingReports()
    {
        for (var i = 0; i < _pendingReports.Count;)
        {
            if (TryAttach(_pendingReports[i]))
            {
                _pendingReports.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// Pairs a plug-in report with the CLI's row of the same call: the oldest row of that tool still waiting for it. A
    /// result goes to the row whose arguments it carries (parallel calls of one tool stay apart), else to a row that has
    /// not been reported at all.
    /// </summary>
    private bool TryAttach(ToolReport report)
    {
        if (report.Result is null)
        {
            var row = _turnRows.FirstOrDefault(r => r.Name == report.Name && !r.HasCallDetails && !r.HasResultDetails);
            if (row is null)
            {
                return false;
            }

            row.ApplyArguments(report.Arguments);
            return true;
        }

        var target = _turnRows.FirstOrDefault(r => r.Name == report.Name && r.HasCallDetails && !r.HasResultDetails && JsonNode.DeepEquals(r.Arguments, report.Arguments))
            ?? _turnRows.FirstOrDefault(r => r.Name == report.Name && !r.HasCallDetails && !r.HasResultDetails);
        if (target is null)
        {
            return false;
        }

        if (!target.HasCallDetails)
        {
            target.ApplyArguments(report.Arguments);
        }

        target.ApplyResult(report.Result);
        return true;
    }

    private void UpdateDocumentText()
    {
        var acad = _backend.Acad;
        var version = !string.IsNullOrWhiteSpace(acad.Product) ? acad.Product.Trim()
            : string.IsNullOrWhiteSpace(acad.AcadVersion) ? "AutoCAD" : "AutoCAD " + acad.AcadVersion;
        DocumentText = string.IsNullOrWhiteSpace(acad.DocumentTitle) ? $"{version} · No drawing open" : $"{version} · {acad.DocumentTitle}";
        var drawing = _session?.Drawing;
        DrawingTitle = drawing?.DisplayTitle ?? (string.IsNullOrWhiteSpace(acad.DocumentTitle) ? "No drawing" : acad.DocumentTitle);
        IsDrawingClosed = drawing?.IsClosed == true;
    }

    private void UpdateTitle()
    {
        var first = Items.OfType<UserMessageItem>().FirstOrDefault();
        Title = first is null ? NewChatTitle : TitleFrom(first.Text, first.AttachmentNames.Count);
    }

    /// <summary>A tab title from a first message: one line, at most <see cref="MaxTitleLength"/> characters.</summary>
    public static string TitleFrom(string? text, int attachmentCount = 0)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0)
        {
            return attachmentCount > 0 ? "Attached files" : NewChatTitle;
        }

        if (line.Length <= MaxTitleLength)
        {
            return line;
        }

        var cut = line[..MaxTitleLength];
        var space = line[MaxTitleLength] == ' ' ? MaxTitleLength : cut.LastIndexOf(' ');
        return (space > MaxTitleLength / 2 ? cut[..space] : cut).TrimEnd(',', '.', ';', ':', '-') + "…";
    }

    /// <summary>
    /// Recomputes the live status line from the turn's state (also run by a half-second timer while a turn runs, which
    /// advances the clock). Public for tests.
    /// </summary>
    public void RefreshActivity()
    {
        string? label = null;
        var waitingForUser = _pendingQuestions.Count + _pendingApprovals.Count > 0;
        IsWaitingForUser = waitingForUser;
        var acadTurn = AcadTurn;
        if (IsBusy && _turn is { Finished: false })
        {
            var streaming = _currentAssistant is { IsStreaming: true }
                && _lastTextDelta != 0
                && Stopwatch.GetElapsedTime(_lastTextDelta) < ActivityStatus.StreamingQuietTime;
            label = ActivityStatus.Label(
                waitingForUser,
                _turnRows.LastOrDefault(r => r.IsRunning),
                streaming,
                acadTurn);
        }

        UpdateTabState(waitingForUser, acadTurn);

        if (label is null)
        {
            _activityLabel = null;
            ShowActivity = false;
            return;
        }

        if (!string.Equals(label, _activityLabel, StringComparison.Ordinal))
        {
            _activityLabel = label;
            _activitySince = Stopwatch.GetTimestamp();
        }

        ActivityText = label + "...";
        ActivityElapsed = ActivityStatus.FormatElapsed(Stopwatch.GetElapsedTime(_activitySince));
        ShowActivity = true;
    }

    private void UpdateTabState(bool waitingForUser, AcadTurnStatus acadTurn)
    {
        var busy = IsBusy && _turn is { Finished: false };
        var state = !busy
            ? ChatTabState.Idle
            : waitingForUser
                ? ChatTabState.NeedsAnswer
                : acadTurn.IsDrawing
                    ? ChatTabState.Drawing
                    : acadTurn.IsWaiting ? ChatTabState.Waiting : ChatTabState.Working;
        TabState = state;
        TabStatusText = state switch
        {
            ChatTabState.NeedsAnswer => "Needs your answer",
            ChatTabState.Drawing => "Drawing…",
            ChatTabState.Waiting => acadTurn.ShortText,
            ChatTabState.Working => "Working",
            _ => null,
        };
    }

    private void UpdateActivityTimer()
    {
        if (IsBusy && !_disposed)
        {
            if (_activityTimer is null)
            {
                _activityTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
                _activityTimer.Tick += (_, _) => RefreshActivity();
            }

            _activityTimer.Start();
        }
        else
        {
            _activityTimer?.Stop();
        }
    }

    private void FinishTurn(TurnInfo turn, TurnOutcome outcome, TurnFailed? failure)
    {
        turn.Finished = true;
        _lastOutcome = outcome;
        CloseAssistant();
        foreach (var activity in _activities.Values)
        {
            activity.Cancel();
        }

        CancelPendingCards(outcome == TurnOutcome.Stopped ? "Stopped" : "Cancelled");
        if (Plan.HasTasks)
        {
            Items.Add(Plan.Snapshot());
            Plan.Clear();
            OnPropertyChanged(nameof(IsPlanPinned));
        }

        if (failure is not null && failure.Kind != AgentFailureKind.Cancelled)
        {
            Items.Add(CreateError(turn.Provider, failure));
            if (failure.Kind is AgentFailureKind.NotSignedIn or AgentFailureKind.NotInstalled)
            {
                _ = RefreshStatusAsync(turn.Provider);
            }
        }

        Items.Add(new TurnFooterItem(turn.FooterText(outcome), outcome));
    }

    private ErrorItem CreateError(ProviderViewModel provider, TurnFailed failure)
    {
        var signIn = new RelayCommand(() => SignIn(provider));
        return failure.Kind switch
        {
            AgentFailureKind.NotSignedIn => new ErrorItem(
                $"Not signed in to {provider.DisplayName}",
                $"{failure.Message}\nSign in once with your {provider.AccountKind}. No API key is needed.",
                showSignIn: true,
                provider.DisplayName,
                signIn),
            AgentFailureKind.NotInstalled => new ErrorItem(
                $"{provider.CliName} is not installed",
                $"{failure.Message}\nInstall it with: {provider.InstallCommand}",
                showSignIn: false,
                provider.DisplayName,
                null),
            AgentFailureKind.RateLimited => new ErrorItem(
                "Usage limit reached",
                $"{failure.Message}\nWait for the limit to reset or switch to another provider.",
                showSignIn: false,
                provider.DisplayName,
                null),
            _ => new ErrorItem("Something went wrong", failure.Message, showSignIn: false, provider.DisplayName, null),
        };
    }

    private void CloseAssistant()
    {
        if (_currentAssistant is not null)
        {
            _currentAssistant.IsStreaming = false;
            _currentAssistant = null;
        }
    }

    private void CloseActivityGroup()
    {
        if (Items.LastOrDefault() is ActivityGroupItem group)
        {
            group.IsClosed = true;
        }
    }

    private void CancelPendingCards(string reason)
    {
        foreach (var question in _pendingQuestions.ToList())
        {
            question.Cancel();
        }

        foreach (var approval in _pendingApprovals.ToList())
        {
            approval.Cancel(reason);
        }
    }

    private void AttachFiles()
    {
        if (_filePicker is null)
        {
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = _filePicker.PickFiles();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("File picker failed", ex);
            SetNotice("Could not open the file picker: " + ex.Message, isError: true);
            return;
        }

        AddAttachments(files);
    }

    private async Task PollSignInAsync(ProviderViewModel provider)
    {
        if (_signInPolls.Remove(provider.Id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var poll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _signInPolls[provider.Id] = poll;
        try
        {
            for (var attempt = 0; attempt < SignInPollAttempts; attempt++)
            {
                await Task.Delay(SignInPollInterval, poll.Token);
                await RefreshStatusAsync(provider);
                if (provider.IsSignedIn)
                {
                    SetNotice($"Signed in to {provider.DisplayName}. You can start chatting.");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or panel closing.
        }
        finally
        {
            if (_signInPolls.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, poll))
            {
                _signInPolls.Remove(provider.Id);
                poll.Dispose();
            }
        }
    }

    private void SetNotice(string? text, bool isError = false)
    {
        NoticeIsError = isError;
        Notice = text;
    }

    private bool RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return true;
        }

        if (_dispatcher.HasShutdownStarted)
        {
            return false;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CopilotLog.Error("Panel UI update failed", ex);
            }
        });
        return true;
    }

    private void RaiseStatusDependents()
    {
        OnPropertiesChanged(nameof(StatusesKnown), nameof(NoProviderUsable));
        RaiseEmptyState();
    }

    private void RaiseEmptyState() => OnPropertiesChanged(nameof(ShowSetup), nameof(ShowWelcome), nameof(HasItems));

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SelectProviderCommand.NotifyCanExecuteChanged();
        AttachCommand.NotifyCanExecuteChanged();
        RefreshComposerCommands();
    }

    /// <summary>One plug-in report: a tool call starting (no result yet) or finished.</summary>
    private sealed record ToolReport(string Name, JsonObject? Arguments, ToolResult? Result);
}

/// <summary>Bookkeeping of one running turn (footer data).</summary>
public sealed class TurnInfo
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>Creates the turn info.</summary>
    public TurnInfo(int generation, ProviderViewModel provider, string? modelName)
    {
        Generation = generation;
        Provider = provider;
        ModelName = modelName;
    }

    /// <summary>Conversation generation (New chat increments it).</summary>
    public int Generation { get; }

    /// <summary>Provider that answers.</summary>
    public ProviderViewModel Provider { get; }

    /// <summary>Selected model display name (null = CLI default).</summary>
    public string? ModelName { get; }

    /// <summary>Model reported by the CLI.</summary>
    public string? ReportedModel { get; set; }

    /// <summary>Input tokens reported.</summary>
    public long InputTokens { get; set; }

    /// <summary>Output tokens reported.</summary>
    public long OutputTokens { get; set; }

    /// <summary>True after the terminal event.</summary>
    public bool Finished { get; set; }

    /// <summary>"Claude · claude-sonnet · 12 s · 3.4k tokens".</summary>
    public string FooterText(TurnOutcome outcome)
    {
        var parts = new List<string> { Provider.DisplayName };
        if ((ReportedModel ?? ModelName) is { } model)
        {
            parts.Add(model);
        }

        parts.Add(ToolNames.FormatDuration(_stopwatch.Elapsed));
        var tokens = InputTokens + OutputTokens;
        if (tokens > 0)
        {
            parts.Add(ToolNames.FormatTokens(tokens) + " tokens");
        }

        var text = string.Join(" · ", parts);
        return outcome switch
        {
            TurnOutcome.Stopped => "Stopped · " + text,
            TurnOutcome.Failed => "Failed · " + text,
            _ => text,
        };
    }
}

/// <summary>Pure helpers of the panel's live status line (what the AI is doing while a turn runs).</summary>
public static class ActivityStatus
{
    /// <summary>After this long without a streamed text fragment the status line comes back.</summary>
    public static readonly TimeSpan StreamingQuietTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The status label, or null when it is hidden (answer text is streaming right now). A question or approval card
    /// waiting for the user wins, then the running tool call, otherwise the model is thinking.
    /// </summary>
    public static string? Label(bool waitingForUser, ToolActivityItem? runningTool, bool textStreaming) =>
        Label(waitingForUser, runningTool, textStreaming, AcadTurnStatus.Idle);

    /// <summary>
    /// The status label with the chat's place in the AutoCAD turn queue: while another chat types into AutoCAD, a drawing
    /// action of this chat shows "Waiting for AutoCAD (2nd in line)".
    /// </summary>
    public static string? Label(bool waitingForUser, ToolActivityItem? runningTool, bool textStreaming, AcadTurnStatus acadTurn)
    {
        if (waitingForUser)
        {
            return "Waiting for your answer";
        }

        if (acadTurn.IsWaiting)
        {
            return acadTurn.Text;
        }

        if (acadTurn.IsDrawing && runningTool is null)
        {
            return "Drawing in AutoCAD";
        }

        if (runningTool is not null)
        {
            if (runningTool.Kind == ActivityKind.Commands)
            {
                return "Typing AutoCAD commands: " + runningTool.Title;
            }

            return runningTool.Name == "ask_user" ? "Waiting for your answer" : ToolLabel(runningTool.Name);
        }

        return textStreaming ? null : "Thinking";
    }

    /// <summary>"Looking at the drawing" for known AutoCAD tools, "Using Some tool" otherwise.</summary>
    public static string ToolLabel(string name)
    {
        var running = ToolActivityText.Title(name, ActivityState.Running);
        var plain = ToolNames.Humanize(name);
        return string.Equals(running, plain, StringComparison.Ordinal) ? "Using " + plain : running;
    }

    /// <summary>"0:07", "1:42", "1:02:03".</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
    }
}

/// <summary>What a chat tab shows next to its title.</summary>
public enum ChatTabState
{
    /// <summary>No turn runs.</summary>
    Idle,

    /// <summary>The AI is thinking, planning or answering.</summary>
    Working,

    /// <summary>A drawing action of this chat waits for another chat's (AutoCAD has one command line).</summary>
    Waiting,

    /// <summary>This chat is typing into AutoCAD now.</summary>
    Drawing,

    /// <summary>A question or approval card waits for the user.</summary>
    NeedsAnswer,
}
