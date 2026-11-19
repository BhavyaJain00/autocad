using System.IO;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Learning;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Learning;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// "Learn from me": the Learning bar above every chat tab (Start learning; red "Learning from you · 03:12 · 47 actions"
/// with Pause / Resume, Stop &amp; learn and Discard), one recorder for all tabs, and Stop &amp; learn sending
/// "Learn from my recording ..." in the tab shown. Renders artifacts\ui\15-learn-from-me-*.png.
/// </summary>
public sealed class LearnFromMeTests : IDisposable
{
    private const double Width = 430;
    private const double Height = 760;

    private readonly string _temp;

    public LearnFromMeTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-learn-ui-").FullName;
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
    public void Only_a_click_starts_it_and_pause_resume_discard_work() => Sta.Run(async () =>
    {
        var recorder = new FakeRecorder();
        var (shell, _) = CreateShell(recorder);
        var learning = shell.Learning!;
        Assert.True(shell.HasLearning);
        Assert.True(learning.IsOff);
        Assert.True(learning.ShowIdle);
        Assert.Equal(0, recorder.Starts);

        learning.StartCommand.Execute(null);
        Assert.Equal(1, recorder.Starts);
        Assert.True(learning.IsRecording);
        Assert.False(learning.ShowIdle);
        Assert.True(learning.ShowActive);
        Assert.False(learning.StartCommand.CanExecute(null));

        recorder.Set(TimeSpan.FromSeconds(192), 47);
        await Sta.Settle();
        Assert.Equal("Learning from you · 03:12 · 47 actions", learning.StatusText);

        learning.PauseCommand.Execute(null);
        Assert.True(learning.IsPaused);
        Assert.Equal("Paused · 03:12 · 47 actions", learning.StatusText);
        Assert.False(learning.PauseCommand.CanExecute(null));
        learning.ResumeCommand.Execute(null);
        Assert.True(learning.IsRecording);

        // Discard asks first; Keep goes back to recording.
        learning.DiscardCommand.Execute(null);
        Assert.True(learning.IsConfirmingDiscard);
        Assert.False(learning.ShowActive);
        Assert.Equal("Discard this recording (47 actions)? It is deleted and nothing is learned.", learning.DiscardQuestion);
        learning.CancelDiscardCommand.Execute(null);
        Assert.True(learning.IsRecording);
        Assert.Equal(0, recorder.Discards);

        learning.DiscardCommand.Execute(null);
        learning.ConfirmDiscardCommand.Execute(null);
        Assert.Equal(1, recorder.Discards);
        Assert.True(learning.IsOff);
        Assert.False(learning.IsConfirmingDiscard);
        Assert.Equal("Recording discarded. Nothing was saved.", shell.SelectedTab.Notice);
        Assert.Empty(recorder.Saves);
        shell.Dispose();
    });

    [Fact]
    public void The_recording_survives_tab_switches_and_stop_and_learn_asks_the_tab_shown() => Sta.Run(async () =>
    {
        var recorder = new FakeRecorder { Outcome = new RecordingOutcome(@"C:\k\recordings\2026-11-19_1412_House.md", 47, "style.md updated (Layers, Text).", null) };
        var (shell, hub) = CreateShell(recorder);
        var learning = shell.Learning!;
        learning.StartCommand.Execute(null);

        var second = shell.NewTab()!;
        Assert.Same(second, shell.SelectedTab);
        Assert.Same(learning, shell.Learning);
        Assert.True(learning.IsRecording);
        shell.SelectTab(shell.Tabs[0]);
        shell.SelectTab(second);
        Assert.Equal(1, recorder.Starts);

        await learning.StopAndLearnAsync();
        Assert.True(learning.IsOff);
        Assert.Single(recorder.Saves);
        await Sta.WaitUntil(() => hub.Backends[1].Sent.Count == 1, "message sent in the tab shown");
        Assert.Equal("Learn from my recording 2026-11-19_1412_House.md: read it, save how I draw as recipes and rules, and tell me briefly what you learned.", hub.Backends[1].Sent[0]);
        Assert.Empty(hub.Backends[0].Sent);
        var user = second.Items.OfType<UserMessageItem>().Single();
        Assert.StartsWith("Learn from my recording 2026-11-19_1412_House.md", user.Text, StringComparison.Ordinal);
        Assert.Contains("Recording saved (47 actions)", second.Notice, StringComparison.Ordinal);
        Assert.Contains("style.md updated", second.Notice, StringComparison.Ordinal);
        await Sta.WaitUntil(() => !second.IsBusy, "turn done");
        shell.Dispose();
    });

