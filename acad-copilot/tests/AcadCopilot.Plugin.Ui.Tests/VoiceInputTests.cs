using System.IO;
using System.Windows.Threading;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Interaction;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>The mic of a chat tab with a stand-in recognizer (no microphone, no Windows speech), and the engine choice.</summary>
public sealed class VoiceInputTests : IDisposable
{
    private readonly string _temp;

    public VoiceInputTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-voice-").FullName;
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
    public void Words_appear_live_after_the_typed_text_and_stay_after_stopping() => Sta.Run(async () =>
    {
        var (vm, backend, factory, _, _) = Create();
        var voice = vm.Voice!;
        vm.ComposerText = "Draw the plan for";
        await voice.StartAsync();

        Assert.Equal(VoiceState.Listening, voice.State);
        Assert.True(voice.IsListening);
        Assert.Equal("English (India) · Windows dictation", voice.EngineText);
        Assert.Equal("Listening... speak now", voice.StatusText);
        Assert.Equal("Speak now: your words appear here...", vm.ComposerPlaceholder);
        var engine = factory.Last!;
        engine.Sink.OnHypothesis("a 30 by 40");
        await Sta.Settle();
        Assert.Equal("Draw the plan for a 30 x 40", vm.ComposerText);
        Assert.Equal("Listening...", voice.StatusText);

        engine.Sink.OnPhrase("a thirty by forty feet plot.");
        engine.Sink.OnHypothesis("with nine inch");
        await Sta.Settle();
        Assert.Equal("Draw the plan for a 30' x 40' plot. with 9\"", vm.ComposerText);

        engine.FinalPhraseOnStop = "With nine inch walls.";
        await voice.StopAsync();

        Assert.Equal(VoiceState.Idle, voice.State);
        Assert.Equal("Draw the plan for a 30' x 40' plot. With 9\" walls.", vm.ComposerText);
        Assert.Equal(1, engine.Stops);
        await Sta.WaitUntil(() => engine.Disposed, "recognizer closed");
        Assert.Empty(backend.Sent);
        Assert.False(voice.HasProblem);
        Assert.StartsWith("Ask Claude", vm.ComposerPlaceholder, StringComparison.Ordinal);

        // Late events of the finished session change nothing.
        engine.Sink.OnPhrase("ignored");
        await Sta.Settle();
        Assert.Equal("Draw the plan for a 30' x 40' plot. With 9\" walls.", vm.ComposerText);
        vm.Dispose();
    });

    [Fact]
    public void Two_seconds_of_silence_stop_and_auto_send_sends() => Sta.Run(async () =>
    {
        var (vm, backend, factory, clock, _) = Create();
        vm.VoiceAutoSend = true;
        var voice = vm.Voice!;
        await voice.ToggleAsync();
        factory.Last!.Sink.OnPhrase("Add dimensions to the plan");
        await Sta.Settle();

        clock.Advance(TimeSpan.FromSeconds(1.5));
        voice.CheckSilence();
        await Sta.Settle();
        Assert.Equal(VoiceState.Listening, voice.State);

        clock.Advance(TimeSpan.FromSeconds(0.6));
        voice.CheckSilence();
        await Sta.WaitUntil(() => backend.Sent.Count == 1, "auto-send");
        Assert.Equal(["Add dimensions to the plan"], backend.Sent);
        Assert.Equal(VoiceState.Idle, voice.State);
        await Sta.WaitUntil(() => !vm.IsBusy, "turn end");
        vm.Dispose();
    });

    [Fact]
    public void Without_auto_send_the_words_wait_for_enter() => Sta.Run(async () =>
    {
        var (vm, backend, factory, clock, _) = Create();
        var voice = vm.Voice!;
        await voice.StartAsync();
        factory.Last!.Sink.OnPhrase("What is in this drawing?");
        await Sta.Settle();
        clock.Advance(TimeSpan.FromSeconds(3));
        voice.CheckSilence();
        await Sta.WaitUntil(() => voice.State == VoiceState.Idle, "stopped");
        Assert.Empty(backend.Sent);
        Assert.Equal("What is in this drawing?", vm.ComposerText);

        await vm.SendAsync();
        Assert.Equal(["What is in this drawing?"], backend.Sent);
        vm.Dispose();
    });

    [Fact]
    public void Enter_while_listening_sends_what_was_heard() => Sta.Run(async () =>
    {
        var (vm, backend, factory, _, _) = Create();
        vm.VoiceAutoSend = true;
        var voice = vm.Voice!;
        await voice.StartAsync();
        factory.Last!.Sink.OnHypothesis("draw the front elevation");
        await Sta.Settle();

        await vm.SendAsync();
        Assert.Equal(["draw the front elevation"], backend.Sent);
        Assert.Equal(VoiceState.Idle, voice.State);
        Assert.Equal(string.Empty, vm.ComposerText);
        vm.Dispose();
    });

