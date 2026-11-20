using System.Windows.Threading;
using AcadCopilot.Plugin.Interaction;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>How the Quick Ask box was opened.</summary>
public enum QuickAskOpenReason
{
    /// <summary>The floating AI button (the cursor is on the button, so no cursor point is taken).</summary>
    Button,

    /// <summary>The keyboard shortcut (the cursor is in the drawing).</summary>
    Shortcut,

    /// <summary>The AI / QUICKAI command.</summary>
    Command,
}

/// <summary>
/// The compact Quick Ask box shown next to the cursor in the drawing area: one line of text, the point chip (where the
/// cursor was, or the point clicked with Pick), the selection chip, the mic (the panel's voice input) and Send. Enter
/// sends, Esc closes. The window layer listens to <see cref="SendRequested"/>, <see cref="PickRequested"/> and
/// <see cref="CloseRequested"/>. UI thread only.
/// </summary>
public sealed class QuickAskBoxViewModel : ObservableObject, IVoiceTarget, IDisposable
{
    private readonly PanelSettings _panelSettings;
    private string _text = string.Empty;
    private PickedPoint? _point;
    private QuickAskPointSource _pointSource;
    private SelectionSnapshot? _selection;
    private bool _isPicking;
    private string? _problem;
    private bool _disposed;

