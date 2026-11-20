using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Interaction;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Voice;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>The outcome of Quick Ask's Undo.</summary>
/// <param name="Success">True when every drawing step of the request was taken back.</param>
/// <param name="Message">What happened (shown when it failed).</param>
public sealed record QuickAskUndoResult(bool Success, string Message);

/// <summary>
/// What Quick Ask needs from AutoCAD and the panel (implemented inside AutoCAD by <see cref="AcadQuickAskHost"/>; tests
/// use a fake). Free of AutoCAD types. Everything is called on AutoCAD's main (UI) thread.
/// </summary>
public interface IQuickAskHost
{
    /// <summary>The panel's chat tabs, or null while the panel does not exist yet.</summary>
    CopilotPanelViewModel? Shell { get; }

    /// <summary>Id of the active drawing (DrawingDirectory), or null when none is open.</summary>
    string? ActiveDrawingId { get; }

    /// <summary>File name of the active drawing ("House.dwg"), or null.</summary>
    string? ActiveDrawingTitle { get; }

    /// <summary>Where the cursor was last in the active drawing (world coordinates, feet-inches when the units allow), or null.</summary>
    PickedPoint? CursorPoint { get; }

    /// <summary>The user's selection in the active drawing now (null or empty when nothing is selected).</summary>
    SelectionSnapshot? Selection { get; }

    /// <summary>Lets the user click one point in the drawing (waits for AutoCAD's turn; never throws for expected problems).</summary>
    Task<PickResult> PickPointAsync(CancellationToken cancellationToken);

    /// <summary>Shows the side panel on <paramref name="chat"/> (Open chat, or a question waiting there).</summary>
    void ShowChat(ChatViewModel chat);

    /// <summary>Takes back the last <paramref name="calls"/> run_commands calls of <paramref name="chat"/> in its drawing.</summary>
    Task<QuickAskUndoResult> UndoAsync(ChatViewModel chat, int calls, CancellationToken cancellationToken);

    /// <summary>Gives the keyboard back to the drawing (after the box closes).</summary>
    void ReturnFocusToDrawing();
}

/// <summary>
/// Quick Ask: the floating AI in the drawing area. Opens the compact box (button, shortcut or the AI command), sends the
/// request to the chat tab bound to the active drawing (a new background tab when there is none) as a normal message
/// with the cursor point (or a picked point) and the selection, and drives the status bubble next to the button. The
/// side panel is never shown for this (only Open chat or a question / approval brings it up). UI thread only; the window
/// layer (<see cref="QuickAskService"/>) shows <see cref="Box"/> and the bubble.
/// </summary>
public sealed class QuickAskController : ObservableObject, IDisposable
{
    private readonly IQuickAskHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly VoiceInputService? _voice;
    private readonly PanelSettingsStore? _panelSettings;
    private QuickAskBoxViewModel? _box;
    private CancellationTokenSource? _pickCts;
    private bool _disposed;

    /// <summary>Creates the controller.</summary>
    /// <param name="host">AutoCAD and the panel.</param>
    /// <param name="options">The Quick Ask settings (button on/off, shortcut).</param>
    /// <param name="dispatcher">The UI dispatcher.</param>
    /// <param name="voice">The panel's voice input (null hides the mic in the box).</param>
    /// <param name="panelSettings">The panel's settings file (voice language, auto-send); null = defaults.</param>
    public QuickAskController(IQuickAskHost host, QuickAskOptions options, Dispatcher dispatcher, VoiceInputService? voice = null, PanelSettingsStore? panelSettings = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _host = host;
        _dispatcher = dispatcher;
        _voice = voice;
        _panelSettings = panelSettings;
        Options = options;
        Status = new QuickAskStatusViewModel(host, dispatcher);
        ToggleBoxCommand = new RelayCommand(() => ToggleBox(QuickAskOpenReason.Button));
        OpenPanelCommand = new RelayCommand(OpenPanel);
        HideButtonCommand = new RelayCommand(() => Options.ShowButton = false);
        options.PropertyChanged += OnOptionsChanged;
    }

    /// <summary>Raised after <see cref="Box"/> changed (opened, closed or replaced).</summary>
    public event EventHandler? BoxChanged;

