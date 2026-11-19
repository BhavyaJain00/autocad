using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>Where voice input writes: the composer of one chat tab.</summary>
public interface IVoiceTarget
{
    /// <summary>The message box text.</summary>
    string ComposerText { get; set; }

    /// <summary>The voice language chosen in the settings (null = Windows language).</summary>
    string? VoiceLanguageTag { get; }

    /// <summary>True when the message is sent as soon as the speaker stops.</summary>
    bool VoiceAutoSend { get; }

    /// <summary>Sends the message box (when sending is possible).</summary>
    void SendFromVoice();
}

/// <summary>What the mic is doing.</summary>
public enum VoiceState
{
    /// <summary>Not listening.</summary>
    Idle,

    /// <summary>The recognizer and microphone are starting.</summary>
    Starting,

    /// <summary>Listening: the words appear in the message box as they are recognized.</summary>
    Listening,

    /// <summary>Finishing the last phrase.</summary>
    Stopping,
}

/// <summary>
/// The mic button of one chat tab. Click (or Ctrl+Space) starts listening: the button turns red and pulses, the words
/// appear in the message box live (appended to what was typed), normalized for drafting ("30 by 40 feet" -> 30' x 40').
/// Click again, or about <see cref="SilenceAfterSpeech"/> of silence, stops; the text stays for editing and Enter, or is
/// sent at once with "Send automatically after speaking". Problems (no microphone, access blocked, online speech
/// recognition off, language missing) are explained with a link to the Windows Settings page. The recognizer runs off
/// the UI thread and is closed in the background (stop, tab closed, AutoCAD closing). UI thread only.
/// </summary>
public sealed class VoiceInputViewModel : ObservableObject, IDisposable
{
    /// <summary>Silence after speech that ends listening.</summary>
    public static readonly TimeSpan SilenceAfterSpeech = TimeSpan.FromSeconds(2);

    /// <summary>Silence before anything was said that ends listening.</summary>
    public static readonly TimeSpan SilenceBeforeSpeech = TimeSpan.FromSeconds(8);

    /// <summary>Longest wait for the recognizer to start.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(4);

    private readonly VoiceInputService _service;
    private readonly Dispatcher _dispatcher;
    private readonly IVoiceTarget _target;
    private readonly List<string> _phrases = [];
    private VoiceState _state;
    private ISpeechEngine? _engine;
    private CancellationTokenSource? _startCts;
    private Task? _stopTask;
    private DispatcherTimer? _silenceTimer;
    private int _generation;
    private string _baseText = string.Empty;
    private string _hypothesis = string.Empty;
    private bool _heardSpeech;
    private long _listeningSince;
    private long _lastHeard;
    private VoiceProblem? _problem;
    private string _engineText = string.Empty;
    private string? _engineNotice;
    private bool _disposed;

