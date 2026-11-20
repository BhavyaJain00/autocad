using System.Runtime.CompilerServices;
using System.Text;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;

namespace AcadCopilot.Core.Panel;

/// <summary>
/// One chat tab (<see cref="ICopilotSession"/>): owns its per-provider CLI session ids, provider and model choice, the
/// attached tab UI, the "always approve" memory, the running turn and a <see cref="ConversationJournal"/> of the conversation.
/// Created by <see cref="CopilotBackend.CreateSession"/>; the backend holds what is shared (runners, settings, sign-in,
/// the AutoCAD turn queue, the running-turn limit, the feature switches).
/// <para>
/// A turn may continue with another provider (AI switching, <see cref="CopilotFeatures.AutoSwitch"/>), may end with an
/// automatic second-opinion review (<see cref="CopilotFeatures.AutoReview"/>), and "review this plan" runs the review
/// (<see cref="ReviewAsync"/>). Contains no AutoCAD code.
/// </para>
/// </summary>
public sealed class CopilotSession : ICopilotSession, ICopilotAssist
{
    private readonly CopilotBackend _hub;
    private readonly Lock _gate = new();
    private readonly Dictionary<ProviderId, string> _cliSessions = [];
    private readonly Dictionary<ProviderId, string?> _models = [];
    private readonly HashSet<string> _alwaysApproved = new(StringComparer.Ordinal);
    private ProviderId _provider;
    private ProviderId? _lastTurnProvider;
    private ReviewReport? _lastReview;
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

    /// <summary>What this chat remembers about its conversation and the job in progress (for handovers and reviews).</summary>
    public ConversationJournal Journal { get; } = new();

    /// <summary>The last second-opinion review of this chat, if any ("fix these" sends its findings).</summary>
    public ReviewReport? LastReview
    {
        get
        {
            lock (_gate)
            {
                return _lastReview;
            }
        }
    }

    /// <inheritdoc />
    public CopilotFeatures Features
    {
        get => _hub.Features;
        set => _hub.Features = value;
    }

    /// <inheritdoc />
    public bool EmitNoticesAsText
    {
        get => _hub.EmitNoticesAsText;
        set => _hub.EmitNoticesAsText = value;
    }

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
        if (TryEnterTurn(cancellationToken) is { } refused)
        {
            yield return refused;
            yield break;
        }

