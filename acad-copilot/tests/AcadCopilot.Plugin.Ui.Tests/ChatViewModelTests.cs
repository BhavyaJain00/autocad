using System.IO;
using System.Windows.Documents;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>Behaviour of the chat view model (no rendering).</summary>
public sealed class ChatViewModelTests : IDisposable
{
    private readonly string _temp;

    public ChatViewModelTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-vm-").FullName;
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
    public void Streams_text_groups_tool_calls_and_ends_with_footer() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.Script.AddRange(PaneRenderTests.DraftingScript());
        backend.Script.Add(new ToolCallFinished("c5", false, "Stopped after 2 of 5 commands.", "step 3 OFFSET: input 2 'L' rejected (Invalid selection)."));
        backend.Script.Add(new TextDone("Plot and outer walls are drawn; the inner walls need another try."));
        vm.ComposerText = "Draw it";
        await vm.SendAsync();

        Assert.False(vm.IsBusy);
        Assert.Equal(["Draw it"], backend.Sent);
        Assert.Collection(
            vm.Items,
            item => Assert.Equal("Draw it", Assert.IsType<UserMessageItem>(item).Text),
            item => Assert.StartsWith("Starting with the layers", Assert.IsType<AssistantMessageItem>(item).Text, StringComparison.Ordinal),
            item => Assert.Equal(5, Assert.IsType<ActivityGroupItem>(item).Rows.Count),
            item => Assert.Equal("Plot and outer walls are drawn; the inner walls need another try.", Assert.IsType<AssistantMessageItem>(item).Text),
            item => Assert.True(Assert.IsType<TurnFooterItem>(item).IsCompleted));
        var rows = vm.Items.OfType<ActivityGroupItem>().Single().Rows;
        Assert.Equal("Read the drawing info", rows[0].Title);
        Assert.Equal("Plot and outer walls", rows[2].Title);
        Assert.Equal("6 commands", rows[2].SubtitleText);
        Assert.Equal("Looked at the drawing", rows[3].Title);
        Assert.Equal("step 3 OFFSET: input 2 'L' rejected (Invalid selection).", rows[4].Error);
        Assert.True(rows[4].IsFailed);
        Assert.Equal("5 actions · 1 failed", vm.Items.OfType<ActivityGroupItem>().Single().CountText);
        Assert.All(vm.Items.OfType<AssistantMessageItem>(), m => Assert.False(m.IsStreaming));
        vm.Dispose();
    });

    [Fact]
    public void Live_status_shows_thinking_tools_and_questions_until_the_turn_ends() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        vm.ComposerText = "Draw the ground floor";
        var send = vm.SendAsync();

        // Nothing streamed yet: the panel must not look dead while the model thinks.
        await Sta.WaitUntil(() => vm.ShowActivity, "status line");
        Assert.Equal("Thinking...", vm.ActivityText);
        Assert.Matches(@"^\d+:\d\d$", vm.ActivityElapsed);

        // A tool call names what runs.
        vm.Apply(new ToolCallStarted("c1", "run_commands", "title: Outer walls, steps: 4 items", "acad"), CurrentTurn(vm));
        Assert.Equal("Typing AutoCAD commands: Outer walls...", vm.ActivityText);
        vm.Apply(new ToolCallFinished("c1", true, "Typed 4 commands."), CurrentTurn(vm));
        Assert.Equal("Thinking...", vm.ActivityText);

        // Streaming text hides the line (the words are the progress); it comes back when the text pauses.
        vm.Apply(new TextDelta("The walls are "), CurrentTurn(vm));
        Assert.False(vm.ShowActivity);
        vm.Apply(new TextDone("The walls are drawn."), CurrentTurn(vm));
        Assert.True(vm.ShowActivity);
        Assert.Equal("Thinking...", vm.ActivityText);

        // A question card waits for the user.
        var question = backend.Ui!.AskUserAsync(PaneRenderTests.BriefQuestions(), CancellationToken.None);
        await Sta.Settle();
        Assert.Equal("Waiting for your answer...", vm.ActivityText);

        vm.StopCommand.Execute(null);
        await send;
        Assert.True((await question).Cancelled);
        Assert.False(vm.ShowActivity);
        vm.Dispose();
    });

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(42, "0:42")]
    [InlineData(100, "1:40")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Activity_elapsed_is_minutes_and_seconds(int seconds, string expected)
    {
        Assert.Equal(expected, ActivityStatus.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Activity_label_prefers_the_user_then_the_tool_then_thinking()
    {
        var group = new ActivityGroupItem();
        var look = group.Add("c1", "capture_view", string.Empty, "acad");
        var other = group.Add("c2", "some_tool", string.Empty, "x");
        Assert.Equal("Waiting for your answer", ActivityStatus.Label(true, look, false));
        Assert.Equal("Looking at the drawing", ActivityStatus.Label(false, look, false));
        Assert.Equal("Using Some tool", ActivityStatus.Label(false, other, false));
        Assert.Equal("Thinking", ActivityStatus.Label(false, null, false));
        Assert.Null(ActivityStatus.Label(false, null, true));
    }

    private static TurnInfo CurrentTurn(ChatViewModel vm) =>
        (TurnInfo)typeof(ChatViewModel).GetField("_turn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;

    [Fact]
    public void Stop_cancels_turn_question_and_running_tool() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        backend.Script.Add(new ToolCallStarted("t1", "run_commands", "title: Outer walls, steps: 4 items", "acad"));
        vm.ComposerText = "Draw the outer walls";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.Items.OfType<ActivityGroupItem>().Any(), "tool row");
        var question = backend.Ui!.AskUserAsync(PaneRenderTests.BriefQuestions(), CancellationToken.None);
        await Sta.Settle();
        Assert.True(vm.StopCommand.CanExecute(null));

        vm.StopCommand.Execute(null);
        await send;

        Assert.True((await question).Cancelled);
        Assert.True(vm.Items.OfType<QuestionCardItem>().Single().IsSkipped);
        Assert.True(vm.Items.OfType<ActivityGroupItem>().Single().Rows[0].IsCancelled);
        Assert.True(vm.Items.OfType<TurnFooterItem>().Single().IsStopped);
        Assert.Empty(vm.Items.OfType<ErrorItem>());
        Assert.False(vm.IsBusy);
        vm.Dispose();
    });

    [Fact]
    public void Single_choice_question_answers_on_click_from_a_background_caller() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        var request = new QuestionRequest
        {
            Questions = [new UserQuestion { Question = "Units?", Options = [new QuestionOption { Label = "Feet and inches" }, new QuestionOption { Label = "Millimetres" }] }],
        };

        // ICopilotUi is called from pipe threads: the call must not block and must marshal to the dispatcher.
        var answer = Task.Run(() => backend.Ui!.AskUserAsync(request, CancellationToken.None));
        await Sta.WaitUntil(() => vm.Items.OfType<QuestionCardItem>().Any(), "card");
        var card = vm.Items.OfType<QuestionCardItem>().Single();
        Assert.False(card.ShowSubmit);
        card.Questions[0].Options[1].SelectCommand.Execute(null);

        var response = await answer;
        Assert.False(response.Cancelled);
        Assert.Equal("Units?", response.Answers[0].Question);
        Assert.Equal(["Millimetres"], response.Answers[0].Selected);
        Assert.True(card.IsAnswered);
        Assert.Equal("Millimetres", card.Questions[0].AnswerText);
        Assert.False(card.Questions[0].Options[0].SelectCommand.CanExecute(null));
        vm.Dispose();
    });

    [Fact]
    public void Multi_select_question_needs_submit_and_cancellation_token_skips() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        var request = new QuestionRequest
        {
            Questions =
            [
                new UserQuestion
                {
                    Question = "Which rooms get an attached toilet?",
                    MultiSelect = true,
                    Options = [new QuestionOption { Label = "Bedroom 1" }, new QuestionOption { Label = "Bedroom 2" }, new QuestionOption { Label = "Guest room" }],
                },
            ],
        };
        var answer = backend.Ui!.AskUserAsync(request, CancellationToken.None);
        var card = vm.Items.OfType<QuestionCardItem>().Single();
        Assert.True(card.ShowSubmit);
        Assert.False(card.SubmitCommand.CanExecute(null));
        card.Questions[0].Options[0].SelectCommand.Execute(null);
        card.Questions[0].Options[2].SelectCommand.Execute(null);
        Assert.False(answer.IsCompleted);
        card.SubmitCommand.Execute(null);
        Assert.Equal(["Bedroom 1", "Guest room"], (await answer).Answers[0].Selected);

        using var cts = new CancellationTokenSource();
        var second = backend.Ui.AskUserAsync(request, cts.Token);
        cts.Cancel();
        Assert.True((await second).Cancelled);
        await Sta.Settle();
        Assert.True(vm.Items.OfType<QuestionCardItem>().Last().IsSkipped);
        vm.Dispose();
    });

    [Fact]
    public void Approval_cards_resolve_and_auto_mode_skips_them() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var settings = Path.Combine(_temp, "panel.json");
        var vm = Create(backend, settings);
        var save = new ApprovalRequest
        {
            ToolName = "run_commands:Save the drawing (QSAVE)",
            ToolTitle = "Save the drawing (QSAVE)",
            Summary = "QSAVE writes the drawing to disk.",
            Risk = ToolRisk.Destructive,
        };

        var first = backend.Ui!.RequestApprovalAsync(save, CancellationToken.None);
        var card = vm.Items.OfType<ApprovalCardItem>().Single();
        Assert.False(card.ShowAlways);
        Assert.False(card.AlwaysCommand.CanExecute(null));
        card.RejectCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Reject, await first);
        Assert.True(card.IsRejected);

        var write = backend.Ui.RequestApprovalAsync(save with { Risk = ToolRisk.Write, ToolName = "some_write_tool" }, CancellationToken.None);
        var writeCard = vm.Items.OfType<ApprovalCardItem>().Last();
        Assert.True(writeCard.ShowAlways);
        writeCard.AlwaysCommand.Execute(null);
        Assert.Equal(ApprovalDecision.AlwaysApprove, await write);

        vm.IsAutoApprove = true;
        Assert.Equal(ApprovalMode.AutoApprove, new PanelSettingsStore(settings).Load().ApprovalMode);
        var count = vm.Items.Count;
        Assert.Equal(ApprovalDecision.Approve, await backend.Ui.RequestApprovalAsync(save, CancellationToken.None));
        Assert.Equal(count, vm.Items.Count);
        vm.Dispose();

        var reopened = Create(FakeBackend.Create(), settings);
        Assert.True(reopened.IsAutoApprove);
        reopened.Dispose();
    });

    [Fact]
    public void Plan_is_pinned_while_running_and_kept_after_the_turn() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.HoldOpen = true;
        vm.ComposerText = "Draw it";
        var send = vm.SendAsync();
        await Sta.WaitUntil(() => vm.IsBusy, "busy");
        backend.Ui!.UpdatePlan(PaneRenderTests.Plan(PlanTaskStatus.Completed, PlanTaskStatus.InProgress, PlanTaskStatus.Pending, PlanTaskStatus.Pending, PlanTaskStatus.Failed));
        Assert.Equal("Task Progress (1/5)", vm.Plan.Header);
        Assert.Equal(0.2, vm.Plan.Fraction, 3);
        Assert.Equal("Plot and outer walls (9\")", vm.Plan.CurrentTaskText);
        Assert.Equal("Now: Plot and outer walls (9\")", vm.Plan.SubtitleText);
        Assert.True(vm.IsPlanPinned);

        backend.Release();
        await send;
        Assert.False(vm.IsPlanPinned);
        Assert.False(vm.Plan.HasTasks);
        var kept = vm.Items.OfType<PlanCardItem>().Single();
        Assert.Equal("Task Progress (1/5)", kept.Plan.Header);
        vm.Dispose();
    });

    [Fact]
    public void Provider_and_model_selection_go_to_the_backend() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        await vm.RefreshStatusesAsync();
        Assert.True(vm.Providers[0].IsReady);
        Assert.True(vm.Providers[1].IsSignedOut);
        Assert.True(vm.Providers[1].CanSignIn);
        Assert.Equal("Signed in · Claude Max", vm.Providers[0].StatusText);

        vm.SelectProviderCommand.Execute(vm.Providers[2]);
        Assert.Equal(ProviderId.Gemini, backend.SelectedProvider);
        Assert.True(vm.Providers[2].IsSelected);
        Assert.False(vm.Providers[0].IsSelected);
        Assert.Equal("Ask Gemini to draw or check the drawing...", vm.ComposerPlaceholder);

        vm.SelectedModel = vm.Models[1];
        Assert.Equal("gemini-2.5-pro", backend.GetModel(ProviderId.Gemini));
        Assert.Equal("AutoCAD 2027 · GF-plan-30x40.dwg", vm.DocumentText);

        backend.Acad = backend.Acad with { DocumentTitle = null };
        backend.SelectedProvider = ProviderId.Claude;
        await Sta.Settle();
        Assert.Equal("AutoCAD 2027 · No drawing open", vm.DocumentText);
        Assert.Same(vm.Providers[0], vm.SelectedProvider);
        vm.Dispose();
    });

    [Fact]
    public void New_chat_clears_the_conversation_and_resets_the_backend() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        backend.Script.Add(new TextDone("The drawing is empty."));
        vm.UseExampleCommand.Execute("What is in this drawing?");
        Assert.Equal("What is in this drawing?", vm.ComposerText);
        await vm.SendAsync();
        Assert.True(vm.HasItems);

        vm.NewChatCommand.Execute(null);
        Assert.Empty(vm.Items);
        Assert.Equal(1, backend.NewChats);
        Assert.True(vm.ShowWelcome);
        vm.Dispose();
    });

    [Fact]
    public void Example_prompts_are_drafting_requests() => Sta.Run(() =>
    {
        var vm = Create(FakeBackend.Create());
        Assert.Equal(
            [
                PaneRenderTests.ExamplePlanPrompt,
                "Draw the front elevation of this plan",
                "Add dimensions to the plan",
                "What is in this drawing?",
                "Learn my style from this drawing",
            ],
            vm.ExamplePrompts);
        vm.Dispose();
        return Task.CompletedTask;
    });

    [Fact]
    public void Watch_and_fast_switch_drives_the_drafting_pace_and_is_remembered() => Sta.Run(async () =>
    {
        var settingsPath = Path.Combine(_temp, "settings.json");
        var backend = FakeBackend.Create();
        var vm = Create(backend, copilotSettingsPath: settingsPath);
        Assert.True(vm.IsWatchPace);
        Assert.False(vm.IsFastPace);
        Assert.True(DraftingPace.Watch);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.FastPaceCommand.Execute(null);
        Assert.False(DraftingPace.Watch);
        Assert.True(vm.IsFastPace);
        Assert.Contains(nameof(ChatViewModel.IsWatchPace), changed);
        Assert.False(new CopilotSettingsStore(settingsPath).Load().DraftingWatch);

        // The backend rewrites settings.json from the copy it loaded at start-up when the provider or model changes.
        new CopilotSettingsStore(settingsPath).Save(new CopilotSettings { Provider = ProviderId.Codex });
        backend.SetModel(ProviderId.Claude, "opus");
        await Sta.Settle();
        var saved = new CopilotSettingsStore(settingsPath).Load();
        Assert.False(saved.DraftingWatch);
        Assert.Equal(ProviderId.Codex, saved.Provider);

        // Changed elsewhere (any thread): the switch follows and the choice is saved.
        await Task.Run(() => DraftingPace.Watch = true);
        await Sta.Settle();
        Assert.True(vm.IsWatchPace);
        Assert.True(new CopilotSettingsStore(settingsPath).Load().DraftingWatch);

        vm.IsFastPace = true;
        vm.Dispose();
        DraftingPace.Watch = true;
        Assert.False(new CopilotSettingsStore(settingsPath).Load().DraftingWatch);

        // The next panel applies the saved choice.
        var reopened = Create(FakeBackend.Create(), copilotSettingsPath: settingsPath);
        Assert.False(DraftingPace.Watch);
        Assert.True(reopened.IsFastPace);
        reopened.Dispose();
    });

    [Fact]
    public void Attachments_are_validated_and_sent() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        var vm = Create(backend);
        var file = Path.Combine(_temp, "site-plan.pdf");
        File.WriteAllText(file, "x");
        vm.AddAttachments([file, file, Path.Combine(_temp, "nope.png")]);
        Assert.Single(vm.Attachments);
        Assert.True(vm.Attachments[0].IsPdf);
        Assert.Contains("nope.png", vm.Notice, StringComparison.Ordinal);
        Assert.True(vm.CanSend);

        await vm.SendAsync();
        Assert.Equal([file], backend.SentAttachments[0]);
        Assert.Empty(vm.Attachments);
        Assert.Equal(["site-plan.pdf"], vm.Items.OfType<UserMessageItem>().Single().AttachmentNames);
        vm.Dispose();
    });

    [Fact]
    public void Setup_state_appears_only_when_no_provider_is_usable() => Sta.Run(async () =>
    {
        var vm = Create(FakeBackend.Create(claude: SignInState.SignedOut, codexInstalled: false, gemini: SignInState.SignedOut));
        Assert.False(vm.ShowSetup);
        await vm.RefreshStatusesAsync();
        Assert.True(vm.ShowSetup);
        Assert.True(vm.Providers[1].IsNotInstalled);
        Assert.False(vm.Providers[1].CanSignIn);
        vm.Dispose();

        var usable = Create(FakeBackend.Create(claude: SignInState.Unknown, codex: SignInState.SignedOut, gemini: SignInState.SignedOut));
        await usable.RefreshStatusesAsync();
        Assert.False(usable.ShowSetup);
        Assert.Equal("Ready", usable.Providers[0].StatusText);
        usable.Dispose();
    });

    [Fact]
    public void Tool_names_and_numbers_are_humanized()
    {
        Assert.Equal("Run commands", ToolNames.Humanize("run_commands"));
        Assert.Equal("Get drawing info", ToolNames.Humanize("mcp__acad__get_drawing_info"));
        Assert.Equal("Bash", ToolNames.Humanize("Bash"));
        Assert.Equal("3.4k", ToolNames.FormatTokens(3400));
        Assert.Equal("950", ToolNames.FormatTokens(950));
        Assert.Equal("2 min 5 s", ToolNames.FormatDuration(TimeSpan.FromSeconds(125)));
    }

    [Fact]
    public void Markdown_renders_headings_lists_tables_code_and_links() => Sta.Run(() =>
    {
        var document = new FlowDocument();
        MarkdownRenderer.Render(
            "# Title\n\nSome **bold** and *italic* and `code` and [docs](https://example.com).\n\n- a\n- b\n  - c\n1. one\n2. two\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```\nx = 1\n```",
            document);
        var blocks = document.Blocks.ToList();
        Assert.IsType<Paragraph>(blocks[0]);
        var paragraph = Assert.IsType<Paragraph>(blocks[1]);
        Assert.Contains(paragraph.Inlines, i => i is Bold);
        Assert.Contains(paragraph.Inlines, i => i is Italic);
        Assert.Contains(paragraph.Inlines, i => i is Hyperlink { NavigateUri.Host: "example.com" });
        var list = Assert.IsType<List>(blocks[2]);
        Assert.Equal(2, list.ListItems.Count);
        Assert.Contains(list.ListItems.LastListItem!.Blocks, b => b is List);
        var numbered = Assert.IsType<List>(blocks[3]);
        Assert.Equal(System.Windows.TextMarkerStyle.Decimal, numbered.MarkerStyle);
        Assert.Equal(2, numbered.ListItems.Count);
        Assert.IsType<Table>(blocks[4]);
        Assert.Equal("x = 1", new TextRange(blocks[5].ContentStart, blocks[5].ContentEnd).Text);
        return Task.CompletedTask;
    });

    private ChatViewModel Create(FakeBackend backend, string? settingsPath = null, string? copilotSettingsPath = null) =>
        new(
            backend,
            Dispatcher.CurrentDispatcher,
            null,
            new PanelSettingsStore(settingsPath ?? Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json")),
            new CopilotSettingsStore(copilotSettingsPath ?? Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json")));
}