    [Fact]
    public void Nothing_said_ends_with_a_hint() => Sta.Run(async () =>
    {
        var (vm, _, _, clock, _) = Create();
        var voice = vm.Voice!;
        await voice.StartAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        voice.CheckSilence();
        await Sta.Settle();
        Assert.Equal(VoiceState.Listening, voice.State);

        clock.Advance(VoiceInputViewModel.SilenceBeforeSpeech);
        voice.CheckSilence();
        await Sta.WaitUntil(() => voice.State == VoiceState.Idle, "stopped");
        Assert.Equal(VoiceProblemKind.NothingHeard, voice.Problem!.Kind);
        Assert.False(voice.ProblemIsError);
        Assert.Equal(string.Empty, vm.ComposerText);
        vm.Dispose();
    });

    [Fact]
    public void A_start_problem_is_explained_with_the_windows_settings_page() => Sta.Run(async () =>
    {
        var (vm, _, factory, _, opened) = Create();
        factory.Failure = VoiceProblems.MicrophoneBlocked();
        var voice = vm.Voice!;
        vm.ComposerText = "typed";
        await voice.StartAsync();

        Assert.Equal(VoiceState.Idle, voice.State);
        Assert.True(voice.HasProblem);
        Assert.True(voice.ProblemIsError);
        Assert.Equal("Microphone access is blocked", voice.Problem!.Title);
        Assert.Contains("Allow desktop apps to access your microphone", voice.Problem.Detail, StringComparison.Ordinal);
        Assert.True(voice.HasProblemSettings);
        voice.OpenProblemSettingsCommand.Execute(null);
        Assert.Equal(["ms-settings:privacy-microphone"], opened);
        Assert.Equal("typed", vm.ComposerText);

        voice.DismissProblemCommand.Execute(null);
        Assert.False(voice.HasProblem);

        // The next try clears the old problem.
        factory.Failure = null;
        voice.DismissProblemCommand.Execute(null);
        await voice.StartAsync();
        Assert.False(voice.HasProblem);
        Assert.True(voice.IsListening);
        vm.Dispose();
    });

    [Fact]
    public void A_recognizer_error_while_listening_keeps_the_words_and_explains() => Sta.Run(async () =>
    {
        var (vm, backend, factory, _, _) = Create();
        vm.VoiceAutoSend = true;
        var voice = vm.Voice!;
        await voice.StartAsync();
        factory.Last!.Sink.OnPhrase("Draw the site plan");
        factory.Last.Sink.OnStopped(VoiceProblems.NoInternet());
        await Sta.WaitUntil(() => voice.State == VoiceState.Idle, "stopped");

        Assert.Equal("Draw the site plan", vm.ComposerText);
        Assert.Equal(VoiceProblemKind.NoInternet, voice.Problem!.Kind);
        Assert.Empty(backend.Sent);
        vm.Dispose();
    });

    [Fact]
    public void Stopping_while_the_recognizer_starts_closes_it_when_it_arrives() => Sta.Run(async () =>
    {
        var (vm, _, factory, _, _) = Create();
        factory.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = vm.Voice!;
        var start = voice.StartAsync();
        await Sta.Settle();
        Assert.Equal(VoiceState.Starting, voice.State);
        Assert.Equal("Starting the microphone...", voice.StatusText);
        Assert.True(voice.IsListening);

        await voice.StopAsync();
        Assert.Equal(VoiceState.Idle, voice.State);
        factory.Gate.SetResult();
        await start;
        await Sta.WaitUntil(() => factory.Last is { Disposed: true }, "late recognizer closed");
        Assert.Equal(VoiceState.Idle, voice.State);
        Assert.False(voice.HasProblem);
        vm.Dispose();
    });

    [Fact]
    public void Closing_the_tab_closes_the_recognizer() => Sta.Run(async () =>
    {
        var (vm, _, factory, _, _) = Create();
        await vm.Voice!.StartAsync();
        var engine = factory.Last!;
        vm.Dispose();
        await Sta.WaitUntil(() => engine.Disposed, "recognizer closed");
        Assert.False(vm.Voice.ToggleCommand.CanExecute(null));
    });

