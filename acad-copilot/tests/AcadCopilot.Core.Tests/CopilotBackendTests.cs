using System.Runtime.CompilerServices;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests;

public sealed class CopilotBackendTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private CopilotBackend CreateBackend(params IAgentRunner[] runners) =>
        new(
            runners,
            () => new McpServerLaunch { Command = @"C:\bridge\AcadCopilotMcp.exe", Arguments = ["--pid", "1", "--panel"] },
            new AcadSessionInfo(1, "2027", "House.dwg"),
            new CopilotSettingsStore(_dir.File("settings.json")),
            _dir.File("workspace"));

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var e in events)
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public void Provider_and_model_changes_keep_the_panels_watch_setting()
    {
        var backend = CreateBackend(
            new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("x"))),
            new FakeRunner(ProviderId.Codex, (_, _) => Script(new TurnCompleted("x"))));
        var store = new CopilotSettingsStore(_dir.File("settings.json"));
        store.Update(s => s with { DraftingWatch = false });

        backend.SelectedProvider = ProviderId.Codex;
        backend.SetModel(ProviderId.Codex, "gpt-5");

        var saved = store.Load();
        Assert.False(saved.DraftingWatch);
        Assert.Equal(ProviderId.Codex, saved.Provider);
        Assert.Equal("gpt-5", saved.Models[CopilotSettings.Key(ProviderId.Codex)]);
    }

    [Fact]
    public async Task Session_is_resumed_until_new_chat()
    {
        var runner = new FakeRunner(ProviderId.Claude, (_, _) => Script(new SessionStarted("s-1"), new TextDone("Hi"), new TurnCompleted("end_turn")));
        var backend = CreateBackend(runner);
        var resets = 0;
        backend.ChatReset += (_, _) => resets++;

        var first = await CollectAsync(backend.SendAsync("hello", [], CancellationToken.None));
        Assert.IsType<TurnCompleted>(first[^1]);
        Assert.Null(runner.Requests[0].ResumeSessionId);
        Assert.Equal("s-1", backend.GetSessionId(ProviderId.Claude));

        await CollectAsync(backend.SendAsync("again", ["C:\\plan.pdf"], CancellationToken.None));
        Assert.Equal("s-1", runner.Requests[1].ResumeSessionId);
        Assert.Equal(["C:\\plan.pdf"], runner.Requests[1].Attachments);
        Assert.Equal(@"C:\bridge\AcadCopilotMcp.exe", runner.Requests[1].AcadMcp.Command);
        Assert.True(Directory.Exists(runner.Requests[1].WorkingDirectory));

        backend.NewChat();
        Assert.Equal(1, resets);
        await CollectAsync(backend.SendAsync("fresh", [], CancellationToken.None));
        Assert.Null(runner.Requests[2].ResumeSessionId);
    }

    [Fact]
    public async Task Every_turn_gets_the_instructions_for_the_product_and_what_was_learned_so_far()
    {
        var runner = new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("end_turn")));
        var backend = CreateBackend(runner);

        // The knowledge folder sits next to the test's workspace, never in the user's real profile.
        Assert.Equal(_dir.File("knowledge"), backend.Knowledge.Directory);
        await CollectAsync(backend.SendAsync("hello", [], CancellationToken.None));
        var first = runner.Requests[0].Instructions;
        Assert.StartsWith(AcadCopilot.Core.Agents.CopilotInstructions.Default, first, StringComparison.Ordinal);
        Assert.Contains("Nothing learned yet", first, StringComparison.Ordinal);
        Assert.DoesNotContain("WALLADD", first, StringComparison.Ordinal);

        // A rule saved during a turn (by the bridge) applies from the next turn on.
        backend.Knowledge.RememberRule("Toilets at least 5'x7' clear", "planning");
        backend.UpdateAcad(new AcadSessionInfo(1, "2027", "House.dwg") { Product = "AutoCAD Architecture 2027" });
        await CollectAsync(backend.SendAsync("next", [], CancellationToken.None));
        var second = runner.Requests[1].Instructions;
        Assert.Contains("Toilets at least 5'x7' clear", second, StringComparison.Ordinal);
        Assert.Contains(AcadCopilot.Core.Agents.CopilotInstructions.ArchitectureNote, second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ui_tool_calls_are_hidden_but_acad_tools_are_shown()
    {
        var runner = new FakeRunner(ProviderId.Claude, (_, _) => Script(
            new ToolCallStarted("1", "ask_user", "question", "acad"),
            new ToolCallFinished("1", true, "answered"),
            new ToolCallStarted("2", "create_wall", "4 walls", "acad"),
            new ToolCallFinished("2", true, "Created 4 walls"),
            new ToolCallStarted("3", "update_plan", "plan", "acad"),
            new ToolCallFinished("3", true, "ok"),
            new TurnCompleted("end_turn")));
        var events = await CollectAsync(CreateBackend(runner).SendAsync("build", [], CancellationToken.None));
        Assert.Equal(3, events.Count);
        Assert.Equal("create_wall", Assert.IsType<ToolCallStarted>(events[0]).Name);
        Assert.Equal("2", Assert.IsType<ToolCallFinished>(events[1]).CallId);
    }

    [Fact]
    public async Task Runner_exception_becomes_turn_failed()
    {
        var runner = new FakeRunner(ProviderId.Codex, (_, _) => Throwing());
        var events = await CollectAsync(CreateBackend(runner).SendAsync("x", [], CancellationToken.None));
        Assert.IsType<TextDelta>(events[0]);
        var failed = Assert.IsType<TurnFailed>(events[^1]);
        Assert.Equal(AgentFailureKind.Other, failed.Kind);
        Assert.Contains("boom", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_terminal_event_is_added_and_duplicates_dropped()
    {
        var silent = new FakeRunner(ProviderId.Claude, (_, _) => Script(new TextDelta("partial")));
        var events = await CollectAsync(CreateBackend(silent).SendAsync("x", [], CancellationToken.None));
        Assert.IsType<TurnFailed>(events[^1]);

        var twice = new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("a"), new TurnCompleted("b")));
        var events2 = await CollectAsync(CreateBackend(twice).SendAsync("x", [], CancellationToken.None));
        Assert.Equal("a", Assert.IsType<TurnCompleted>(Assert.Single(events2)).StopReason);
    }

    [Fact]
    public async Task Stop_yields_cancelled_and_busy_guard_rejects_overlap()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(ProviderId.Claude, (_, ct) => Hanging(started, ct));
        var backend = CreateBackend(runner);
        using var cts = new CancellationTokenSource();
        var running = CollectAsync(backend.SendAsync("long", [], cts.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(backend.IsBusy);

        var overlap = await CollectAsync(backend.SendAsync("second", [], CancellationToken.None));
        Assert.Contains("already running", Assert.IsType<TurnFailed>(Assert.Single(overlap)).Message, StringComparison.Ordinal);

        await cts.CancelAsync();
        var events = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentFailureKind.Cancelled, Assert.IsType<TurnFailed>(events[^1]).Kind);
        Assert.False(backend.IsBusy);
    }

    [Fact]
    public async Task Bridge_setup_failure_is_reported_as_turn_failed()
    {
        var runner = new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("x")));
        var backend = new CopilotBackend(
            [runner],
            () => throw new FileNotFoundException("bridge missing"),
            new AcadSessionInfo(1, "2027", null),
            new CopilotSettingsStore(_dir.File("settings.json")),
            _dir.File("workspace"));
        var events = await CollectAsync(backend.SendAsync("x", [], CancellationToken.None));
        Assert.Contains("bridge missing", Assert.IsType<TurnFailed>(Assert.Single(events)).Message, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Provider_and_model_choices_persist()
    {
        var claude = new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("x")));
        var gemini = new FakeRunner(ProviderId.Gemini, (_, _) => Script(new TurnCompleted("x")));
        var backend = CreateBackend(claude, gemini);
        var changes = 0;
        backend.StateChanged += (_, _) => changes++;
        Assert.Equal(ProviderId.Claude, backend.SelectedProvider);

        backend.SelectedProvider = ProviderId.Gemini;
        backend.SetModel(ProviderId.Gemini, "gemini-pro");
        Assert.True(changes >= 2);
        await CollectAsync(backend.SendAsync("x", [], CancellationToken.None));
        Assert.Equal("gemini-pro", Assert.Single(gemini.Requests).Model);
        Assert.Empty(claude.Requests);

        var reloaded = CreateBackend(claude, gemini);
        Assert.Equal(ProviderId.Gemini, reloaded.SelectedProvider);
        Assert.Equal("gemini-pro", reloaded.GetModel(ProviderId.Gemini));
        Assert.Null(reloaded.GetModel(ProviderId.Claude));
        Assert.Throws<ArgumentOutOfRangeException>(() => reloaded.SelectedProvider = ProviderId.Codex);
    }

    [Fact]
    public async Task Corrupt_settings_fall_back_to_defaults()
    {
        File.WriteAllText(_dir.File("settings.json"), "{ broken");
        var backend = CreateBackend(new FakeRunner(ProviderId.Codex, (_, _) => Script(new TurnCompleted("x"))));
        Assert.Equal(ProviderId.Codex, backend.SelectedProvider);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Approvals_need_a_ui_and_remember_always()
    {
        var backend = CreateBackend(new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("x"))));
        var request = new ApprovalRequest { ToolName = "delete_elements", ToolTitle = "Delete elements", Summary = "Delete 2 elements", Risk = ToolRisk.Destructive };
        await Assert.ThrowsAsync<UiUnavailableException>(() => backend.RequestApprovalAsync(request, CancellationToken.None));

        var ui = new FakeUi { Decision = ApprovalDecision.AlwaysApprove };
        backend.AttachUi(ui);
        Assert.Equal(ApprovalDecision.AlwaysApprove, await backend.RequestApprovalAsync(request, CancellationToken.None));
        Assert.Equal(ApprovalDecision.Approve, await backend.RequestApprovalAsync(request, CancellationToken.None));
        Assert.Equal(1, ui.ApprovalCount);

        backend.NewChat();
        ui.Decision = ApprovalDecision.Reject;
        Assert.Equal(ApprovalDecision.Reject, await backend.RequestApprovalAsync(request, CancellationToken.None));
        Assert.Equal(2, ui.ApprovalCount);
    }

    [Fact]
    public async Task Questions_and_plans_are_forwarded_to_the_ui()
    {
        var backend = CreateBackend(new FakeRunner(ProviderId.Claude, (_, _) => Script(new TurnCompleted("x"))));
        var question = new QuestionRequest { Questions = [new UserQuestion { Question = "Size?", Options = [new() { Label = "40x60" }, new() { Label = "30x50" }] }] };
        await Assert.ThrowsAsync<UiUnavailableException>(() => backend.AskUserAsync(question, CancellationToken.None));
        backend.UpdatePlan(new PlanUpdate());

        var ui = new FakeUi();
        backend.AttachUi(ui);
        var answer = await backend.AskUserAsync(question, CancellationToken.None);
        Assert.Equal("40x60", answer.Answers[0].Selected[0]);
        backend.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Walls" }] });
        Assert.Equal("Walls", Assert.Single(ui.Plans).Tasks[0].Title);
        Assert.Equal("House.dwg", backend.Acad.DocumentTitle);
        backend.UpdateAcad(backend.Acad with { DocumentTitle = "Other.dwg" });
        Assert.Equal("Other.dwg", backend.Acad.DocumentTitle);
    }

    [Fact]
    public async Task Contract_stub_runners_report_honestly()
    {
        IAgentRunner[] runners = [new ClaudeCodeRunner(), new CodexRunner(), new GeminiRunner()];
        Assert.Equal([ProviderId.Claude, ProviderId.Codex, ProviderId.Gemini], runners.Select(r => r.ProviderId));
        foreach (var runner in runners)
        {
            Assert.False(string.IsNullOrWhiteSpace(runner.DisplayName));
            Assert.NotEmpty(runner.Models);
            Assert.False(string.IsNullOrWhiteSpace(runner.GetSignInCommand().FileName));
            var status = await runner.GetStatusAsync();
            Assert.Equal(runner.ProviderId, status.Provider);
        }
    }

    private static async IAsyncEnumerable<AgentEvent> Script(params AgentEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static async IAsyncEnumerable<AgentEvent> Throwing()
    {
        await Task.Yield();
        yield return new TextDelta("start");
        throw new InvalidOperationException("boom");
    }

    private static async IAsyncEnumerable<AgentEvent> Hanging(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TextDelta("thinking");
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        yield return new TurnCompleted("never");
    }

    private sealed class FakeRunner(ProviderId id, Func<TurnRequest, CancellationToken, IAsyncEnumerable<AgentEvent>> script) : IAgentRunner
    {
        public List<TurnRequest> Requests { get; } = [];

        public ProviderId ProviderId => id;

        public string DisplayName => id.ToString();

        public IReadOnlyList<ModelChoice> Models { get; } = [new(null, "Default")];

        public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus { Provider = id, Installed = true, SignIn = SignInState.SignedIn });

        public CommandLine GetSignInCommand() => new("fake", ["login"]);

        public IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return script(request, cancellationToken);
        }
    }

    private sealed class FakeUi : ICopilotUi
    {
        public ApprovalDecision Decision { get; set; } = ApprovalDecision.Approve;

        public int ApprovalCount { get; private set; }

        public List<PlanUpdate> Plans { get; } = [];

        public Task<QuestionResponse> AskUserAsync(QuestionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestionResponse
            {
                Answers = [.. request.Questions.Select(q => new QuestionAnswer { Question = q.Question, Selected = [q.Options[0].Label] })],
            });

        public void UpdatePlan(PlanUpdate plan) => Plans.Add(plan);

        public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            ApprovalCount++;
            return Task.FromResult(Decision);
        }
    }
}