    /// <summary>Creates the box.</summary>
    /// <param name="dispatcher">The UI dispatcher (AutoCAD's main thread).</param>
    /// <param name="reason">How it was opened.</param>
    /// <param name="cursorPoint">Where the cursor was in the drawing (null: unknown or opened from the button).</param>
    /// <param name="selection">The user's selection now (null or empty: none).</param>
    /// <param name="drawingTitle">The active drawing ("House.dwg"), for the hint line.</param>
    /// <param name="voice">The panel's voice input (null hides the mic).</param>
    /// <param name="panelSettings">The panel's settings (voice language and "send automatically after speaking").</param>
    public QuickAskBoxViewModel(
        Dispatcher dispatcher,
        QuickAskOpenReason reason,
        PickedPoint? cursorPoint,
        SelectionSnapshot? selection,
        string? drawingTitle,
        VoiceInputService? voice = null,
        PanelSettings? panelSettings = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        Reason = reason;
        DrawingTitle = drawingTitle;
        _panelSettings = panelSettings ?? new PanelSettings();
        if (cursorPoint is not null)
        {
            _point = cursorPoint;
            _pointSource = QuickAskPointSource.Cursor;
        }

        _selection = selection is { IsEmpty: false } ? selection : null;
        Voice = voice is null ? null : new VoiceInputViewModel(voice, dispatcher, this);
        if (Voice is not null)
        {
            Voice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(VoiceInputViewModel.State) or nameof(VoiceInputViewModel.IsActive))
                {
                    OnPropertiesChanged(nameof(Placeholder), nameof(StatusLine), nameof(HasStatusLine));
                }
                else if (e.PropertyName is nameof(VoiceInputViewModel.Problem))
                {
                    OnPropertiesChanged(nameof(StatusLine), nameof(HasStatusLine), nameof(StatusIsError));
                }
            };
        }

        SendCommand = new RelayCommand(RequestSend, () => CanSend);
        PickCommand = new RelayCommand(() => PickRequested?.Invoke(this, EventArgs.Empty), () => !IsPicking);
        RemovePointCommand = new RelayCommand(() => SetPoint(null, QuickAskPointSource.None));
        RemoveSelectionCommand = new RelayCommand(() => Selection = null);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised by Enter / Send (and by voice auto-send).</summary>
    public event EventHandler? SendRequested;

    /// <summary>Raised by Pick: the window hides while the user clicks in the drawing.</summary>
    public event EventHandler? PickRequested;

    /// <summary>Raised by Esc or the x.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>How the box was opened.</summary>
    public QuickAskOpenReason Reason { get; }

    /// <summary>The active drawing.</summary>
    public string? DrawingTitle { get; }

    /// <summary>The request typed (or spoken).</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <inheritdoc />
    string IVoiceTarget.ComposerText
    {
        get => Text;
        set => Text = value;
    }

    /// <inheritdoc />
    string? IVoiceTarget.VoiceLanguageTag => _panelSettings.VoiceLanguage;

    /// <inheritdoc />
    bool IVoiceTarget.VoiceAutoSend => _panelSettings.VoiceAutoSend;

    /// <inheritdoc />
    void IVoiceTarget.SendFromVoice() => RequestSend();

    /// <summary>"Ask the AI to draw or change something..." / "Speak now..." while listening.</summary>
    public string Placeholder => Voice?.State switch
    {
        VoiceState.Starting => "One moment: starting the microphone...",
        VoiceState.Listening or VoiceState.Stopping => "Speak now: your words appear here...",
        _ => "Ask the AI... e.g. make the kitchen 10 by 20",
    };

    /// <summary>The mic, or null when voice input is not available.</summary>
    public VoiceInputViewModel? Voice { get; }

    /// <summary>True when the mic button is shown.</summary>
    public bool HasVoice => Voice is not null;

    /// <summary>The point that goes with the request, or null.</summary>
    public PickedPoint? Point => _point;

    /// <summary>Where <see cref="Point"/> came from.</summary>
    public QuickAskPointSource PointSource => _pointSource;

    /// <summary>True when the point chip is shown.</summary>
    public bool HasPoint => _point is not null && _pointSource != QuickAskPointSource.None;

    /// <summary>"Cursor 12'-6", 30'-0"" or "Point 12'-6", 30'-0"".</summary>
    public string PointChipText
    {
        get
        {
            if (_point is not { } point)
            {
                return string.Empty;
            }

            var where = point.XText is { Length: > 0 } x && point.YText is { Length: > 0 } y
                ? $"{x}, {y}"
                : $"{InteractionText.Number(point.X)}, {InteractionText.Number(point.Y)}";
            return (_pointSource == QuickAskPointSource.Picked ? "Point " : "Cursor ") + where;
        }
    }

    /// <summary>Tool tip of the point chip.</summary>
    public string PointChipToolTip => _pointSource == QuickAskPointSource.Picked
        ? "The point you clicked goes with the request. Click x to leave it out."
        : "Where your cursor was in the drawing goes with the request, so the AI knows where you mean. Click x to leave it out, or Pick another point.";

    /// <summary>The selection that goes with the request, or null.</summary>
    public SelectionSnapshot? Selection
    {
        get => _selection;
        private set
        {
            if (Set(ref _selection, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(SelectionChipText), nameof(SelectionChipToolTip));
            }
        }
    }

    /// <summary>True when the selection chip is shown.</summary>
    public bool HasSelection => _selection is not null;

    /// <summary>"5 objects selected".</summary>
    public string SelectionChipText => _selection is { } selection ? InteractionText.SelectionChip(selection) : string.Empty;

    /// <summary>The selection chip's tool tip.</summary>
    public string SelectionChipToolTip => _selection is { } selection ? InteractionText.SelectionToolTip(selection) : string.Empty;

    /// <summary>True while the user clicks a point in the drawing (the box is hidden meanwhile).</summary>
    public bool IsPicking
    {
        get => _isPicking;
        set
        {
            if (Set(ref _isPicking, value))
            {
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
                PickCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>A problem to show under the text (pick failed ...), or null.</summary>
    public string? Problem
    {
        get => _problem;
        set
        {
            if (Set(ref _problem, value))
            {
                OnPropertiesChanged(nameof(StatusLine), nameof(HasStatusLine), nameof(StatusIsError));
            }
        }
    }

    /// <summary>The line under the text: the mic's state or problem, or a pick problem; empty when none.</summary>
    public string StatusLine => Voice is { IsActive: true } voice
        ? voice.StatusText
        : Voice?.Problem is { } voiceProblem ? voiceProblem.Title + ". " + voiceProblem.Detail : Problem ?? string.Empty;

    /// <summary>True when <see cref="StatusLine"/> is shown.</summary>
    public bool HasStatusLine => StatusLine.Length > 0;

    /// <summary>True when the status line is a problem (red).</summary>
    public bool StatusIsError => Voice is not { IsActive: true } && (Voice?.Problem is { IsError: true } || Problem is not null);

    /// <summary>"Enter to send · Esc to close · Chat of House.dwg".</summary>
    public string HintText => DrawingTitle is { Length: > 0 } title
        ? $"Enter to send · Esc to close · goes to the chat of {title}"
        : "Enter to send · Esc to close";

    /// <summary>True when Send is possible.</summary>
    public bool CanSend => !string.IsNullOrWhiteSpace(Text) && !IsPicking && !_disposed;

    /// <summary>Sends the request.</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>Lets the user click the point for the request.</summary>
    public RelayCommand PickCommand { get; }

    /// <summary>Leaves the point out.</summary>
    public RelayCommand RemovePointCommand { get; }

    /// <summary>Leaves the selection out.</summary>
    public RelayCommand RemoveSelectionCommand { get; }

    /// <summary>Closes the box.</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>Uses <paramref name="point"/> (from Pick) or leaves the point out (null / None).</summary>
    public void SetPoint(PickedPoint? point, QuickAskPointSource source)
    {
        _point = source == QuickAskPointSource.None ? null : point;
        _pointSource = _point is null ? QuickAskPointSource.None : source;
        OnPropertiesChanged(nameof(Point), nameof(PointSource), nameof(HasPoint), nameof(PointChipText), nameof(PointChipToolTip));
    }

    /// <summary>The message this box sends now.</summary>
    public QuickAskMessage Compose() => QuickAskText.Compose(Text, _point, _pointSource, _selection);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Voice?.Dispose();
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RequestSend()
    {
        if (CanSend)
        {
            SendRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
