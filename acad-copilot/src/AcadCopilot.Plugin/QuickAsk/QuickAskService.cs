using System.ComponentModel;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Drafting;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;
using AcadCopilot.Plugin.Voice;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>
/// Starts and stops Quick Ask inside AutoCAD (one per session, main thread): the floating button window, the box window,
/// the keyboard shortcut and the AI commands' entry points. Started by <see cref="CopilotApp"/> after the palette exists;
/// stopped when AutoCAD starts closing.
/// </summary>
public static class QuickAskService
{
    private static QuickAskSession? _session;

    /// <summary>True while Quick Ask runs.</summary>
    public static bool IsRunning => _session is not null;

    /// <summary>Starts Quick Ask (once; never throws: failures are logged and Quick Ask stays off).</summary>
    public static void Start(CopilotApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (_session is not null || app.Dispatcher is not { } dispatcher)
        {
            return;
        }

        try
        {
            _session = new QuickAskSession(app, dispatcher);
            CopilotLog.Info($"Quick Ask ready (floating button {(QuickAskOptions.Shared.ShowButton ? "on" : "off")}, shortcut {QuickAskOptions.Shared.Shortcut}).");
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Quick Ask could not start", ex);
            _session = null;
        }
    }

    /// <summary>Closes the Quick Ask windows and releases AutoCAD's events.</summary>
    public static void Stop()
    {
        var session = _session;
        _session = null;
        try
        {
            session?.Dispose();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask did not stop cleanly", ex);
        }
    }

    /// <summary>Opens the box next to the cursor (the AI command). False with a reason when it cannot open now.</summary>
    public static bool OpenFromCommand(out string? problem)
    {
        if (_session is not { } session)
        {
            problem = "Quick Ask is not running (see the AI Copilot log).";
            return false;
        }

        return session.TryOpen(QuickAskOpenReason.Command, out problem);
    }

    /// <summary>Sends <paramref name="text"/> as a Quick Ask (the -AI command line version). False when it was not sent.</summary>
    public static bool SubmitText(string text, out string? problem)
    {
        if (_session is not { } session)
        {
            problem = "Quick Ask is not running (see the AI Copilot log).";
            return false;
        }

        problem = null;
        return session.Controller.SubmitText(text);
    }
}

