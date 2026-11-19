using System.Runtime.CompilerServices;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;

namespace AcadCopilot.Core.Panel;

/// <summary>
/// One chat tab (<see cref="ICopilotSession"/>): owns its per-provider CLI session ids, provider and model choice, the
/// attached tab UI, the "always approve" memory and the running turn. Created by <see cref="CopilotBackend.CreateSession"/>;
/// the backend holds what is shared (runners, settings, sign-in, the AutoCAD turn queue, the running-turn limit).
/// Contains no AutoCAD code.
/// </summary>
public sealed class CopilotSession : ICopilotSession
{
    private readonly CopilotBackend _hub;
    private readonly Lock _gate = new();
    private readonly Dictionary<ProviderId, string> _cliSessions = [];
    private readonly Dictionary<ProviderId, string?> _models = [];
    private readonly HashSet<string> _alwaysApproved = new(StringComparer.Ordinal);
    private ProviderId _provider;
    private DrawingRef? _drawing;
    private AcadTurnStatus _acadTurn;
    private ICopilotUi? _ui;
    private CancellationToken _turnToken = new(canceled: true);
    private int _busy;
    private volatile bool _closed;

    internal CopilotSession(CopilotBackend hub, string id, DrawingRef? drawing, ProviderId provider, IReadOnlyDictionary<ProviderId, string?> models)
    {
        _hub = hub;
        Id = id;
        _drawing = drawing;
        _provider = provider;
        foreach (var (key, value) in models)
        {
            _models[key] = value;
        }
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler? ChatReset;

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlyList<IAgentRunner> Runners => _hub.Runners;

    /// <inheritdoc />
    public DrawingRef? Drawing
    {
        get
        {
            lock (_gate)
            {
                return _drawing;
            }
        }
    }

    /// <inheritdoc />
    public AcadTurnStatus AcadTurn
    {
        get
        {
            lock (_gate)
            {
                return _acadTurn;
            }
        }
    }

    /// <inheritdoc />
    public bool CanStartTurn => IsBusy || _hub.RunningSessionCount < _hub.MaxRunningSessions;

    /// <inheritdoc />
    public int MaxRunningTurns => _hub.MaxRunningSessions;

    /// <summary>True after the tab was closed.</summary>
    public bool IsClosed => _closed;

    /// <inheritdoc />
    public ProviderId SelectedProvider
    {
        get
        {
            lock (_gate)
            {
                return _provider;
            }
        }

        set
        {
            _hub.EnsureRunner(value);
            lock (_gate)
            {
                if (_provider == value)
                {
                    return;
                }

                _provider = value;
            }

            _hub.RememberProvider(value);
            RaiseStateChanged();
        }
    }

    /// <inheritdoc />
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>
    /// AutoCAD host info for this tab: the document title is this chat's drawing (with "(closed)" once it was closed),
    /// or the active drawing while the chat is not bound to one yet.
    /// </summary>
    public AcadSessionInfo Acad
    {
        get
        {
            var acad = _hub.Acad;
            return Drawing is { } drawing ? acad with { DocumentTitle = drawing.DisplayTitle } : acad;
        }
    }

    /// <summary>The attached tab UI, if any.</summary>
    public ICopilotUi? Ui => Volatile.Read(ref _ui);

    /// <summary>
    /// Cancelled when this chat's running turn is stopped (and while no turn runs): the plug-in links it into the tool
    /// calls of this chat, so Stop also leaves the AutoCAD queue or presses Esc at once.
    /// </summary>
    public CancellationToken TurnToken
    {
        get
        {
            lock (_gate)
            {
                return _turnToken;
            }
        }
    }

    /// <inheritdoc />
    public string? GetModel(ProviderId provider)
    {
        lock (_gate)
        {
            return _models.TryGetValue(provider, out var model) ? model : null;
        }
    }

    /// <inheritdoc />
    public void SetModel(ProviderId provider, string? modelId)
    {
        lock (_gate)
        {
            _models[provider] = modelId;
        }

        _hub.RememberModel(provider, modelId);
        RaiseStateChanged();
    }

    /// <inheritdoc />
    public Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default) =>
        _hub.GetStatusAsync(provider, cancellationToken);

