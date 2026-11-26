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
/// Native WPF window that hosts <see cref="CopilotPane"/> docked on the right side of Blender 5.3 Desktop,
/// plus the floating <see cref="QuickAskOverlayWindow"/> and <see cref="QuickAskBoxWindow"/> over Blender's 3D area.
/// </summary>
public sealed class BlenderDockWindow : Window
{
    public const double DefaultDockWidth = 460;
    public const double MinDockWidth = 340;
    public const double MaxDockWidth = 860;

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
    private bool _resizingDivider;
    private int _dragStartMouseX;
    private double _dragStartWidthDip;
    private bool _syncingPing;

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
        _focusHost.Changed += (_, _) => ApplyDockLayout(forceResizeBlender: true);

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

        // Root grid: 5px left resize divider + the CopilotPane
        var rootGrid = new Grid();
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var divider = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x33, 0x40)),
            Cursor = Cursors.SizeWE,
            ToolTip = "Drag to resize the AI Copilot panel",
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
            ApplyDockLayout(forceResizeBlender: true);
        };

        _syncTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        _syncTimer.Tick += OnSyncTick;
        Loaded += (_, _) =>
        {
            _syncTimer.Start();
            ApplyDockLayout(forceResizeBlender: true);
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

        ApplyDockLayout(forceResizeBlender: true);
        Activate();
    }

    public void HidePanel()
    {
        _panelWanted = false;
        if (IsVisible)
        {
            Hide();
        }

        RestoreBlenderFullWorkArea();
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
            ApplyDockLayout(forceResizeBlender: true);
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
        // 1. Check if Blender process is still alive
        if (!IsBlenderProcessAlive(_blenderPid))
        {
            Close();
            Application.Current?.Shutdown();
            return;
        }

        // 2. Track window position & global Ctrl+Shift+Space shortcut when Blender is focused
        CheckShortcut();
        SyncWindowVisibilityAndDock();

        // 3. Poll Blender worker for selection, 3D cursor, active .blend title, recorder actions, and N-panel button clicks
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
                        ApplyDockLayout(forceResizeBlender: true);
                    }
                }

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
            ApplyDockLayout(forceResizeBlender: false);
        }

        if (_panelWanted && IsVisible)
        {
            ApplyDockLayout(forceResizeBlender: false);
        }

        UpdateQuickAskOverlay();
    }

    private void ApplyDockLayout(bool forceResizeBlender)
    {
        if (_blenderHwnd == IntPtr.Zero || !QuickAskNative.IsShown(_blenderHwnd))
        {
            return;
        }

        var myHwnd = new WindowInteropHelper(this).Handle;
        if (myHwnd == IntPtr.Zero)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var panelWidthPx = (int)Math.Round(_dockWidthDip * Math.Max(1.0, scale));
        var work = Win32.GetMonitorWorkArea(_blenderHwnd);
        if (work.Width <= panelWidthPx + 300)
        {
            return;
        }

        if (forceResizeBlender && _panelWanted)
        {
            if (Win32.IsZoomed(_blenderHwnd))
            {
                _ = Win32.ShowWindow(_blenderHwnd, Win32.SwRestore);
            }

            var blenderWidth = work.Width - panelWidthPx;
            _ = Win32.SetWindowPos(
                _blenderHwnd,
                IntPtr.Zero,
                work.Left,
                work.Top,
                blenderWidth,
                work.Height,
                Win32.SwpNoZOrder | Win32.SwpNoActivate);
        }

        var panelX = work.Right - panelWidthPx;
        var panelY = work.Top;
        var panelH = work.Height;

        if (QuickAskNative.WindowRect(myHwnd) is not { } current ||
            Math.Abs(current.Left - panelX) > 2 ||
            Math.Abs(current.Top - panelY) > 2 ||
            Math.Abs(current.Width - panelWidthPx) > 2 ||
            Math.Abs(current.Height - panelH) > 2)
        {
            _ = Win32.SetWindowPos(
                myHwnd,
                IntPtr.Zero,
                panelX,
                panelY,
                panelWidthPx,
                panelH,
                Win32.SwpNoZOrder | Win32.SwpNoActivate);
        }
    }

    private void RestoreBlenderFullWorkArea()
    {
        if (_blenderHwnd == IntPtr.Zero || !QuickAskNative.IsShown(_blenderHwnd))
        {
            return;
        }

        var work = Win32.GetMonitorWorkArea(_blenderHwnd);
        _ = Win32.SetWindowPos(
            _blenderHwnd,
            IntPtr.Zero,
            work.Left,
            work.Top,
            work.Width,
            work.Height,
            Win32.SwpNoZOrder | Win32.SwpNoActivate);
    }

    private void UpdateQuickAskOverlay()
    {
        if (_blenderHwnd == IntPtr.Zero || !QuickAskNative.IsShown(_blenderHwnd) || !QuickAskOptions.Shared.ShowButton)
        {
            _overlayWindow?.PlaceIn(null);
            return;
        }

        if (QuickAskNative.ClientRect(_blenderHwnd) is not { } area ||
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

        var area = QuickAskNative.ClientRect(_blenderHwnd) ?? Win32.GetMonitorWorkArea(_blenderHwnd);
        var window = new QuickAskBoxWindow(box, PanelTheme.Dark, _blenderHwnd);
        window.ClickedAway += (_, _) => _quickAsk.CloseBox(returnFocus: false);
        _boxWindow = window;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (box.OpenReason == QuickAskOpenReason.Button && _overlayWindow?.ScreenBounds is { } btnRect)
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
