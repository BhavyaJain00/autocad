using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Tools;
using AcadCopilot.Plugin.ViewModels;

namespace AcadCopilot.Plugin.Ipc;

/// <summary>
/// Answers the MCP bridge on the named pipe (thread-pool threads): server info, tools.list from the
/// <see cref="ToolRegistry"/>, tools.call (schema validation -&gt; approval for Destructive tools -&gt; the handler) and the
/// panel methods ui.askUser / ui.updatePlan. Every tool result carries the current drawing revision.
/// <para>
/// <b>Chat tabs.</b> Each call carries the chat session (tab) it belongs to (bridge <c>--session</c>). Question cards,
/// plan updates, approvals and the activity rows go to that tab; tool calls act on that tab's drawing; Stop in that tab
/// cancels its calls (<see cref="CopilotSession.TurnToken"/>). Calls without a session (the user's own MCP clients) use
/// the active tab and the active drawing, as before.
/// </para>
/// <para>
/// <b>Taking turns.</b> AutoCAD has one command line: every tool that types into it (any risk other than Read:
/// run_commands, undo, cancel_command) waits in <see cref="CopilotBackend.DrawingQueue"/>, first come first served, and
/// when its turn comes the tab's drawing is made the active one first (the result says so). Read tools run at once when
/// the tab's drawing is the active one; otherwise they also take a turn (switching drawings is an action).
/// </para>
/// </summary>
public sealed class PluginPipeHandler : IPipeHandler
{
    /// <summary>Server name reported to clients after auth.</summary>
    public const string ServerName = "AcadCopilot.Plugin";

    private static readonly TimeSpan RevealTimeout = TimeSpan.FromSeconds(5);

    private readonly ToolRegistry _registry;
    private readonly AcadDispatcher _dispatcher;
    private readonly RevisionTracker _revisions;
    private readonly CopilotBackend _backend;
    private readonly Func<string?, CancellationToken, Task>? _revealPanel;