    /// <summary>The CLI session id that the next turn of <paramref name="provider"/> resumes, if any.</summary>
    public string? GetSessionId(ProviderId provider)
    {
        lock (_gate)
        {
            return _cliSessions.TryGetValue(provider, out var id) ? id : null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_closed)
        {
            yield return new TurnFailed("This chat tab was closed.", AgentFailureKind.Other);
            yield break;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            yield return new TurnFailed("A request is already running. Press Stop first.", AgentFailureKind.Other);
            yield break;
        }

        if (!_hub.TryBeginTurn())
        {
            Volatile.Write(ref _busy, 0);
            yield return new TurnFailed(
                $"{_hub.MaxRunningSessions} chats are already working, the most at once (each runs its own AI app, and memory is limited). Wait for one to finish or press Stop in one of them, then send again.",
                AgentFailureKind.Other);
            yield break;
        }

        lock (_gate)
        {
            _turnToken = cancellationToken;
        }

        RaiseStateChanged();
        try
        {
            var provider = SelectedProvider;
            var runner = _hub.Runner(provider);
            TurnRequest request;
            AgentEvent? setupFailure = null;
            try
            {
                request = new TurnRequest
                {
                    Prompt = text,
                    Attachments = attachments,
                    Model = GetModel(provider),
                    ResumeSessionId = GetSessionId(provider),
                    AcadMcp = _hub.CreateLaunch(Id),
                    WorkingDirectory = AppPaths.Ensure(_hub.Workspace),
                    Instructions = _hub.BuildInstructions(),
                };
            }
            catch (Exception ex)
            {
                CopilotLog.Error("Could not prepare the turn", ex);
                request = null!;
                setupFailure = new TurnFailed("Could not start: " + ex.Message, AgentFailureKind.Other);
            }

            if (setupFailure is not null)
            {
                yield return setupFailure;
                yield break;
            }

            var hiddenCalls = new HashSet<string>(StringComparer.Ordinal);
            var serverName = request.AcadMcp.ServerName;
            var sawTerminal = false;
            AgentEvent? failure = null;
            await using var events = runner.RunTurnAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                AgentEvent current;
                try
                {
                    if (!await events.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    current = events.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failure = new TurnFailed("Stopped.", AgentFailureKind.Cancelled);
                    break;
                }
                catch (Exception ex)
                {
                    CopilotLog.Error($"{runner.DisplayName} runner failed", ex);
                    failure = new TurnFailed($"{runner.DisplayName} failed: {ex.Message}", AgentFailureKind.Other);
                    break;
                }

                switch (current)
                {
                    case SessionStarted started:
                        lock (_gate)
                        {
                            _cliSessions[provider] = started.SessionId;
                        }

                        break;
                    case ToolCallStarted call when IsUiTool(call, serverName):
                        hiddenCalls.Add(call.CallId);
                        continue;
                    case ToolCallFinished finished when hiddenCalls.Contains(finished.CallId):
                        continue;
                    case TurnCompleted or TurnFailed:
                        if (sawTerminal)
                        {
                            continue;
                        }

                        sawTerminal = true;
                        break;
                }

                yield return current;
            }

            if (!sawTerminal)
            {
                yield return failure ?? (cancellationToken.IsCancellationRequested
                    ? new TurnFailed("Stopped.", AgentFailureKind.Cancelled)
                    : new TurnFailed($"{runner.DisplayName} ended without a result.", AgentFailureKind.Other));
            }
        }
        finally
        {
            lock (_gate)
            {
                _turnToken = new CancellationToken(canceled: true);
            }

            Volatile.Write(ref _busy, 0);
            _hub.EndTurn();
            RaiseStateChanged();
        }
    }

    /// <inheritdoc />
    public void NewChat()
    {
        lock (_gate)
        {
            _cliSessions.Clear();
            _alwaysApproved.Clear();
        }

        ChatReset?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool SignIn(ProviderId provider) => _hub.SignIn(provider);

    /// <inheritdoc />
    public void AttachUi(ICopilotUi ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        Volatile.Write(ref _ui, ui);
    }

    /// <summary>ui.askUser for this chat: shows the card in its tab; throws <see cref="UiUnavailableException"/> without one.</summary>
    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken)
    {
        var ui = Ui ?? throw new UiUnavailableException("The AI Copilot panel is not available to show questions. Ask the user in your reply instead.");
        return ui.AskUserAsync(request, cancellationToken);
    }

    /// <summary>ui.updatePlan for this chat (ignored without a UI).</summary>
    public void UpdatePlan(PlanUpdate plan) => Ui?.UpdatePlan(plan);

    /// <summary>
    /// Approval in this chat's tab. Returns <see cref="ApprovalDecision.Approve"/> at once for tools the user chose
    /// "Always" for in this chat; throws <see cref="UiUnavailableException"/> without a UI.
    /// </summary>
    public async Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_alwaysApproved.Contains(request.ToolName))
            {
                return ApprovalDecision.Approve;
            }
        }

        var ui = Ui ?? throw new UiUnavailableException("The AI Copilot panel is not available to approve this action.");
        var decision = await ui.RequestApprovalAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision == ApprovalDecision.AlwaysApprove)
        {
            lock (_gate)
            {
                _alwaysApproved.Add(request.ToolName);
            }
        }

        return decision;
    }

    /// <summary>Binds the chat to <paramref name="drawing"/> when it has no drawing yet; returns the drawing it works on.</summary>
    public DrawingRef BindDrawing(DrawingRef drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        bool changed;
        DrawingRef bound;
        lock (_gate)
        {
            changed = _drawing is null;
            _drawing ??= drawing;
            bound = _drawing;
        }

        if (changed)
        {
            RaiseStateChanged();
        }

        return bound;
    }

    /// <summary>Updates the drawing's title or closed state (same id); other ids are ignored.</summary>
    internal void UpdateDrawing(DrawingRef drawing)
    {
        lock (_gate)
        {
            if (_drawing is null || _drawing.Id != drawing.Id || _drawing == drawing)
            {
                return;
            }

            _drawing = drawing;
        }

        RaiseStateChanged();
    }

    internal void SetAcadTurn(AcadTurnStatus status)
    {
        lock (_gate)
        {
            if (_acadTurn == status)
            {
                return;
            }

            _acadTurn = status;
        }

        RaiseStateChanged();
    }

    internal void MarkClosed() => _closed = true;

    internal void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Session StateChanged handler failed", ex);
        }
    }

    private static bool IsUiTool(ToolCallStarted call, string serverName) =>
        (call.Server is null || call.Server == serverName) &&
        (call.Name == BridgeTools.AskUser || call.Name == BridgeTools.UpdatePlan);
}
