using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Interaction;
using AcadCopilot.Plugin.QuickAsk;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// Quick Ask (the floating AI in the drawing area): shortcut and settings, the message text, placement, the controller
/// over the real chat tabs (<see cref="CopilotBackend"/> with scripted CLIs and a fake AutoCAD host), the status bubble,
/// the box, and off-screen renders artifacts\ui\21-quick-ask-*.png. No window is ever shown.
/// </summary>
public sealed class QuickAskTests : IDisposable
{
    private static readonly PickedPoint Cursor = new(150, 360, 0, "12'-6\"", "30'-0\"");

    private readonly string _temp;
    private DrawingRef? _activeDrawing = new("dwg-1", "GF-plan-30x40.dwg");

    public QuickAskTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-quickask-").FullName;
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

    // ------------------------------------------------------------------ shortcut and settings

    [Theory]
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space", 0x20)]
    [InlineData(" ctrl + alt + q ", "Ctrl+Alt+Q", 0x51)]
    [InlineData("Control-Shift-F2", "Ctrl+Shift+F2", 0x71)]
    [InlineData("alt+shift+1", "Shift+Alt+1", 0x31)]
    [InlineData("Shift+Alt+A", "Shift+Alt+A", 0x41)]
    public void Shortcuts_parse_and_print(string text, string expected, int key)
    {
        Assert.True(QuickAskShortcut.TryParse(text, out var shortcut, out var error), error);
        Assert.Equal(expected, shortcut.ToString());
        Assert.Equal(key, shortcut.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("Shift+Q")]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Q+W")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+F13")]
    public void Unusable_shortcuts_are_refused_with_a_reason(string text)
    {
        Assert.False(QuickAskShortcut.TryParse(text, out var shortcut, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(QuickAskShortcut.Default, shortcut);
    }

    [Fact]
    public void The_shortcut_matches_only_its_exact_modifiers()
    {
        var shortcut = QuickAskShortcut.Default;
        Assert.True(shortcut.Matches(0x20, ctrl: true, shift: true, alt: false));
        Assert.False(shortcut.Matches(0x20, ctrl: true, shift: false, alt: false)); // the panel's Ctrl+Space (mic)
        Assert.False(shortcut.Matches(0x20, ctrl: true, shift: true, alt: true));
        Assert.False(shortcut.Matches(0x41, ctrl: true, shift: true, alt: false));
    }

    [Fact]
    public void Options_default_on_and_save_changes_at_once()
    {
        var path = Path.Combine(_temp, "quickask.json");
        var options = new QuickAskOptions(new QuickAskSettingsStore(path));
        Assert.True(options.ShowButton);
        Assert.Equal("Ctrl+Shift+Space", options.ShortcutText);
        Assert.Equal("Ctrl+Shift+Space, or type AI on the command line", options.ShortcutHint);

        options.ShowButton = false;
        options.ShortcutText = "Ctrl+Alt+Q";
        Assert.False(options.HasShortcutError);
        var reloaded = new QuickAskOptions(new QuickAskSettingsStore(path));
        Assert.False(reloaded.ShowButton);
        Assert.Equal("Ctrl+Alt+Q", reloaded.Shortcut.ToString());

        // A bad shortcut keeps the last good one and explains.
        options.ShortcutText = "Ctrl+V";
        Assert.True(options.HasShortcutError);
        Assert.Contains("already used", options.ShortcutError, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Alt+Q", options.Shortcut.ToString());
        Assert.Equal("Ctrl+Alt+Q", new QuickAskSettingsStore(path).Load().Shortcut);
        options.ResetShortcut();
        Assert.Equal(QuickAskShortcut.Default, options.Shortcut);
        Assert.False(options.HasShortcutError);

        // Unreadable file: defaults.
        File.WriteAllText(path, "{ not json");
        Assert.True(new QuickAskSettingsStore(path).Load().ShowButton);
    }

    // ------------------------------------------------------------------ message text

    [Fact]
    public void The_message_carries_the_cursor_point_and_the_selection()
    {
        var selection = Selection(3);
        var message = QuickAskText.Compose("  make the kitchen   10 by 20 ", Cursor, QuickAskPointSource.Cursor, selection);
        Assert.Equal("make the kitchen 10 by 20 [point 12'-6\",30'-0\" (150,360)]", message.DisplayText);
        Assert.StartsWith("make the kitchen 10 by 20\n\n" + QuickAskText.Origin + "\n", message.SentText, StringComparison.Ordinal);
        Assert.Contains("The user's cursor was here in the drawing when they asked", message.SentText, StringComparison.Ordinal);
        Assert.Contains("[point 12'-6\",30'-0\" (150,360)]", message.SentText, StringComparison.Ordinal);
        Assert.Contains("[selected in AutoCAD: 3 objects in GF-plan-30x40.dwg", message.SentText, StringComparison.Ordinal);
        Assert.Equal("3 objects selected", message.SelectionNote);
        Assert.Equal("kitchen 10'x20'", message.Subject);

        var picked = QuickAskText.Compose("door here", new PickedPoint(10, 20), QuickAskPointSource.Picked, null);
        Assert.Equal("door here [point (10,20)]", picked.DisplayText);
        Assert.Contains("The user clicked this point for the request: [point (10,20)].", picked.SentText, StringComparison.Ordinal);
        Assert.Null(picked.SelectionNote);

        var plain = QuickAskText.Compose("add dimensions", Cursor, QuickAskPointSource.None, new SelectionSnapshot());
        Assert.Equal("add dimensions", plain.DisplayText);
        Assert.DoesNotContain("[point", plain.SentText, StringComparison.Ordinal);
        Assert.DoesNotContain("[selected", plain.SentText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("make the kitchen 10 by 20", "kitchen 10'x20'")]
    [InlineData("Please draw a 4 ft wide staircase please", "4' wide staircase")]
    [InlineData("can you add the bedroom 12x14 here", "bedroom 12'x14' here")]
    [InlineData("Draw a toilet 5' x 8'", "toilet 5'x8'")]
    [InlineData("put a 3 x 2.5 m window", "3x2.5 m window")]
    [InlineData("door", "door")]
    [InlineData("   ", "your request")]
    [InlineData("draw the complete ground floor plan with parking, living, kitchen and two bedrooms", "complete ground floor plan…")]
    public void The_subject_is_the_job_in_a_few_words(string text, string expected) =>
        Assert.Equal(expected, QuickAskText.Subject(text));

    [Fact]
    public void Status_lines_prefer_the_plan_task_then_the_activity()
    {
        Assert.Equal("Draw kitchen walls...", QuickAskText.Working("kitchen 10'x20'", "Draw kitchen walls", "Thinking..."));
        Assert.Equal("Drawing: Kitchen walls...", QuickAskText.Working("kitchen 10'x20'", null, "Typing AutoCAD commands: Kitchen walls..."));
        Assert.Equal("Looking at the drawing...", QuickAskText.Working("kitchen 10'x20'", " ", "Looking at the drawing..."));
        Assert.Equal("Working on kitchen 10'x20'...", QuickAskText.Working("kitchen 10'x20'", null, "Thinking..."));
        Assert.Equal("Working on kitchen 10'x20'...", QuickAskText.Working("kitchen 10'x20'", null, null));
        Assert.Equal("Done - kitchen 10'x20'", QuickAskText.Done("kitchen 10'x20'"));
        Assert.Equal("Could not finish: Usage limit reached: wait", QuickAskText.Failed("Usage limit reached: wait\nmore"));
        Assert.Equal("Something went wrong", QuickAskText.Failed(null));
        Assert.Equal("Next in the chat's queue...", QuickAskText.Queued(1));
        Assert.Equal("Waiting in the chat's queue (3)...", QuickAskText.Queued(3));
        Assert.True(QuickAskText.Working("x", new string('a', 200), null).Length <= QuickAskText.MaxStatusLength + 1);
    }

    // ------------------------------------------------------------------ placement

    [Fact]
    public void The_button_sits_left_of_the_viewcube_and_the_box_stays_in_the_drawing()
    {
        var area = new ScreenRect(100, 200, 1700, 1100);
        Assert.Equal((1700 - 150 - 60, 202), QuickAskPlacement.Button(area, 60, 1.0));
        Assert.Equal((1700 - 225 - 60, 203), QuickAskPlacement.Button(area, 60, 1.5));

        // Below-right of the cursor.
        Assert.Equal((506, 606), QuickAskPlacement.Box(area, 500, 600, 460, 120, 1.0));

        // Near the right / bottom edges it flips to the left / above.
        Assert.Equal((1600 - 460 - 6, 1050 - 120 - 6), QuickAskPlacement.Box(area, 1600, 1050, 460, 120, 1.0));

        // Never outside the drawing area.
        var (x, y) = QuickAskPlacement.Box(area, 105, 205, 460, 120, 1.0);
        Assert.True(x >= area.Left && y >= area.Top);
        Assert.Equal((1300, 200), QuickAskPlacement.Box(new ScreenRect(1300, 200, 1700, 300), 1310, 210, 460, 120, 1.0));

        // From the button: right-aligned under it.
        Assert.Equal((1500 - 460, 252), QuickAskPlacement.BoxUnderButton(area, new ScreenRect(1440, 200, 1500, 260), 460, 120));
    }

    // ------------------------------------------------------------------ controller over the real chat tabs

    [Fact]
    public void A_quick_ask_goes_to_the_chat_of_the_active_drawing_without_showing_the_panel() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        var shell = CreateShell(backend);
        var host = new FakeHost(shell) { Cursor = Cursor, Current = Selection(2) };
        using var controller = CreateController(host);
        var tab = shell.SelectedTab;
        tab.ComposerText = "half-typed message";

        var box = controller.OpenBox(QuickAskOpenReason.Shortcut)!;
        Assert.True(controller.IsBoxOpen);
        Assert.Equal(QuickAskPointSource.Cursor, box.PointSource);
        Assert.Equal("Cursor 12'-6\", 30'-0\"", box.PointChipText);
        Assert.Equal("2 objects selected", box.SelectionChipText);
        Assert.Contains("goes to the chat of GF-plan-30x40.dwg", box.HintText, StringComparison.Ordinal);
        Assert.False(box.CanSend);
        box.Text = "make the kitchen 10 by 20";
        box.SendCommand.Execute(null);

        Assert.False(controller.IsBoxOpen);
        Assert.Equal(1, host.FocusReturns);
        await Sta.WaitUntil(() => claude.Requests.Count == 1, "the CLI got the request");
        var request = claude.Requests.Single();
        Assert.StartsWith("make the kitchen 10 by 20\n\n" + QuickAskText.Origin, request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[point 12'-6\",30'-0\" (150,360)]", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[selected in AutoCAD: 2 objects", request.Prompt, StringComparison.Ordinal);

        // A normal message in that tab; the tab's own message box is untouched; nothing opened the panel.
        var shown = Assert.IsType<UserMessageItem>(tab.Items[0]);
        Assert.Equal("make the kitchen 10 by 20 [point 12'-6\",30'-0\" (150,360)]", shown.Text);
        Assert.Equal("2 objects selected", shown.SelectionNote);
        Assert.Equal("half-typed message", tab.ComposerText);
        Assert.Empty(host.ShownChats);
        Assert.Same(tab, controller.Status.Chat);
        Assert.Equal(QuickAskState.Working, controller.Status.State);
        Assert.Equal("Working on kitchen 10'x20'...", controller.Status.Text);

        claude.Release(request.Prompt);
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "done");
        Assert.Equal("Done - kitchen 10'x20'", controller.Status.Text);
        Assert.False(controller.Status.CanUndo); // nothing was drawn
        shell.Dispose();
    });

    [Fact]
    public void A_drawing_without_a_chat_gets_a_background_tab() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        claude.Hold = false;
        var shell = CreateShell(backend);
        var first = shell.SelectedTab;
        _activeDrawing = new DrawingRef("dwg-2", "FF-plan-30x40.dwg");
        var host = new FakeHost(shell) { DrawingId = "dwg-2", DrawingTitle = "FF-plan-30x40.dwg" };
        using var controller = CreateController(host);

        Assert.True(controller.SubmitText("add the staircase"));
        Assert.Equal(2, shell.Tabs.Count);
        var created = shell.Tabs[1];
        Assert.Equal("FF-plan-30x40.dwg", created.DrawingTitle);
        Assert.Same(first, shell.SelectedTab); // the panel keeps showing its tab
        Assert.False(created.IsTabSelected);
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "done");
        Assert.Equal("add the staircase", Assert.IsType<UserMessageItem>(created.Items[0]).Text);
        Assert.Empty(first.Items);

        // The next Quick Ask on that drawing reuses the tab.
        Assert.True(controller.SubmitText("add dimensions"));
        Assert.Equal(2, shell.Tabs.Count);
        await Sta.WaitUntil(() => created.Items.OfType<UserMessageItem>().Count() == 2, "second request in the same tab");
        shell.Dispose();
    });

    [Fact]
    public void A_busy_chat_queues_the_request_and_the_bubble_follows_it() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        var shell = CreateShell(backend);
        var tab = shell.SelectedTab;
        tab.ComposerText = "Draw the ground floor";
        var first = tab.SendAsync();
        await Sta.WaitUntil(() => tab.IsBusy && claude.Requests.Count == 1, "first turn running");

        var host = new FakeHost(shell);
        using var controller = CreateController(host);
        Assert.True(controller.SubmitText("make the kitchen 10 by 20"));
        Assert.Equal(QuickAskState.Queued, controller.Status.State);
        Assert.Equal("Next in the chat's queue...", controller.Status.Text);
        Assert.Single(tab.Queue);

        claude.Release("Draw the ground floor");
        await first;
        await Sta.WaitUntil(() => claude.Requests.Count == 2, "queued request started");
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Working, "working");
        claude.Release(claude.Requests.Last().Prompt);
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "done");
        Assert.Equal("Done - kitchen 10'x20'", controller.Status.Text);
        shell.Dispose();
    });

    [Fact]
    public void Drawing_steps_can_be_undone_from_the_bubble() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        claude.Before = _ =>
        [
            new ToolCallStarted("c1", "run_commands", "Kitchen walls", "acad"),
            new ToolCallFinished("c1", true, "ok"),
            new ToolCallStarted("c2", "capture_view", "", "acad"),
            new ToolCallFinished("c2", true, "ok"),
            new ToolCallStarted("c3", "run_commands", "Kitchen door", "acad"),
            new ToolCallFinished("c3", true, "ok"),
            new ToolCallStarted("c4", "run_commands", "Kitchen text", "acad"),
            new ToolCallFinished("c4", false, "failed", "AutoCAD rejected it"),
        ];
        claude.Hold = false;
        var shell = CreateShell(backend);
        var host = new FakeHost(shell);
        using var controller = CreateController(host);

        Assert.True(controller.SubmitText("make the kitchen 10 by 20"));
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "done");
        Assert.Equal(2, controller.Status.UndoCalls);
        Assert.True(controller.Status.CanUndo);
        Assert.True(controller.Status.ShowDismiss);

        controller.Status.UndoCommand.Execute(null);
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Undone, "undone");
        Assert.Equal((shell.SelectedTab, 2), Assert.Single(host.Undos));
        Assert.Equal("Undone (2 drawing steps)", controller.Status.Text);
        Assert.False(controller.Status.CanUndo);

        // A refused undo is explained.
        host.UndoResult = new QuickAskUndoResult(false, "The drawing was changed after the copilot's last call.");
        Assert.True(controller.SubmitText("make it 12 by 20"));
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "second done");
        controller.Status.UndoCommand.Execute(null);
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Failed, "undo refused");
        Assert.StartsWith("Could not undo: The drawing was changed", controller.Status.Text, StringComparison.Ordinal);
        Assert.True(controller.Status.ShowOpenChat);
        shell.Dispose();
    });

    [Fact]
    public void A_question_opens_the_chat_once_and_the_bubble_says_so() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        var shell = CreateShell(backend);
        var host = new FakeHost(shell);
        using var controller = CreateController(host);
        Assert.True(controller.SubmitText("make the kitchen 10 by 20"));
        var tab = shell.SelectedTab;
        await Sta.WaitUntil(() => tab.IsBusy && claude.Requests.Count == 1, "running");

        tab.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Draw kitchen walls", Status = PlanTaskStatus.InProgress }] });
        await Sta.WaitUntil(() => controller.Status.Text == "Draw kitchen walls...", "plan task shown");

        var answer = tab.AskUserAsync(new QuestionRequest { Questions = [new UserQuestion { Question = "Which wall gets the platform?", Options = [new QuestionOption { Label = "North" }] }] }, CancellationToken.None);
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.NeedsAnswer, "needs answer");
        Assert.Equal("The AI needs your answer", controller.Status.Text);
        Assert.Same(tab, Assert.Single(host.ShownChats));
        controller.Status.Refresh();
        Assert.Single(host.ShownChats); // only once

        tab.Items.OfType<QuestionCardItem>().Single().Cancel();
        await answer;
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Working, "working again");
        controller.Status.OpenChatCommand.Execute(null);
        Assert.Equal(2, host.ShownChats.Count);
        tab.Stop();
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Stopped, "stopped");
        Assert.Equal("Stopped - kitchen 10'x20'", controller.Status.Text);
        shell.Dispose();
    });

    [Fact]
    public void Failures_and_missing_panels_are_shown_briefly() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        claude.Hold = false;
        claude.Before = _ => [new TurnFailed("You've hit your usage limit.", AgentFailureKind.RateLimited)];
        var shell = CreateShell(backend);
        var host = new FakeHost(shell);
        using var controller = CreateController(host);
        Assert.True(controller.SubmitText("draw the toilet"));
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Failed, "failed");
        Assert.StartsWith("Could not finish: Usage limit reached", controller.Status.Text, StringComparison.Ordinal);
        Assert.True(controller.Status.ShowOpenChat);
        controller.Status.DismissCommand.Execute(null);
        Assert.Equal(QuickAskState.Hidden, controller.Status.State);

        var noPanel = new FakeHost(null);
        using var early = CreateController(noPanel);
        Assert.False(early.SubmitText("draw the toilet"));
        Assert.Equal(QuickAskState.Failed, early.Status.State);
        Assert.Contains("still starting", early.Status.Text, StringComparison.Ordinal);
        Assert.False(early.Status.ShowOpenChat);
        shell.Dispose();
    });

    [Fact]
    public void Pick_replaces_the_cursor_point_and_the_button_opens_without_one() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        claude.Hold = false;
        var shell = CreateShell(backend);
        var host = new FakeHost(shell) { Cursor = Cursor };
        using var controller = CreateController(host);

        var box = controller.OpenBox(QuickAskOpenReason.Button)!;
        Assert.False(box.HasPoint);
        Assert.Same(box, controller.OpenBox(QuickAskOpenReason.Shortcut)); // already open
        controller.ToggleBox(QuickAskOpenReason.Shortcut);
        Assert.False(controller.IsBoxOpen);

        box = controller.OpenBox(QuickAskOpenReason.Command)!;
        Assert.True(box.HasPoint);
        host.NextPick = new TaskCompletionSource<PickResult>();
        box.PickCommand.Execute(null);
        Assert.True(box.IsPicking);
        Assert.False(box.PickCommand.CanExecute(null));
        box.Text = "door here";
        Assert.False(box.CanSend); // not while picking
        host.NextPick.SetResult(new PickResult { Status = PickStatus.Picked, Points = [new PickedPoint(48, 96, 0, "4'-0\"", "8'-0\"")] });
        await Sta.Settle();
        Assert.False(box.IsPicking);
        Assert.Equal(QuickAskPointSource.Picked, box.PointSource);
        Assert.Equal("Point 4'-0\", 8'-0\"", box.PointChipText);

        // A failed pick keeps the point and says why.
        host.NextPick = new TaskCompletionSource<PickResult>();
        box.PickCommand.Execute(null);
        host.NextPick.SetResult(PickResult.Fail("AutoCAD is busy with a command (LINE)."));
        await Sta.Settle();
        Assert.Equal("AutoCAD is busy with a command (LINE).", box.Problem);
        Assert.True(box.StatusIsError);
        Assert.Equal(QuickAskPointSource.Picked, box.PointSource);

        box.RemovePointCommand.Execute(null);
        Assert.False(box.HasPoint);
        box.PickCommand.Execute(null);
        host.NextPick.TrySetResult(new PickResult { Status = PickStatus.Picked, Points = [new PickedPoint(1, 2)] });
        host.NextPick = new TaskCompletionSource<PickResult>();
        await Sta.Settle();
        Assert.True(box.HasPoint);
        Assert.True(controller.SubmitBox());
        await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "done");
        Assert.Contains("The user clicked this point", claude.Requests.Last().Prompt, StringComparison.Ordinal);

        // Esc (Close) drops the box and gives the keyboard back.
        var focus = host.FocusReturns;
        box = controller.OpenBox(QuickAskOpenReason.Shortcut)!;
        box.Text = "something";
        box.CloseCommand.Execute(null);
        Assert.False(controller.IsBoxOpen);
        Assert.Equal(focus + 1, host.FocusReturns);
        shell.Dispose();
    });

    [Fact]
    public void The_box_mic_writes_the_words_and_can_send_them() => Sta.Run(async () =>
    {
        var (backend, claude) = CreateBackend();
        claude.Hold = false;
        var shell = CreateShell(backend);
        var host = new FakeHost(shell);
        var factory = new FakeSpeechFactory();
        var panel = new PanelSettingsStore(Path.Combine(_temp, "panel.json"));
        panel.Save(new PanelSettings { VoiceAutoSend = true, VoiceLanguage = "en-IN" });
        using var controller = new QuickAskController(host, new QuickAskOptions(new QuickAskSettingsStore(Path.Combine(_temp, Guid.NewGuid() + ".json"))), Dispatcher.CurrentDispatcher, new VoiceInputService(factory, new ManualClock(), _ => { }), panel);

        var box = controller.OpenBox(QuickAskOpenReason.Shortcut)!;
        Assert.True(box.HasVoice);
        await box.Voice!.StartAsync();
        Assert.Equal("en-IN", factory.Requests.Single().LanguageTag);
        Assert.Equal("Speak now: your words appear here...", box.Placeholder);
        Assert.True(box.HasStatusLine);
        factory.Last!.Sink.OnPhrase("make the kitchen 10 by 20 feet");
        await Sta.Settle();
        Assert.Equal("make the kitchen 10' x 20'", box.Text);
        await box.Voice.StopAsync();
        await Sta.WaitUntil(() => claude.Requests.Count == 1, "auto-sent after speaking");
        Assert.False(controller.IsBoxOpen);
        Assert.StartsWith("make the kitchen 10' x 20'", claude.Requests.Single().Prompt, StringComparison.Ordinal);
        Assert.True(factory.Last.Disposed || factory.Last.Stops > 0);
        shell.Dispose();
    });

    [Fact]
    public void Chat_view_model_quick_ask_starts_now_or_queues() => Sta.Run(async () =>
    {
        var backend = FakeBackend.Create();
        backend.HoldOpen = true;
        var chat = new ChatViewModel(backend, Dispatcher.CurrentDispatcher, null, new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid() + ".json")), new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid() + "-s.json")));
        chat.ComposerText = "my own words";
        Assert.Equal(QuickAskDelivery.Started, chat.SendQuickAsk("one [point (1,2)]", "one sent", "1 object selected"));
        Assert.True(chat.IsBusy);
        Assert.Equal("one sent", backend.Sent.Single());
        var item = Assert.IsType<UserMessageItem>(chat.Items[0]);
        Assert.Equal("one [point (1,2)]", item.Text);
        Assert.Equal("1 object selected", item.SelectionNote);
        Assert.Equal("my own words", chat.ComposerText);

        Assert.Equal(QuickAskDelivery.Queued, chat.SendQuickAsk("two", "two sent", null));
        Assert.Equal("two", Assert.Single(chat.Queue).DisplayText);
        backend.Release();
        await Sta.WaitUntil(() => backend.Sent.Count == 2, "queued one ran");
        Assert.Equal("two sent", backend.Sent[1]);
        backend.Release();
        await Sta.WaitUntil(() => !chat.IsBusy, "idle");
        chat.Dispose();
    });

    // ------------------------------------------------------------------ renders

    [Fact]
    public void Render_quick_ask_states() => Sta.Run(async () =>
    {
        foreach (var theme in new[] { PanelTheme.Dark, PanelTheme.Light })
        {
            var (backend, claude) = CreateBackend();
            var shell = CreateShell(backend);
            var host = new FakeHost(shell) { Cursor = Cursor, Current = Selection(3) };
            using var controller = CreateController(host);
            var name = theme.ToString().ToLowerInvariant();

            // Idle: just the button.
            Save(Scene(theme, controller, box: null), $"21-quick-ask-1-button-{name}");

            // The box next to the cursor, with the cursor point and the selection.
            var box = controller.OpenBox(QuickAskOpenReason.Shortcut)!;
            box.Text = "make the kitchen 10 by 20";
            Save(Scene(theme, controller, box), $"21-quick-ask-2-box-{name}");
            controller.SubmitBox();

            // Working: the plan task in the bubble.
            var tab = shell.SelectedTab;
            await Sta.WaitUntil(() => tab.IsBusy && claude.Requests.Count == 1, "running");
            tab.UpdatePlan(new PlanUpdate { Tasks = [new PlanTask { Title = "Drawing kitchen walls", Status = PlanTaskStatus.InProgress }] });
            await Sta.WaitUntil(() => controller.Status.Text == "Drawing kitchen walls...", "plan task");
            Save(Scene(theme, controller, null), $"21-quick-ask-3-working-{name}");

            // Needs an answer.
            var question = tab.AskUserAsync(new QuestionRequest { Questions = [new UserQuestion { Question = "Platform on which wall?" }] }, CancellationToken.None);
            await Sta.WaitUntil(() => controller.Status.IsNeedsAnswer, "needs answer");
            Save(Scene(theme, controller, null), $"21-quick-ask-4-needs-answer-{name}");
            tab.Items.OfType<QuestionCardItem>().Single().Cancel();
            await question;

            // Done with Undo.
            claude.Before = null;
            tab.Apply(new ToolCallStarted("r1", "run_commands", "Kitchen walls", "acad"), tab.CurrentTurn!);
            tab.Apply(new ToolCallFinished("r1", true, "ok"), tab.CurrentTurn!);
            claude.Release(claude.Requests.Single().Prompt);
            await Sta.WaitUntil(() => controller.Status.State == QuickAskState.Done, "done");
            Assert.True(controller.Status.CanUndo);
            Save(Scene(theme, controller, null), $"21-quick-ask-5-done-{name}");

            // An error.
            controller.Status.ShowProblem("Could not finish: Usage limit reached. Wait for the reset or switch the AI.", tab);
            Save(Scene(theme, controller, null), $"21-quick-ask-6-error-{name}");
            shell.Dispose();
        }
    });

    // ------------------------------------------------------------------ helpers

    private static SelectionSnapshot Selection(int count) => new()
    {
        Count = count,
        DrawingTitle = "GF-plan-30x40.dwg",
        Items = [.. Enumerable.Range(0, count).Select(i => new SelectedObject($"2A{i:X2}", "LWPOLYLINE", "A-WALL", "length 10'-0\""))],
    };

    private QuickAskController CreateController(FakeHost host) =>
        new(host, new QuickAskOptions(new QuickAskSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-qa.json"))), Dispatcher.CurrentDispatcher);

    private (CopilotBackend Backend, ScriptedRunner Claude) CreateBackend()
    {
        var claude = new ScriptedRunner(ProviderId.Claude, "Claude");
        var backend = new CopilotBackend(
            [claude],
            () => new McpServerLaunch { Command = @"C:\bridge\AcadCopilotMcp.exe", Arguments = ["--pid", "4242", "--panel"] },
            new AcadSessionInfo(4242, "2027", "GF-plan-30x40.dwg"),
            new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json")),
            Path.Combine(_temp, "workspace"),
            () => _activeDrawing);
        return (backend, claude);
    }

    private CopilotPanelViewModel CreateShell(CopilotBackend backend)
    {
        var panelSettings = new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"));
        var settings = new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-pace.json"));
        return new CopilotPanelViewModel(backend, session => new ChatViewModel(session, Dispatcher.CurrentDispatcher, null, panelSettings, settings));
    }

    // A slice of AutoCAD's drawing area (dark model space with a plan) with the button in its top-right corner (left of
    // where the ViewCube sits) and, when given, the box next to a cursor.
    private static FrameworkElement Scene(PanelTheme theme, QuickAskController controller, QuickAskBoxViewModel? box)
    {
        const double width = 980;
        const double height = 360;
        var png = TestImages.DrawingPng((int)width, (int)height);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(png);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x21, 0x28, 0x30)) };
        root.Children.Add(new Image { Source = image, Stretch = Stretch.Fill });

        // The ViewCube's place (for scale).
        root.Children.Add(new Border
        {
            Width = 96,
            Height = 96,
            Margin = new Thickness(0, 22, 30, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(48),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xB0, 0xB8, 0xC4)),
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock { Text = "TOP", Foreground = Brushes.Gainsboro, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });

        var button = new QuickAskButtonView(theme)
        {
            DataContext = controller,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, QuickAskPlacement.TopInset, QuickAskPlacement.RightInset, 0),
        };
        root.Children.Add(button);
        if (box is not null)
        {
            // Cursor at (430, 150): the box opens below-right of it.
            root.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M430,120 V180 M400,150 H460 M424,144 H436 V156 H424 Z"),
                Stroke = Brushes.White,
                StrokeThickness = 1,
            });
            root.Children.Add(new QuickAskBoxView(theme)
            {
                DataContext = box,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(430 + 6 - 12, 150 + 6 - 12, 0, 0),
            });
        }

        root.Width = width;
        root.Height = height;
        return root;
    }

    private static void Save(FrameworkElement scene, string name)
    {
        var path = Snapshot.Render(scene, scene.Width, scene.Height, name);
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    /// <summary>AutoCAD and the panel as Quick Ask sees them.</summary>
    private sealed class FakeHost(CopilotPanelViewModel? shell) : IQuickAskHost
    {
        public CopilotPanelViewModel? Shell { get; } = shell;

        public string? DrawingId { get; set; } = "dwg-1";

        public string? DrawingTitle { get; set; } = "GF-plan-30x40.dwg";

        public string? ActiveDrawingId => DrawingId;

        public string? ActiveDrawingTitle => DrawingTitle;

        public PickedPoint? Cursor { get; set; }

        public SelectionSnapshot? Current { get; set; }

        public PickedPoint? CursorPoint => Cursor;

        public SelectionSnapshot? Selection => Current;

        public TaskCompletionSource<PickResult> NextPick { get; set; } = new();

        public List<ChatViewModel> ShownChats { get; } = [];

        public List<(ChatViewModel, int)> Undos { get; } = [];

        public QuickAskUndoResult UndoResult { get; set; } = new(true, "Undone.");

        public int FocusReturns { get; private set; }

        public Task<PickResult> PickPointAsync(CancellationToken cancellationToken) => NextPick.Task;

        public void ShowChat(ChatViewModel chat) => ShownChats.Add(chat);

        public async Task<QuickAskUndoResult> UndoAsync(ChatViewModel chat, int calls, CancellationToken cancellationToken)
        {
            Undos.Add((chat, calls));
            await Task.Yield();
            return UndoResult;
        }

        public void ReturnFocusToDrawing() => FocusReturns++;
    }

    /// <summary>A CLI stand-in: optional events first, then (when <see cref="Hold"/>) works until released or stopped.</summary>
    private sealed class ScriptedRunner(ProviderId id, string displayName) : IAgentRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private int _sessions;

        public List<TurnRequest> Requests { get; } = [];

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
            Requests.Add(request);
            var hold = Hold;
            var before = Before?.Invoke(request) ?? [];
            await Task.Yield();
            yield return new SessionStarted(request.ResumeSessionId ?? $"{id}-cli-{Interlocked.Increment(ref _sessions)}");
            foreach (var item in before)
            {
                yield return item;
                if (item is TurnFailed)
                {
                    yield break;
                }
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

            yield return new TextDone("Done: " + request.Prompt[..Math.Min(20, request.Prompt.Length)]);
            yield return new TurnCompleted("end_turn");
        }

        private TaskCompletionSource Gate(string prompt) =>
            _gates.GetOrAdd(prompt, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
