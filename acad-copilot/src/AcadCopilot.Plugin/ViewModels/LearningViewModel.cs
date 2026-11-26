using System.Globalization;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Learning;
using AcadCopilot.Plugin.Learning;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// The "Learn from me" bar of the panel, shown above every chat tab: <b>Start learning</b>; while recording a red
/// "Learning from you · 03:12 · 47 actions" with <b>Pause</b> / <b>Resume</b>, <b>Stop &amp; learn</b> and <b>Discard</b>
/// (which asks first). One recorder for the whole panel, so the state survives tab switches. Stop &amp; learn saves the
/// recording and hands it to the chat (<see cref="CopilotPanelViewModel"/> sends "Learn from my recording ..." in the tab
/// shown). UI thread only.
/// </summary>
public sealed class LearningViewModel : ObservableObject, IDisposable
{
    private readonly IUserActionRecorder _recorder;
    private readonly Dispatcher _dispatcher;
    private readonly Action<RecordingOutcome> _learned;
    private readonly Action<string, bool> _notice;
    private readonly DispatcherTimer _timer;
    private bool _isSaving;
    private bool _isConfirmingDiscard;
    private bool _disposed;

    /// <summary>Creates the bar over <paramref name="recorder"/>.</summary>
    /// <param name="recorder">The session's recorder.</param>
    /// <param name="dispatcher">The panel's dispatcher.</param>
    /// <param name="learned">Called after Stop &amp; learn saved a recording (sends it to the chat).</param>
    /// <param name="notice">Shows a short message in the tab shown (text, is error).</param>
    public LearningViewModel(IUserActionRecorder recorder, Dispatcher dispatcher, Action<RecordingOutcome> learned, Action<string, bool> notice)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _learned = learned ?? throw new ArgumentNullException(nameof(learned));
        _notice = notice ?? throw new ArgumentNullException(nameof(notice));
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RaiseCounters();

        StartCommand = new RelayCommand(Start, () => IsOff && !IsSaving);
        PauseCommand = new RelayCommand(() => _recorder.Pause(), () => IsRecording && !IsSaving);
        ResumeCommand = new RelayCommand(() => _recorder.Resume(), () => IsPaused && !IsSaving);
        StopAndLearnCommand = new RelayCommand(() => _ = StopAndLearnAsync(), () => IsActive && !IsSaving);
        DiscardCommand = new RelayCommand(() => IsConfirmingDiscard = true, () => IsActive && !IsSaving);
        ConfirmDiscardCommand = new RelayCommand(Discard);
        CancelDiscardCommand = new RelayCommand(() => IsConfirmingDiscard = false);