    [Fact]
    public void A_busy_tab_keeps_the_request_in_the_message_box() => Sta.Run(async () =>
    {
        var recorder = new FakeRecorder { Outcome = new RecordingOutcome(@"C:\k\recordings\2026-11-19_1412_House.md", 3, null, null) };
        var (shell, hub) = CreateShell(recorder);
        var tab = shell.SelectedTab;
        hub.Backends[0].HoldOpen = true;
        tab.ComposerText = "Draw the stair";
        var running = tab.SendAsync();
        await Sta.WaitUntil(() => tab.IsBusy, "busy");
        tab.ComposerText = "and then the doors";

        shell.Learning!.StartCommand.Execute(null);
        await shell.Learning.StopAndLearnAsync();
        Assert.Equal(["Draw the stair"], hub.Backends[0].Sent);
        Assert.Equal("and then the doors" + Environment.NewLine + ChatViewModel.LearnFromRecordingPrompt("2026-11-19_1412_House.md"), tab.ComposerText);
        Assert.Contains("This chat is busy", tab.Notice, StringComparison.Ordinal);

        hub.Backends[0].Release();
        await running;
        shell.Dispose();
    });

    [Fact]
    public void Nothing_recorded_or_a_failed_start_is_said_plainly() => Sta.Run(async () =>
    {
        var recorder = new FakeRecorder { Outcome = new RecordingOutcome(null, 0, null, "Nothing was recorded: no commands of yours ran while learning was on."), StartProblem = "Open a drawing first, then start learning." };
        var (shell, hub) = CreateShell(recorder);
        var learning = shell.Learning!;
        learning.StartCommand.Execute(null);
        Assert.True(learning.IsOff);
        Assert.Equal("Open a drawing first, then start learning.", shell.SelectedTab.Notice);
        Assert.True(shell.SelectedTab.NoticeIsError);

        recorder.StartProblem = null;
        learning.StartCommand.Execute(null);
        await learning.StopAndLearnAsync();
        Assert.Equal("Nothing was recorded: no commands of yours ran while learning was on.", shell.SelectedTab.Notice);
        Assert.False(shell.SelectedTab.NoticeIsError);
        Assert.Empty(hub.Backends[0].Sent);
        shell.Dispose();
    });

