using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Voice;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;

namespace AcadCopilot.Plugin.Views;

/// <summary>Options for <see cref="CopilotPane"/> (tests and hosts other than AutoCAD's dockable pane).</summary>
public sealed record CopilotPaneOptions
{
    /// <summary>Fixed theme; null follows AutoCAD's UI theme (UIThemeManager) and updates when it changes.</summary>
    public PanelTheme? Theme { get; init; }

    /// <summary>Attachment picker; null uses the Windows open-file dialog.</summary>
    public IFilePicker? FilePicker { get; init; }

    /// <summary>Panel settings file; null uses %LOCALAPPDATA%\AcadCopilot\panel.json.</summary>
    public string? PanelSettingsPath { get; init; }

    /// <summary>
    /// The settings file shared with the backend, where the Watch / Fast choice is kept; null uses
    /// %LOCALAPPDATA%\AcadCopilot\settings.json (the backend's default).
    /// </summary>
    public string? SettingsPath { get; init; }

    /// <summary>When true (default) the provider sign-in states are checked when the pane is shown.</summary>
    public bool CheckProviderStatusOnLoad { get; init; } = true;

    /// <summary>
    /// Voice input of the tabs; null uses Windows speech recognition (<see cref="WindowsSpeechEngineFactory"/>), which
    /// opens the microphone only when the mic button is clicked.
    /// </summary>
    public VoiceInputService? Voice { get; init; }

    /// <summary>False hides the mic button.</summary>
    public bool EnableVoice { get; init; } = true;

    /// <summary>Opens a folder (the knowledge folder link); null uses Windows Explorer.</summary>
    public Action<string>? OpenFolder { get; init; }

    /// <summary>The "Learn from me" recorder of the session; null hides the Learning bar.</summary>
    public AcadCopilot.Plugin.Learning.IUserActionRecorder? Recorder { get; init; }
}

/// <summary>
/// The chat panel hosted in AutoCAD's dockable pane: a strip of chat tabs (<see cref="CopilotPanelViewModel"/>) above
/// the conversation of the selected tab (<see cref="ChatViewModel"/>, the pane's DataContext).
/// </summary>
public partial class CopilotPane : UserControl
{
    private static readonly TimeSpan StatusRefreshAge = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ThemePollInterval = TimeSpan.FromSeconds(2);

    // The settings section takes at most this part of the pane's height (it scrolls beyond), never less than the minimum.
    private const double SettingsMaxFraction = 0.55;
    private const double SettingsMinHeight = 180;

    private readonly CopilotPaneOptions _options;
    private readonly DispatcherTimer? _themeTimer;
    private bool _stickToBottom = true;
    private ChatViewModel? _shownTab;

    /// <summary>
    /// Creates the pane for <paramref name="backend"/> (called on AutoCAD's main thread). A backend that is also an
    /// <see cref="ICopilotHub"/> (the plug-in's <see cref="CopilotBackend"/>) gets chat tabs; any other backend is one
    /// fixed conversation.
    /// </summary>
    public CopilotPane(ICopilotBackend backend)
        : this(backend, new CopilotPaneOptions())
    {
    }

    /// <summary>Creates the pane with explicit options.</summary>
    public CopilotPane(ICopilotBackend backend, CopilotPaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        Backend = backend;
        _options = options;
        InitializeComponent();

        ApplyTheme(options.Theme ?? AcadThemeProbe.TryGetCurrent() ?? PanelTheme.Light);
        if (options.Theme is null)
        {
            _themeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ThemePollInterval };
            _themeTimer.Tick += (_, _) => FollowAcadTheme();
        }

        var filePicker = options.FilePicker ?? new WpfFilePicker();
        var panelSettings = new PanelSettingsStore(options.PanelSettingsPath);
        var copilotSettings = new CopilotSettingsStore(options.SettingsPath);
        var voice = options.EnableVoice ? options.Voice ?? new VoiceInputService() : null;
        var knowledge = (backend as CopilotBackend)?.Knowledge.Directory ?? AcadCopilot.Core.AppPaths.Knowledge;
        var hub = backend as ICopilotHub;
        Shell = new CopilotPanelViewModel(
            hub ?? new SingleSessionHub(backend),
            session => new ChatViewModel(session, Dispatcher, filePicker, panelSettings, copilotSettings, voice)
            {
                KnowledgeDirectory = knowledge,
                FolderOpener = options.OpenFolder,
            },
            canCreateTabs: hub is not null,
            recorder: options.Recorder);
        Shell.SelectedTabChanged += (_, _) => ShowSelectedTab();
        TabStrip.DataContext = Shell;
        ShowSelectedTab();

