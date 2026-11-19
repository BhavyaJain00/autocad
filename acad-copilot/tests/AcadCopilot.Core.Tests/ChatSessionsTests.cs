using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Ipc;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests;

/// <summary>The AutoCAD turn queue: first come first served, one at a time, Stop leaves it.</summary>
public sealed class AcadTurnQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Actions_run_one_at_a_time_in_arrival_order()
    {
        var queue = new AcadTurnQueue();
        var changes = 0;
        queue.Changed += (_, _) => Interlocked.Increment(ref changes);

        var first = await queue.EnterAsync("tab-a", "Outer walls", CancellationToken.None);
        var second = queue.EnterAsync("tab-b", "First floor walls", CancellationToken.None);
        var third = queue.EnterAsync("tab-c", "Site boundary", CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        Assert.Equal(new AcadTurnStatus(AcadTurnState.Drawing, 1, "Outer walls"), queue.StatusOf("tab-a"));
        Assert.Equal(new AcadTurnStatus(AcadTurnState.Waiting, 2, "First floor walls"), queue.StatusOf("tab-b"));
        Assert.Equal("Waiting for AutoCAD (3rd in line)", queue.StatusOf("tab-c").Text);
        Assert.Equal(AcadTurnStatus.Idle, queue.StatusOf("tab-x"));
        Assert.Equal(["tab-a", "tab-b", "tab-c"], queue.Snapshot().Select(e => e.Owner));
        Assert.True(queue.Snapshot()[0].IsRunning);

        first.Dispose();
        var secondTurn = await second.WaitAsync(Timeout);
        Assert.False(third.IsCompleted);
        Assert.Equal(AcadTurnState.Drawing, queue.StatusOf("tab-b").State);
        Assert.Equal(2, queue.StatusOf("tab-c").Position);

        first.Dispose(); // A second release of the same turn changes nothing.
        Assert.False(third.IsCompleted);
        secondTurn.Dispose();
        (await third.WaitAsync(Timeout)).Dispose();
        Assert.Equal(0, queue.Count);
        Assert.True(changes >= 6);
    }

    [Fact]
    public async Task Stop_removes_a_waiting_action_and_the_rest_move_up()
    {
        var queue = new AcadTurnQueue();
        var running = await queue.EnterAsync("tab-a", "Walls", CancellationToken.None);
        using var stopB = new CancellationTokenSource();
        var b = queue.EnterAsync("tab-b", "Doors", stopB.Token);
        var c = queue.EnterAsync("tab-c", "Text", CancellationToken.None);
        Assert.Equal(3, queue.StatusOf("tab-c").Position);

        await stopB.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(Timeout));
        Assert.Equal(AcadTurnStatus.Idle, queue.StatusOf("tab-b"));
        Assert.Equal(2, queue.StatusOf("tab-c").Position);

        running.Dispose();
        (await c.WaitAsync(Timeout)).Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnterAsync("tab-d", "x", new CancellationToken(canceled: true)));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Many_parallel_callers_never_overlap()
    {
        var queue = new AcadTurnQueue();
        var inside = 0;
        var maxInside = 0;
        var order = new ConcurrentQueue<int>();
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            using var turn = await queue.EnterAsync("tab-" + (i % 4), "step " + i, CancellationToken.None);
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref maxInside, now);
            order.Enqueue(i);
            await Task.Delay(2);
            Interlocked.Decrement(ref inside);
        }));
        await Task.WhenAll(tasks).WaitAsync(Timeout);
        Assert.Equal(1, maxInside);
        Assert.Equal(20, order.Count);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(22, "22nd")]
    public void Ordinals(int number, string expected) => Assert.Equal(expected, AcadTurnStatus.Ordinal(number));

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>Chat sessions of the backend: parallel turns, the running limit, per-tab state and routing.</summary>
public sealed class ChatSessionsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TestDirectory _dir = new();
    private DrawingRef? _drawing = new("dwg-1", "GF.dwg");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Sessions_run_in_parallel_each_with_its_own_cli_session_and_bridge_session_id()
    {
        var runner = new HoldingRunner(ProviderId.Claude);
        var backend = CreateBackend([runner]);
        var a = (CopilotSession)backend.CreateSession();
        _drawing = new DrawingRef("dwg-2", "FF.dwg");
        var b = (CopilotSession)backend.CreateSession();
        Assert.Equal("GF.dwg", a.Drawing!.Title);
        Assert.Equal("FF.dwg", b.Acad.DocumentTitle);
        Assert.Matches("^tab-[0-9a-f]{8}$", a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Same(a, backend.ActiveSession);

        using var stopA = new CancellationTokenSource();
        var turnA = Collect(a.SendAsync("ground floor", [], stopA.Token));
        var turnB = Collect(b.SendAsync("first floor", [], CancellationToken.None));
        await runner.Started(2).WaitAsync(Timeout);
        Assert.True(a.IsBusy && b.IsBusy);
        Assert.Equal(2, backend.RunningSessionCount);
        Assert.False(a.TurnToken.IsCancellationRequested);

        var requestA = runner.Requests.Single(r => r.Prompt == "ground floor");
        var requestB = runner.Requests.Single(r => r.Prompt == "first floor");
        Assert.Equal(["--pid", "1", "--panel", "--session", a.Id], requestA.AcadMcp.Arguments);
        Assert.Equal(["--pid", "1", "--panel", "--session", b.Id], requestB.AcadMcp.Arguments);

        // Stop in A cancels A only (its tool calls are linked to its TurnToken).
        var token = a.TurnToken;
        await stopA.CancelAsync();
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(AgentFailureKind.Cancelled, Assert.IsType<TurnFailed>((await turnA.WaitAsync(Timeout))[^1]).Kind);
        Assert.True(b.IsBusy);
        Assert.True(a.TurnToken.IsCancellationRequested);
        runner.Release("first floor");
        Assert.IsType<TurnCompleted>((await turnB.WaitAsync(Timeout))[^1]);
        Assert.Equal(0, backend.RunningSessionCount);
        Assert.Equal("cli-first floor", b.GetSessionId(ProviderId.Claude));
        Assert.Equal("cli-ground floor", a.GetSessionId(ProviderId.Claude));

        runner.Hold = false;
        await Collect(b.SendAsync("stairs", [], CancellationToken.None));
        Assert.Equal("cli-first floor", runner.Requests.Single(r => r.Prompt == "stairs").ResumeSessionId);
    }

    [Fact]
    public async Task Running_limit_refuses_another_chat_until_one_finishes()
    {
        var runner = new HoldingRunner(ProviderId.Claude);
        var backend = CreateBackend([runner], maxRunning: 2);
        var sessions = Enumerable.Range(0, 3).Select(_ => (CopilotSession)backend.CreateSession()).ToList();
        var first = Collect(sessions[0].SendAsync("one", [], CancellationToken.None));
        var second = Collect(sessions[1].SendAsync("two", [], CancellationToken.None));
        await runner.Started(2).WaitAsync(Timeout);
        Assert.False(sessions[2].CanStartTurn);
        Assert.True(sessions[0].CanStartTurn);

        var refused = await Collect(sessions[2].SendAsync("three", [], CancellationToken.None));
        Assert.StartsWith("2 chats are already working", Assert.IsType<TurnFailed>(Assert.Single(refused)).Message, StringComparison.Ordinal);
        Assert.False(sessions[2].IsBusy);
        Assert.Equal(2, runner.Requests.Count);

        runner.Release("one");
        await first.WaitAsync(Timeout);
        Assert.True(sessions[2].CanStartTurn);
        runner.Hold = false;
        Assert.IsType<TurnCompleted>((await Collect(sessions[2].SendAsync("three", [], CancellationToken.None)))[^1]);
        runner.Release("two");
        await second.WaitAsync(Timeout);
        Assert.Equal(0, backend.RunningSessionCount);
    }

    [Fact]
    public async Task Provider_model_ui_and_always_approvals_belong_to_one_session()
    {
        var backend = CreateBackend([new HoldingRunner(ProviderId.Claude) { Hold = false }, new HoldingRunner(ProviderId.Codex) { Hold = false }]);
        var a = (CopilotSession)backend.CreateSession();
        var b = (CopilotSession)backend.CreateSession();
        b.SelectedProvider = ProviderId.Codex;
        b.SetModel(ProviderId.Codex, "gpt-5");
        Assert.Equal(ProviderId.Claude, a.SelectedProvider);
        Assert.Null(a.GetModel(ProviderId.Codex));

        // A new tab starts with the choice made last.
        var c = backend.CreateSession();
        Assert.Equal(ProviderId.Codex, c.SelectedProvider);
        Assert.Equal("gpt-5", c.GetModel(ProviderId.Codex));

        var uiA = new RecordingUi { Decision = ApprovalDecision.AlwaysApprove };
        var uiB = new RecordingUi { Decision = ApprovalDecision.Reject };
        a.AttachUi(uiA);
        b.AttachUi(uiB);
        var request = new ApprovalRequest { ToolName = "run_commands:Allow QSAVE", ToolTitle = "Allow QSAVE", Summary = "Save", Risk = ToolRisk.Write };
        Assert.Equal(ApprovalDecision.AlwaysApprove, await a.RequestApprovalAsync(request, CancellationToken.None));
        Assert.Equal(ApprovalDecision.Approve, await a.RequestApprovalAsync(request, CancellationToken.None));
        Assert.Equal(ApprovalDecision.Reject, await b.RequestApprovalAsync(request, CancellationToken.None));
        Assert.Equal(1, uiA.Approvals);
        Assert.Equal(1, uiB.Approvals);

        a.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Walls" }] });
        Assert.Single(uiA.Plans);
        Assert.Empty(uiB.Plans);

        // Closing a tab: its calls are refused from then on; the active tab moves on.
        backend.ActiveSession = b;
        backend.CloseSession(b);
        Assert.True(b.IsClosed);
        Assert.Null(backend.ResolveSession(b.Id, out var closed));
        Assert.True(closed);
        Assert.Same(a, backend.ResolveSession(null, out closed));
        Assert.False(closed);
        Assert.Contains("closed", Assert.IsType<TurnFailed>(Assert.Single(await Collect(b.SendAsync("x", [], CancellationToken.None)))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawings_bind_rename_and_close()
    {
        var backend = CreateBackend([new HoldingRunner(ProviderId.Claude)]);
        _drawing = null;
        var unbound = (CopilotSession)backend.CreateSession();
        Assert.Null(unbound.Drawing);
        Assert.Equal("House.dwg", unbound.Acad.DocumentTitle);
        Assert.Equal("Site.dwg", unbound.BindDrawing(new DrawingRef("dwg-5", "Site.dwg")).Title);
        Assert.Equal("Site.dwg", unbound.BindDrawing(new DrawingRef("dwg-6", "Other.dwg")).Title);

        var changes = 0;
        unbound.StateChanged += (_, _) => changes++;
        backend.UpdateDrawings([new DrawingRef("dwg-5", "Site-v2.dwg"), new DrawingRef("dwg-9", "Unrelated.dwg")]);
        Assert.Equal("Site-v2.dwg", unbound.Drawing!.Title);
        backend.MarkDrawingClosed("dwg-5");
        Assert.True(unbound.Drawing!.IsClosed);
        Assert.Equal("Site-v2.dwg (closed)", unbound.Acad.DocumentTitle);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Queue_changes_show_up_as_each_sessions_turn_status()
    {
        var backend = CreateBackend([new HoldingRunner(ProviderId.Claude)]);
        var a = (CopilotSession)backend.CreateSession();
        var b = (CopilotSession)backend.CreateSession();
        var bChanges = 0;
        b.StateChanged += (_, _) => Interlocked.Increment(ref bChanges);

        var turnA = await backend.DrawingQueue.EnterAsync(a.Id, "Walls", CancellationToken.None);
        var waitB = backend.DrawingQueue.EnterAsync(b.Id, "Doors", CancellationToken.None);
        Assert.Equal(AcadTurnState.Drawing, a.AcadTurn.State);
        Assert.Equal(new AcadTurnStatus(AcadTurnState.Waiting, 2, "Doors"), b.AcadTurn);
        turnA.Dispose();
        (await waitB.WaitAsync(Timeout)).Dispose();
        Assert.Equal(AcadTurnStatus.Idle, a.AcadTurn);
        Assert.Equal(AcadTurnStatus.Idle, b.AcadTurn);
        Assert.True(bChanges >= 3);
    }

    /// <summary>
    /// Two bridges (one per tab) over the real pipe: every request carries its tab, questions and plans reach that tab's
    /// UI, and typing calls take turns in the queue, Stop of a waiting tab removing it at once. The handler mirrors the
    /// plug-in's routing (PluginPipeHandler needs AutoCAD and is compile-verified only).
    /// </summary>
    [Fact]
    public async Task Bridges_of_two_tabs_are_routed_over_the_pipe_and_take_turns()
    {
        var backend = CreateBackend([new HoldingRunner(ProviderId.Claude)]);
        var a = (CopilotSession)backend.CreateSession();
        var b = (CopilotSession)backend.CreateSession();
        var uiA = new RecordingUi();
        var uiB = new RecordingUi();
        a.AttachUi(uiA);
        b.AttachUi(uiB);
        var handler = new RoutingHandler(backend);
        var pipe = "AcadCopilotTest-" + Guid.NewGuid().ToString("N");
        var token = PipeNames.NewToken();
        await using var server = new PipeServer(pipe, token, handler);
        server.Start();
        var instance = new AcadInstanceInfo { Pid = Environment.ProcessId, Pipe = PipeNames.FullPath(pipe), Token = token };

        await using var bridgeA = await PipeClient.ConnectAsync(instance, "test", TimeSpan.FromSeconds(5), a.Id, CancellationToken.None);
        await using var bridgeB = await PipeClient.ConnectAsync(instance, "test", TimeSpan.FromSeconds(5), b.Id, CancellationToken.None);
        await using var outside = await PipeClient.ConnectAsync(instance, "test", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(a.Id, bridgeA.SessionId);

        var question = new QuestionRequest { Questions = [new UserQuestion { Question = "Roof?", Options = [new() { Label = "Flat" }, new() { Label = "Sloped" }] }] };
        Assert.Equal("Flat", (await bridgeB.AskUserAsync(question, CancellationToken.None)).Answers[0].Selected[0]);
        await bridgeA.UpdatePlanAsync(new PlanUpdate { Tasks = [new PlanTask { Title = "Walls" }] }, CancellationToken.None);
        Assert.Single(uiB.Questions);
        Assert.Empty(uiA.Questions);
        Assert.Single(uiA.Plans);
        Assert.Empty(uiB.Plans);

        // A client outside the panel (no session) reaches the active tab.
        backend.ActiveSession = b;
        await outside.UpdatePlanAsync(new PlanUpdate { Tasks = [new PlanTask { Title = "Doors" }] }, CancellationToken.None);
        Assert.Single(uiB.Plans);

        // A's typing call holds AutoCAD; B's waits 2nd in line; Stop in B (its turn token) removes it from the queue.
        using var turnB = new CancellationTokenSource();
        handler.TurnTokens[b.Id] = turnB.Token;
        var callA = bridgeA.CallToolAsync("run_commands", new JsonObject { ["title"] = "Outer walls" }, "c1", CancellationToken.None);
        await handler.Entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        var callB = bridgeB.CallToolAsync("run_commands", new JsonObject { ["title"] = "First floor" }, "c2", CancellationToken.None);
        await WaitUntil(() => b.AcadTurn.IsWaiting);
        Assert.Equal("Waiting for AutoCAD (2nd in line)", b.AcadTurn.Text);
        Assert.Equal(AcadTurnState.Drawing, a.AcadTurn.State);
        var readB = await bridgeB.CallToolAsync("get_drawing_info", null, "c3", CancellationToken.None).WaitAsync(Timeout);
        Assert.True(readB.Success);

        await turnB.CancelAsync();
        var stopped = await callB.WaitAsync(Timeout);
        Assert.False(stopped.Success);
        Assert.Contains("Stopped", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(1, backend.DrawingQueue.Count);

        handler.Release();
        Assert.True((await callA.WaitAsync(Timeout)).Success);
        Assert.Equal(0, backend.DrawingQueue.Count);
        Assert.Equal([a.Id, b.Id, b.Id], handler.Calls.Select(c => c.SessionId));
        Assert.Equal(["run_commands", "run_commands", "get_drawing_info"], handler.Calls.Select(c => c.Name));

        // An invalid session id is refused at sign-in.
        var error = await Assert.ThrowsAsync<ArgumentException>(() => PipeClient.ConnectAsync(instance, "test", TimeSpan.FromSeconds(5), "bad id!", CancellationToken.None));
        Assert.Contains("session", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_ids_are_validated()
    {
        Assert.True(RpcSessions.IsValid("tab-1a2b3c4d"));
        Assert.True(RpcSessions.IsValid("A_b-9"));
        Assert.False(RpcSessions.IsValid(""));
        Assert.False(RpcSessions.IsValid(null));
        Assert.False(RpcSessions.IsValid("has space"));
        Assert.False(RpcSessions.IsValid(new string('a', 65)));
        Assert.Equal("tab-1", RpcSessions.Read(new JsonObject { ["sessionId"] = "tab-1", ["questions"] = new JsonArray() }));
        Assert.Null(RpcSessions.Read(new JsonObject { ["questions"] = new JsonArray() }));
        Assert.Equal(RpcErrorCodes.InvalidParams, Assert.Throws<JsonRpcException>(() => RpcSessions.Read(new JsonObject { ["sessionId"] = "x y" })).Code);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10);
        }
    }

    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }

    private CopilotBackend CreateBackend(IAgentRunner[] runners, int maxRunning = CopilotBackend.DefaultMaxRunningSessions) =>
        new(
            runners,
            () => new McpServerLaunch { Command = @"C:\bridge\AcadCopilotMcp.exe", Arguments = ["--pid", "1", "--panel"] },
            new AcadSessionInfo(1, "2027", "House.dwg"),
            new CopilotSettingsStore(_dir.File(Guid.NewGuid().ToString("N") + ".json")),
            _dir.File("workspace"),
            () => _drawing,
            maxRunning);

    /// <summary>Mirrors the plug-in's routing: session per request, typing tools take turns, Stop links in.</summary>
    private sealed class RoutingHandler(CopilotBackend backend) : IPipeHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<ToolsCallParams> Calls { get; } = new();

        public ConcurrentDictionary<string, CancellationToken> TurnTokens { get; } = new();

        public System.Threading.Channels.Channel<string> Entered { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public void Release() => _release.TrySetResult();

        public AuthResult GetServerInfo() => new() { ServerName = "Routing", ServerVersion = "1" };

        public Task<ToolsListResult> ListToolsAsync(CancellationToken cancellationToken) => Task.FromResult(new ToolsListResult());

        public async Task<ToolResult> CallToolAsync(ToolsCallParams call, CancellationToken cancellationToken)
        {
            Calls.Enqueue(call);
            var session = backend.ResolveSession(call.SessionId, out var closed);
            if (closed || session is null)
            {
                return ToolResult.Fail("closed");
            }

            if (call.Name == "get_drawing_info")
            {
                return ToolResult.Ok("Read without waiting.");
            }

            var turn = TurnTokens.TryGetValue(session.Id, out var t) ? t : CancellationToken.None;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn);
            try
            {
                using var lease = await backend.DrawingQueue.EnterAsync(session.Id, call.Arguments?["title"]?.GetValue<string>() ?? call.Name, linked.Token);
                await Entered.Writer.WriteAsync(session.Id, linked.Token);
                await _release.Task.WaitAsync(linked.Token);
                return ToolResult.Ok("Typed.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ToolResult.Fail("Stopped by the user in the AI Copilot panel.");
            }
        }

        public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
            AskUserAsync(request, null, cancellationToken);

        public Task<QuestionResponse> AskUserAsync(QuestionRequest request, string? sessionId, CancellationToken cancellationToken) =>
            (backend.ResolveSession(sessionId, out _) ?? throw new UiUnavailableException("none")).AskUserAsync(request, cancellationToken);

        public Task UpdatePlanAsync(PlanUpdate plan, CancellationToken cancellationToken) => UpdatePlanAsync(plan, null, cancellationToken);

        public Task UpdatePlanAsync(PlanUpdate plan, string? sessionId, CancellationToken cancellationToken)
        {
            (backend.ResolveSession(sessionId, out _) ?? throw new UiUnavailableException("none")).UpdatePlan(plan);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUi : ICopilotUi
    {
        public ApprovalDecision Decision { get; set; } = ApprovalDecision.Approve;

        public int Approvals { get; private set; }

        public ConcurrentQueue<QuestionRequest> Questions { get; } = new();

        public ConcurrentQueue<PlanUpdate> Plans { get; } = new();

        public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken)
        {
            Questions.Enqueue(request);
            return Task.FromResult(new QuestionResponse
            {
                Answers = [.. request.Questions.Select(q => new QuestionAnswer { Question = q.Question, Selected = [q.Options[0].Label] })],
            });
        }

        public void UpdatePlan(PlanUpdate plan) => Plans.Enqueue(plan);

        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Approvals++;
            return Task.FromResult(Decision);
        }
    }

    /// <summary>A CLI stand-in whose turns run until released (or stopped) when <see cref="Hold"/> is set.</summary>
    private sealed class HoldingRunner(ProviderId id) : IAgentRunner
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private int _startedCount;

        public ConcurrentQueue<TurnRequest> Requests { get; } = new();

        public bool Hold { get; set; } = true;

        public ProviderId ProviderId => id;

        public string DisplayName => id.ToString();

        public IReadOnlyList<ModelChoice> Models { get; } = [new(null, "Default")];

        public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus { Provider = id, Installed = true, SignIn = SignInState.SignedIn });

        public CommandLine GetSignInCommand() => new("fake", ["login"]);

        public void Release(string prompt) => Gate(prompt).TrySetResult();

        /// <summary>Completes once <paramref name="count"/> turns have started.</summary>
        public async Task Started(int count)
        {
            while (Volatile.Read(ref _startedCount) < count)
            {
                await Task.Delay(5);
            }
        }

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var hold = Hold;
            await Task.Yield();
            yield return new SessionStarted(request.ResumeSessionId ?? "cli-" + request.Prompt);
            Interlocked.Increment(ref _startedCount);
            if (hold)
            {
                var stopped = false;
                try
                {
                    await Gate(request.Prompt).Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                }

                if (stopped)
                {
                    yield return new TurnFailed("Stopped.", AgentFailureKind.Cancelled);
                    yield break;
                }
            }

            yield return new TextDone("ok");
            yield return new TurnCompleted("end_turn");
        }

        private TaskCompletionSource Gate(string prompt) =>
            _gates.GetOrAdd(prompt, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