    [Fact]
    public void Without_a_recorder_there_is_no_learning_bar() => Sta.Run(() =>
    {
        var (shell, _) = CreateShell(null);
        Assert.False(shell.HasLearning);
        Assert.Null(shell.Learning);
        shell.Dispose();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Renders_the_learning_bar_states(PanelTheme theme) => Sta.Run(async () =>
    {
        var recorder = new FakeRecorder();
        var backend = FakeBackend.Create();
        var pane = await CreatePaneAsync(backend, theme, recorder);
        var learning = pane.Shell.Learning!;
        Save(pane, "15-learn-from-me-off", theme);

        learning.StartCommand.Execute(null);
        recorder.Set(TimeSpan.FromSeconds(192), 47);
        await Sta.Settle();
        Save(pane, "15-learn-from-me-recording", theme);

        learning.PauseCommand.Execute(null);
        await Sta.Settle();
        Save(pane, "15-learn-from-me-paused", theme);

        learning.ResumeCommand.Execute(null);
        learning.DiscardCommand.Execute(null);
        await Sta.Settle();
        Save(pane, "15-learn-from-me-discard", theme);
        learning.CancelDiscardCommand.Execute(null);

        // Stop & learn: saving, then the request in the chat and the AI reading the recording and saving what it learned.
        recorder.Hold = new TaskCompletionSource<RecordingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = learning.StopAndLearnAsync();
        await Sta.Settle();
        Assert.True(learning.IsSaving);
        Save(pane, "15-learn-from-me-saving", theme);

        backend.HoldOpen = true;
        backend.Script.AddRange(LearningFromRecordingScript());
        recorder.Hold.SetResult(new RecordingOutcome(@"C:\k\recordings\2026-11-19_1412_GF-plan-30x40.md", 47, "style.md updated (Layers, Text, Dimensions).", null));
        await stop;
        await Sta.WaitUntil(() => pane.ViewModel.Items.OfType<ActivityGroupItem>().Any(g => g.Rows.Count == 4), "learning rows");
        Save(pane, "15-learn-from-me-learned", theme);
        Assert.True(learning.IsOff);

        backend.Release();
        await Sta.WaitUntil(() => !pane.ViewModel.IsBusy, "turn done");
        pane.Shell.Dispose();
    });

    [Fact]
    public void Renders_the_recording_bar_on_a_narrow_panel() => Sta.Run(async () =>
    {
        var recorder = new FakeRecorder();
        var pane = await CreatePaneAsync(FakeBackend.Create(), PanelTheme.Light, recorder);
        pane.Shell.Learning!.StartCommand.Execute(null);
        recorder.Set(TimeSpan.FromSeconds(3725), 1284);
        await Sta.Settle();
        Assert.Equal("Learning from you · 1:02:05 · 1,284 actions", pane.Shell.Learning.StatusText);
        Save(pane, "15-learn-from-me-recording-narrow", PanelTheme.Light, 300);
        pane.Shell.Dispose();
    });

    private static IEnumerable<AgentEvent> LearningFromRecordingScript() =>
    [
        new SessionStarted("session-learn-recording"),
        new ToolCallStarted("r1", KnowledgeTools.ReadRecording, "name: 2026-11-19_1412_GF-plan-30x40.md", "acad"),
        new ToolCallFinished("r1", true, "2026-11-19_1412_GF-plan-30x40.md (18,420 characters)."),
        new ToolCallStarted("r2", KnowledgeTools.SaveRecipe, "name: Door with swing (your way), steps: 4 items", "acad"),
        new ToolCallFinished("r2", true, "Saved the recipe \"Door with swing (your way)\" (4 steps)."),
        new ToolCallStarted("r3", KnowledgeTools.SaveRecipe, "name: Dimension chain outside the walls, steps: 3 items", "acad"),
        new ToolCallFinished("r3", true, "Saved the recipe \"Dimension chain outside the walls\" (3 steps)."),
        new ToolCallStarted("r4", KnowledgeTools.RememberRule, "rule: Inner face by OFFSET 9 from the outer PLINE", "acad"),
        new ToolCallFinished("r4", true, "Remembered: inner wall face by OFFSET 9\" from the outer polyline."),
        new TextDone("I learned from your recording:\n\n- **Walls:** outer PLINE, then OFFSET 9\" inwards, partitions 4 1/2\".\n- **Doors:** leaf LINE from the hinge + ARC, on A-DOOR.\n- **Dimensions:** DIMLINEAR then DIMCONTINUE, 3'6\" outside the walls."),
    ];

    private (CopilotPanelViewModel Shell, TestHub Hub) CreateShell(IUserActionRecorder? recorder)
    {
        var hub = new TestHub();
        var panelSettings = new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"));
        var settings = new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-pace.json"));
        var shell = new CopilotPanelViewModel(hub, session => new ChatViewModel(session, Dispatcher.CurrentDispatcher, null, panelSettings, settings), recorder: recorder);
        return (shell, hub);
    }

    private async Task<CopilotPane> CreatePaneAsync(FakeBackend backend, PanelTheme theme, IUserActionRecorder recorder)
    {
        var pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = theme,
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
            EnableVoice = false,
            OpenFolder = _ => { },
            Recorder = recorder,
        });
        await pane.ViewModel.RefreshStatusesAsync();
        return pane;
    }

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width = Width, double height = Height)
    {
        var path = Snapshot.Render(pane, width, height, $"{name}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    /// <summary>A recorder whose state the test sets.</summary>
    private sealed class FakeRecorder : IUserActionRecorder
    {
        public event EventHandler? Changed;

        public RecorderState State { get; private set; }

        public int ActionCount { get; private set; }

        public TimeSpan Elapsed { get; private set; }

        public int Starts { get; private set; }

        public int Discards { get; private set; }

        public List<RecordingOutcome> Saves { get; } = [];

        public string? StartProblem { get; set; }

        public RecordingOutcome Outcome { get; set; } = new(@"C:\k\recordings\2026-11-19_1412_Drawing1.md", 1, null, null);

        public TaskCompletionSource<RecordingOutcome>? Hold { get; set; }

        public void Set(TimeSpan elapsed, int actions)
        {
            Elapsed = elapsed;
            ActionCount = actions;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public string? Start()
        {
            if (StartProblem is not null)
            {
                return StartProblem;
            }

            Starts++;
            State = RecorderState.Recording;
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }

        public void Pause()
        {
            State = RecorderState.Paused;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Resume()
        {
            State = RecorderState.Recording;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public async Task<RecordingOutcome> StopAndSaveAsync(CancellationToken cancellationToken = default)
        {
            var outcome = Hold is null ? Outcome : await Hold.Task;
            State = RecorderState.Off;
            Saves.Add(outcome);
            Changed?.Invoke(this, EventArgs.Empty);
            return outcome;
        }

        public void Discard()
        {
            Discards++;
            State = RecorderState.Off;
            ActionCount = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Chat tabs over <see cref="FakeBackend"/>s (one per tab).</summary>
    private sealed class TestHub : ICopilotHub
    {
        private readonly List<ICopilotSession> _sessions = [];

        public TestHub() => CreateSession();

        public event EventHandler? SessionsChanged;

        public List<FakeBackend> Backends { get; } = [];

        public IReadOnlyList<IAgentRunner> Runners => Backends[0].Runners;

        public IReadOnlyList<ICopilotSession> Sessions => _sessions;

        public ICopilotSession? ActiveSession { get; set; }

        public int MaxRunningSessions => 4;

        public int RunningSessionCount => Backends.Count(b => b.IsBusy);

        public ICopilotSession CreateSession()
        {
            var backend = FakeBackend.Create();
            Backends.Add(backend);
            var session = new TestSession("tab-" + Backends.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), backend);
            _sessions.Add(session);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return session;
        }

        public void CloseSession(ICopilotSession session) => _sessions.Remove(session);
    }

    private sealed class TestSession(string id, FakeBackend backend) : ICopilotSession
    {
        public event EventHandler? StateChanged
        {
            add => backend.StateChanged += value;
            remove => backend.StateChanged -= value;
        }

        public event EventHandler? ChatReset
        {
            add => backend.ChatReset += value;
            remove => backend.ChatReset -= value;
        }

        public string Id => id;

        public DrawingRef? Drawing => new("dwg-" + id, "GF-plan-30x40.dwg");

        public AcadTurnStatus AcadTurn => AcadTurnStatus.Idle;

        public bool CanStartTurn => true;

        public int MaxRunningTurns => 4;

        public IReadOnlyList<IAgentRunner> Runners => backend.Runners;

        public ProviderId SelectedProvider
        {
            get => backend.SelectedProvider;
            set => backend.SelectedProvider = value;
        }

        public bool IsBusy => backend.IsBusy;

        public AcadSessionInfo Acad => backend.Acad;

        public string? GetModel(ProviderId provider) => backend.GetModel(provider);

        public void SetModel(ProviderId provider, string? modelId) => backend.SetModel(provider, modelId);

        public Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default) => backend.GetStatusAsync(provider, cancellationToken);

        public IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, CancellationToken cancellationToken) =>
            backend.SendAsync(text, attachments, cancellationToken);

        public void NewChat() => backend.NewChat();

        public bool SignIn(ProviderId provider) => backend.SignIn(provider);

        public void AttachUi(ICopilotUi ui) => backend.AttachUi(ui);
    }
}