/// <summary>The running Quick Ask: windows, shortcut and AutoCAD event handlers. Main thread only.</summary>
internal sealed class QuickAskSession : IDisposable
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private static readonly TimeSpan TrackInterval = TimeSpan.FromMilliseconds(200);

    private readonly AcadQuickAskHost _host;
    private readonly QuickAskOptions _options;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private readonly QuickAskOverlayWindow _overlay;
    private QuickAskBoxWindow? _boxWindow;
    private string? _boxDrawing;
    private IntPtr _documentWindow;
    private PanelTheme _theme;
    private int _ticks;
    private bool _disposed;

    public QuickAskSession(CopilotApp app, AcadDispatcher dispatcher)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _options = QuickAskOptions.Shared;
        _host = new AcadQuickAskHost(app, dispatcher);
        Controller = new QuickAskController(_host, _options, _ui, new VoiceInputService(), new PanelSettingsStore());
        Controller.BoxChanged += OnBoxChanged;
        Controller.Status.PropertyChanged += OnStatusChanged;
        _options.PropertyChanged += OnOptionsChanged;

        _theme = AcadThemeProbe.TryGetCurrent() ?? PanelTheme.Light;
        _overlay = new QuickAskOverlayWindow(Controller, _theme, AcadInput.MainWindowHandle())
        {
            Left = -32000,
            Top = -32000,
        };

        var documents = AcadApp.DocumentManager;
        documents.DocumentBecameCurrent += OnDocumentBecameCurrent;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        AcadApp.PreTranslateMessage += OnPreTranslateMessage;
        AcadApp.QuitWillStart += OnQuitWillStart;
        _documentWindow = DocumentWindow(AcadQuickAskHost.ActiveDocument);

        _timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TrackInterval };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        UpdateOverlay();
    }

    public QuickAskController Controller { get; }

    /// <summary>Opens the box (or explains why not).</summary>
    public bool TryOpen(QuickAskOpenReason reason, out string? problem)
    {
        problem = null;
        if (_disposed)
        {
            problem = "Quick Ask has stopped.";
            return false;
        }

        if (DrawingArea() is null)
        {
            problem = "Open a drawing (and close any dialog) first.";
            return false;
        }

        // Open after the command (or key press) is fully handled, so AutoCAD does not take the keyboard back.
        _ = _ui.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_disposed && !Controller.IsBoxOpen)
            {
                Controller.OpenBox(reason);
            }
        }));
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        try
        {
            AcadApp.PreTranslateMessage -= OnPreTranslateMessage;
            AcadApp.QuitWillStart -= OnQuitWillStart;
            var documents = AcadApp.DocumentManager;
            documents.DocumentBecameCurrent -= OnDocumentBecameCurrent;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            // AutoCAD is closing.
        }

        _options.PropertyChanged -= OnOptionsChanged;
        Controller.Status.PropertyChanged -= OnStatusChanged;
        Controller.BoxChanged -= OnBoxChanged;
        Controller.Dispose();
        CloseBoxWindow();
        try
        {
            _overlay.Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing.
        }

        _host.Dispose();
    }

    // AutoCAD's message filter (every key press of AutoCAD's UI thread): only compares the key with the shortcut.
    private void OnPreTranslateMessage(object? sender, PreTranslateMessageEventArgs e)
    {
        try
        {
            var message = e.Message;
            if (message.message is not (WmKeyDown or WmSysKeyDown))
            {
                return;
            }

            var key = (int)message.wParam.ToInt64();
            if (!_options.Shortcut.Matches(key, QuickAskNative.IsKeyDown(VkControl), QuickAskNative.IsKeyDown(VkShift), QuickAskNative.IsKeyDown(VkMenu)))
            {
                return;
            }

            e.Handled = true;
            var repeat = (message.lParam.ToInt64() & 0x40000000) != 0;
            if (repeat || _disposed)
            {
                return;
            }

            _ = _ui.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (Controller.IsBoxOpen)
                {
                    Controller.CloseBox();
                }
                else if (DrawingArea() is not null)
                {
                    Controller.OpenBox(QuickAskOpenReason.Shortcut);
                }
            }));
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: shortcut handling failed", ex);
        }
    }

    private void OnTick()
    {
        if (_disposed)
        {
            return;
        }

        if (++_ticks % 10 == 0 && AcadThemeProbe.TryGetCurrent() is { } theme && theme != _theme)
        {
            _theme = theme;
            _overlay.View.ApplyTheme(theme);
            _boxWindow?.View.ApplyTheme(theme);
        }

        UpdateOverlay();
    }

    // Where the button goes: the active drawing's view, while AutoCAD is shown, not minimised and no dialog is open.
    private ScreenRect? DrawingArea()
    {
        var main = AcadInput.MainWindowHandle();
        if (!QuickAskNative.IsShown(main) || !QuickAskNative.IsEnabled(main) || !QuickAskNative.IsShown(_documentWindow))
        {
            return null;
        }

        return QuickAskNative.ClientRect(_documentWindow) is { } area && area.Width >= QuickAskPlacement.MinAreaSize && area.Height >= QuickAskPlacement.MinAreaSize
            ? area
            : null;
    }

    private void UpdateOverlay()
    {
        try
        {
            var area = DrawingArea();
            var wanted = area is not null && (_options.ShowButton || Controller.Status.IsVisible);
            _overlay.PlaceIn(wanted ? area : null);
            if (area is null && _boxWindow is not null && !(Controller.Box?.IsPicking ?? false))
            {
                // Minimised, a dialog opened, or the drawing closed: the box goes too.
                Controller.CloseBox();
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: placing the button failed", ex);
            _timer.Interval = TimeSpan.FromSeconds(2);
        }
    }

    private void OnBoxChanged(object? sender, EventArgs e)
    {
        try
        {
            if (Controller.Box is not { } box)
            {
                CloseBoxWindow();
                return;
            }

            if (_boxWindow is not null)
            {
                return;
            }

            if (DrawingArea() is not { } area)
            {
                // Nowhere to show it (minimised, a dialog is open, no drawing).
                Controller.CloseBox();
                return;
            }

            _boxDrawing = _host.ActiveDrawingId;
            var window = new QuickAskBoxWindow(box, _theme, AcadInput.MainWindowHandle());
            window.ClickedAway += (_, _) => Controller.CloseBox();
            box.PropertyChanged += OnBoxPropertyChanged;
            _boxWindow = window;
            var cursor = QuickAskNative.CursorPosition();
            var button = _overlay.IsVisible ? _overlay.ScreenBounds : null;
            window.ShowAt((width, height) =>
                box.Reason != QuickAskOpenReason.Button && cursor is { } c && area.Contains(c.X, c.Y)
                    ? QuickAskPlacement.Box(area, c.X, c.Y, width, height, System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX)
                    : QuickAskPlacement.BoxUnderButton(area, button ?? new ScreenRect(area.Right - 220, area.Top, area.Right - 150, area.Top + 60), width, height));
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Quick Ask: the box could not open", ex);
            Controller.CloseBox();
        }
    }

    private void OnBoxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickAskBoxViewModel.IsPicking) && sender is QuickAskBoxViewModel box && ReferenceEquals(box, Controller.Box))
        {
            _boxWindow?.SetPicking(box.IsPicking);
        }
    }

    private void CloseBoxWindow()
    {
        if (_boxWindow is not { } window)
        {
            return;
        }

        _boxWindow = null;
        _boxDrawing = null;
        if (window.View.DataContext is QuickAskBoxViewModel box)
        {
            box.PropertyChanged -= OnBoxPropertyChanged;
        }

        try
        {
            window.Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing.
        }
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickAskStatusViewModel.IsVisible))
        {
            UpdateOverlay();
        }
    }

    private void OnOptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickAskOptions.ShowButton))
        {
            UpdateOverlay();
        }
    }

    private void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            _documentWindow = DocumentWindow(e.Document);
            if (Controller.IsBoxOpen && e.Document is { } document && _boxDrawing is { } drawing
                && !string.Equals(DrawingDirectory.IdOf(document), drawing, StringComparison.Ordinal))
            {
                // Another drawing became current: the box belonged to the one before.
                Controller.CloseBox();
            }

            UpdateOverlay();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: drawing switch handling failed", ex);
        }
    }

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document is { } document && _boxDrawing is { } drawing && string.Equals(DrawingDirectory.IdOf(document), drawing, StringComparison.Ordinal))
            {
                Controller.CloseBox();
            }

            if (DocumentWindow(e.Document) == _documentWindow)
            {
                _documentWindow = IntPtr.Zero;
                UpdateOverlay();
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: drawing close handling failed", ex);
        }
    }

    private void OnQuitWillStart(object? sender, EventArgs e) => QuickAskService.Stop();

    private static IntPtr DocumentWindow(Document? document)
    {
        try
        {
            return document?.Window?.Handle ?? IntPtr.Zero;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }
}
