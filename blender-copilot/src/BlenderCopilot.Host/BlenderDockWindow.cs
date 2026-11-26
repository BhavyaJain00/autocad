using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.QuickAsk;
using AcadCopilot.Plugin.Resources;
using AcadCopilot.Plugin.ViewModels;
using AcadCopilot.Plugin.Views;
using AcadCopilot.Plugin.Voice;

namespace BlenderCopilot.Host;

/// <summary>
/// Native WPF window that hosts <see cref="CopilotPane"/> embedded INSIDE Blender 5.3's main window
/// (in the rightmost workspace column below Blender's top menu/workspace bar and above Blender's bottom status bar),
/// plus the floating <see cref="QuickAskOverlayWindow"/> and <see cref="QuickAskBoxWindow"/> inside Blender's 3D Viewport.
/// </summary>
public sealed class BlenderDockWindow : Window
{
    public const double DefaultDockWidth = 430;
    public const double MinDockWidth = 340;
    public const double MaxDockWidth = 780;

    private readonly int _blenderPid;
    private readonly BlenderWorkerClient _worker;
    private readonly CopilotBackend _backend;
    private readonly BlenderDrawingInteraction _interaction;
    private readonly BlenderActionRecorder _recorder;
    private readonly BlenderFocusLayoutHost _focusHost;
    private readonly BlenderQuickAskHost _quickAskHost;
    private readonly QuickAskController _quickAsk;
    private readonly DispatcherTimer _syncTimer;

    private IntPtr _blenderHwnd;
    private QuickAskOverlayWindow? _overlayWindow;
    private QuickAskBoxWindow? _boxWindow;
    private bool _panelWanted = true;
    private bool _shortcutDown;
    private double _dockWidthDip = DefaultDockWidth;
    private bool _userResizedWidth;
    private bool _resizingDivider;
    private int _dragStartMouseX;
    private double _dragStartWidthDip;
    private bool _syncingPing;
    private bool _maximizedBlenderOnce;

    // Inner layout rects reported by Blender 5.3 in client-area pixels (origin bottom-left)
    private (int X, int Y, int Width, int Height, int WinW, int WinH)? _blenderDockRect;
    private (int X, int Y, int Width, int Height, int WinW, int WinH)? _blenderView3dRect;