    /// <summary>Creates the handler.</summary>
    /// <param name="registry">The plug-in's tools.</param>
    /// <param name="dispatcher">AutoCAD main-thread dispatcher.</param>
    /// <param name="revisions">Drawing revision counter.</param>
    /// <param name="backend">Panel backend: chat sessions, approvals, questions, plan, the turn queue and the AutoCAD info.</param>
    /// <param name="revealPanel">
    /// Shows the chat palette (and, when it was hidden, the tab of the given session) before a question or approval card
    /// is added to it (the AI may run while the palette is hidden); null = never.
    /// </param>
    public PluginPipeHandler(
        ToolRegistry registry,
        AcadDispatcher dispatcher,
        RevisionTracker revisions,
        CopilotBackend backend,
        Func<string?, CancellationToken, Task>? revealPanel = null)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _revisions = revisions;
        _backend = backend;
        _revealPanel = revealPanel;
    }

    /// <inheritdoc />
    public AuthResult GetServerInfo()
    {
        var acad = _backend.Acad;
        return new AuthResult
        {
            ServerName = ServerName,
            ServerVersion = AcadHost.PluginVersion,
            Pid = acad.Pid,
            AcadVersion = acad.AcadVersion,
            DocumentTitle = acad.DocumentTitle,
        };
    }

    /// <inheritdoc />
    public Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ToolsListResult { Tools = _registry.Definitions });

    /// <inheritdoc />
    public async Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_registry.TryGet(call.Name, out var spec))
        {
            return WithRevision(ToolResult.Fail(
                $"Unknown AutoCAD tool '{call.Name}'. Available: {string.Join(", ", _registry.Definitions.Select(d => d.Name))}."));
        }

        var arguments = call.Arguments ?? [];
        var errors = JsonSchemaValidator.Validate(spec.Definition.InputSchema, arguments);
        if (errors.Count > 0)
        {
            return WithRevision(ToolResult.Fail($"Invalid arguments for {spec.Definition.Name}. Fix them and call again.", errors));
        }

        var session = _backend.ResolveSession(call.SessionId, out var closed);
        if (closed)
        {
            CopilotLog.Info($"Tool {call.Name} ({call.CallId}) refused: chat session {call.SessionId} is closed.");
            return WithRevision(ToolResult.Fail("The chat tab that made this call was closed in the AI Copilot panel, so nothing was done. Stop here."));
        }

        // A panel tab's calls end with its turn: Stop in that tab cancels them (waiting in the queue or typing).
        var routed = call.SessionId is not null && session is not null;
        using var linked = routed
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session!.TurnToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        if (spec.Definition.Risk == ToolRisk.Destructive && !await ApproveDestructiveAsync(session, spec, arguments, token).ConfigureAwait(false))
        {
            return WithRevision(ToolResult.Fail(
                $"The user did not approve '{spec.Definition.Title}'. Do not retry it; ask the user what to do instead."));
        }

        var started = DateTime.UtcNow;
        var name = spec.Definition.Name;
        var context = new ToolContext(_dispatcher, _revisions, spec.Definition, call.CallId, (request, ct) => RequestApprovalAsync(session, request, ct));
        var sink = session?.Ui as IToolActivitySink;
        Notify(() => sink?.OnToolCallStarting(name, arguments));
        try
        {
            var target = routed ? await TargetDrawingAsync(session!, token).ConfigureAwait(false) : null;
            var result = WithRevision(await RunAsync(spec, context, arguments, routed ? session!.Id : null, target, token).ConfigureAwait(false));
            CopilotLog.Info($"Tool {name} ({call.CallId}{(routed ? ", " + session!.Id : string.Empty)}) {(result.Success ? "ok" : "failed")} in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms: {result.Message}");
            return Finished(sink, name, arguments, result);
        }
        catch (ToolException ex)
        {
            CopilotLog.Info($"Tool {name} ({call.CallId}) rejected: {ex.Message}");
            return Finished(sink, name, arguments, WithRevision(ToolResult.Fail(ex.Message)));
        }
        catch (AcadBusyException ex)
        {
            CopilotLog.Warn($"Tool {name} ({call.CallId}): {ex.Message}");
            Finished(sink, name, arguments, WithRevision(ToolResult.Fail(ex.Message)));
            throw new JsonRpcException(RpcErrorCodes.AcadBusy, ex.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CopilotLog.Info($"Tool {name} ({call.CallId}) cancelled.");
            Finished(sink, name, arguments, WithRevision(ToolResult.Fail("Cancelled.")));
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Stopped in the panel: the CLI is being stopped too; answer instead of leaving the call open.
            return WithRevision(ToolResult.Fail("Stopped by the user in the AI Copilot panel. Do not continue."));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            CopilotLog.Warn($"Tool {name} ({call.CallId}) AutoCAD error {ex.ErrorStatus}", ex);
            return Finished(sink, name, arguments, WithRevision(ToolResult.Fail($"AutoCAD error {ex.ErrorStatus} in {name}: {ex.Message}")));
        }
        catch (Exception ex)
        {
            CopilotLog.Error($"Tool {name} ({call.CallId}) crashed", ex);
            return Finished(sink, name, arguments, WithRevision(ToolResult.Fail($"Unexpected error in {name}: {ex.GetType().Name}: {ex.Message}")));
        }
    }

    /// <inheritdoc />
    public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
        AskUserAsync(request, null, cancellationToken);

    /// <inheritdoc />
    public async Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken)
    {
        var session = _backend.ResolveSession(sessionId, out var closed)
            ?? throw new UiUnavailableException(closed
                ? "The chat tab of this conversation was closed in the AI Copilot panel. Stop here."
                : "The AI Copilot panel is not available to show questions. Ask the user in your reply instead.");
        using var linked = sessionId is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.TurnToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answer = session.AskUserAsync(request, linked.Token);
        if (!answer.IsCompleted)
        {
            await RevealPanelAsync(session.Id, linked.Token).ConfigureAwait(false);
        }

        return await answer.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) =>
        UpdatePlanAsync(plan, null, cancellationToken);

    /// <inheritdoc />
    public Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken)
    {
        var session = _backend.ResolveSession(sessionId, out _);
        if (session?.Ui is null)
        {
            throw new UiUnavailableException("The AI Copilot panel is not open, so the task list cannot be shown. Continue without it.");
        }

        session.UpdatePlan(plan);
        return Task.CompletedTask;
    }

    // The chat's drawing; a chat created while no drawing was open binds to the active drawing on its first call.
    private async Task<DrawingRef?> TargetDrawingAsync(CopilotSession session, CancellationToken cancellationToken)
    {
        if (session.Drawing is { } drawing)
        {
            return drawing;
        }

        var active = await _dispatcher.InvokeAsync(DrawingDirectory.Active, cancellationToken).ConfigureAwait(false);
        return active is null ? null : session.BindDrawing(active);
    }

    private async Task<ToolResult> RunAsync(ToolSpec spec, ToolContext context, JsonObject arguments, string? owner, DrawingRef? target, CancellationToken cancellationToken)
    {
        var typing = spec.Definition.Risk != ToolRisk.Read;
        if (!typing)
        {
            if (target is null)
            {
                return await RunHandlerAsync(spec, context, arguments, null, cancellationToken).ConfigureAwait(false);
            }

            // Reading needs no turn while the chat's drawing is the active one (also during another chat's command in it).
            if (!target.IsClosed && await _dispatcher.InvokeAsync(() => DrawingDirectory.IsActive(target.Id), cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    return await RunHandlerAsync(spec, context, arguments, target.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (DrawingNotActiveException)
                {
                    // Another chat switched drawings in between: take a turn, which switches back.
                }
            }
        }

        var label = Label(spec, arguments);
        using var turn = await _backend.DrawingQueue.EnterAsync(owner, label, cancellationToken).ConfigureAwait(false);

        // From here the copilot types, switches drawings or changes the drawing: "Learn from me" must not record it.
        using var copilotWorking = AcadCopilot.Core.Learning.CopilotActivity.Shared.Begin();
        string? note = null;
        if (target is not null)
        {
            note = await _dispatcher.InvokeAsync(() => DrawingDirectory.ActivateAsync(_dispatcher, target, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        var result = await RunHandlerAsync(spec, context, arguments, target?.Id, cancellationToken).ConfigureAwait(false);
        return note is null ? result : result with { Message = string.IsNullOrWhiteSpace(result.Message) ? note : note + " " + result.Message };
    }

    private static async Task<ToolResult> RunHandlerAsync(ToolSpec spec, ToolContext context, JsonObject arguments, string? drawingId, CancellationToken cancellationToken)
    {
        // Flows into every main-thread item the handler posts: they refuse to act on another drawing.
        AcadDispatcher.ExpectedDrawingId = drawingId;
        try
        {
            return await spec.Handler(context, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AcadDispatcher.ExpectedDrawingId = null;
        }
    }

    private static string Label(ToolSpec spec, JsonObject arguments)
    {
        try
        {
            if (arguments["title"] is JsonValue value && value.TryGetValue<string>(out var title) && !string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }
        }
        catch (InvalidOperationException)
        {
            // Not a string.
        }

        return spec.Definition.Title;
    }

    private async Task<ApprovalDecision> RequestApprovalAsync(CopilotSession? session, ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            throw new UiUnavailableException("The AI Copilot panel is not available to approve this action.");
        }

        // Auto-approved requests ("Always", or the panel's auto-approve mode) complete at once: only a card that waits for
        // the user brings the palette up.
        var decision = session.RequestApprovalAsync(request, cancellationToken);
        if (!decision.IsCompleted)
        {
            await RevealPanelAsync(session.Id, cancellationToken).ConfigureAwait(false);
        }

        return await decision.ConfigureAwait(false);
    }

    private async Task RevealPanelAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (_revealPanel is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RevealTimeout);
        try
        {
            await _revealPanel(sessionId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AcadBusyException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The card is still added to the (hidden) panel; the user sees it when they open the panel.
            CopilotLog.Info("Could not show the AI Copilot panel for a question or approval: " + ex.Message);
        }
    }

    private async Task<bool> ApproveDestructiveAsync(CopilotSession? session, ToolSpec spec, JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new ApprovalRequest
        {
            ToolName = spec.Definition.Name,
            ToolTitle = spec.Definition.Title,
            Summary = $"{spec.Definition.Title}: the AI wants to run '{spec.Definition.Name}', which can delete or overwrite work.",
            Risk = spec.Definition.Risk,
            ArgumentsJson = arguments.ToJsonString(),
        };
        try
        {
            var decision = await RequestApprovalAsync(session, request, cancellationToken).ConfigureAwait(false);
            return decision is ApprovalDecision.Approve or ApprovalDecision.AlwaysApprove;
        }
        catch (UiUnavailableException ex)
        {
            CopilotLog.Info($"Destructive tool {spec.Definition.Name} refused: {ex.Message}");
            return false;
        }
    }

    private ToolResult WithRevision(ToolResult result) => result with { Revision = _revisions.Current };

    // Tells the panel (when it is attached) what ran, so it can show each typed command and the capture_view picture.
    private static ToolResult Finished(IToolActivitySink? sink, string name, JsonObject arguments, ToolResult result)
    {
        Notify(() => sink?.OnToolCallFinished(name, arguments, result));
        return result;
    }

    private static void Notify(Action report)
    {
        try
        {
            report();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("The panel could not show a tool call", ex);
        }
    }
}