    /// <summary>The settings.</summary>
    public QuickAskOptions Options { get; }

    /// <summary>The status bubble.</summary>
    public QuickAskStatusViewModel Status { get; }

    /// <summary>The open box, or null.</summary>
    public QuickAskBoxViewModel? Box
    {
        get => _box;
        private set
        {
            if (Set(ref _box, value))
            {
                OnPropertyChanged(nameof(IsBoxOpen));
                BoxChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>True while the box is open.</summary>
    public bool IsBoxOpen => _box is not null;

    /// <summary>The floating button: opens the box, or closes it when open.</summary>
    public RelayCommand ToggleBoxCommand { get; }

    /// <summary>The button's menu: shows the side panel (on the chat of the active drawing when there is one).</summary>
    public RelayCommand OpenPanelCommand { get; }

    /// <summary>The button's menu: hides the floating button (the AIBUTTON command or the panel's Settings bring it back).</summary>
    public RelayCommand HideButtonCommand { get; }

    /// <summary>"Shortcut: Ctrl+Shift+Space" (the button's menu).</summary>
    public string ShortcutMenuText => $"Shortcut: {Options.Shortcut} (change it in the panel's Settings)";

    /// <summary>"Ask the AI (Ctrl+Shift+Space)" for the button's tool tip.</summary>
    public string ButtonToolTip => $"Ask the AI right here ({Options.Shortcut}, or type AI). Right-click for options.";

    /// <summary>Opens the box, or closes it when it is open (the button and the shortcut).</summary>
    public void ToggleBox(QuickAskOpenReason reason)
    {
        if (IsBoxOpen)
        {
            CloseBox();
        }
        else
        {
            OpenBox(reason);
        }
    }

    /// <summary>
    /// Opens the box with the context of this moment: the cursor point (not for the button, where the cursor is on the
    /// button) and the selection. Nothing happens without a drawing.
    /// </summary>
    public QuickAskBoxViewModel? OpenBox(QuickAskOpenReason reason)
    {
        if (_disposed)
        {
            return null;
        }

        if (_box is { } open)
        {
            return open;
        }

        PickedPoint? cursor = null;
        SelectionSnapshot? selection = null;
        string? title = null;
        try
        {
            cursor = reason == QuickAskOpenReason.Button ? null : _host.CursorPoint;
            selection = _host.Selection;
            title = _host.ActiveDrawingTitle;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: could not read the drawing context", ex);
        }

        var box = new QuickAskBoxViewModel(_dispatcher, reason, cursor, selection, title, _voice, _panelSettings?.Load());
        box.SendRequested += OnSendRequested;
        box.PickRequested += OnPickRequested;
        box.CloseRequested += OnCloseRequested;
        Box = box;
        return box;
    }

    /// <summary>Closes the box (the typed text is dropped; the mic stops) and gives the keyboard back to the drawing.</summary>
    public void CloseBox() => CloseBox(returnFocus: true);

    /// <summary>
    /// Sends <paramref name="text"/> as a Quick Ask now (the command-line version, -AI): with the cursor point and the
    /// selection of this moment. False when nothing could be sent (the bubble says why).
    /// </summary>
    public bool SubmitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        PickedPoint? cursor = null;
        SelectionSnapshot? selection = null;
        try
        {
            cursor = _host.CursorPoint;
            selection = _host.Selection is { IsEmpty: false } selected ? selected : null;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: could not read the drawing context", ex);
        }

        return Send(QuickAskText.Compose(text, cursor, cursor is null ? QuickAskPointSource.None : QuickAskPointSource.Cursor, selection));
    }

    /// <summary>Sends what the box holds and closes it. False when nothing was sent.</summary>
    public bool SubmitBox()
    {
        if (_box is not { CanSend: true } box)
        {
            return false;
        }

        var message = box.Compose();
        CloseBox(returnFocus: true);
        return Send(message);
    }

    /// <summary>Sends <paramref name="message"/> to the chat of the active drawing and follows it in the bubble.</summary>
    public bool Send(QuickAskMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_disposed)
        {
            return false;
        }

        if (_host.Shell is not { } shell)
        {
            Status.ShowProblem("The AI Copilot panel is still starting. Try again in a moment.");
            return false;
        }

        var chat = ChatFor(shell);
        if (chat is null)
        {
            Status.ShowProblem($"Too many chat tabs are open ({CopilotPanelViewModel.MaxTabs}). Close one, then ask again.", shell.SelectedTab);
            return false;
        }

        QuickAskDelivery delivery;
        try
        {
            delivery = chat.SendQuickAsk(message.DisplayText, message.SentText, message.SelectionNote);
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Quick Ask: sending failed", ex);
            Status.ShowProblem("Could not send: " + ex.Message, chat);
            return false;
        }

        if (delivery == QuickAskDelivery.Rejected)
        {
            Status.ShowProblem("That chat already has many requests waiting. Open it to see them.", chat);
            return false;
        }

        CopilotLog.Info($"Quick Ask sent to chat '{chat.Title}' ({chat.DrawingTitle}): {delivery}.");
        Status.Track(chat, message, delivery);
        return true;
    }

    /// <summary>The chat bound to the active drawing (an idle one first), else a new background tab on it.</summary>
    public ChatViewModel? ChatFor(CopilotPanelViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var drawing = _host.ActiveDrawingId;
        if (string.IsNullOrWhiteSpace(drawing))
        {
            return shell.SelectedTab;
        }

        return shell.TabForDrawing(drawing) ?? shell.NewTab(show: false);
    }

    /// <summary>Lets the user click the point for the request (the box hides meanwhile, then comes back).</summary>
    public async Task PickAsync()
    {
        if (_box is not { IsPicking: false } box)
        {
            return;
        }

        box.Problem = null;
        box.IsPicking = true;
        using var cts = new CancellationTokenSource();
        _pickCts = cts;
        PickResult result;
        try
        {
            result = await _host.PickPointAsync(cts.Token);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: pick failed", ex);
            result = PickResult.Fail("Could not ask AutoCAD for a point: " + ex.Message);
        }
        finally
        {
            _pickCts = null;
        }

        if (!ReferenceEquals(box, _box))
        {
            return;
        }

        switch (result.Status)
        {
            case PickStatus.Picked when result.Points.Count > 0:
                box.SetPoint(result.Points[0], QuickAskPointSource.Picked);
                break;
            case PickStatus.Failed:
                box.Problem = result.Message;
                break;
            case PickStatus.TimedOut:
                box.Problem = "No point was clicked in time.";
                break;
        }

        box.IsPicking = false;
    }

    /// <summary>Closes the box and stops following requests (AutoCAD closing).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CloseBox(returnFocus: false);
        _disposed = true;
        Options.PropertyChanged -= OnOptionsChanged;
        Status.Dispose();
    }

