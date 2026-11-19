using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// Chat tabs: several conversations in parallel, each bound to its drawing, taking turns in AutoCAD. Uses the real
/// <see cref="CopilotBackend"/> (sessions, running limit, turn queue) with scripted CLI runners; the AutoCAD side of the
/// queue is simulated by entering <see cref="CopilotBackend.DrawingQueue"/> the way the plug-in's pipe handler does.
/// </summary>
public sealed class ChatTabsTests : IDisposable
{
    private const double Width = 430;
    private const double Height = 760;

    private readonly string _temp;
    private DrawingRef? _activeDrawing = new("dwg-1", "GF-plan-30x40.dwg");

    public ChatTabsTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-tabs-").FullName;
    }

    public void Dispose()
    {
        DraftingPace.Watch = true;
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Tabs_are_separate_conversations_on_their_own_drawings_running_in_parallel() => Sta.Run(async () =>
    {
        var (backend, claude, codex) = CreateBackend();
        var shell = CreateShell(backend);
        var a = Assert.Single(shell.Tabs);
        Assert.Equal(ChatViewModel.NewChatTitle, a.Title);
        Assert.Equal("GF-plan-30x40.dwg", a.DrawingTitle);
        Assert.True(a.IsTabSelected);

        _activeDrawing = new DrawingRef("dwg-2", "FF-plan-30x40.dwg");
        var b = shell.NewTab()!;
        Assert.Same(b, shell.SelectedTab);
        Assert.False(a.IsTabSelected);
        Assert.True(b.IsTabSelected);
        Assert.Equal("FF-plan-30x40.dwg", b.DrawingTitle);
        Assert.Contains("FF-plan-30x40.dwg", b.DocumentText, StringComparison.Ordinal);
        Assert.Same(b.Backend, backend.ActiveSession);

        a.ComposerText = "Draw the ground floor plan for a 30' x 40' east-facing plot";
        var sendA = a.SendAsync();
        b.ComposerText = "Draw the first floor";
        var sendB = b.SendAsync();
        await Sta.WaitUntil(() => claude.Requests.Count == 2 && a.IsBusy && b.IsBusy, "two turns at once");
        Assert.Equal(2, backend.RunningSessionCount);
        Assert.Equal("Draw the ground floor plan for…", a.Title);
        Assert.Equal("Draw the first floor", b.Title);

        // Each CLI's bridge names its tab, so AutoCAD routes its questions, plan and drawing.
        var requestA = claude.Requests.Single(r => r.Prompt.StartsWith("Draw the ground", StringComparison.Ordinal));
        var requestB = claude.Requests.Single(r => r.Prompt == "Draw the first floor");
        Assert.Equal(["--pid", "4242", "--panel", "--session", SessionId(a)], requestA.AcadMcp.Arguments);
        Assert.Equal(["--pid", "4242", "--panel", "--session", SessionId(b)], requestB.AcadMcp.Arguments);
        Assert.NotEqual(SessionId(a), SessionId(b));

        // Stop in one tab stops only that tab.
        b.Stop();
        await sendB;
        Assert.False(b.IsBusy);
        Assert.True(a.IsBusy);
        Assert.True(Assert.IsType<TurnFooterItem>(b.Items[^1]).IsStopped);
        claude.Release(requestA.Prompt);
        await sendA;
        Assert.True(Assert.IsType<TurnFooterItem>(a.Items[^1]).IsCompleted);
        Assert.Equal(0, backend.RunningSessionCount);

        // Each tab resumes its own CLI session and keeps its own provider.
        claude.Hold = false;
        a.ComposerText = "Add dimensions";
        await a.SendAsync();
        b.SelectProviderCommand.Execute(b.Providers.Single(p => p.Id == ProviderId.Codex));
        b.ComposerText = "Add the staircase";
        await b.SendAsync();
        Assert.Equal(ProviderId.Claude, a.SelectedProvider.Id);
        Assert.Equal(ProviderId.Codex, b.SelectedProvider.Id);
        Assert.Equal(((CopilotSession)a.Backend).GetSessionId(ProviderId.Claude), claude.Requests.Last().ResumeSessionId);
        Assert.Null(Assert.Single(codex.Requests).ResumeSessionId);
        Assert.Equal(SessionId(b), codex.Requests.Single().AcadMcp.Arguments[^1]);
        shell.Dispose();
    });

    [Fact]
    public void Drawing_actions_take_turns_and_stop_leaves_the_queue() => Sta.Run(async () =>
    {
        var (backend, claude, _) = CreateBackend();
        var shell = CreateShell(backend);
        var a = shell.Tabs[0];
        _activeDrawing = new DrawingRef("dwg-2", "FF-plan-30x40.dwg");
        var b = shell.NewTab()!;
        a.ComposerText = "Draw the ground floor";
        var sendA = a.SendAsync();
        b.ComposerText = "Draw the first floor";
        var sendB = b.SendAsync();
        await Sta.WaitUntil(() => a.IsBusy && b.IsBusy && claude.Requests.Count == 2, "both running");

        // What the plug-in does for each typing call: A types now, B's call waits for its turn.
        var turnA = await backend.DrawingQueue.EnterAsync(SessionId(a), "Outer walls", Session(a).TurnToken);
        var waitB = backend.DrawingQueue.EnterAsync(SessionId(b), "First floor walls", Session(b).TurnToken);
        await Sta.WaitUntil(() => b.TabState == ChatTabState.Waiting, "B waiting");
        Assert.Equal(ChatTabState.Drawing, a.TabState);
        Assert.Equal("Drawing…", a.TabStatusText);
        Assert.Equal("2nd in line", b.TabStatusText);
        Assert.Equal(AcadTurnState.Waiting, b.AcadTurn.State);
        Assert.Equal("Waiting for AutoCAD (2nd in line)...", b.ActivityText);
        Assert.Equal("Drawing in AutoCAD...", a.ActivityText);
        Assert.Contains("2nd in line", b.TabToolTip, StringComparison.Ordinal);

        // Stop in B removes its waiting action from the queue; A keeps drawing.
        b.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitB);
        await sendB;
        Assert.Equal(1, backend.DrawingQueue.Count);
        Assert.Equal(AcadTurnState.Drawing, a.AcadTurn.State);

        turnA.Dispose();
        await Sta.WaitUntil(() => a.TabState == ChatTabState.Working, "A back to thinking");
        Assert.Equal("Working", a.TabStatusText);
        claude.Release("Draw the ground floor");
        await sendA;
        Assert.Equal(ChatTabState.Idle, a.TabState);
        Assert.False(a.HasTabStatus);
        shell.Dispose();
    });

    [Fact]
    public void Closing_a_working_tab_asks_first_and_the_last_tab_is_replaced() => Sta.Run(async () =>
    {
        var (backend, claude, _) = CreateBackend();
        var shell = CreateShell(backend);
        var a = shell.Tabs[0];
        var b = shell.NewTab()!;
        b.ComposerText = "Draw the elevation";
        var sendB = b.SendAsync();
        await Sta.WaitUntil(() => b.IsBusy, "B running");

        shell.CloseTabCommand.Execute(b);
        Assert.Same(b, shell.PendingClose);
        Assert.Equal("\"Draw the elevation\" is still working. Stop it and close the tab?", shell.PendingCloseText);
        Assert.Equal(2, shell.Tabs.Count);
        shell.CancelCloseCommand.Execute(null);
        Assert.False(shell.HasPendingClose);
        Assert.True(b.IsBusy);

        shell.CloseTabCommand.Execute(b);
        shell.ConfirmCloseCommand.Execute(null);
        await sendB;
        Assert.Equal([a], shell.Tabs);
        Assert.Same(a, shell.SelectedTab);
        Assert.Same(a.Backend, Assert.Single(backend.Sessions));
        Assert.True(Session(b).IsClosed);
        Assert.Equal(0, backend.RunningSessionCount);
        Assert.Null(backend.FindSession(SessionId(b)));

        // The last tab is never removed without a fresh one taking its place.
        shell.CloseTabCommand.Execute(a);
        var fresh = Assert.Single(shell.Tabs);
        Assert.NotSame(a, fresh);
        Assert.Equal(ChatViewModel.NewChatTitle, fresh.Title);
        Assert.Same(fresh.Backend, Assert.Single(backend.Sessions));
        Assert.DoesNotContain(claude.Requests, r => r.Prompt != "Draw the elevation");
        shell.Dispose();
    });

    [Fact]
    public void At_most_the_running_limit_of_chats_work_at_once() => Sta.Run(async () =>
    {
        var (backend, claude, _) = CreateBackend(maxRunning: 2);
        var shell = CreateShell(backend);
        var a = shell.Tabs[0];
        var b = shell.NewTab()!;
        var c = shell.NewTab()!;
        a.ComposerText = "One";
        var sendA = a.SendAsync();
        b.ComposerText = "Two";
        var sendB = b.SendAsync();
        await Sta.WaitUntil(() => a.IsBusy && b.IsBusy, "two running");
        Assert.Equal("2 of 2 chats working", shell.RunningText);

        c.ComposerText = "Three";
        await c.SendAsync();
        Assert.False(c.IsBusy);
        Assert.Equal("Three", c.ComposerText);
        Assert.Empty(c.Items);
        Assert.True(c.NoticeIsError);
        Assert.StartsWith("2 chats are already working", c.Notice, StringComparison.Ordinal);
        Assert.Equal(2, claude.Requests.Count);

        claude.Release("One");
        await sendA;
        claude.Hold = false;
        await c.SendAsync();
        Assert.True(Assert.IsType<TurnFooterItem>(c.Items[^1]).IsCompleted);
        b.Stop();
        await sendB;
        Assert.Equal(string.Empty, shell.RunningText);
        shell.Dispose();
    });

    [Fact]
    public void Questions_plans_and_approvals_go_to_their_own_tab() => Sta.Run(async () =>
    {
        var (backend, _, _) = CreateBackend();
        var shell = CreateShell(backend);
        var a = shell.Tabs[0];
        var b = shell.NewTab()!;
        a.ComposerText = "Plan the ground floor";
        var sendA = a.SendAsync();
        b.ComposerText = "Plan the first floor";
        var sendB = b.SendAsync();
        await Sta.WaitUntil(() => a.IsBusy && b.IsBusy, "both running");

        var question = new QuestionRequest { Questions = [new UserQuestion { Question = "Stair position?", Options = [new() { Label = "North-east" }, new() { Label = "South-west" }] }] };
        var answer = backend.ResolveSession(SessionId(a), out _)!.AskUserAsync(question, CancellationToken.None);
        backend.FindSession(SessionId(b))!.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "First floor walls", Status = PlanTaskStatus.InProgress }] });
        await Sta.Settle();
        Assert.Single(a.Items.OfType<QuestionCardItem>());
        Assert.Empty(b.Items.OfType<QuestionCardItem>());
        Assert.Equal(ChatTabState.NeedsAnswer, a.TabState);
        Assert.Equal("Needs your answer", a.TabStatusText);
        Assert.True(b.Plan.HasTasks);
        Assert.False(a.Plan.HasTasks);

        // "Always" is remembered per chat.
        var approvalA = Session(a).RequestApprovalAsync(new ApprovalRequest { ToolName = "run_commands:Allow QSAVE", ToolTitle = "Allow QSAVE", Summary = "Save", Risk = ToolRisk.Write }, CancellationToken.None);
        await Sta.Settle();
        a.Items.OfType<ApprovalCardItem>().Single().AlwaysCommand.Execute(null);
        Assert.Equal(ApprovalDecision.AlwaysApprove, await approvalA);
        Assert.Equal(ApprovalDecision.Approve, await Session(a).RequestApprovalAsync(new ApprovalRequest { ToolName = "run_commands:Allow QSAVE", ToolTitle = "Allow QSAVE", Summary = "Save", Risk = ToolRisk.Write }, CancellationToken.None));
        var approvalB = Session(b).RequestApprovalAsync(new ApprovalRequest { ToolName = "run_commands:Allow QSAVE", ToolTitle = "Allow QSAVE", Summary = "Save", Risk = ToolRisk.Write }, CancellationToken.None);
        await Sta.Settle();
        Assert.Single(b.Items.OfType<ApprovalCardItem>());
        Assert.False(approvalB.IsCompleted);

        // A client outside the panel (no session id) talks to the tab the user sees.
        shell.SelectTab(a);
        Assert.Same(a.Backend, backend.ResolveSession(null, out var closed));
        Assert.False(closed);
        Assert.Null(backend.ResolveSession("tab-gone", out closed));
        Assert.True(closed);

        a.Stop();
        b.Stop();
        Assert.True((await answer).Cancelled);
        Assert.Equal(ApprovalDecision.Reject, await approvalB);
        await sendA;
        await sendB;
        shell.Dispose();
    });

    [Fact]
    public void Sign_in_states_approval_mode_and_pace_are_shared_by_all_tabs() => Sta.Run(async () =>
    {
        var (backend, _, _) = CreateBackend();
        var shell = CreateShell(backend);
        var a = shell.Tabs[0];
        var b = shell.NewTab()!;
        await a.RefreshStatusesAsync();
        Assert.All(b.Providers, p => Assert.NotNull(p.Status));
        Assert.True(b.Providers.Single(p => p.Id == ProviderId.Claude).IsSignedIn);
        var c = shell.NewTab()!;
        Assert.True(c.StatusesKnown);

        a.ApprovalMode = ApprovalMode.AutoApprove;
        Assert.Equal(ApprovalMode.AutoApprove, b.ApprovalMode);
        Assert.Equal(ApprovalMode.AutoApprove, c.ApprovalMode);

        b.IsFastPace = true;
        await Sta.Settle();
        Assert.True(a.IsFastPace);
        Assert.True(c.IsFastPace);
        a.IsWatchPace = true;
        await Sta.Settle();
        Assert.True(b.IsWatchPace);
        shell.Dispose();
    });

    [Fact]
    public void Drawing_titles_follow_renames_and_closing() => Sta.Run(async () =>
    {
        var (backend, _, _) = CreateBackend();
        var shell = CreateShell(backend);
        var a = shell.Tabs[0];
        backend.UpdateDrawings([new DrawingRef("dwg-1", "House-GF.dwg")]);
        await Sta.Settle();
        Assert.Equal("House-GF.dwg", a.DrawingTitle);
        backend.MarkDrawingClosed("dwg-1");
        await Sta.Settle();
        Assert.Equal("House-GF.dwg (closed)", a.DrawingTitle);
        Assert.True(a.IsDrawingClosed);

        // A tab opened while no drawing is open binds to the drawing of its first tool call.
        _activeDrawing = null;
        var b = shell.NewTab()!;
        Assert.Null(Session(b).Drawing);
        Assert.Equal("GF-plan-30x40.dwg", b.DrawingTitle);
        Session(b).BindDrawing(new DrawingRef("dwg-7", "Site.dwg"));
        await Sta.Settle();
        Assert.Equal("Site.dwg", b.DrawingTitle);
        Assert.Equal("Site.dwg", Session(b).BindDrawing(new DrawingRef("dwg-8", "Other.dwg")).Title);
        shell.Dispose();
    });

    [Fact]
    public void Tab_titles_come_from_the_first_message() => Sta.Run(() =>
    {
        Assert.Equal("Draw it", ChatViewModel.TitleFrom("  Draw\n it "));
        Assert.Equal("Draw the ground floor plan for…", ChatViewModel.TitleFrom("Draw the ground floor plan for a 30' x 40' east-facing plot"));
        Assert.Equal("Attached files", ChatViewModel.TitleFrom("", 2));
        Assert.Equal(ChatViewModel.NewChatTitle, ChatViewModel.TitleFrom(null));
        Assert.Equal("2nd", AcadTurnStatus.Ordinal(2));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(PanelTheme.Light, Width)]
    [InlineData(PanelTheme.Dark, Width)]
    [InlineData(PanelTheme.Light, 320)]
    public void Renders_three_chat_tabs_drawing_waiting_and_done(PanelTheme theme, double width) => Sta.Run(async () =>
    {
        var (backend, claude, _) = CreateBackend();
        claude.Before = request => request.Prompt.StartsWith("Draw the first", StringComparison.Ordinal)
            ?
            [
                new TextDone("The first floor follows the ground floor walls. Starting with the setup, then the outer walls."),
                new ToolCallStarted("f1", "get_drawing_info", string.Empty, "acad"),
                new ToolCallFinished("f1", true, "Architectural units (feet-inches), 2 layers"),
                new ToolCallStarted("f2", "run_commands", "title: First floor outer walls, steps: 6 items", "acad"),
            ]
            : [new TextDone("Drawing the ground floor in stages.")];
        var pane = CreatePane(backend, theme);
        await pane.ViewModel.RefreshStatusesAsync();
        var shell = pane.Shell;
        var a = shell.Tabs[0];
        a.ComposerText = "Draw the ground floor plan for a 30' x 40' east-facing plot";
        var sendA = a.SendAsync();

        _activeDrawing = new DrawingRef("dwg-2", "FF-plan-30x40.dwg");
        var b = shell.NewTab()!;
        b.ComposerText = "Draw the first floor plan";
        var sendB = b.SendAsync();

        _activeDrawing = new DrawingRef("dwg-3", "Site-plan.dwg");
        var c = shell.NewTab()!;
        claude.Hold = false;
        claude.Before = _ => [new TextDone("The drawing has the site boundary, the setbacks and the north arrow. No building is drawn yet.")];
        c.ComposerText = "What is in this drawing?";
        await c.SendAsync();
        claude.Hold = true;

        await Sta.WaitUntil(() => a.IsBusy && b.IsBusy, "two working tabs");
        var turnA = await backend.DrawingQueue.EnterAsync(SessionId(a), "Outer walls", Session(a).TurnToken);
        var waitB = backend.DrawingQueue.EnterAsync(SessionId(b), "First floor outer walls", Session(b).TurnToken);
        shell.SelectTab(b);
        await Sta.WaitUntil(() => b.TabState == ChatTabState.Waiting && a.TabState == ChatTabState.Drawing, "queue states");
        Assert.Same(b, pane.ViewModel);
        Assert.Same(b, pane.DataContext);
        Assert.Equal("Waiting for AutoCAD (2nd in line)...", b.ActivityText);
        Save(pane, "11-chat-tabs", theme, width);

        // Asking to close a working tab shows the confirmation bar.
        shell.CloseTabCommand.Execute(a);
        Assert.True(shell.HasPendingClose);
        if (width == Width)
        {
            Save(pane, "12-close-working-tab", theme, width);
        }

        shell.CancelCloseCommand.Execute(null);
        turnA.Dispose();
        await waitB.ContinueWith(t => t.Result.Dispose(), TaskScheduler.Default);
        claude.Release("Draw the ground floor plan for a 30' x 40' east-facing plot");
        claude.Release("Draw the first floor plan");
        await sendA;
        await sendB;
        shell.Dispose();
    });

    private static string SessionId(ChatViewModel chat) => ((ICopilotSession)chat.Backend).Id;

    private static CopilotSession Session(ChatViewModel chat) => (CopilotSession)chat.Backend;

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width, double height = Height)
    {
        var suffix = width == Width ? string.Empty : "-narrow";
        var path = Snapshot.Render(pane, width, height, $"{name}{suffix}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    private (CopilotBackend Backend, ScriptedRunner Claude, ScriptedRunner Codex) CreateBackend(int maxRunning = CopilotBackend.DefaultMaxRunningSessions)
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude");
        var codex = new ScriptedRunner(ProviderId.Codex, "ChatGPT") { Hold = false };
        var backend = new CopilotBackend(
            [claude, codex],
            () => new McpServerLaunch { Command = @"C:\bridge\AcadCopilotMcp.exe", Arguments = ["--pid", "4242", "--panel"] },
            new AcadSessionInfo(4242, "2027", "GF-plan-30x40.dwg"),
            new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json")),
            Path.Combine(_temp, "workspace"),
            () => _activeDrawing,
            maxRunning);
        return (backend, claude, codex);
    }

    private CopilotPanelViewModel CreateShell(CopilotBackend backend)
    {
        var panelSettings = new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"));
        var settings = new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-pace.json"));
        return new CopilotPanelViewModel(backend, session => new ChatViewModel(session, Dispatcher.CurrentDispatcher, null, panelSettings, settings));
    }

    private CopilotPane CreatePane(CopilotBackend backend, PanelTheme theme) =>
        new(backend, new CopilotPaneOptions
        {
            Theme = theme,
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-pace.json"),
            CheckProviderStatusOnLoad = false,
        });

    /// <summary>A CLI stand-in: streams a short answer and (when <see cref="Hold"/>) works until released or stopped.</summary>
    private sealed class ScriptedRunner(ProviderId id, string displayName) : IAgentRunner
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private int _sessions;

        public ConcurrentQueue<TurnRequest> Requests { get; } = new();

        public bool Hold { get; set; } = true;

        public Func<TurnRequest, IEnumerable<AgentEvent>>? Before { get; set; }

        public ProviderId ProviderId => id;

        public string DisplayName => displayName;

        public IReadOnlyList<ModelChoice> Models { get; } = [new(null, "Default")];

        public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus { Provider = id, Installed = true, Version = "1.0", SignIn = SignInState.SignedIn, Account = displayName + " plan" });

        public CommandLine GetSignInCommand() => new("fake", ["login"]);

        public void Release(string prompt) => Gate(prompt).TrySetResult();

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var hold = Hold;
            var before = Before?.Invoke(request) ?? [];
            await Task.Yield();
            yield return new SessionStarted(request.ResumeSessionId ?? $"{id}-cli-{Interlocked.Increment(ref _sessions)}");
            foreach (var item in before)
            {
                yield return item;
            }

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

            yield return new TextDone("Done: " + request.Prompt);
            yield return new TurnCompleted("end_turn");
        }

        private TaskCompletionSource Gate(string prompt) =>
            _gates.GetOrAdd(prompt, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