        _recorder.Changed += OnRecorderChanged;
        UpdateTimer();
    }

    /// <summary>Recording, paused or off.</summary>
    public RecorderState State => _recorder.State;

    /// <summary>True while nothing is recorded (the Start learning button is shown).</summary>
    public bool IsOff => State == RecorderState.Off;

    /// <summary>True while the user's work is captured (red bar).</summary>
    public bool IsRecording => State == RecorderState.Recording;

    /// <summary>True while paused.</summary>
    public bool IsPaused => State == RecorderState.Paused;

    /// <summary>True while a recording exists (recording or paused).</summary>
    public bool IsActive => State != RecorderState.Off;

    /// <summary>True while Stop &amp; learn saves the recording.</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (Set(ref _isSaving, value))
            {
                OnPropertiesChanged(nameof(ShowIdle), nameof(ShowActive));
                RaiseCommands();
            }
        }
    }

    /// <summary>True while "Discard this recording?" is asked.</summary>
    public bool IsConfirmingDiscard
    {
        get => _isConfirmingDiscard;
        private set
        {
            if (Set(ref _isConfirmingDiscard, value))
            {
                OnPropertiesChanged(nameof(ShowActive), nameof(DiscardQuestion));
            }
        }
    }

    /// <summary>The idle row (Start learning) is shown.</summary>
    public bool ShowIdle => IsOff && !IsSaving;

    /// <summary>The recording / paused bar is shown.</summary>
    public bool ShowActive => IsActive && !IsSaving && !IsConfirmingDiscard;

    /// <summary>"03:12" recorded (without pauses).</summary>
    public string ElapsedText => RecordingMarkdown.Duration(_recorder.Elapsed);

    /// <summary>"47 actions".</summary>
    public string ActionsText => _recorder.ActionCount == 1 ? "1 action" : _recorder.ActionCount.ToString("N0", CultureInfo.InvariantCulture) + " actions";

    /// <summary>"Learning from you" or "Paused".</summary>
    public string HeadlineText => IsPaused ? "Paused" : "Learning from you";

    /// <summary>"03:12 · 47 actions".</summary>
    public string CountersText => ElapsedText + " · " + ActionsText;

    /// <summary>The whole status line ("Learning from you · 03:12 · 47 actions").</summary>
    public string StatusText => HeadlineText + " · " + CountersText;

    /// <summary>The discard question.</summary>
    public string DiscardQuestion => $"Discard this recording ({ActionsText})? It is deleted and nothing is learned.";

    /// <summary>Idle banner text shown on the Learning bar.</summary>
    public string IdleBannerText => AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase)
        ? "Teach it by modeling: it learns from your own Blender actions"
        : "Teach it by drawing: it learns from your own commands";

    /// <summary>Tool tip of Start learning.</summary>
    public string StartToolTip => AcadCopilot.Core.AppPaths.Root.Contains("BlenderCopilot", StringComparison.OrdinalIgnoreCase)
        ? "Records your own 3D modeling actions in Blender 5.3 until you stop it. The AI's own actions are never recorded. Stop & learn saves it on this PC and asks the AI to learn your 3D workflow from it."
        : "Records your own work in AutoCAD (commands, what you type, the objects you draw) until you stop it. The AI's own commands are never recorded. Stop & learn saves it on this PC and asks the AI to learn your way of drawing from it.";

    /// <summary>Starts recording.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Pauses (nothing is lost).</summary>
    public RelayCommand PauseCommand { get; }

    /// <summary>Resumes after a pause.</summary>
    public RelayCommand ResumeCommand { get; }

    /// <summary>Stops, saves and lets the AI learn from it.</summary>
    public RelayCommand StopAndLearnCommand { get; }

    /// <summary>Asks whether to discard.</summary>
    public RelayCommand DiscardCommand { get; }

    /// <summary>Discards the recording.</summary>
    public RelayCommand ConfirmDiscardCommand { get; }

    /// <summary>Keeps recording.</summary>
    public RelayCommand CancelDiscardCommand { get; }

    /// <summary>Stops, saves and hands the recording to the chat (also used by tests).</summary>
    public async Task StopAndLearnAsync()
    {
        if (!IsActive || IsSaving)
        {
            return;
        }

        IsConfirmingDiscard = false;
        IsSaving = true;
        RecordingOutcome outcome;
        try
        {
            outcome = await _recorder.StopAndSaveAsync();
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Learn from me: Stop & learn failed", ex);
            outcome = new RecordingOutcome(null, 0, null, "The recording could not be saved: " + ex.Message);
        }
        finally
        {
            IsSaving = false;
            Refresh();
        }

        if (outcome.FilePath is null)
        {
            _notice(outcome.Problem ?? "Nothing was recorded.", outcome.Problem is not null && outcome.Actions > 0);
            return;
        }

        _learned(outcome);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _recorder.Changed -= OnRecorderChanged;
    }

    private void Start()
    {
        string? problem;
        try
        {
            problem = _recorder.Start();
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Learn from me: Start learning failed", ex);
            problem = "Could not start learning: " + ex.Message;
        }

        if (problem is not null)
        {
            _notice(problem, true);
        }

        Refresh();
    }

    private void Discard()
    {
        IsConfirmingDiscard = false;
        try
        {
            _recorder.Discard();
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Learn from me: Discard failed", ex);
        }

        Refresh();
        _notice("Recording discarded. Nothing was saved.", false);
    }

    private void OnRecorderChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _ = _dispatcher.BeginInvoke(new Action(Refresh));
        }
    }

    private void Refresh()
    {
        if (!IsActive)
        {
            IsConfirmingDiscard = false;
        }

        OnPropertiesChanged(nameof(State), nameof(IsOff), nameof(IsRecording), nameof(IsPaused), nameof(IsActive), nameof(ShowIdle), nameof(ShowActive), nameof(HeadlineText));
        RaiseCounters();
        RaiseCommands();
        UpdateTimer();
    }

    private void RaiseCounters() => OnPropertiesChanged(nameof(ElapsedText), nameof(ActionsText), nameof(CountersText), nameof(StatusText), nameof(DiscardQuestion));

    private void RaiseCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StopAndLearnCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTimer()
    {
        if (IsRecording && !_disposed)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}