    private void OnOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickAskOptions.Shortcut))
        {
            OnPropertiesChanged(nameof(ButtonToolTip), nameof(ShortcutMenuText));
        }
    }

    private void OpenPanel()
    {
        if (_host.Shell is not { } shell)
        {
            return;
        }

        var drawing = _host.ActiveDrawingId;
        var chat = (string.IsNullOrWhiteSpace(drawing) ? null : shell.TabForDrawing(drawing)) ?? shell.SelectedTab;
        try
        {
            _host.ShowChat(chat);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: could not open the chat panel", ex);
        }
    }

    private void CloseBox(bool returnFocus)
    {
        if (_box is not { } box)
        {
            return;
        }

        try
        {
            _pickCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pick just ended.
        }

        box.SendRequested -= OnSendRequested;
        box.PickRequested -= OnPickRequested;
        box.CloseRequested -= OnCloseRequested;
        Box = null;
        box.Dispose();
        if (returnFocus)
        {
            try
            {
                _host.ReturnFocusToDrawing();
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Quick Ask: could not give the keyboard back to the drawing", ex);
            }
        }
    }

    private void OnSendRequested(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _box))
        {
            SubmitBox();
        }
    }

    private void OnPickRequested(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _box))
        {
            _ = PickAsync();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _box))
        {
            CloseBox();
        }
    }
}