        try
        {
            var review = attachments.Count == 0 && ReviewRequests.IsReviewRequest(text);
            if (review)
            {
                Journal.BeginJob(text);
                await foreach (var agentEvent in ReviewCoreAsync(ReviewRequests.FocusOf(text), final: true, cancellationToken).ConfigureAwait(false))
                {
                    yield return agentEvent;
                }

                yield break;
            }

            await foreach (var agentEvent in DraftCoreAsync(text, attachments, cancellationToken).ConfigureAwait(false))
            {
                yield return agentEvent;
            }
        }
        finally
        {
            ExitTurn();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> ReviewAsync(string? focus, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (TryEnterTurn(cancellationToken) is { } refused)
        {
            yield return refused;
            yield break;
        }

        try
        {
            await foreach (var agentEvent in ReviewCoreAsync(focus, final: true, cancellationToken).ConfigureAwait(false))
            {
                yield return agentEvent;
            }
        }
        finally
        {
            ExitTurn();
        }
    }

    /// <inheritdoc />
    public void NewChat()
    {
        lock (_gate)
        {
            _cliSessions.Clear();
            _alwaysApproved.Clear();
            _lastTurnProvider = null;
            _lastReview = null;
        }

        Journal.Clear();
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

    /// <summary>ui.updatePlan for this chat (ignored without a UI; always remembered for handovers).</summary>
    public void UpdatePlan(PlanUpdate plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Journal.SetPlan(plan);
        Ui?.UpdatePlan(plan);
    }

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

    /// <summary>Takes the busy flag and a running-turn slot; returns the refusal when that is not possible.</summary>
    private TurnFailed? TryEnterTurn(CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return new TurnFailed("This chat tab was closed.", AgentFailureKind.Other);
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return new TurnFailed("A request is already running. Press Stop first.", AgentFailureKind.Other);
        }

        if (!_hub.TryBeginTurn())
        {
            Volatile.Write(ref _busy, 0);
            return new TurnFailed(
                $"{_hub.MaxRunningSessions} chats are already working, the most at once (each runs its own AI app, and memory is limited). Wait for one to finish or press Stop in one of them, then send again.",
                AgentFailureKind.Other);
        }

        lock (_gate)
        {
            _turnToken = cancellationToken;
        }

        RaiseStateChanged();
        return null;
    }

    private void ExitTurn()
    {
        lock (_gate)
        {
            _turnToken = new CancellationToken(canceled: true);
        }

        Volatile.Write(ref _busy, 0);
        _hub.EndTurn();
        RaiseStateChanged();
    }

    /// <summary>Switches this chat to <paramref name="provider"/> without making it the default for new tabs.</summary>
    private void UseProvider(ProviderId provider)
    {
        lock (_gate)
        {
            if (_provider == provider)
            {
                return;
            }

            _provider = provider;
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// A drafting turn: the selected provider's CLI with the drafting instructions; on a limit, an expired sign-in or a
    /// repeated failure the same job continues with the next signed-in provider (<see cref="ProviderSwitched"/>), and a
    /// finished drawing may get an automatic review. Ends with exactly one terminal event.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> DraftCoreAsync(string text, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var features = _hub.Features;
        var provider = SelectedProvider;
        ProviderId? previous;
        lock (_gate)
        {
            previous = _lastTurnProvider;
        }

        // "fix these" after a review: the drafting AI never saw the reviewer's answer, so send the findings.
        var prompt = text;
        if (ReviewRequests.IsFixRequest(text) && LastReview is { IsClean: false } review)
        {
            prompt = review.FixPrompt;
        }

        Journal.BeginJob(text);
        if (previous is { } last && last != provider && Journal.HasEarlierConversation)
        {
            // The user picked another provider in an ongoing chat: give it the conversation so far.
            prompt = JobHandover.ForProviderChange(Journal, _hub.Runner(last).DisplayName, _hub.Runner(provider).DisplayName, prompt);
        }

        var resume = GetSessionId(provider);
        var tried = new List<ProviderId>();
        var failures = new Dictionary<ProviderId, int>();
        while (true)
        {
            if (!tried.Contains(provider))
            {
                tried.Add(provider);
            }

            AgentEvent? terminal = null;
            await foreach (var agentEvent in RunProviderAsync(provider, prompt, attachments, resume, () => _hub.BuildInstructions(features.TurnOptions), () => _hub.CreateLaunch(Id), keepSession: true, webResearch: features.WebResearch, cancellationToken).ConfigureAwait(false))
            {
                if (agentEvent is TurnCompleted or TurnFailed)
                {
                    terminal = agentEvent;
                    break;
                }

                Journal.Observe(agentEvent);
                yield return agentEvent;
            }

            terminal ??= new TurnFailed($"{_hub.Runner(provider).DisplayName} ended without a result.", AgentFailureKind.Other);
            if (terminal is TurnFailed failed && features.AutoSwitch && !cancellationToken.IsCancellationRequested && failed.Kind != AgentFailureKind.Cancelled)
            {
                var reason = ReasonOf(failed);
                failures[provider] = failures.GetValueOrDefault(provider) + 1;
                if (reason == SwitchReason.Failure && failed.Kind == AgentFailureKind.Other && failures[provider] == 1)
                {
                    // One retry with the same provider before giving the job to another one.
                    CopilotLog.Info($"Chat {Id}: {provider} failed ({failed.Message}); retrying once.");
                    prompt = JobHandover.ForRetry(Journal, failed.Message);
                    resume = GetSessionId(provider);
                    continue;
                }

                var next = await _hub.FindNextProviderAsync(features.ProviderOrder, tried, cancellationToken).ConfigureAwait(false);
                if (next is { } nextProvider)
                {
                    var fromName = _hub.Runner(provider).DisplayName;
                    var toName = _hub.Runner(nextProvider).DisplayName;
                    var message = JobHandover.SwitchMessage(fromName, toName, reason);
                    CopilotLog.Info($"Chat {Id}: {message} ({failed.Message})");

                    // Close a message that was still streaming, then say what happens.
                    yield return new TextDone(string.Empty);
                    yield return new ProviderSwitched(provider, nextProvider, reason, message, failed.Message);
                    if (_hub.EmitNoticesAsText)
                    {
                        yield return new TextDone(message);
                    }

                    Journal.AddAssistant(message);
                    prompt = JobHandover.ForSwitch(Journal, fromName, toName, reason, failed.Message);
                    UseProvider(nextProvider);
                    provider = nextProvider;
                    resume = null;
                    continue;
                }
            }

            lock (_gate)
            {
                _lastTurnProvider = provider;
            }

            if (terminal is TurnCompleted && features.AutoReview && Journal.FinishedDrawing && !cancellationToken.IsCancellationRequested)
            {
                await foreach (var agentEvent in ReviewCoreAsync(null, final: false, cancellationToken).ConfigureAwait(false))
                {
                    yield return agentEvent;
                }
            }

            yield return terminal;
            yield break;
        }
    }

    /// <summary>
    /// A second-opinion review: a fresh CLI call (another signed-in provider when possible) with the reviewer instructions
    /// and a read-only bridge. Streams the reviewer's tool activity, then <see cref="ReviewCompleted"/>; with
    /// <paramref name="final"/> also the terminal event (otherwise a failure becomes a note).
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> ReviewCoreAsync(string? focus, bool final, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reviewer = await _hub.PickReviewerAsync(SelectedProvider, _hub.Features.ProviderOrder, cancellationToken).ConfigureAwait(false);
        var runner = _hub.Runner(reviewer);
        var prompt = BuildReviewPrompt(focus);
        var texts = new List<string>();
        var streamed = new StringBuilder();
        AgentEvent? terminal = null;
        await foreach (var agentEvent in RunProviderAsync(reviewer, prompt, [], null, _hub.BuildReviewerInstructions, () => _hub.CreateLaunch(Id, readOnly: true), keepSession: false, webResearch: false, cancellationToken).ConfigureAwait(false))
        {
            switch (agentEvent)
            {
                case TurnCompleted or TurnFailed:
                    terminal = agentEvent;
                    break;
                case TextDelta delta:
                    streamed.Append(delta.Text);
                    continue;
                case TextDone done:
                    if (!string.IsNullOrWhiteSpace(done.Text))
                    {
                        texts.Add(done.Text);
                    }

                    streamed.Clear();
                    continue;
                case SessionStarted:
                    continue;
                default:
                    yield return agentEvent;
                    continue;
            }

            break;
        }

        if (streamed.Length > 0)
        {
            texts.Add(streamed.ToString());
        }

        if (terminal is TurnCompleted)
        {
            var report = ReviewReport.Parse(reviewer, runner.DisplayName, string.Join("\n", texts));
            lock (_gate)
            {
                _lastReview = report;
            }

            Journal.AddAssistant(report.Markdown);
            yield return new TextDone(string.Empty);
            yield return new ReviewCompleted(report);
            if (_hub.EmitNoticesAsText)
            {
                yield return new TextDone(report.Markdown);
            }

            if (final)
            {
                yield return new TurnCompleted("review");
            }

            yield break;
        }

        var failed = terminal as TurnFailed ?? new TurnFailed($"{runner.DisplayName} ended without a result.", AgentFailureKind.Other);
        if (final)
        {
            yield return failed.Kind == AgentFailureKind.Cancelled ? failed : failed with { Message = "The second-opinion review could not run: " + failed.Message };
        }
        else if (failed.Kind != AgentFailureKind.Cancelled)
        {
            yield return new TextDone(string.Empty);
            yield return new TextDone("The automatic second-opinion review could not run: " + failed.Message);
        }
    }

    private string BuildReviewPrompt(string? focus)
    {
        var builder = new StringBuilder("Review ")
            .Append(string.IsNullOrWhiteSpace(focus) ? "the drawing in this chat's AutoCAD drawing" : focus.Trim())
            .Append(" as a senior architect. Use only the read-only tools, follow your checklist and answer in the exact format.");
        var context = Journal.DescribeContext(includeCurrentJob: false);
        if (context.Length > 0)
        {
            builder.Append("\n\nWhat the user asked for and the task list (context only; check the drawing itself):\n").Append(context);
        }

        var text = builder.ToString();
        return text.Length <= JobHandover.MaxLength ? text : text[..JobHandover.MaxLength];
    }

    private static SwitchReason ReasonOf(TurnFailed failed) => failed.Kind switch
    {
        AgentFailureKind.RateLimited => SwitchReason.Limit,
        AgentFailureKind.NotSignedIn => SwitchReason.SignIn,
        _ => SwitchReason.Failure,
    };

    /// <summary>
    /// Runs one CLI call of <paramref name="provider"/> and streams its events: ask_user / update_plan calls hidden, only
    /// the first terminal event kept, runner exceptions and a missing terminal turned into <see cref="TurnFailed"/>.
    /// Always ends with exactly one terminal event.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunProviderAsync(
        ProviderId provider,
        string prompt,
        IReadOnlyList<string> attachments,
        string? resume,
        Func<string> instructions,
        Func<McpServerLaunch> launch,
        bool keepSession,
        bool webResearch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runner = _hub.Runner(provider);
        TurnRequest request;
        AgentEvent? setupFailure = null;
        try
        {
            request = new TurnRequest
            {
                Prompt = prompt,
                Attachments = attachments,
                Model = GetModel(provider),
                ResumeSessionId = resume,
                AcadMcp = launch(),
                WorkingDirectory = AppPaths.Ensure(_hub.Workspace),
                Instructions = instructions(),
                WebResearch = webResearch,
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
                    if (keepSession)
                    {
                        lock (_gate)
                        {
                            _cliSessions[provider] = started.SessionId;
                        }
                    }

                    break;
                case ToolCallStarted call when IsUiTool(call, serverName):
                    hiddenCalls.Add(call.CallId);
                    continue;
                case ToolCallFinished finished when hiddenCalls.Contains(finished.CallId):
                    continue;
                case TurnCompleted or TurnFailed:
                    yield return current;
                    yield break;
            }

            yield return current;
        }

        yield return failure ?? (cancellationToken.IsCancellationRequested
            ? new TurnFailed("Stopped.", AgentFailureKind.Cancelled)
            : new TurnFailed($"{runner.DisplayName} ended without a result.", AgentFailureKind.Other));
    }
}
