using AcadCopilot.Plugin.Palette;

namespace AcadCopilot.Plugin.ViewModels;

/// <summary>
/// The panel's drawing-area controls, shared by every chat tab: the Focus button in the header (more room for the
/// drawing: ribbon minimised, ViewCube and navigation bar hidden) and the settings "Use focus mode when the AI Copilot
/// opens" and "Follow the AI's drawing" (panel.json). UI thread only.
/// </summary>
public sealed class FocusLayoutViewModel : ObservableObject, IDisposable
{
    /// <summary>Tool tip of the Focus button.</summary>
    public const string FocusToolTipText = "Focus mode: more room for the drawing";

    private readonly IFocusLayoutHost? _host;
    private readonly PanelSettingsStore _store;
    private readonly Action<string>? _reportProblem;
    private PanelSettings _settings;

    /// <summary>Creates the view model.</summary>
    /// <param name="host">Focus mode inside AutoCAD; null hides the Focus button (no AutoCAD).</param>
    /// <param name="store">panel.json.</param>
    /// <param name="reportProblem">Shows what focus mode could not do (the notice banner of the tab shown).</param>
    public FocusLayoutViewModel(IFocusLayoutHost? host, PanelSettingsStore store, Action<string>? reportProblem = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _host = host;
        _store = store;
        _reportProblem = reportProblem;
        _settings = store.Load();
        ToggleFocusCommand = new RelayCommand(ToggleFocus, () => CanFocus);
        if (host is not null)
        {
            host.Changed += OnHostChanged;
        }
    }

    /// <summary>True when focus mode is available (inside AutoCAD).</summary>
    public bool CanFocus => _host is not null;

    /// <summary>True while focus mode is on (the Focus button is highlighted).</summary>
    public bool IsFocusMode => _host?.IsFocusMode == true;

    /// <summary>Tool tip of the Focus button: what it does and its state.</summary>
    public string FocusToolTip => IsFocusMode
        ? FocusToolTipText + " (on). Click to bring back the full ribbon, the ViewCube and the navigation bar."
        : FocusToolTipText + ". Minimises the ribbon and hides the ViewCube and navigation bar; the command line stays.";

    /// <summary>Switches focus mode on or off.</summary>
    public RelayCommand ToggleFocusCommand { get; }

    /// <summary>Use focus mode whenever the AI Copilot opens (persisted; default on).</summary>
    public bool FocusModeOnOpen
    {
        get => _settings.FocusModeOnOpen;
        set
        {
            if (_settings.FocusModeOnOpen != value)
            {
                Update(settings => settings with { FocusModeOnOpen = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Zoom to what the AI drew when it is off screen or too small (persisted; default on).</summary>
    public bool FollowAiDrawing
    {
        get => _settings.FollowAiDrawing;
        set
        {
            if (_settings.FollowAiDrawing != value)
            {
                Update(settings => settings with { FollowAiDrawing = value });
                OnPropertyChanged();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_host is not null)
        {
            _host.Changed -= OnHostChanged;
        }
    }

    private void ToggleFocus()
    {
        if (_host is null)
        {
            return;
        }

        if (_host.SetFocusMode(!_host.IsFocusMode) is { } problem)
        {
            _reportProblem?.Invoke(problem);
        }
    }

    private void OnHostChanged(object? sender, EventArgs e) => OnPropertiesChanged(nameof(IsFocusMode), nameof(FocusToolTip));

    // Read-modify-write of panel.json, so the chat tabs' own settings are kept.
    private void Update(Func<PanelSettings, PanelSettings> change)
    {
        _settings = change(_store.Load());
        _store.Save(_settings);
    }
}
