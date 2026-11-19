using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Panel;

/// <summary>AI switching (handover to the next signed-in provider), the second-opinion review and the feature settings.</summary>
public sealed class SwitchingAndReviewTests : IDisposable
{
    private const string Review = """
        SUMMARY: Usable plan, two things to fix.
        - [major] Bedroom 2 door swings into the passage -> hinge it on the east jamb and swing into the room.
        - [minor] Kitchen size text overlaps the window line -> move it 1'0 south.
        """;

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_limit_hands_the_same_job_to_the_next_signed_in_provider()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(
            new SessionStarted("c-1"),
            new TextDelta("Drawing the outer walls"),
            new ToolCallStarted("1", "run_commands", "title: Outer walls, steps: 5 items", "acad"),
            new ToolCallFinished("1", true, "Typed 5 commands, created 6 objects"),
            new ToolCallStarted("2", "get_drawing_info", string.Empty, "acad"),
            new ToolCallFinished("2", true, "House.dwg: 6 objects on 2 layers, LUNITS 4"),
            new TurnFailed("Claude reports a usage or rate limit: You've hit your limit · resets 3pm.", AgentFailureKind.RateLimited)));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(
            new SessionStarted("x-1"),
            new TextDone("Continuing with the inner walls."),
            new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);
        var session = (CopilotSession)backend.CreateSession();
        session.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Setup", Status = PlanTaskStatus.Completed }, new PlanTask { Title = "Outer walls", Status = PlanTaskStatus.InProgress }, new PlanTask { Title = "Inner walls" }] });

        var events = await CollectAsync(session.SendAsync("Draw the ground floor of a 30x40 east-facing plot", [], CancellationToken.None));

        var switched = Assert.Single(events.OfType<ProviderSwitched>());
        Assert.Equal((ProviderId.Claude, ProviderId.Codex, SwitchReason.Limit), (switched.From, switched.To, switched.Reason));
        Assert.Equal("Claude reached its limit - continuing with ChatGPT.", switched.Message);
        Assert.Contains(new TextDone(switched.Message), events);
        Assert.Equal(new TurnCompleted("completed"), events[^1]);
        Assert.Single(events, e => e is TurnCompleted or TurnFailed);

        var handover = Assert.Single(codex.Requests);
        Assert.Null(handover.ResumeSessionId);
        Assert.StartsWith("HANDOVER from Claude to ChatGPT", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("ORIGINAL REQUEST: Draw the ground floor of a 30x40 east-facing plot", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("run_commands (title: Outer walls, steps: 5 items): ok - Typed 5 commands", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("- [in progress] Outer walls", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("- [todo] Inner walls", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("LAST DRAWING FACTS (get_drawing_info): House.dwg: 6 objects", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("do not ask again", handover.Prompt, StringComparison.Ordinal);
        Assert.Contains("--session", handover.AcadMcp.Arguments);

        // The chat goes on with ChatGPT; the default provider for new tabs is not changed by the switch.
        Assert.Equal(ProviderId.Codex, session.SelectedProvider);
        Assert.Equal("x-1", session.GetSessionId(ProviderId.Codex));
        Assert.Equal(ProviderId.Claude, new CopilotSettingsStore(_dir.File("settings.json")).Load().Provider);
    }

    [Fact]
    public async Task Switching_follows_the_order_and_skips_providers_that_are_signed_out()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnFailed("Not logged in · Please run /login", AgentFailureKind.NotSignedIn)));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TurnCompleted("completed")));
        var gemini = new ScriptedRunner(ProviderId.Gemini, "Gemini", (_, _) => Script(new TurnCompleted("success"))) { SignIn = SignInState.SignedOut };
        var backend = CreateBackend(claude, codex, gemini);
        backend.Features = backend.Features with { ProviderOrder = [ProviderId.Claude, ProviderId.Gemini, ProviderId.Codex] };

        var events = await CollectAsync(backend.SendAsync("hello", [], CancellationToken.None));

        var switched = Assert.Single(events.OfType<ProviderSwitched>());
        Assert.Equal(SwitchReason.SignIn, switched.Reason);
        Assert.Equal(ProviderId.Codex, switched.To);
        Assert.Empty(gemini.Requests);
        Assert.IsType<TurnCompleted>(events[^1]);
    }

    [Fact]
    public async Task Other_failures_are_retried_once_then_handed_over()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnFailed("Claude Code: stream disconnected", AgentFailureKind.Other)));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);

        var events = await CollectAsync(backend.SendAsync("draw a room", [], CancellationToken.None));

        Assert.Equal(2, claude.Requests.Count);
        Assert.StartsWith("Your previous attempt stopped with an error", claude.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(SwitchReason.Failure, Assert.Single(events.OfType<ProviderSwitched>()).Reason);
        Assert.Contains("failed again - continuing with ChatGPT", Assert.Single(events.OfType<ProviderSwitched>()).Message, StringComparison.Ordinal);
        Assert.IsType<TurnCompleted>(events[^1]);
    }

    [Fact]
    public async Task Without_auto_switch_or_another_provider_the_failure_is_shown()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnFailed("limit", AgentFailureKind.RateLimited)));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);
        backend.Features = backend.Features with { AutoSwitch = false };
        var off = await CollectAsync(backend.SendAsync("x", [], CancellationToken.None));
        Assert.Equal(AgentFailureKind.RateLimited, Assert.IsType<TurnFailed>(Assert.Single(off)).Kind);
        Assert.Empty(codex.Requests);

        var alone = CreateBackend(new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnFailed("limit", AgentFailureKind.RateLimited))));
        var events = await CollectAsync(alone.SendAsync("x", [], CancellationToken.None));
        Assert.Equal(AgentFailureKind.RateLimited, Assert.IsType<TurnFailed>(Assert.Single(events)).Kind);

        // Stop never switches.
        var stopped = CreateBackend(new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnFailed("Stopped.", AgentFailureKind.Cancelled))), codex);
        Assert.Equal(AgentFailureKind.Cancelled, Assert.IsType<TurnFailed>(Assert.Single(await CollectAsync(stopped.SendAsync("x", [], CancellationToken.None)))).Kind);
    }

    [Fact]
    public async Task Text_notices_can_be_left_to_the_view()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnFailed("limit", AgentFailureKind.RateLimited)));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);
        backend.EmitNoticesAsText = false;
        var events = await CollectAsync(backend.SendAsync("x", [], CancellationToken.None));
        Assert.Single(events.OfType<ProviderSwitched>());
        Assert.DoesNotContain(events, e => e is TextDone { Text.Length: > 0 });
    }

    [Fact]
    public async Task A_manual_provider_change_gets_the_conversation_so_far()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TextDone("| Room | Size |\n| Living | 12'x16' |"), new TurnCompleted("end_turn")));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);
        await CollectAsync(backend.SendAsync("Plan a 2BHK on 30x40", [], CancellationToken.None));

        backend.SelectedProvider = ProviderId.Codex;
        await CollectAsync(backend.SendAsync("Draw it", [], CancellationToken.None));

        var prompt = Assert.Single(codex.Requests).Prompt;
        Assert.StartsWith("HANDOVER: this chat was handled by Claude until now; you are ChatGPT", prompt, StringComparison.Ordinal);
        Assert.Contains("User: Plan a 2BHK on 30x40", prompt, StringComparison.Ordinal);
        Assert.Contains("Living | 12'x16'", prompt, StringComparison.Ordinal);
        Assert.EndsWith("THE USER'S NEW MESSAGE:\nDraw it", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_uses_another_provider_with_a_read_only_bridge_and_returns_a_card()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnCompleted("end_turn")));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(
            new SessionStarted("review-session"),
            new ToolCallStarted("r1", "capture_view", string.Empty, "acad"),
            new ToolCallFinished("r1", true, "Captured"),
            new TextDone(Review),
            new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);
        var session = (CopilotSession)backend.CreateSession();

        var events = await CollectAsync(session.ReviewAsync("the ground floor plan", CancellationToken.None));

        var request = Assert.Single(codex.Requests);
        Assert.Empty(claude.Requests);
        Assert.Contains("--readonly", request.AcadMcp.Arguments);
        Assert.DoesNotContain("--panel", request.AcadMcp.Arguments);
        Assert.Equal(session.Id, request.AcadMcp.Arguments[request.AcadMcp.Arguments.ToList().IndexOf("--session") + 1]);
        Assert.StartsWith(CopilotInstructions.Reviewer, request.Instructions, StringComparison.Ordinal);
        Assert.Null(request.ResumeSessionId);
        Assert.StartsWith("Review the ground floor plan as a senior architect", request.Prompt, StringComparison.Ordinal);

        Assert.Contains(events, e => e is ToolCallStarted { Name: "capture_view" });
        var report = Assert.Single(events.OfType<ReviewCompleted>()).Report;
        Assert.Equal("ChatGPT", report.ReviewerName);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(1, report.MajorCount);
        Assert.Equal(new TurnCompleted("review"), events[^1]);
        Assert.DoesNotContain(events, e => e is TextDone done && done.Text.Contains("SUMMARY:", StringComparison.Ordinal));
        Assert.Contains(events, e => e is TextDone done && done.Text.StartsWith("**Second opinion (ChatGPT)**", StringComparison.Ordinal));

        // The reviewer's CLI session never becomes the chat's session.
        Assert.Null(session.GetSessionId(ProviderId.Codex));
        Assert.Same(report, session.LastReview);
    }

    [Fact]
    public async Task Review_request_in_the_chat_and_fix_these_reach_the_right_ai()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnCompleted("end_turn")));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TextDone(Review), new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);

        var reviewed = await CollectAsync(backend.SendAsync("Please review this plan", [], CancellationToken.None));
        Assert.Single(reviewed.OfType<ReviewCompleted>());
        Assert.Empty(claude.Requests);

        await CollectAsync(backend.SendAsync("fix these", [], CancellationToken.None));
        var fix = Assert.Single(claude.Requests).Prompt;
        Assert.StartsWith("Fix these findings of the second-opinion review (by ChatGPT)", fix, StringComparison.Ordinal);
        Assert.Contains("1. [major] Bedroom 2 door swings into the passage", fix, StringComparison.Ordinal);
        Assert.Contains("2. [minor] Kitchen size text overlaps", fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_review_runs_after_a_finished_drawing_before_the_turn_ends()
    {
        CopilotSession? session = null;
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Drawing(session!));
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT", (_, _) => Script(new TextDone("SUMMARY: No problems found."), new TurnCompleted("completed")));
        var backend = CreateBackend(claude, codex);
        backend.Features = backend.Features with { AutoReview = true };
        session = (CopilotSession)backend.CreateSession();

        var events = await CollectAsync(session.SendAsync("draw it", [], CancellationToken.None));

        var report = Assert.Single(events.OfType<ReviewCompleted>()).Report;
        Assert.True(report.IsClean);
        Assert.Equal(new TurnCompleted("end_turn"), events[^1]);
        Assert.Single(events, e => e is TurnCompleted or TurnFailed);

        // A turn that drew nothing is not reviewed.
        backend.Features = backend.Features with { AutoReview = true };
        var plain = CreateBackend(new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TextDone("Hi"), new TurnCompleted("end_turn"))), codex);
        plain.Features = plain.Features with { AutoReview = true };
        Assert.DoesNotContain(await CollectAsync(plain.SendAsync("hi", [], CancellationToken.None)), e => e is ReviewCompleted);
    }

    [Fact]
    public void Features_persist_and_shape_the_instructions()
    {
        var backend = CreateBackend(new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnCompleted("x"))));
        Assert.True(backend.Features.AutoSwitch);
        Assert.True(backend.Features.AskBeforeDrawing);
        Assert.False(backend.Features.AutoReview);
        Assert.False(backend.Features.TutorMode);
        Assert.Equal([ProviderId.Claude, ProviderId.Codex, ProviderId.Gemini], backend.Features.ProviderOrder);
        Assert.DoesNotContain("TUTOR MODE", backend.BuildInstructions(), StringComparison.Ordinal);

        var store = new CopilotSettingsStore(_dir.File("settings.json"));
        store.Update(s => s with { DraftingWatch = false });
        backend.Features = backend.Features with { TutorMode = true, AskBeforeDrawing = false, ProviderOrder = [ProviderId.Gemini] };

        var saved = store.Load();
        Assert.False(saved.DraftingWatch);
        Assert.True(saved.TutorMode);
        Assert.False(saved.AskBeforeDrawing);
        Assert.Equal([ProviderId.Gemini, ProviderId.Claude, ProviderId.Codex], saved.Features.ProviderOrder);
        Assert.Contains(CopilotInstructions.TutorNote, backend.BuildInstructions(), StringComparison.Ordinal);
        Assert.Contains(CopilotInstructions.NoApprovalNote, backend.BuildInstructions(), StringComparison.Ordinal);

        var reloaded = CreateBackend(new ScriptedRunner(ProviderId.Claude, "Claude", (_, _) => Script(new TurnCompleted("x"))));
        Assert.True(reloaded.Features.TutorMode);
        Assert.Equal(ProviderId.Gemini, reloaded.Features.ProviderOrder[0]);
    }

    private static async IAsyncEnumerable<AgentEvent> Drawing(CopilotSession session)
    {
        await Task.Yield();
        session.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Setup" }, new PlanTask { Title = "Walls" }, new PlanTask { Title = "Doors" }] });
        yield return new ToolCallStarted("w", "run_commands", "title: Walls", "acad");
        yield return new ToolCallFinished("w", true, "Typed 5 commands");
        session.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Setup", Status = PlanTaskStatus.Completed }, new PlanTask { Title = "Walls", Status = PlanTaskStatus.Completed }, new PlanTask { Title = "Doors", Status = PlanTaskStatus.Completed }] });
        yield return new TextDone("Done.");
        yield return new TurnCompleted("end_turn");
    }

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

    private static async IAsyncEnumerable<AgentEvent> Script(params AgentEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private sealed class ScriptedRunner(ProviderId id, string name, Func<TurnRequest, CancellationToken, IAsyncEnumerable<AgentEvent>> script) : IAgentRunner
    {
        public List<TurnRequest> Requests { get; } = [];

        public SignInState SignIn { get; init; } = SignInState.SignedIn;

        public ProviderId ProviderId => id;

        public string DisplayName => name;

        public IReadOnlyList<ModelChoice> Models { get; } = [new(null, "Default")];

        public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus { Provider = id, Installed = true, SignIn = SignIn });

        public CommandLine GetSignInCommand() => new("fake", ["login"]);

        public IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return script(request, cancellationToken);
        }
    }
}
