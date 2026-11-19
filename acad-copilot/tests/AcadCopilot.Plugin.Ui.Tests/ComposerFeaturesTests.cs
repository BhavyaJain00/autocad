using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Interaction;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.Tools;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// The composer's helpers: 📍 Point (click to point), the selection chip (select and ask), the task queue, the ⚡ quick
/// prompts, and the assistant switches / review card / resume card. Renders artifacts\ui\16-*.png .. 19-*.png.
/// </summary>
public sealed class ComposerFeaturesTests : IDisposable
{
    private const double Width = 430;
    private const double Height = 760;

    private readonly string _temp;

    public ComposerFeaturesTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-composer-ui-").FullName;
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
            // A file is still open; the temp folder is cleaned up later.
        }
    }

    // ------------------------------------------------------------------ pure text

    [Fact]
    public void Point_tokens_carry_feet_inches_and_drawing_units()
    {
        Assert.Equal("[point 12'-6\",30'-0\" (150,360)]", InteractionText.PointToken(new PickedPoint(150, 360, 0, "12'-6\"", "30'-0\"")));
        Assert.Equal("[point (1.25,-3)]", InteractionText.PointToken(new PickedPoint(1.2500001, -3)));
        Assert.Equal("[point (0,0)] [point (2,0)]", InteractionText.PointTokens([new PickedPoint(-0.001, 0), new PickedPoint(2, 0)]));
    }

    [Theory]
    [InlineData("", 0, "[p]", "[p]", 3)]
    [InlineData("Put the kitchen here", 20, "[p]", "Put the kitchen here [p]", 24)]
    [InlineData("Door at  please", 8, "[p]", "Door at [p] please", 11)]
    [InlineData("abc", 1, "[p]", "a [p] bc", 6)]
    [InlineData("abc", 99, "[p]", "abc [p]", 7)]
    public void Tokens_are_inserted_at_the_caret_with_spaces(string text, int caret, string token, string expected, int expectedCaret)
    {
        var (result, newCaret) = InteractionText.Insert(text, caret, token);
        Assert.Equal(expected, result);
        Assert.Equal(expectedCaret, newCaret);
    }

    [Fact]
    public void Pick_requests_are_clamped()
    {
        var normalized = new PickRequest { Prompt = "  Click   where\nthe kitchen goes:  ", MaxPoints = 99, Timeout = TimeSpan.FromHours(2) }.Normalized();
        Assert.Equal("Click where the kitchen goes", normalized.Prompt);
        Assert.Equal(PickRequest.MaxPointsLimit, normalized.MaxPoints);
        Assert.Equal(TimeSpan.FromSeconds(PickRequest.MaxTimeoutSeconds), normalized.Timeout);
        Assert.Equal("Click a point in the drawing", new PickRequest { Prompt = " ", MaxPoints = 0, Timeout = TimeSpan.Zero }.Normalized().Prompt);
        Assert.Equal(1, new PickRequest { MaxPoints = 0 }.Normalized().MaxPoints);
    }

    [Fact]
    public void The_selection_block_lists_handles_and_points_to_get_selection_beyond_the_limit()
    {
        var items = Enumerable.Range(1, 30).Select(i => new SelectedObject($"2A{i:X2}", i % 3 == 0 ? "LINE" : "LWPOLYLINE", "A-WALL", i == 1 ? "length 40'-0\"" : string.Empty)).ToList();
        var selection = new SelectionSnapshot { Count = 70, Items = items, DrawingTitle = "House.dwg" };

        Assert.Equal("70 objects selected", InteractionText.SelectionChip(selection));
        Assert.Equal("1 object selected", InteractionText.SelectionChip(selection with { Count = 1 }));
        Assert.Equal("20 LWPOLYLINE, 10 LINE, 40 more", InteractionText.TypeBreakdown(selection));
        var block = InteractionText.SelectionBlock(selection);
        Assert.StartsWith("[selected in AutoCAD: 70 objects in House.dwg (20 LWPOLYLINE, 10 LINE, 40 more)\n- 2A01 LWPOLYLINE on A-WALL, length 40'-0\"", block, StringComparison.Ordinal);
        Assert.Contains("- ... 45 more (call get_selection", block, StringComparison.Ordinal);
        Assert.EndsWith("]", block, StringComparison.Ordinal);
        Assert.Equal(InteractionText.MaxObjectsInMessage + 2, block.Split('\n').Length);
    }

    [Fact]
    public void Short_details_come_from_entity_summaries()
    {
        Assert.Equal("length 12'-0\"", InteractionText.ShortDetail(JsonNode.Parse("""{"length":{"du":144,"ftIn":"12'-0\""}}""")!.AsObject()));
        Assert.Equal("length 144", InteractionText.ShortDetail(JsonNode.Parse("""{"length":{"du":144}}""")!.AsObject()));
        Assert.Equal("block DOOR-36", InteractionText.ShortDetail(JsonNode.Parse("""{"block":"DOOR-36","position":{}}""")!.AsObject()));
        Assert.Equal("\"KITCHEN 10'x12'\"", InteractionText.ShortDetail(JsonNode.Parse("""{"text":"KITCHEN   10'x12'"}""")!.AsObject()));
        Assert.Equal("radius 1'-6\"", InteractionText.ShortDetail(JsonNode.Parse("""{"radius":{"du":18,"ftIn":"1'-6\""}}""")!.AsObject()));
        Assert.Equal(string.Empty, InteractionText.ShortDetail(JsonNode.Parse("""{"handle":"2A"}""")!.AsObject()));
        Assert.Equal(string.Empty, InteractionText.ShortDetail(null));
    }

    [Fact]
    public void The_registry_offers_pick_point_get_selection_and_select_objects()
    {
        var registry = ToolRegistry.CreateDefault();
        foreach (var name in new[] { ToolRegistry.PickPointTool, ToolRegistry.GetSelectionTool, ToolRegistry.SelectObjectsTool })
        {
            Assert.True(registry.TryGet(name, out var spec), name);
            Assert.Empty(spec.Definition.Lint());
        }

        Assert.True(registry.TryGet(ToolRegistry.GetSelectionTool, out var get));
        Assert.Equal(ToolRisk.Read, get.Definition.Risk);
        Assert.True(registry.TryGet(ToolRegistry.PickPointTool, out var pick));
        Assert.NotEqual(ToolRisk.Read, pick.Definition.Risk); // takes a turn in AutoCAD like typing
        Assert.Empty(JsonSchemaValidator.Validate(pick.Definition.InputSchema, new JsonObject { ["prompt"] = "Click where the kitchen should go", ["count"] = 2 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(pick.Definition.InputSchema, new JsonObject { ["prompt"] = "x", ["count"] = 50 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(pick.Definition.InputSchema, new JsonObject()));
        Assert.True(registry.TryGet(ToolRegistry.SelectObjectsTool, out var select));
        Assert.Empty(JsonSchemaValidator.Validate(select.Definition.InputSchema, new JsonObject { ["handles"] = new JsonArray("2A3F", "2A40") }));
    }

    // ------------------------------------------------------------------ 📍 Point

    [Fact]
    public void Point_inserts_the_clicked_coordinates_at_the_caret() => Sta.Run(async () =>
    {
        var interaction = new FakeInteraction();
        var (vm, _) = CreateChat(interaction);
        Assert.True(vm.CanPickPoint);
        vm.ComposerText = "Put the kitchen here and the stair here";
        vm.ComposerCaret = "Put the kitchen here".Length;
        interaction.NextResult = new PickResult
        {
            Status = PickStatus.Picked,
            Points = [new PickedPoint(150, 360, 0, "12'-6\"", "30'-0\"")],
        };

        var pick = vm.PickPointAsync();
        Assert.True(vm.IsPickingPoint);
        Assert.True(vm.ShowPickBanner);
        Assert.False(vm.PickPointCommand.CanExecute(null));
        interaction.Complete();
        await pick;

        Assert.Equal("Put the kitchen here [point 12'-6\",30'-0\" (150,360)] and the stair here", vm.ComposerText);
        Assert.False(vm.IsPickingPoint);
        Assert.False(vm.ShowPickBanner);
        Assert.Equal(PickSource.User, interaction.Requests.Single().Source);
        Assert.True(interaction.Requests.Single().MaxPoints > 1);
        vm.Dispose();
    });

    [Fact]
    public void A_failed_pick_explains_why_and_a_cancelled_one_changes_nothing() => Sta.Run(async () =>
    {
        var interaction = new FakeInteraction { AutoComplete = true };
        var (vm, _) = CreateChat(interaction);
        vm.ComposerText = "here";
        interaction.NextResult = PickResult.Fail("AutoCAD is busy with a command (LINE). Finish or cancel it (Esc), then try again.");
        await vm.PickPointAsync();
        Assert.True(vm.NoticeIsError);
        Assert.Contains("busy with a command", vm.Notice, StringComparison.Ordinal);
        Assert.Equal("here", vm.ComposerText);

        vm.DismissNoticeCommand.Execute(null);
        interaction.NextResult = new PickResult { Status = PickStatus.Cancelled };
        await vm.PickPointAsync();
        Assert.Null(vm.Notice);
        Assert.Equal("here", vm.ComposerText);
        vm.Dispose();
    });

    [Fact]
    public void Cancel_in_the_panel_ends_the_pick_and_the_ai_pick_shows_its_prompt() => Sta.Run(async () =>
    {
        var interaction = new FakeInteraction();
        var (vm, _) = CreateChat(interaction);
        var pick = vm.PickPointAsync();
        vm.CancelPickCommand.Execute(null);
        await pick;
        Assert.Equal(1, interaction.Cancels);
        Assert.False(vm.ShowPickBanner);

        interaction.SetActivePick(new PickInProgress("Click where the kitchen should go", PickSource.Ai, 1, 0));
        await Sta.Settle();
        Assert.True(vm.ShowPickBanner);
        Assert.Equal("The AI asks: Click where the kitchen should go", vm.PickBannerText);
        Assert.EndsWith("the point goes back to the AI. Esc cancels.", vm.PickBannerHint, StringComparison.Ordinal);
        interaction.SetActivePick(new PickInProgress("Click the room corners", PickSource.Ai, 4, 2));
        await Sta.Settle();
        Assert.Equal("The AI asks: Click the room corners (2 picked)", vm.PickBannerText);
        Assert.EndsWith("Enter when done, Esc cancels.", vm.PickBannerHint, StringComparison.Ordinal);
        interaction.SetActivePick(null);
        await Sta.Settle();
        Assert.False(vm.ShowPickBanner);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ selection chip

    [Fact]
    public void The_selection_goes_with_the_message_until_it_is_dismissed() => Sta.Run(async () =>
    {
        var interaction = new FakeInteraction();
        var (vm, backend) = CreateChat(interaction);
        Assert.False(vm.HasSelectionChip);
        interaction.Select(Selection(5));
        await Sta.Settle();
        Assert.True(vm.HasSelectionChip);
        Assert.Equal("5 objects selected", vm.SelectionChipText);
        Assert.Contains("House.dwg", vm.SelectionChipToolTip, StringComparison.Ordinal);

        vm.ComposerText = "Make these walls 9 inch";
        await vm.SendAsync();
        Assert.StartsWith("Make these walls 9 inch\n\n[selected in AutoCAD: 5 objects in House.dwg", backend.Sent[0], StringComparison.Ordinal);
        Assert.Contains("- 2A01 LWPOLYLINE on A-WALL, length 40'-0\"", backend.Sent[0], StringComparison.Ordinal);
        var bubble = vm.Items.OfType<UserMessageItem>().Single();
        Assert.Equal("Make these walls 9 inch", bubble.Text);
        Assert.Equal("5 objects selected", bubble.SelectionNote);
        Assert.Equal("Make these walls 9 inch", vm.Title);

        vm.DismissSelectionCommand.Execute(null);
        Assert.False(vm.HasSelectionChip);
        vm.ComposerText = "And now?";
        await vm.SendAsync();
        Assert.Equal("And now?", backend.Sent[1]);

        interaction.Select(Selection(2));
        await Sta.Settle();
        Assert.True(vm.HasSelectionChip);
        interaction.Select(null);
        await Sta.Settle();
        Assert.False(vm.HasSelectionChip);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ queue

    [Fact]
    public void Queued_requests_run_one_after_another() => Sta.Run(async () =>
    {
        var (vm, backend) = CreateChat(null);
        backend.HoldOpen = true;
        vm.ComposerText = "Draw the ground floor";
        var first = vm.SendAsync();
        await Sta.Settle();
        Assert.True(vm.IsBusy);
        Assert.True(vm.AddToQueueCommand.CanExecute(null) is false);

        vm.ComposerText = "Add dimensions";
        Assert.True(vm.AddToQueue());
        vm.ComposerText = "Add room names";
        Assert.True(vm.AddToQueue());
        Assert.Equal(string.Empty, vm.ComposerText);
        Assert.Equal("Queue (2)", vm.QueueHeader);
        Assert.Equal("Next starts when this answer is done.", vm.QueueStatusText);
        Assert.Equal([1, 2], vm.Queue.Select(q => q.Position));

        // Reorder: room names first.
        vm.MoveQueued(vm.Queue[1], 0);
        Assert.Equal(["Add room names", "Add dimensions"], vm.Queue.Select(q => q.Title));
        Assert.Equal([1, 2], vm.Queue.Select(q => q.Position));

        backend.Release();
        await first;
        await Sta.WaitUntil(() => backend.Sent.Count == 2, "the first queued request to start");
        Assert.Equal("Add room names", backend.Sent[1]);
        Assert.True(vm.Items.OfType<UserMessageItem>().Last().FromQueue);
        Assert.Equal("Queue (1)", vm.QueueHeader);

        await Sta.WaitUntil(() => vm.IsBusy, "the queued turn to run");
        backend.Release();
        await Sta.WaitUntil(() => backend.Sent.Count == 3, "the second queued request to start");
        Assert.Equal("Add dimensions", backend.Sent[2]);
        Assert.False(vm.HasQueue);
        await Sta.WaitUntil(() => vm.IsBusy, "the last turn to run");
        backend.Release();
        await Sta.WaitUntil(() => !vm.IsBusy, "the last turn to end");
        vm.Dispose();
    });

    [Fact]
    public void Stop_pauses_the_queue_and_resume_continues_it() => Sta.Run(async () =>
    {
        var (vm, backend) = CreateChat(null);
        backend.HoldOpen = true;
        vm.ComposerText = "Draw the first floor";
        var first = vm.SendAsync();
        await Sta.Settle();
        vm.ComposerText = "Then the front elevation";
        vm.AddToQueue();

        vm.Stop();
        await first;
        await Sta.Settle();
        Assert.True(vm.IsQueuePaused);
        Assert.Equal("Paused: you pressed Stop. Resume to continue.", vm.QueueStatusText);
        Assert.Single(backend.Sent);

        vm.ResumeQueueCommand.Execute(null);
        await Sta.WaitUntil(() => backend.Sent.Count == 2, "the queue to continue");
        Assert.Equal("Then the front elevation", backend.Sent[1]);
        await Sta.WaitUntil(() => vm.IsBusy, "the queued turn to run");
        backend.Release();
        await Sta.WaitUntil(() => !vm.IsBusy, "the queued turn to end");

        // Idle: Add to queue starts at once; Remove and Clear.
        backend.HoldOpen = false;
        vm.ComposerText = "What is in this drawing?";
        vm.AddToQueue();
        await Sta.WaitUntil(() => backend.Sent.Count == 3, "an idle queue to start at once");
        await Sta.WaitUntil(() => !vm.IsBusy, "that turn to end");
        vm.Dispose();
    });

    [Fact]
    public void A_failed_turn_pauses_the_queue() => Sta.Run(async () =>
    {
        var (vm, backend) = CreateChat(null);
        backend.Script.Add(new TurnFailed("Claude crashed", AgentFailureKind.Other));
        vm.ComposerText = "one";
        var queued = new QueuedRequest("two", "two", []);
        vm.Queue.Add(queued);
        await vm.SendAsync();
        await Sta.Settle();
        Assert.True(vm.IsQueuePaused);
        Assert.Contains("the last request failed", vm.QueueStatusText, StringComparison.Ordinal);
        vm.RemoveQueuedCommand.Execute(queued);
        Assert.False(vm.HasQueue);
        vm.Dispose();
    });

    // ------------------------------------------------------------------ quick prompts

    [Fact]
    public void Quick_prompts_fill_the_message_and_saved_ones_persist() => Sta.Run(async () =>
    {
        var path = Path.Combine(_temp, "quick-panel.json");
        var store = new PanelSettingsStore(path);
        var prompts = new QuickPromptsViewModel(store);
        var (vm, _) = CreateChat(null, prompts, store);

        Assert.Equal(["30x40 G+1 standard plan", "Add dimensions", "Review this plan", "Learn my style from this drawing", "Plot to PDF"], prompts.BuiltIns.Select(b => b.Title));
        Assert.Equal(ChatViewModel.ReviewPrompt, prompts.BuiltIns[2].Text);
        vm.ToggleQuickMenuCommand.Execute(null);
        Assert.True(vm.IsQuickMenuOpen);
        vm.UseQuickPromptCommand.Execute(prompts.BuiltIns[1]);
        Assert.False(vm.IsQuickMenuOpen);
        Assert.StartsWith("Add dimensions to this plan", vm.ComposerText, StringComparison.Ordinal);

        Assert.True(vm.SaveQuickPromptCommand.CanExecute(null));
        vm.ComposerText = "Draw a 3 BHK on a 40' x 60' plot, east facing, with a car porch";
        vm.SaveQuickPromptCommand.Execute(null);
        Assert.True(vm.IsQuickMenuOpen);
        Assert.True(prompts.IsEditorOpen);
        Assert.Equal("Save as quick prompt", prompts.EditorHeader);
        prompts.EditTitle = "3 BHK 40x60";
        Assert.True(prompts.CommitEdit());
        Assert.False(prompts.IsEditorOpen);
        Assert.Equal("3 BHK 40x60", prompts.Saved.Single().Title);

        // Persisted in panel.json, next to the other panel settings.
        var reloaded = new QuickPromptsViewModel(new PanelSettingsStore(path));
        Assert.Equal("Draw a 3 BHK on a 40' x 60' plot, east facing, with a car porch", reloaded.Saved.Single().Text);

        prompts.BeginEdit(prompts.Saved[0]);
        Assert.Equal("Edit quick prompt", prompts.EditorHeader);
        prompts.EditText = " ";
        Assert.False(prompts.CommitEdit());
        Assert.True(prompts.HasEditError);
        prompts.EditText = "Draw a 3 BHK on a 40' x 60' plot";
        Assert.True(prompts.CommitEdit());
        Assert.Equal("Draw a 3 BHK on a 40' x 60' plot", new QuickPromptsViewModel(new PanelSettingsStore(path)).Saved.Single().Text);

        prompts.DeleteCommand.Execute(prompts.Saved[0]);
        Assert.False(prompts.HasSaved);
        Assert.Empty(new QuickPromptsViewModel(new PanelSettingsStore(path)).Saved);
        Assert.False(prompts.DeleteCommand.CanExecute(prompts.BuiltIns[0]));

        // Changing another panel setting keeps the saved prompts.
        prompts.BeginSave("Add a site plan");
        prompts.CommitEdit();
        vm.ApprovalMode = ApprovalMode.AutoApprove;
        Assert.Single(new QuickPromptsViewModel(new PanelSettingsStore(path)).Saved);
        await Task.CompletedTask;
        vm.Dispose();
    });

    // ------------------------------------------------------------------ agent B / agent A cards and switches

    [Fact]
    public void The_ai_behaviour_switches_change_the_backend_features() => Sta.Run(async () =>
    {
        var backend = new AssistBackend();
        var vm = new ChatViewModel(backend, Dispatcher.CurrentDispatcher, null, NewPanelStore(), NewSettingsStore());
        Assert.True(backend.EmitNoticesAsText is false);
        Assert.True(vm.HasFeatures);
        Assert.True(vm.AutoSwitchProvider);
        Assert.True(vm.AskBeforeDrawing);
        Assert.False(vm.AutoReview);
        Assert.False(vm.TutorMode);

        vm.TutorMode = true;
        vm.AutoReview = true;
        vm.AskBeforeDrawing = false;
        vm.AutoSwitchProvider = false;
        Assert.Equal(new CopilotFeatures { AutoSwitch = false, AskBeforeDrawing = false, AutoReview = true, TutorMode = true }.TurnOptions, backend.Features.TurnOptions);
        Assert.False(backend.Features.AutoSwitch);
        Assert.True(backend.Features.AutoReview);
        Assert.Equal("Takes over in this order: Claude, ChatGPT, Gemini (only AIs you are signed in to).", vm.ProviderOrderText);
        await Task.CompletedTask;
        vm.Dispose();
    });

    [Fact]
    public void A_review_card_fixes_its_findings_and_a_switch_is_shown() => Sta.Run(async () =>
    {
        var backend = new AssistBackend();
        var report = ReviewReport.Parse(ProviderId.Codex, "ChatGPT", "SUMMARY: Usable plan, two fixes.\n- [major] Bedroom 2 door swings into the passage -> hinge it on the east jamb\n- [minor] Kitchen label overlaps the platform line");
        backend.Inner.Script.Add(new ProviderSwitched(ProviderId.Claude, ProviderId.Codex, SwitchReason.Limit, "Claude reached its usage limit, so ChatGPT continues this job.", "usage limit"));
        backend.Inner.Script.Add(new ReviewCompleted(report));
        var vm = new ChatViewModel(backend, Dispatcher.CurrentDispatcher, null, NewPanelStore(), NewSettingsStore());
        vm.ComposerText = ChatViewModel.ReviewPrompt;
        await vm.SendAsync();

        Assert.Equal("Claude reached its usage limit, so ChatGPT continues this job.", vm.Items.OfType<SwitchNoticeItem>().Single().Message);
        var card = vm.Items.OfType<ReviewCardItem>().Single();
        Assert.Equal("Second opinion · ChatGPT", card.Title);
        Assert.Equal("1 major · 1 minor", card.CountText);
        Assert.True(card.Findings[0].IsMajor);

        backend.Inner.Script.Clear();
        card.FixCommand.Execute(null);
        await Sta.WaitUntil(() => backend.Inner.Sent.Count == 2, "Fix these to be sent");
        Assert.Equal(report.FixPrompt, backend.Inner.Sent[1]);
        Assert.Equal("Fix these", vm.Items.OfType<UserMessageItem>().Last().Text);
        Assert.True(card.IsFixRequested);
        Assert.False(card.FixCommand.CanExecute(null));
        await Sta.WaitUntil(() => !vm.IsBusy, "the fix turn to end");
        vm.Dispose();
    });

    [Fact]
    public void A_resume_card_continues_or_forgets_the_job() => Sta.Run(async () =>
    {
        var (vm, backend) = CreateChat(null);
        var dismissed = new List<string>();
        vm.ShowResumeOffer(SampleResume("s1"), info => dismissed.Add(info.Session));
        vm.ShowResumeOffer(SampleResume("s1"), info => dismissed.Add(info.Session)); // same job: one card
        var card = vm.Items.OfType<ResumeCardItem>().Single();
        Assert.Equal("Resume previous job", card.Title);
        Assert.Equal("G+1 house · House-G1.dwg", card.Subtitle);
        Assert.Equal("2 done · 2 left · 'Inner walls' stopped at step 12 of 25", card.ProgressText);

        card.ResumeCommand.Execute(null);
        await Sta.WaitUntil(() => backend.Sent.Count == 1, "the resume message");
        Assert.StartsWith("Resume the previous job that did not finish. Call get_resume_info first", backend.Sent[0], StringComparison.Ordinal);
        Assert.Equal("Resume previous job: G+1 house", vm.Items.OfType<UserMessageItem>().Single().Text);
        Assert.False(card.IsOpen);

        vm.ShowResumeOffer(SampleResume("s2"), info => dismissed.Add(info.Session));
        var second = vm.Items.OfType<ResumeCardItem>().Last();
        second.DismissCommand.Execute(null);
        Assert.Equal(["s2"], dismissed);
        Assert.Equal("Dismissed", second.Outcome);
        await Sta.WaitUntil(() => !vm.IsBusy, "the resume turn to end");
        vm.Dispose();
    });

    // ------------------------------------------------------------------ renders

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Renders_point_selection_queue_and_quick_prompts(PanelTheme theme) => Sta.Run(async () =>
    {
        var interaction = new FakeInteraction();
        var backend = FakeBackend.Create(codex: SignInState.SignedIn);
        var pane = await CreatePaneAsync(backend, theme, interaction);
        var vm = pane.ViewModel;

        // 16: the AI asks for a click; a selection is waiting to go with the next message.
        backend.Script.Add(new TextDone("Where should the kitchen go? I will ask you to click in the drawing."));
        backend.HoldOpen = true;
        vm.ComposerText = "Add a kitchen to the ground floor";
        var turn = vm.SendAsync();
        await Sta.Settle();
        interaction.Select(Selection(5));
        interaction.SetActivePick(new PickInProgress("Click where the kitchen should go", PickSource.Ai, 1, 0));
        await Sta.Settle();
        vm.ComposerText = "Make it 10' x 12' [point 12'-6\",30'-0\" (150,360)]";
        await Sta.Settle();
        Save(pane, "16-point-and-selection", theme);

        // 17: two requests wait in the queue while the AI works.
        interaction.SetActivePick(null);
        vm.ComposerText = "Add dimensions to the plan";
        vm.AddToQueue();
        vm.ComposerText = "Draw the front elevation of this plan";
        vm.AddToQueue();
        vm.ComposerText = "Then plot it to PDF";
        await Sta.Settle();
        Save(pane, "17-task-queue", theme);

        // 18: the quick-prompt menu, then its editor.
        vm.Stop();
        await turn;
        vm.ClearQueueCommand.Execute(null);
        interaction.Select(null);
        pane.ViewModel.QuickPrompts!.BeginSave("Draw a 3 BHK on a 40' x 60' plot, east facing");
        pane.ViewModel.QuickPrompts.CommitEdit();
        vm.ComposerText = "Add a utility area behind the kitchen";
        vm.IsQuickMenuOpen = true;
        await Sta.Settle();
        Save(pane, "18-quick-prompts", theme);
        vm.SaveQuickPromptCommand.Execute(null);
        await Sta.Settle();
        Save(pane, "18-quick-prompts-save", theme);
        vm.IsQuickMenuOpen = false;
        pane.Shell.Dispose();
    });

    [Fact]
    public void Renders_the_composer_on_a_narrow_panel() => Sta.Run(async () =>
    {
        var interaction = new FakeInteraction();
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, PanelTheme.Light, interaction);
        interaction.Select(Selection(1));
        backend.HoldOpen = true;
        pane.ViewModel.ComposerText = "Draw the site plan";
        var turn = pane.ViewModel.SendAsync();
        await Sta.Settle();
        pane.ViewModel.ComposerText = "Then the section";
        pane.ViewModel.AddToQueue();
        pane.ViewModel.ComposerText = "Then the stair plan";
        await Sta.Settle();
        Save(pane, "17-task-queue-narrow", PanelTheme.Light, 300);
        backend.Release();
        await turn;
        pane.Shell.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Renders_the_review_and_resume_cards_and_the_ai_behaviour_settings(PanelTheme theme) => Sta.Run(async () =>
    {
        var backend = new AssistBackend();
        backend.Inner.Script.Add(new ProviderSwitched(ProviderId.Claude, ProviderId.Codex, SwitchReason.Limit, "Claude reached its usage limit, so ChatGPT continues this job.", "usage limit"));
        backend.Inner.Script.Add(new ReviewCompleted(ReviewReport.Parse(ProviderId.Codex, "ChatGPT",
            "SUMMARY: Usable plan; fix the bedroom door and two labels.\n- [major] Bedroom 2 door swings into the passage -> hinge it on the east jamb\n- [minor] Kitchen label overlaps the platform line\n- [minor] Overall dimension on the south side is missing")));
        var pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = theme,
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
            EnableVoice = false,
            OpenFolder = _ => { },
            ResumableJobs = () => [SampleResume("s9")],
        });
        await pane.ViewModel.RefreshStatusesAsync();
        pane.OfferResumableJobs();
        pane.ViewModel.ComposerText = ChatViewModel.ReviewPrompt;
        await pane.ViewModel.SendAsync();
        await Sta.Settle();
        Save(pane, "19-review-and-resume-cards", theme, Width, 900);

        pane.ViewModel.IsSettingsOpen = true;
        pane.ViewModel.TutorMode = true;
        await Sta.Settle();
        Save(pane, "19-ai-behaviour-settings", theme, Width, 900);
        pane.Shell.Dispose();
    });

    // ------------------------------------------------------------------ helpers

    private static SelectionSnapshot Selection(int count) => new()
    {
        Count = count,
        DrawingTitle = "House.dwg",
        Items = [.. Enumerable.Range(1, count).Select(i => new SelectedObject($"2A{i:X2}", i == 1 ? "LWPOLYLINE" : "LINE", "A-WALL", i == 1 ? "length 40'-0\"" : "length 12'-0\""))],
    };

    private static ResumeInfo SampleResume(string session) => new()
    {
        Session = session,
        Reason = ResumeReason.AutoCadStopped,
        DrawingTitle = "House-G1.dwg",
        PlanTitle = "G+1 house",
        CompletedTasks = ["Setup", "Outer walls"],
        RemainingTasks = ["Inner walls (in progress)", "Doors and windows"],
        Interrupted = new InterruptedRun { Title = "Inner walls", State = "interrupted", StepsTotal = 25, CompletedSteps = [.. Enumerable.Range(1, 12)], RemainingSteps = ["13 LINE", "14 OFFSET"] },
        UpdatedUtc = new DateTime(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc),
    };

    private PanelSettingsStore NewPanelStore() => new(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"));

    private CopilotSettingsStore NewSettingsStore() => new(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"));

    private (ChatViewModel Vm, FakeBackend Backend) CreateChat(IDrawingInteraction? interaction, QuickPromptsViewModel? prompts = null, PanelSettingsStore? store = null)
    {
        var backend = FakeBackend.Create();
        var vm = new ChatViewModel(backend, Dispatcher.CurrentDispatcher, null, store ?? NewPanelStore(), NewSettingsStore())
        {
            Interaction = interaction,
            QuickPrompts = prompts,
        };
        return (vm, backend);
    }

    private async Task<CopilotPane> CreatePaneAsync(FakeBackend backend, PanelTheme theme, IDrawingInteraction interaction)
    {
        var pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = theme,
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
            EnableVoice = false,
            OpenFolder = _ => { },
            Interaction = interaction,
        });
        await pane.ViewModel.RefreshStatusesAsync();
        return pane;
    }

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width = Width, double height = Height)
    {
        var path = Snapshot.Render(pane, width, height, $"{name}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    /// <summary>A drawing link the test drives: selection, active pick and the next pick's result.</summary>
    private sealed class FakeInteraction : IDrawingInteraction
    {
        private TaskCompletionSource<PickResult>? _pending;

        public event EventHandler? SelectionChanged;

        public event EventHandler? PickStateChanged;

        public SelectionSnapshot? CurrentSelection { get; private set; }

        public PickInProgress? ActivePick { get; private set; }

        public PickResult NextResult { get; set; } = new() { Status = PickStatus.Cancelled };

        public bool AutoComplete { get; init; }

        public List<PickRequest> Requests { get; } = [];

        public int Cancels { get; private set; }

        public Task<PickResult> PickPointsAsync(PickRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (AutoComplete)
            {
                return Task.FromResult(NextResult);
            }

            _pending = new TaskCompletionSource<PickResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => _pending.TrySetResult(new PickResult { Status = PickStatus.Cancelled }));
            return _pending.Task;
        }

        public void Complete() => _pending?.TrySetResult(NextResult);

        public void CancelPick()
        {
            Cancels++;
            _pending?.TrySetResult(new PickResult { Status = PickStatus.Cancelled });
        }

        public void Select(SelectionSnapshot? selection)
        {
            CurrentSelection = selection;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetActivePick(PickInProgress? pick)
        {
            ActivePick = pick;
            PickStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A <see cref="FakeBackend"/> with agent B's assistant switches.</summary>
    private sealed class AssistBackend : ICopilotBackend, ICopilotAssist
    {
        public FakeBackend Inner { get; } = FakeBackend.Create(codex: SignInState.SignedIn);

        public CopilotFeatures Features { get; set; } = new();

        public bool EmitNoticesAsText { get; set; } = true;

        public IReadOnlyList<IAgentRunner> Runners => Inner.Runners;

        public ProviderId SelectedProvider
        {
            get => Inner.SelectedProvider;
            set => Inner.SelectedProvider = value;
        }

        public bool IsBusy => Inner.IsBusy;

        public AcadSessionInfo Acad => Inner.Acad;

        public event EventHandler? StateChanged
        {
            add => Inner.StateChanged += value;
            remove => Inner.StateChanged -= value;
        }

        public event EventHandler? ChatReset
        {
            add => Inner.ChatReset += value;
            remove => Inner.ChatReset -= value;
        }

        public string? GetModel(ProviderId provider) => Inner.GetModel(provider);

        public void SetModel(ProviderId provider, string? modelId) => Inner.SetModel(provider, modelId);

        public Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default) => Inner.GetStatusAsync(provider, cancellationToken);

        public IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, CancellationToken cancellationToken) =>
            Inner.SendAsync(text, attachments, cancellationToken);

        public void NewChat() => Inner.NewChat();

        public bool SignIn(ProviderId provider) => Inner.SignIn(provider);

        public void AttachUi(ICopilotUi ui) => Inner.AttachUi(ui);

        public async IAsyncEnumerable<AgentEvent> ReviewAsync(string? focus, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new TurnCompleted("end_turn");
        }
    }
}