    [Fact]
    public void Only_one_tab_listens_and_switching_tabs_stops_the_mic() => Sta.Run(async () =>
    {
        var backend = CreateHub();
        var factory = new FakeSpeechFactory();
        var service = new VoiceInputService(factory, new ManualClock(), _ => { });
        var store = new PanelSettingsStore(Path.Combine(_temp, "panel.json"));
        var settings = new CopilotSettingsStore(Path.Combine(_temp, "settings.json"));
        using var panel = new CopilotPanelViewModel(backend, session => new ChatViewModel(session, Dispatcher.CurrentDispatcher, null, store, settings, service));
        var first = panel.Tabs[0];
        var second = panel.NewTab()!;
        await Sta.Settle();

        await second.Voice!.StartAsync();
        var secondEngine = factory.Last!;
        secondEngine.Sink.OnPhrase("draw the stair plan");
        await Sta.Settle();

        // The first tab takes the mic: the second one stops and keeps its words.
        await first.Voice!.StartAsync();
        Assert.Equal(VoiceState.Idle, second.Voice.State);
        Assert.Equal("draw the stair plan", second.ComposerText);
        Assert.Equal(1, secondEngine.Stops);
        Assert.Equal(VoiceState.Listening, first.Voice.State);

        // Switching tabs stops listening in the tab left.
        panel.SelectTab(first);
        panel.SelectTab(second);
        await Sta.WaitUntil(() => first.Voice.State == VoiceState.Idle, "first tab stopped");
        Assert.Equal(2, factory.Engines.Count);
    });

    [Fact]
    public void Voice_settings_are_remembered_and_shared_by_the_tabs() => Sta.Run(async () =>
    {
        var backend = CreateHub();
        var factory = new FakeSpeechFactory();
        var service = new VoiceInputService(factory, new ManualClock(), _ => { });
        var path = Path.Combine(_temp, "panel.json");
        var store = new PanelSettingsStore(path);
        var settings = new CopilotSettingsStore(Path.Combine(_temp, "settings.json"));
        using var panel = new CopilotPanelViewModel(backend, session => new ChatViewModel(session, Dispatcher.CurrentDispatcher, null, store, settings, service));
        var first = panel.Tabs[0];
        var second = panel.NewTab()!;
        Assert.False(first.VoiceAutoSend);
        Assert.Null(first.SelectedVoiceLanguage.Tag);

        first.VoiceAutoSend = true;
        first.SelectedVoiceLanguage = first.VoiceLanguageChoices.Single(c => c.Tag == "hi-IN");
        first.ApprovalMode = ApprovalMode.AutoApprove;
        Assert.True(second.VoiceAutoSend);
        Assert.Equal("hi-IN", second.SelectedVoiceLanguage.Tag);

        var saved = new PanelSettingsStore(path).Load();
        Assert.True(saved.VoiceAutoSend);
        Assert.Equal("hi-IN", saved.VoiceLanguage);
        Assert.Equal(ApprovalMode.AutoApprove, saved.ApprovalMode);

        await second.Voice!.StartAsync();
        Assert.Equal("hi-IN", factory.Requests.Single().LanguageTag);

        var third = panel.NewTab()!;
        Assert.True(third.VoiceAutoSend);
        Assert.Equal("Hindi (India)", third.SelectedVoiceLanguage.DisplayName);
    });

    [Fact]
    public void No_voice_service_hides_the_mic() => Sta.Run(() =>
    {
        var backend = FakeBackend.Create();
        var vm = new ChatViewModel(backend, Dispatcher.CurrentDispatcher, null, new PanelSettingsStore(Path.Combine(_temp, "p.json")), new CopilotSettingsStore(Path.Combine(_temp, "s.json")));
        Assert.False(vm.HasVoice);
        Assert.Null(vm.Voice);
        vm.Dispose();
        return Task.CompletedTask;
    });

    // ---- Engine choice (WindowsSpeechEngineFactory over stand-ins) ----