    /// <summary>Creates the mic of one tab.</summary>
    public VoiceInputViewModel(VoiceInputService service, Dispatcher dispatcher, IVoiceTarget target)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(target);
        _service = service;
        _dispatcher = dispatcher;
        _target = target;
        ToggleCommand = new RelayCommand(() => _ = ToggleAsync(), () => !_disposed && _state != VoiceState.Stopping);
        OpenProblemSettingsCommand = new RelayCommand(OpenProblemSettings, () => Problem?.SettingsUri is not null);
        DismissProblemCommand = new RelayCommand(() => Problem = null);
    }

    /// <summary>What the mic is doing.</summary>
    public VoiceState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertiesChanged(nameof(IsActive), nameof(IsListening), nameof(IsStarting), nameof(StatusText), nameof(MicToolTip), nameof(ShowStrip));
                ToggleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True while starting, listening or finishing (the message box is read-only meanwhile).</summary>
    public bool IsActive => _state != VoiceState.Idle;

    /// <summary>True while the mic button is red (starting or listening).</summary>
    public bool IsListening => _state is VoiceState.Starting or VoiceState.Listening;

    /// <summary>True while the recognizer starts.</summary>
    public bool IsStarting => _state == VoiceState.Starting;

    /// <summary>True once something was said in this listening session.</summary>
    public bool HeardSpeech
    {
        get => _heardSpeech;
        private set
        {
            if (Set(ref _heardSpeech, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>"Starting the microphone...", "Listening... speak now", "Listening...", "Finishing...".</summary>
    public string StatusText => _state switch
    {
        VoiceState.Starting => "Starting the microphone...",
        VoiceState.Listening when !HeardSpeech => "Listening... speak now",
        VoiceState.Listening => "Listening...",
        VoiceState.Stopping => "Finishing...",
        _ => string.Empty,
    };

    /// <summary>"English (India) · Windows dictation" while listening.</summary>
    public string EngineText
    {
        get => _engineText;
        private set => Set(ref _engineText, value);
    }

    /// <summary>Why the offline recognizer is used, or null.</summary>
    public string? EngineNotice
    {
        get => _engineNotice;
        private set
        {
            if (Set(ref _engineNotice, value))
            {
                OnPropertyChanged(nameof(HasEngineNotice));
            }
        }
    }

    /// <summary>True when <see cref="EngineNotice"/> is shown.</summary>
    public bool HasEngineNotice => !string.IsNullOrEmpty(EngineNotice);

    /// <summary>True when the listening strip above the message box is shown.</summary>
    public bool ShowStrip => IsActive;

    /// <summary>Tool tip of the mic button.</summary>
    public string MicToolTip => IsActive
        ? "Stop listening (Ctrl+Space). The words stay in the message box."
        : "Speak your request (Ctrl+Space). Stops by itself when you pause.";

    /// <summary>The problem shown above the message box, or null.</summary>
    public VoiceProblem? Problem
    {
        get => _problem;
        private set
        {
            if (Set(ref _problem, value))
            {
                OnPropertiesChanged(nameof(HasProblem), nameof(ProblemIsError), nameof(HasProblemSettings));
                OpenProblemSettingsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True when a problem is shown.</summary>
    public bool HasProblem => Problem is not null;

    /// <summary>True when the problem is an error (red) rather than information.</summary>
    public bool ProblemIsError => Problem?.IsError ?? false;

    /// <summary>True when the problem has a Windows Settings page.</summary>
    public bool HasProblemSettings => Problem?.SettingsUri is not null;

    /// <summary>Starts or stops listening.</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>Opens the Windows Settings page of the problem.</summary>
    public RelayCommand OpenProblemSettingsCommand { get; }

    /// <summary>Hides the problem.</summary>
    public RelayCommand DismissProblemCommand { get; }

    /// <summary>Starts listening, or stops when listening.</summary>
    public Task ToggleAsync() => IsActive ? StopAsync() : StartAsync();

    /// <summary>Starts listening (nothing when already active).</summary>
    public async Task StartAsync()
    {
        if (_disposed || _state != VoiceState.Idle)
        {
            return;
        }

        var generation = ++_generation;
        Problem = null;
        EngineText = string.Empty;
        EngineNotice = null;
        State = VoiceState.Starting;
        if (_service.Active is VoiceInputViewModel other && !ReferenceEquals(other, this))
        {
            // One microphone: the other tab stops first (keeping its words).
            await other.StopAsync(allowAutoSend: false);
            if (generation != _generation || _disposed)
            {
                return;
            }
        }

        _service.Active = this;
        _baseText = _target.ComposerText;
        _phrases.Clear();
        _hypothesis = string.Empty;
        HeardSpeech = false;
        var cts = new CancellationTokenSource();
        _startCts = cts;

        ISpeechEngine engine;
        Task<ISpeechEngine>? starting = null;
        try
        {
            starting = _service.Factory.StartAsync(new VoiceRequest(_target.VoiceLanguageTag), new Sink(this, generation), cts.Token);
            engine = await starting.WaitAsync(StartTimeout, cts.Token);
        }
        catch (Exception ex)
        {
            if (starting is not null)
            {
                CloseWhenStarted(starting);
            }

            if (generation == _generation)
            {
                var problem = ex switch
                {
                    VoiceException voice => voice.Problem,
                    OperationCanceledException => null,
                    TimeoutException => VoiceProblems.Failed("Windows speech recognition did not start. Click the mic to try again."),
                    _ => VoiceProblems.Failed(ex.Message),
                };
                if (problem is not null && problem.Kind == VoiceProblemKind.Failed)
                {
                    CopilotLog.Warn("Voice input could not start", ex);
                }

                EndSession();
                Problem = problem;
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_startCts, cts))
            {
                _startCts = null;
            }

            cts.Dispose();
        }

        if (generation != _generation || _disposed)
        {
            // Stopped (or the tab closed) while starting.
            CloseInBackground(engine);
            return;
        }

        _engine = engine;
        EngineText = VoiceLanguages.DisplayName(engine.LanguageTag) + " · " + engine.Name;
        EngineNotice = engine.Notice;
        _listeningSince = _service.Time.GetTimestamp();
        _lastHeard = _listeningSince;
        State = VoiceState.Listening;
        StartSilenceTimer();
    }

    /// <summary>Stops listening and keeps the words in the message box (sent at once with auto-send).</summary>
    /// <param name="allowAutoSend">False when the caller sends anyway (Enter while listening) or another tab takes the mic.</param>
    public Task StopAsync(bool allowAutoSend = true) => StopCoreAsync(allowAutoSend, problem: null);

    /// <summary>Stops without waiting for the last phrase and closes the recognizer in the background (tab closing).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        _startCts?.Cancel();
        StopSilenceTimer();
        if (_engine is { } engine)
        {
            _engine = null;
            CloseInBackground(engine);
        }

        if (ReferenceEquals(_service.Active, this))
        {
            _service.Active = null;
        }

        ToggleCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Checks the silence time-out (the timer calls this every 200 ms; tests call it directly).</summary>
    internal void CheckSilence()
    {
        if (_state != VoiceState.Listening)
        {
            return;
        }

        var quiet = _service.Time.GetElapsedTime(HeardSpeech ? _lastHeard : _listeningSince);
        if (HeardSpeech && quiet >= SilenceAfterSpeech)
        {
            _ = StopCoreAsync(allowAutoSend: true, problem: null);
        }
        else if (!HeardSpeech && quiet >= SilenceBeforeSpeech)
        {
            _ = StopCoreAsync(allowAutoSend: false, problem: VoiceProblems.NothingHeard());
        }
    }

    private async Task StopCoreAsync(bool allowAutoSend, VoiceProblem? problem)
    {
        if (_state == VoiceState.Stopping && _stopTask is { } running)
        {
            await running;
            return;
        }

        if (_state == VoiceState.Starting)
        {
            // Nothing recognized yet: cancel the start; the late engine is closed when it arrives.
            _generation++;
            _startCts?.Cancel();
            EndSession();
            return;
        }

        if (_state != VoiceState.Listening)
        {
            return;
        }

        var stop = FinishAsync(allowAutoSend, problem);
        _stopTask = stop;
        await stop;
    }

    private async Task FinishAsync(bool allowAutoSend, VoiceProblem? problem)
    {
        State = VoiceState.Stopping;
        StopSilenceTimer();
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            try
            {
                // The recognizer delivers the phrase being spoken before StopAsync ends; its events are queued on this
                // dispatcher, so let them run before reading the words.
                await Task.Run(engine.StopAsync).WaitAsync(StopTimeout);
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Voice input: stopping the recognizer failed", ex);
            }

            await _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
            CloseInBackground(engine);
        }

        _generation++;
        var spoken = VoiceText.Spoken(_phrases, _hypothesis);
        if (!_disposed)
        {
            _target.ComposerText = VoiceText.Append(_baseText, spoken);
        }

        EndSession();
        if (problem is not null && (problem.IsError || spoken.Length == 0))
        {
            Problem = problem;
        }

        if (allowAutoSend && spoken.Length > 0 && !_disposed && _target.VoiceAutoSend)
        {
            _target.SendFromVoice();
        }
    }

    // Back to idle (the words already in the message box stay).
    private void EndSession()
    {
        StopSilenceTimer();
        _stopTask = null;
        State = VoiceState.Idle;
        if (ReferenceEquals(_service.Active, this))
        {
            _service.Active = null;
        }
    }

    private void OnHypothesis(int generation, string text)
    {
        if (generation != _generation || _state is not (VoiceState.Listening or VoiceState.Stopping))
        {
            return;
        }

        _hypothesis = text;
        Heard();
    }

    private void OnPhrase(int generation, string text)
    {
        if (generation != _generation || _state is not (VoiceState.Listening or VoiceState.Stopping))
        {
            return;
        }

        _phrases.Add(text);
        _hypothesis = string.Empty;
        Heard();
    }

    private void OnStopped(int generation, VoiceProblem? problem)
    {
        if (generation != _generation || _state != VoiceState.Listening)
        {
            return;
        }

        _ = StopCoreAsync(allowAutoSend: problem is null, problem ?? (HeardSpeech ? null : VoiceProblems.NothingHeard()));
    }

    private void Heard()
    {
        _lastHeard = _service.Time.GetTimestamp();
        HeardSpeech = true;
        _target.ComposerText = VoiceText.Append(_baseText, VoiceText.Spoken(_phrases, _hypothesis));
    }

    private void StartSilenceTimer()
    {
        _silenceTimer ??= CreateTimer();
        _silenceTimer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => CheckSilence();
        return timer;
    }

    private void StopSilenceTimer() => _silenceTimer?.Stop();

    private void OpenProblemSettings()
    {
        if (Problem?.SettingsUri is { } uri)
        {
            _service.OpenSettings(uri);
        }
    }

    // Closing a recognizer can take a moment (it releases the audio device): never on the UI thread.
    private static void CloseInBackground(ISpeechEngine engine) => _ = Task.Run(() =>
    {
        try
        {
            engine.Dispose();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Voice input: closing the recognizer failed", ex);
        }
    });

    // A recognizer that finished starting after the panel stopped waiting for it is closed as soon as it is there.
    private static void CloseWhenStarted(Task<ISpeechEngine> starting) => _ = starting.ContinueWith(
        task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                CloseInBackground(task.Result);
            }
            else
            {
                _ = task.Exception;
            }
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CopilotLog.Error("Voice input update failed", ex);
            }
        });
    }

    /// <summary>Marshals one listening session's recognizer events to the UI thread (stale sessions are ignored there).</summary>
    private sealed class Sink(VoiceInputViewModel owner, int generation) : ISpeechSink
    {
        public void OnHypothesis(string text) => owner.Post(() => owner.OnHypothesis(generation, text ?? string.Empty));

        public void OnPhrase(string text) => owner.Post(() => owner.OnPhrase(generation, text ?? string.Empty));

        public void OnStopped(VoiceProblem? problem) => owner.Post(() => owner.OnStopped(generation, problem));
    }
}