    public BlenderDockWindow(
        int blenderPid,
        IntPtr blenderHwnd,
        BlenderWorkerClient worker,
        CopilotBackend backend)
    {
        _blenderPid = blenderPid;
        _blenderHwnd = blenderHwnd;
        _worker = worker;
        _backend = backend;

        Title = "AI Copilot — Blender 5.3";
        Width = DefaultDockWidth;
        Height = 860;
        MinWidth = MinDockWidth;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x22, 0x2A));

        _interaction = new BlenderDrawingInteraction(worker, Dispatcher);
        _recorder = new BlenderActionRecorder(worker, backend.Knowledge);
        _focusHost = new BlenderFocusLayoutHost();
        _focusHost.Changed += (_, _) => ApplyDockLayout();

        var voice = new VoiceInputService();
        Pane = new CopilotPane(backend, new CopilotPaneOptions
        {
            Theme = PanelTheme.Dark,
            Voice = voice,
            EnableVoice = true,
            Recorder = _recorder,
            Interaction = _interaction,
            EnableQuickPrompts = true,
            FocusLayout = _focusHost,
        });

        _quickAskHost = new BlenderQuickAskHost(
            worker,
            _interaction,
            ShowForChat,
            () => _blenderHwnd)
        {
            Shell = Pane.Shell,
        };
        _quickAsk = new QuickAskController(_quickAskHost, QuickAskOptions.Shared, Dispatcher, voice);
        _quickAsk.BoxChanged += OnQuickAskBoxChanged;
        QuickAskOptions.Shared.PropertyChanged += (_, _) => UpdateQuickAskOverlay();

        // Root grid: 4px left resize divider + the CopilotPane
        var rootGrid = new Grid();
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var divider = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x3C)),
            Cursor = Cursors.SizeWE,
            ToolTip = "Drag to resize the AI Copilot panel inside Blender",
        };
        divider.MouseLeftButtonDown += OnDividerMouseDown;
        divider.MouseMove += OnDividerMouseMove;
        divider.MouseLeftButtonUp += OnDividerMouseUp;
        Grid.SetColumn(divider, 0);
        Grid.SetColumn(Pane, 1);
        rootGrid.Children.Add(divider);
        rootGrid.Children.Add(Pane);
        Content = rootGrid;

        if (_blenderHwnd != IntPtr.Zero && Win32.IsWindow(_blenderHwnd))
        {
            new WindowInteropHelper(this).Owner = _blenderHwnd;
        }

        SourceInitialized += (_, _) =>
        {
            var myHwnd = new WindowInteropHelper(this).Handle;
            QuickAskNative.SetToolStyle(myHwnd, noActivate: false);
            EnsureBlenderMaximizedOnce();
            ApplyDockLayout();
        };

        _syncTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _syncTimer.Tick += OnSyncTick;
        Loaded += (_, _) =>
        {
            _syncTimer.Start();
            EnsureBlenderMaximizedOnce();
            ApplyDockLayout();
            UpdateQuickAskOverlay();
        };
        Closed += (_, _) =>
        {
            _syncTimer.Stop();
            _overlayWindow?.Close();
            _boxWindow?.Close();
            _quickAsk.Dispose();
            Pane.Shell.Dispose();
        };
    }

    public CopilotPane Pane { get; }

    public void ShowPanel()
    {
        _panelWanted = true;
        if (!IsVisible)
        {
            Show();
        }

        ApplyDockLayout();
        Activate();
    }

    public void HidePanel()
    {
        _panelWanted = false;
        if (IsVisible)
        {
            Hide();
        }
    }

    public void TogglePanel()
    {
        if (_panelWanted && IsVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    private void ShowForChat(ChatViewModel chat)
    {
        ShowPanel();
        Pane.Shell.SelectTab(chat);
    }

    public void RevealSession(string? sessionId)
    {
        ShowPanel();
        Pane.ShowSession(sessionId);
    }

    private void EnsureBlenderMaximizedOnce()
    {
        if (_maximizedBlenderOnce || _blenderHwnd == IntPtr.Zero || !Win32.IsWindow(_blenderHwnd))
        {
            return;
        }

        _maximizedBlenderOnce = true;
        if (!Win32.IsZoomed(_blenderHwnd))
        {
            _ = Win32.ShowWindow(_blenderHwnd, Win32.SwMaximize);
        }
    }

    private void OnDividerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement el && Win32.GetCursorPos(out var pt))
        {
            _resizingDivider = true;
            _dragStartMouseX = pt.X;
            _dragStartWidthDip = _dockWidthDip;
            el.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnDividerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizingDivider || !Win32.GetCursorPos(out var pt))
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var deltaDip = (_dragStartMouseX - pt.X) / Math.Max(0.5, scale);
        var next = Math.Clamp(_dragStartWidthDip + deltaDip, MinDockWidth, MaxDockWidth);
        if (Math.Abs(next - _dockWidthDip) >= 2)
        {
            _dockWidthDip = next;
            _userResizedWidth = true;
            ApplyDockLayout();
        }
    }

    private void OnDividerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizingDivider && sender is UIElement el)
        {
            _resizingDivider = false;
            el.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private async void OnSyncTick(object? sender, EventArgs e)
    {
        if (!IsBlenderProcessAlive(_blenderPid))
        {
            Close();
            Application.Current?.Shutdown();
            return;
        }

        CheckShortcut();
        SyncWindowVisibilityAndDock();

        if (_syncingPing)
        {
            return;
        }

        _syncingPing = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var resp = await _worker.SendRawAsync("ping", null, cts.Token).ConfigureAwait(true);
            if (resp["data"] is JsonObject data)
            {
                if (data["hwnd"] is JsonValue hv && hv.TryGetValue<long>(out var hval) && hval != 0 && _blenderHwnd == IntPtr.Zero)
                {
                    _blenderHwnd = new IntPtr(hval);
                    if (Win32.IsWindow(_blenderHwnd))
                    {
                        new WindowInteropHelper(this).Owner = _blenderHwnd;
                        EnsureBlenderMaximizedOnce();
                    }
                }

                _blenderDockRect = ParseRect(data["dockRect"] as JsonObject);
                _blenderView3dRect = ParseRect(data["view3dRect"] as JsonObject);

                var docTitle = data["documentTitle"]?.GetValue<string>() ?? "Untitled.blend";
                if (!string.Equals(_backend.Acad.DocumentTitle, docTitle, StringComparison.Ordinal))
                {
                    _backend.UpdateAcad(new AcadSessionInfo(_blenderPid, "5.3", docTitle) { Product = "Blender 5.3" });
                    _backend.UpdateDrawings([new DrawingRef("blend-1", docTitle)]);
                }

                _quickAskHost.ActiveDrawingTitle = docTitle;
                _interaction.UpdateFromPing(data["selection"] as JsonObject, data["cursor"] as JsonObject);
                _recorder.UpdateFromPing(data["recorder"] as JsonObject);

                if (data["uiEvents"] is JsonArray events)
                {
                    foreach (var ev in events)
                    {
                        var cmd = ev?.GetValue<string>();
                        if (cmd == "toggle_panel")
                        {
                            TogglePanel();
                        }
                        else if (cmd == "quick_ask")
                        {
                            _quickAsk.ToggleBox(QuickAskOpenReason.Command);
                        }
                    }
                }

                ApplyDockLayout();
                UpdateQuickAskOverlay();
            }
        }
        catch
        {
            // Blender may be busy rendering a frame.
        }
        finally
        {
            _syncingPing = false;
        }
    }

    private static (int X, int Y, int Width, int Height, int WinW, int WinH)? ParseRect(JsonObject? obj)
    {
        if (obj is null)
        {
            return null;
        }

        var x = obj["x"]?.GetValue<int>() ?? 0;
        var y = obj["y"]?.GetValue<int>() ?? 0;
        var w = obj["width"]?.GetValue<int>() ?? 0;
        var h = obj["height"]?.GetValue<int>() ?? 0;
        var ww = obj["winWidth"]?.GetValue<int>() ?? 0;
        var wh = obj["winHeight"]?.GetValue<int>() ?? 0;
        return w > 0 && h > 0 ? (x, y, w, h, ww, wh) : null;
    }

    private void CheckShortcut()
    {
        if (_blenderHwnd == IntPtr.Zero)
        {
            return;
        }

        var fg = Win32.GetForegroundWindow();
        var myHwnd = new WindowInteropHelper(this).Handle;
        if (fg != _blenderHwnd && fg != myHwnd)
        {
            _shortcutDown = false;
            return;
        }

        var shortcut = QuickAskOptions.Shared.Shortcut;
        var ctrl = QuickAskNative.IsKeyDown(0x11);  // VK_CONTROL
        var shift = QuickAskNative.IsKeyDown(0x10); // VK_SHIFT
        var alt = QuickAskNative.IsKeyDown(0x12);   // VK_MENU
        var key = QuickAskNative.IsKeyDown(shortcut.VirtualKey);

        if (key && shortcut.Matches(shortcut.VirtualKey, ctrl, shift, alt))
        {
            if (!_shortcutDown)
            {
                _shortcutDown = true;
                _quickAsk.ToggleBox(QuickAskOpenReason.Shortcut);
            }
        }
        else
        {
            _shortcutDown = false;
        }
    }

    private void SyncWindowVisibilityAndDock()
    {
        if (_blenderHwnd == IntPtr.Zero || !Win32.IsWindow(_blenderHwnd))
        {
            return;
        }

        if (!QuickAskNative.IsShown(_blenderHwnd))
        {
            if (IsVisible)
            {
                Hide();
            }

            _overlayWindow?.PlaceIn(null);
            return;
        }

        if (_panelWanted && !IsVisible)
        {
            Show();
        }

        if (_panelWanted && IsVisible)
        {
            ApplyDockLayout();
        }

        UpdateQuickAskOverlay();
    }

    /// <summary>
    /// Positions the AI Copilot panel strictly INSIDE Blender 5.3's client area on the right side:
    /// below Blender's top menu &amp; workspace bar (TOPBAR) and above Blender's bottom status bar (STATUSBAR).
    /// Blender's main window is never un-maximized or shrunk.
    /// </summary>
    private void ApplyDockLayout()
    {
        if (_blenderHwnd == IntPtr.Zero || !QuickAskNative.IsShown(_blenderHwnd) || !_panelWanted)
        {
            return;
        }

        var myHwnd = new WindowInteropHelper(this).Handle;
        if (myHwnd == IntPtr.Zero || QuickAskNative.ClientRect(_blenderHwnd) is not { } client)
        {
            return;
        }

        var scale = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
        var minWidthPx = (int)Math.Round(MinDockWidth * scale);
        var maxWidthPx = (int)Math.Round(MaxDockWidth * scale);
        var defaultWidthPx = (int)Math.Round(_dockWidthDip * scale);

        int panelLeft;
        int panelTop;
        int panelWidth;
        int panelHeight;

        if (_blenderDockRect is { } dock && dock.WinH > 0)
        {
            // Blender's area Y origin is at bottom-left of client area; convert to Win32 top-left screen pixels
            var topInsetPx = Math.Max(48, client.Height - (dock.Y + dock.Height));
            var bottomInsetPx = Math.Max(22, dock.Y);
            panelTop = client.Top + topInsetPx;
            var panelBottom = client.Bottom - bottomInsetPx;
            panelHeight = Math.Max(260, panelBottom - panelTop);

            panelWidth = _userResizedWidth
                ? Math.Clamp(defaultWidthPx, minWidthPx, maxWidthPx)
                : Math.Clamp(Math.Max(dock.Width, (int)Math.Round(390 * scale)), minWidthPx, maxWidthPx);
            panelLeft = client.Right - panelWidth - 2;
        }
        else
        {
            // Fallback before first ping: inside client area below TOPBAR (54px) and above STATUSBAR (24px)
            var topBarPx = (int)Math.Round(54 * scale);
            var statusBarPx = (int)Math.Round(24 * scale);
            panelTop = client.Top + topBarPx;
            panelHeight = Math.Max(260, client.Height - topBarPx - statusBarPx);
            panelWidth = Math.Clamp(defaultWidthPx, minWidthPx, maxWidthPx);
            panelLeft = client.Right - panelWidth - 2;
        }

        if (QuickAskNative.WindowRect(myHwnd) is not { } current ||
            Math.Abs(current.Left - panelLeft) > 2 ||
            Math.Abs(current.Top - panelTop) > 2 ||
            Math.Abs(current.Width - panelWidth) > 2 ||
            Math.Abs(current.Height - panelHeight) > 2)
        {
            _ = Win32.SetWindowPos(
                myHwnd,
                IntPtr.Zero,
                panelLeft,
                panelTop,
                panelWidth,
                panelHeight,
                Win32.SwpNoZOrder | Win32.SwpNoActivate);
        }
    }

    private ScreenRect? GetView3dScreenRect()
    {
        if (QuickAskNative.ClientRect(_blenderHwnd) is not { } client)
        {
            return null;
        }

        if (_blenderView3dRect is { } v3d && v3d.Width > 200 && v3d.Height > 200)
        {
            var left = client.Left + v3d.X;
            var top = client.Top + (client.Height - (v3d.Y + v3d.Height)) + 54; // below 3D View header + tool settings
            var right = left + v3d.Width;
            var bottom = top + Math.Max(100, v3d.Height - 54);
            return new ScreenRect(left, top, right, bottom);
        }

        // Fallback: left 60% of Blender's client area
        return new ScreenRect(client.Left, client.Top + 105, client.Left + (int)(client.Width * 0.60), client.Bottom - 24);
    }

    private void UpdateQuickAskOverlay()
    {
        if (_blenderHwnd == IntPtr.Zero || !QuickAskNative.IsShown(_blenderHwnd) || !QuickAskOptions.Shared.ShowButton)
        {
            _overlayWindow?.PlaceIn(null);
            return;
        }

        if (GetView3dScreenRect() is not { } area ||
            area.Width < QuickAskPlacement.MinAreaSize ||
            area.Height < QuickAskPlacement.MinAreaSize)
        {
            _overlayWindow?.PlaceIn(null);
            return;
        }

        _overlayWindow ??= new QuickAskOverlayWindow(_quickAsk, PanelTheme.Dark, _blenderHwnd);
        _overlayWindow.PlaceIn(area);
    }

    private void OnQuickAskBoxChanged(object? sender, EventArgs e)
    {
        _boxWindow?.Close();
        _boxWindow = null;

        if (_quickAsk.Box is not { } box || _blenderHwnd == IntPtr.Zero)
        {
            return;
        }

        var area = GetView3dScreenRect() ?? QuickAskNative.ClientRect(_blenderHwnd) ?? Win32.GetMonitorWorkArea(_blenderHwnd);
        var window = new QuickAskBoxWindow(box, PanelTheme.Dark, _blenderHwnd);
        window.ClickedAway += (_, _) => _quickAsk.CloseBox();
        _boxWindow = window;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (box.Reason == QuickAskOpenReason.Button && _overlayWindow?.ScreenBounds is { } btnRect)
        {
            window.ShowAt((w, h) => QuickAskPlacement.BoxUnderButton(area, btnRect, w, h));
        }
        else if (QuickAskNative.CursorPosition() is { } cursor)
        {
            window.ShowAt((w, h) => QuickAskPlacement.Box(area, cursor.X, cursor.Y, w, h, scale));
        }
        else
        {
            window.ShowAt((w, h) => (area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 3));
        }
    }

    private static bool IsBlenderProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return true;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

internal static class Win32
{
    public const int SwMaximize = 3;
    public const int SwRestore = 9;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    public static ScreenRect GetMonitorWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        }

        return new ScreenRect(0, 0, 1920, 1040);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}
