using System.Security.Cryptography;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Learning;

namespace AcadCopilot.Core.Panel;

/// <summary>
/// The panel backend: owns the runners, settings, sign-in, the AutoCAD host info, the chat sessions (one per tab,
/// <see cref="CopilotSession"/>), the limit on chats running at once and the <see cref="AcadTurnQueue"/> in which the
/// chats' drawing actions take turns. Also routes the add-in-side calls (questions, plans, approvals) to the session they
/// belong to.
/// <para>
/// For compatibility it still implements <see cref="ICopilotBackend"/> itself: those members act on
/// <see cref="DefaultSession"/> (the active tab, else the first one, created on demand).
/// </para>
/// Contains no AutoCAD code, so it is unit-tested directly.
/// </summary>
public sealed class CopilotBackend : ICopilotBackend, ICopilotHub
{
    /// <summary>Default for the most chats that may run at once (each runs its own CLI process; RAM is limited).</summary>
    public const int DefaultMaxRunningSessions = 4;

    private readonly Dictionary<ProviderId, IAgentRunner> _runnersById;
    private readonly Func<McpServerLaunch> _acadMcp;
    private readonly Func<DrawingRef?>? _currentDrawing;
    private readonly CopilotSettingsStore _settingsStore;
    private readonly Lock _gate = new();
    private readonly List<CopilotSession> _sessions = [];
    private CopilotSettings _settings;
    private AcadSessionInfo _acad;
    private CopilotSession? _active;
    private int _running;