        Composer.PreviewKeyDown += OnComposerPreviewKeyDown;
        PreviewKeyDown += OnPanePreviewKeyDown;
        ConversationScroll.ScrollChanged += OnConversationScrollChanged;
        ComposerFrame.DragOver += OnComposerDragOver;
        ComposerFrame.Drop += OnComposerDrop;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _themeTimer?.Stop();
        SizeChanged += (_, e) => SettingsScroll.MaxHeight = Math.Max(SettingsMinHeight, e.NewSize.Height * SettingsMaxFraction);
    }

    /// <summary>The backend the view model talks to.</summary>
    public ICopilotBackend Backend { get; }

    /// <summary>The chat tabs.</summary>
    public CopilotPanelViewModel Shell { get; }

    /// <summary>The view model of the selected tab (the DataContext).</summary>
    public ChatViewModel ViewModel => Shell.SelectedTab;

    /// <summary>
    /// Where the plug-in reports the AutoCAD tool calls it runs (full arguments and results), so the CAD activity rows
    /// can list each command typed, what it created or erased, warnings and the capture_view picture. The same object
    /// is the backend's attached UI, so a caller holding the backend can use <c>backend.Ui as IToolActivitySink</c>.
    /// </summary>
    public IToolActivitySink ActivitySink => ViewModel;

    /// <summary>Shows the tab of chat session <paramref name="sessionId"/> (a question or approval waits there).</summary>
    public void ShowSession(string? sessionId) => Shell.ShowSession(sessionId);

    /// <summary>The theme currently applied.</summary>
    public PanelTheme Theme { get; private set; }

    /// <summary>Re-colors the panel (call on the UI thread, e.g. from AutoCAD's ThemeChanged event).</summary>
    public void ApplyTheme(PanelTheme theme)
    {
        var dictionaries = Resources.MergedDictionaries;
        var palette = PanelThemes.Create(theme);
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = palette;
        }
        else
        {
            dictionaries.Add(palette);
        }

        Theme = theme;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_themeTimer is not null)
        {
            FollowAcadTheme();
            _themeTimer.Start();
        }

        if (_options.CheckProviderStatusOnLoad &&
            (ViewModel.LastStatusRefreshUtc is not { } last || DateTime.UtcNow - last > StatusRefreshAge))
        {
            _ = ViewModel.RefreshStatusesAsync();
        }
    }

    private void ShowSelectedTab()
    {
        var tab = Shell.SelectedTab;
        if (ReferenceEquals(_shownTab, tab))
        {
            return;
        }

        if (_shownTab is not null)
        {
            _shownTab.ComposerFocusRequested -= OnComposerFocusRequested;
        }

        _shownTab = tab;
        tab.ComposerFocusRequested += OnComposerFocusRequested;
        DataContext = tab;
        _stickToBottom = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ConversationScroll.ScrollToEnd()));
    }

    private void OnComposerFocusRequested(object? sender, EventArgs e) => FocusComposer();

    private void FollowAcadTheme()
    {
        if (AcadThemeProbe.TryGetCurrent() is { } theme && theme != Theme)
        {
            ApplyTheme(theme);
        }
    }

    private void OnComposerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
        {
            _stickToBottom = true;
            ViewModel.SendCommand.Execute(null);
        }
    }

    private void OnPanePreviewKeyDown(object sender, KeyEventArgs e)
    {
        var voice = ViewModel.Voice;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control && voice is not null)
        {
            // Ctrl+Space: start / stop voice input.
            e.Handled = true;
            voice.ToggleCommand.Execute(null);
            FocusComposer();
            return;
        }

        if (e.Key == Key.Escape && voice is { IsActive: true })
        {
            // Esc while listening stops listening (the words stay); a second Esc stops the AI.
            e.Handled = true;
            _ = voice.StopAsync(allowAutoSend: false);
            return;
        }

        if (e.Key == Key.Escape && ViewModel.IsBusy)
        {
            e.Handled = true;
            ViewModel.StopCommand.Execute(null);
        }
    }

    private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            if (_stickToBottom)
            {
                ConversationScroll.ScrollToEnd();
            }
        }
        else
        {
            _stickToBottom = ConversationScroll.VerticalOffset >= ConversationScroll.ScrollableHeight - 8;
        }
    }

    private void FocusComposer()
    {
        Composer.Focus();
        Composer.CaretIndex = Composer.Text.Length;
    }

    private static void OnComposerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                ViewModel.AddAttachments(files.Where(File.Exists));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            CopilotLog.Warn("Dropped files could not be read", ex);
        }

        e.Handled = true;
    }
}