    [Fact]
    public async Task Blocked_microphone_is_reported_without_starting_anything()
    {
        var calls = new List<string>();
        var factory = Factory(new VoicePrivacy("Allow", "Allow", "Deny", 1), calls);
        var error = await Assert.ThrowsAsync<VoiceException>(() => factory.StartAsync(new VoiceRequest(null), new NullSink(), CancellationToken.None));
        Assert.Equal(VoiceProblemKind.MicrophoneBlocked, error.Problem.Kind);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Windows_dictation_is_used_first()
    {
        var calls = new List<string>();
        var engine = await Factory(new VoicePrivacy("Allow", "Allow", "Allow", null), calls).StartAsync(new VoiceRequest("en-IN"), new NullSink(), CancellationToken.None);
        Assert.Equal("online", engine.Name);
        Assert.Equal(["online"], calls);
    }

    [Fact]
    public async Task Online_speech_off_falls_back_to_the_offline_recognizer_with_a_hint()
    {
        var calls = new List<string>();
        var engine = await Factory(new VoicePrivacy(null, null, null, null), calls, online: VoiceProblems.OnlineSpeechOff("English (India)"))
            .StartAsync(new VoiceRequest(null), new NullSink(), CancellationToken.None);
        Assert.Equal("offline", engine.Name);
        Assert.Equal(["online", "offline"], calls);
        Assert.Contains("Online speech recognition", engine.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Known_online_speech_off_skips_windows_dictation()
    {
        var calls = new List<string>();
        var engine = await Factory(new VoicePrivacy("Allow", "Allow", "Allow", 0), calls).StartAsync(new VoiceRequest(null), new NullSink(), CancellationToken.None);
        Assert.Equal("offline", engine.Name);
        Assert.Equal(["offline"], calls);
    }

    [Fact]
    public async Task Missing_windows_runtime_falls_back_to_the_offline_recognizer()
    {
        var calls = new List<string>();
        var factory = new WindowsSpeechEngineFactory(
            () => new VoicePrivacy(null, null, null, null),
            (_, _, _) => { calls.Add("online"); throw new FileNotFoundException("Microsoft.Windows.SDK.NET"); },
            (request, sink, notice) => { calls.Add("offline"); return new FakeSpeechEngine(sink, "en-US", "offline", notice); });
        var engine = await factory.StartAsync(new VoiceRequest(null), new NullSink(), CancellationToken.None);
        Assert.Equal("offline", engine.Name);
        Assert.Contains("not available", engine.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_microphone_is_reported_at_once()
    {
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<VoiceException>(() =>
            Factory(new VoicePrivacy(null, null, null, 1), calls, online: VoiceProblems.NoMicrophone()).StartAsync(new VoiceRequest(null), new NullSink(), CancellationToken.None));
        Assert.Equal(VoiceProblemKind.NoMicrophone, error.Problem.Kind);
        Assert.Equal(["online"], calls);
    }

    [Fact]
    public async Task When_neither_recognizer_works_the_dictation_problem_is_reported()
    {
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<VoiceException>(() =>
            Factory(new VoicePrivacy(null, null, null, null), calls, online: VoiceProblems.OnlineSpeechOff("Hindi (India)"), offline: VoiceProblems.LanguageMissing("Hindi (India)"))
                .StartAsync(new VoiceRequest("hi-IN"), new NullSink(), CancellationToken.None));
        Assert.Equal(VoiceProblemKind.OnlineSpeechOff, error.Problem.Kind);
        Assert.Equal("ms-settings:privacy-speech", error.Problem.SettingsUri);
        Assert.Contains("Privacy > Speech", error.Problem.Detail, StringComparison.Ordinal);
    }

    // ---- Pure helpers ----

    [Theory]
    [InlineData(null, new[] { "en-US", "en-IN", "hi-IN" }, new[] { "en-IN" }, "en-IN")]
    [InlineData(null, new[] { "en-US" }, new[] { "en-IN", "en-IN" }, "en-US")]
    [InlineData(null, new[] { "en-us", "hi-IN" }, new[] { "hi-IN" }, "hi-IN")]
    [InlineData(null, new[] { "de-DE", "en-GB" }, new[] { "fr-FR" }, "en-GB")]
    [InlineData(null, new[] { "de-DE" }, new[] { "fr-FR" }, "de-DE")]
    [InlineData("hi-IN", new[] { "en-US" }, new[] { "en-US" }, null)]
    [InlineData("en-IN", new[] { "en-US", "EN-IN" }, new[] { "en-US" }, "EN-IN")]
    public void Recognizer_language_choice(string? requested, string[] supported, string[] preferred, string? expected) =>
        Assert.Equal(expected, VoiceLanguages.Pick(requested, supported, preferred));

    [Fact]
    public void Picking_from_no_language_gives_none() => Assert.Null(VoiceLanguages.Pick(null, [], ["en-IN"]));

    [Theory]
    [InlineData("", "draw it", "draw it")]
    [InlineData("Draw", "the plan", "Draw the plan")]
    [InlineData("Draw ", "the plan", "Draw the plan")]
    [InlineData("Line one\n", "line two", "Line one\nline two")]
    [InlineData("Keep", "  ", "Keep")]
    public void Spoken_words_are_appended(string existing, string spoken, string expected) =>
        Assert.Equal(expected, VoiceText.Append(existing, spoken));

    [Theory]
    [InlineData(unchecked((int)0x80045509), VoiceProblemKind.OnlineSpeechOff, "ms-settings:privacy-speech")]
    [InlineData(unchecked((int)0x80070005), VoiceProblemKind.MicrophoneBlocked, "ms-settings:privacy-microphone")]
    [InlineData(unchecked((int)0x8004503A), VoiceProblemKind.NoMicrophone, "ms-settings:sound")]
    [InlineData(unchecked((int)0x80072EE7), VoiceProblemKind.NoInternet, null)]
    public void Windows_speech_errors_are_explained(int hresult, VoiceProblemKind kind, string? settings)
    {
        var problem = VoiceProblems.FromHResult(hresult, "English (India)")!;
        Assert.Equal(kind, problem.Kind);
        Assert.Equal(settings, problem.SettingsUri);
        Assert.Null(VoiceProblems.FromHResult(unchecked((int)0x80004005), "English (India)"));
        Assert.Equal(VoiceProblemKind.MicrophoneBlocked, VoiceProblems.FromException(new UnauthorizedAccessException(), "x").Kind);
        Assert.Equal(VoiceProblemKind.Failed, VoiceProblems.FromException(new InvalidOperationException("boom"), "x").Kind);
    }

    [Fact]
    public void Language_missing_names_the_language_and_the_settings_page()
    {
        var problem = VoiceProblems.LanguageMissing(VoiceLanguages.DisplayName("hi-IN"));
        Assert.Equal("Hindi (India) speech is not installed", problem.Title);
        Assert.Contains("Time & Language > Language", problem.Detail, StringComparison.Ordinal);
        Assert.Equal("ms-settings:regionlanguage", problem.SettingsUri);
    }

    [Theory]
    [InlineData("Allow", "Allow", "Allow", false)]
    [InlineData("Deny", "Allow", "Allow", true)]
    [InlineData("Allow", "Allow", "Deny", true)]
    [InlineData("Allow", "Deny", "Allow", false)]
    [InlineData(null, null, null, false)]
    public void Microphone_privacy(string? device, string? apps, string? desktop, bool blocked) =>
        Assert.Equal(blocked, new VoicePrivacy(device, apps, desktop, null).MicrophoneBlocked);

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(0, null, false)]
    [InlineData(null, null, null)]
    [InlineData(1, 0, false)]
    public void Online_speech_privacy(int? accepted, int? policy, bool? allowed) =>
        Assert.Equal(allowed, new VoicePrivacy(null, null, null, accepted, policy).OnlineSpeechAllowed);

    [Fact]
    public void Reading_the_windows_privacy_settings_never_throws() => Assert.NotNull(VoicePrivacy.Read());

    private (ChatViewModel Vm, FakeBackend Backend, FakeSpeechFactory Factory, ManualClock Clock, List<string> Opened) Create()
    {
        var backend = FakeBackend.Create();
        var factory = new FakeSpeechFactory();
        var clock = new ManualClock();
        var opened = new List<string>();
        var service = new VoiceInputService(factory, clock, opened.Add);
        var vm = new ChatViewModel(
            backend,
            Dispatcher.CurrentDispatcher,
            null,
            new PanelSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json")),
            new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json")),
            service);
        return (vm, backend, factory, clock, opened);
    }

    // Real chat sessions over stand-in CLIs (nothing is sent in these tests).
    private CopilotBackend CreateHub()
    {
        var claude = FakeBackend.Create().Runners[0];
        return new CopilotBackend(
            [claude],
            () => new McpServerLaunch { Command = "AcadCopilotMcp.exe", Arguments = ["--pid", "4242", "--panel"] },
            new AcadSessionInfo(4242, "2027", "GF-plan-30x40.dwg"),
            new CopilotSettingsStore(Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-hub.json")),
            Path.Combine(_temp, "workspace"),
            () => new DrawingRef("dwg-1", "GF-plan-30x40.dwg"));
    }

    private static WindowsSpeechEngineFactory Factory(VoicePrivacy privacy, List<string> calls, VoiceProblem? online = null, VoiceProblem? offline = null) => new(
        () => privacy,
        (request, sink, _) =>
        {
            calls.Add("online");
            return online is null
                ? Task.FromResult<ISpeechEngine>(new FakeSpeechEngine(sink, request.LanguageTag ?? "en-IN", "online"))
                : Task.FromException<ISpeechEngine>(new VoiceException(online));
        },
        (request, sink, notice) =>
        {
            calls.Add("offline");
            return offline is null ? new FakeSpeechEngine(sink, "en-US", "offline", notice) : throw new VoiceException(offline);
        });

    private sealed class NullSink : ISpeechSink
    {
        public void OnHypothesis(string text)
        {
        }

        public void OnPhrase(string text)
        {
        }

        public void OnStopped(VoiceProblem? problem)
        {
        }
    }
}
