using System.IO;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.Ui.Tests;

/// <summary>
/// The composer's voice states rendered off-screen (no window, no microphone: a stand-in recognizer) to
/// artifacts\ui\13-voice-*.png: the idle mic, listening with live text, the offline hint, a problem with its Windows
/// Settings link, and the voice settings.
/// </summary>
public sealed class VoiceRenderTests : IDisposable
{
    private const double Width = 430;
    private const double NarrowWidth = 320;
    private const double Height = 760;

    private readonly string _temp;

    public VoiceRenderTests()
    {
        TestEnvironment.Isolate();
        _temp = Directory.CreateTempSubdirectory("acadcopilot-voice-ui-").FullName;
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

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Idle_mic_in_the_composer(PanelTheme theme) => Sta.Run(async () =>
    {
        var (pane, _) = await CreatePaneAsync(theme);
        var vm = pane.ViewModel;
        Assert.True(vm.HasVoice);
        Assert.Equal(VoiceState.Idle, vm.Voice!.State);
        Assert.True(pane.MicButton.IsVisible || pane.MicButton.Visibility == System.Windows.Visibility.Visible);
        Assert.Equal("Speak your request (Ctrl+Space). Stops by itself when you pause.", vm.Voice.MicToolTip);
        Save(pane, "13-voice-idle", theme);
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light, Width)]
    [InlineData(PanelTheme.Dark, Width)]
    [InlineData(PanelTheme.Light, NarrowWidth)]
    public void Listening_with_live_text(PanelTheme theme, double width) => Sta.Run(async () =>
    {
        var (pane, factory) = await CreatePaneAsync(theme);
        var vm = pane.ViewModel;
        vm.ComposerText = "Draw the ground floor plan for";
        await vm.Voice!.StartAsync();
        factory.Last!.Sink.OnPhrase("a thirty by forty feet east facing plot");
        factory.Last.Sink.OnHypothesis("with nine inch outer walls and four and a half inch");
        await Sta.Settle();

        Assert.True(vm.Voice.IsListening);
        Assert.True(pane.Composer.IsReadOnly);
        Assert.Equal("Draw the ground floor plan for a 30' x 40' east-facing plot with 9\" outer walls and 4 1/2\"", vm.ComposerText);
        Save(pane, "13-voice-listening", theme, width);
        vm.Dispose();
    });

    [Fact]
    public void Starting_the_microphone() => Sta.Run(async () =>
    {
        var (pane, factory) = await CreatePaneAsync(PanelTheme.Dark);
        factory.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = pane.ViewModel;
        var start = vm.Voice!.StartAsync();
        await Sta.Settle();
        Assert.Equal("Starting the microphone...", vm.Voice.StatusText);
        Assert.Equal("One moment: starting the microphone...", vm.ComposerPlaceholder);
        Save(pane, "13-voice-starting", PanelTheme.Dark);
        factory.Gate.SetResult();
        await start;
        vm.Dispose();
    });

    [Fact]
    public void Listening_with_the_offline_recognizer_hint() => Sta.Run(async () =>
    {
        var (pane, factory) = await CreatePaneAsync(PanelTheme.Light);
        factory.LanguageTag = "en-US";
        factory.EngineName = "Windows offline speech";
        factory.Notice = "Offline recognizer. For better accuracy turn on Windows Settings > Privacy > Speech > Online speech recognition.";
        var vm = pane.ViewModel;
        await vm.Voice!.StartAsync();
        Assert.True(vm.Voice.HasEngineNotice);
        Assert.Equal("English (US) · Windows offline speech", vm.Voice.EngineText);
        Save(pane, "13-voice-offline", PanelTheme.Light);
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light, "microphone-blocked")]
    [InlineData(PanelTheme.Dark, "microphone-blocked")]
    [InlineData(PanelTheme.Light, "online-speech-off")]
    [InlineData(PanelTheme.Light, "no-microphone")]
    [InlineData(PanelTheme.Dark, "nothing-heard")]
    public void Problem_explained_above_the_message_box(PanelTheme theme, string which) => Sta.Run(async () =>
    {
        var (pane, factory) = await CreatePaneAsync(theme);
        factory.Failure = which switch
        {
            "microphone-blocked" => VoiceProblems.MicrophoneBlocked(),
            "online-speech-off" => VoiceProblems.OnlineSpeechOff("English (India)"),
            "no-microphone" => VoiceProblems.NoMicrophone(),
            _ => VoiceProblems.NothingHeard(),
        };
        var vm = pane.ViewModel;
        vm.ComposerText = "Draw the front elevation";
        await vm.Voice!.StartAsync();
        Assert.True(vm.Voice.HasProblem);
        Assert.Equal(which != "nothing-heard", vm.Voice.ProblemIsError);
        Assert.False(pane.Composer.IsReadOnly);
        Save(pane, "13-voice-problem-" + which, theme);
        vm.Dispose();
    });

    [Theory]
    [InlineData(PanelTheme.Light)]
    [InlineData(PanelTheme.Dark)]
    public void Voice_settings(PanelTheme theme) => Sta.Run(async () =>
    {
        var (pane, _) = await CreatePaneAsync(theme);
        var vm = pane.ViewModel;
        vm.VoiceAutoSend = true;
        vm.SelectedVoiceLanguage = vm.VoiceLanguageChoices.Single(c => c.Tag == "en-IN");
        vm.IsSettingsOpen = true;
        await Sta.Settle();
        Save(pane, "13-voice-settings", theme, Width, 900);
        vm.Dispose();
    });

    private async Task<(CopilotPane Pane, FakeSpeechFactory Factory)> CreatePaneAsync(PanelTheme theme)
    {
        var factory = new FakeSpeechFactory();
        var pane = new CopilotPane(FakeBackend.Create(), new CopilotPaneOptions
        {
            Theme = theme,
            FilePicker = new NoFiles(),
            PanelSettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".json"),
            SettingsPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + "-settings.json"),
            CheckProviderStatusOnLoad = false,
            Voice = new VoiceInputService(factory, new ManualClock(), _ => { }),
        });
        await pane.ViewModel.RefreshStatusesAsync();
        return (pane, factory);
    }

    private static void Save(CopilotPane pane, string name, PanelTheme theme, double width = Width, double height = Height)
    {
        var suffix = width == Width ? string.Empty : "-narrow";
        var path = Snapshot.Render(pane, width, height, $"{name}{suffix}-{theme.ToString().ToLowerInvariant()}");
        Assert.True(new FileInfo(path).Length > 1000, "PNG was not written: " + path);
    }

    private sealed class NoFiles : IFilePicker
    {
        public IReadOnlyList<string> PickFiles() => [];
    }
}