    /// <summary>Creates the backend.</summary>
    /// <param name="runners">One runner per provider (display order).</param>
    /// <param name="acadMcp">Builds the bridge launch info for each turn (each session adds its <c>--session</c> id).</param>
    /// <param name="acad">Initial AutoCAD host info.</param>
    /// <param name="settingsStore">Settings persistence (defaults to %LOCALAPPDATA%\AcadCopilot\settings.json).</param>
    /// <param name="workspaceDirectory">CLI working directory (defaults to <see cref="AppPaths.Workspace"/>).</param>
    /// <param name="currentDrawing">
    /// The drawing that is active now, for binding a new session (called on the thread that creates the session; null
    /// = sessions start unbound and bind on their first tool call).
    /// </param>
    /// <param name="maxRunningSessions">The most sessions that may run a turn at once.</param>
    /// <param name="knowledgeDirectory">
    /// The knowledge folder whose "WHAT YOU HAVE LEARNED" digest is added to every turn's instructions (defaults to
    /// <see cref="AppPaths.Knowledge"/>, or to the "knowledge" folder next to <paramref name="workspaceDirectory"/> when
    /// that is given, so tests never read the user's real knowledge).
    /// </param>
    public CopilotBackend(
        IEnumerable<IAgentRunner> runners,
        Func<McpServerLaunch> acadMcp,
        AcadSessionInfo acad,
        CopilotSettingsStore? settingsStore = null,
        string? workspaceDirectory = null,
        Func<DrawingRef?>? currentDrawing = null,
        int maxRunningSessions = DefaultMaxRunningSessions,
        string? knowledgeDirectory = null)
    {
        Runners = [.. runners];
        if (Runners.Count == 0)
        {
            throw new ArgumentException("At least one runner is required.", nameof(runners));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxRunningSessions, 1);
        _runnersById = Runners.ToDictionary(r => r.ProviderId);
        _acadMcp = acadMcp;
        _currentDrawing = currentDrawing;
        _acad = acad;
        _settingsStore = settingsStore ?? new CopilotSettingsStore();
        Workspace = workspaceDirectory ?? AppPaths.Workspace;
        Knowledge = new KnowledgeStore(knowledgeDirectory ?? (workspaceDirectory is null
            ? AppPaths.Knowledge
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workspaceDirectory)) ?? workspaceDirectory, "knowledge")));
        MaxRunningSessions = maxRunningSessions;
        _settings = _settingsStore.Load();
        if (!_runnersById.ContainsKey(_settings.Provider))
        {
            _settings = _settings with { Provider = Runners[0].ProviderId };
        }

        DrawingQueue.Changed += (_, _) => RefreshAcadTurns();
    }

    /// <summary>Raised when the AutoCAD info, a session's state, or the default session's provider / model changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised after <see cref="NewChat"/> of the default session.</summary>
    public event EventHandler? ChatReset;

    /// <inheritdoc />
    public event EventHandler? SessionsChanged;

    /// <inheritdoc cref="ICopilotHub.Runners" />
    public IReadOnlyList<IAgentRunner> Runners { get; }

    /// <summary>The queue in which the chats' drawing actions take turns (the plug-in's pipe handler enters it).</summary>
    public AcadTurnQueue DrawingQueue { get; } = new();

    /// <inheritdoc />
    public int MaxRunningSessions { get; }

    /// <inheritdoc />
    public int RunningSessionCount => Volatile.Read(ref _running);

    /// <summary>CLI working directory root.</summary>
    internal string Workspace { get; }

    /// <summary>The knowledge base the AI learns into (read for every turn's instructions).</summary>
    public KnowledgeStore Knowledge { get; }

    /// <summary>
    /// The system instructions of the next turn: the drafting instructions for the host product plus the current
    /// "WHAT YOU HAVE LEARNED" digest (read fresh, so what the AI saved last turn applies now).
    /// </summary>
    public string BuildInstructions() => CopilotInstructions.Compose(Acad.Product, KnowledgeDigest.Build(Knowledge));

    /// <inheritdoc />
    public IReadOnlyList<ICopilotSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <inheritdoc />
    public ICopilotSession? ActiveSession
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }

        set
        {
            lock (_gate)
            {
                _active = value is CopilotSession session && _sessions.Contains(session) ? session : _active;
            }
        }
    }

    /// <summary>The session the single-chat members act on: the active tab, else the first one (created when there is none).</summary>
    public CopilotSession DefaultSession
    {
        get
        {
            lock (_gate)
            {
                if ((_active ?? _sessions.FirstOrDefault()) is { } existing)
                {
                    return existing;
                }
            }

            return (CopilotSession)CreateSession();
        }
    }

    /// <inheritdoc />
    public ProviderId SelectedProvider
    {
        get => DefaultSession.SelectedProvider;
        set => DefaultSession.SelectedProvider = value;
    }

    /// <summary>True while the default session runs a turn.</summary>
    public bool IsBusy => DefaultSession.IsBusy;

    /// <inheritdoc />
    public AcadSessionInfo Acad
    {
        get
        {
            lock (_gate)
            {
                return _acad;
            }
        }
    }

    /// <summary>The panel UI attached to the default session, if any.</summary>
    public ICopilotUi? Ui => DefaultSession.Ui;

    /// <inheritdoc />
    public ICopilotSession CreateSession()
    {
        DrawingRef? drawing = null;
        try
        {
            drawing = _currentDrawing?.Invoke();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not read the active drawing for a new chat", ex);
        }

        CopilotSession session;
        lock (_gate)
        {
            var models = new Dictionary<ProviderId, string?>();
            foreach (var runner in Runners)
            {
                models[runner.ProviderId] = _settings.Models.TryGetValue(CopilotSettings.Key(runner.ProviderId), out var model) ? model : null;
            }

            session = new CopilotSession(this, NewSessionId(), drawing, _settings.Provider, models);
            _sessions.Add(session);
            _active ??= session;
        }

        session.StateChanged += OnSessionStateChanged;
        session.ChatReset += OnSessionChatReset;
        CopilotLog.Info($"Chat session {session.Id} created{(drawing is null ? " (no drawing open)" : " for " + drawing.Title)}.");
        RaiseSessionsChanged();
        return session;
    }

    /// <inheritdoc />
    public void CloseSession(ICopilotSession session)
    {
        if (session is not CopilotSession own)
        {
            return;
        }

        lock (_gate)
        {
            if (!_sessions.Remove(own))
            {
                return;
            }

            if (ReferenceEquals(_active, own))
            {
                _active = _sessions.FirstOrDefault();
            }
        }

        own.MarkClosed();
        own.StateChanged -= OnSessionStateChanged;
        own.ChatReset -= OnSessionChatReset;
        CopilotLog.Info($"Chat session {own.Id} closed.");
        RaiseSessionsChanged();
    }

    /// <summary>The open session with <paramref name="id"/>, or null.</summary>
    public CopilotSession? FindSession(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_gate)
        {
            return _sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The session a pipe request belongs to: the session with <paramref name="sessionId"/>; without an id (a client
    /// outside the panel) the active tab's session (null when no tab exists). <paramref name="closed"/> is true when an
    /// id was given but its tab is gone.
    /// </summary>
    public CopilotSession? ResolveSession(string? sessionId, out bool closed)
    {
        closed = false;
        if (string.IsNullOrEmpty(sessionId))
        {
            lock (_gate)
            {
                return _active ?? _sessions.FirstOrDefault();
            }
        }

        var session = FindSession(sessionId);
        closed = session is null;
        return session;
    }

    /// <inheritdoc />
    public string? GetModel(ProviderId provider) => DefaultSession.GetModel(provider);

    /// <inheritdoc />
    public void SetModel(ProviderId provider, string? modelId) => DefaultSession.SetModel(provider, modelId);

    /// <summary>Updates the AutoCAD host info (called by the add-in when the active document changes).</summary>
    public void UpdateAcad(AcadSessionInfo acad)
    {
        lock (_gate)
        {
            if (_acad == acad)
            {
                return;
            }

            _acad = acad;
        }

        RaiseStateChanged();
        foreach (var session in SessionList())
        {
            session.RaiseStateChanged();
        }
    }

    /// <summary>Updates the titles of the open drawings the chats work on (after SAVEAS, document switches).</summary>
    public void UpdateDrawings(IEnumerable<DrawingRef> openDrawings)
    {
        ArgumentNullException.ThrowIfNull(openDrawings);
        var byId = openDrawings.GroupBy(d => d.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var session in SessionList())
        {
            if (session.Drawing is { IsClosed: false } drawing && byId.TryGetValue(drawing.Id, out var current) && current.Title != drawing.Title)
            {
                session.UpdateDrawing(drawing with { Title = current.Title });
            }
        }
    }

    /// <summary>Marks the drawing <paramref name="drawingId"/> as closed in every chat bound to it.</summary>
    public void MarkDrawingClosed(string drawingId)
    {
        foreach (var session in SessionList())
        {
            if (session.Drawing is { IsClosed: false } drawing && drawing.Id == drawingId)
            {
                session.UpdateDrawing(drawing with { IsClosed = true });
            }
        }
    }

    /// <inheritdoc />
    public Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default) =>
        Runner(provider).GetStatusAsync(cancellationToken);

    /// <summary>The CLI session id that the default session's next turn of <paramref name="provider"/> resumes, if any.</summary>
    public string? GetSessionId(ProviderId provider) => DefaultSession.GetSessionId(provider);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, CancellationToken cancellationToken) =>
        DefaultSession.SendAsync(text, attachments, cancellationToken);

    /// <inheritdoc />
    public void NewChat() => DefaultSession.NewChat();

    /// <inheritdoc />
    public bool SignIn(ProviderId provider)
    {
        var runner = Runner(provider);
        return TerminalLauncher.OpenSignInTerminal(runner.GetSignInCommand(), $"Sign in to {runner.DisplayName}");
    }

    /// <inheritdoc />
    public void AttachUi(ICopilotUi ui) => DefaultSession.AttachUi(ui);

    /// <summary>Add-in side of ui.askUser for the default session.</summary>
    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
        DefaultSession.AskUserAsync(request, cancellationToken);

    /// <summary>Add-in side of ui.updatePlan for the default session.</summary>
    public void UpdatePlan(PlanUpdate plan) => DefaultSession.UpdatePlan(plan);

    /// <summary>Add-in side of the approval gate for the default session.</summary>
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        DefaultSession.RequestApprovalAsync(request, cancellationToken);

    internal IAgentRunner Runner(ProviderId provider) =>
        _runnersById.TryGetValue(provider, out var runner)
            ? runner
            : throw new ArgumentOutOfRangeException(nameof(provider), provider, "No runner for this provider.");

    internal void EnsureRunner(ProviderId provider)
    {
        if (!_runnersById.ContainsKey(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "No runner for this provider.");
        }
    }

    /// <summary>Remembers the provider chosen last (new tabs start with it).</summary>
    internal void RememberProvider(ProviderId provider)
    {
        lock (_gate)
        {
            _settings = _settings with { Provider = provider };
        }

        // Only the provider is written, on top of the file, so values other writers own (DraftingWatch) survive.
        _settingsStore.Update(s => s.Provider == provider ? s : s with { Provider = provider });
    }

    /// <summary>Remembers the model chosen last for <paramref name="provider"/>.</summary>
    internal void RememberModel(ProviderId provider, string? modelId)
    {
        lock (_gate)
        {
            var models = new Dictionary<string, string?>(_settings.Models) { [CopilotSettings.Key(provider)] = modelId };
            _settings = _settings with { Models = models };
        }

        _settingsStore.Update(s => s with { Models = new Dictionary<string, string?>(s.Models) { [CopilotSettings.Key(provider)] = modelId } });
    }

    /// <summary>The bridge launch for a turn of session <paramref name="sessionId"/>: the base launch plus <c>--session &lt;id&gt;</c>.</summary>
    internal McpServerLaunch CreateLaunch(string sessionId)
    {
        var launch = _acadMcp();
        return launch with { Arguments = [.. launch.Arguments, "--session", sessionId] };
    }

    /// <summary>Takes a running-turn slot; false when <see cref="MaxRunningSessions"/> turns run already.</summary>
    internal bool TryBeginTurn()
    {
        while (true)
        {
            var running = Volatile.Read(ref _running);
            if (running >= MaxRunningSessions)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _running, running + 1, running) == running)
            {
                RaiseSessionsChanged();
                return true;
            }
        }
    }

    internal void EndTurn()
    {
        if (Interlocked.Decrement(ref _running) < 0)
        {
            Interlocked.Exchange(ref _running, 0);
        }

        RaiseSessionsChanged();
    }

    private static string NewSessionId() => "tab-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private List<CopilotSession> SessionList()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }

    private void RefreshAcadTurns()
    {
        foreach (var session in SessionList())
        {
            session.SetAcadTurn(DrawingQueue.StatusOf(session.Id));
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private void OnSessionChatReset(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, DefaultSessionOrNull()))
        {
            ChatReset?.Invoke(this, EventArgs.Empty);
        }
    }

    private CopilotSession? DefaultSessionOrNull()
    {
        lock (_gate)
        {
            return _active ?? _sessions.FirstOrDefault();
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("StateChanged handler failed", ex);
        }
    }

    private void RaiseSessionsChanged()
    {
        try
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("SessionsChanged handler failed", ex);
        }
    }
}
