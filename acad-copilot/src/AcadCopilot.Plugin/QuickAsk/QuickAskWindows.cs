using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Resources;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>A rectangle in screen pixels (physical, as Win32 reports it).</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge (exclusive).</param>
/// <param name="Bottom">Bottom edge (exclusive).</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels.</summary>
    public int Height => Bottom - Top;

    /// <summary>True when <paramref name="x"/>, <paramref name="y"/> lies inside.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Pure placement rules of the Quick Ask windows (screen pixels): the button sits in the drawing area's top-right corner,
/// left of the ViewCube; the box opens next to the cursor (or under the button) and always stays inside the drawing area.
/// </summary>
public static class QuickAskPlacement
{
    /// <summary>Gap between the drawing area's right edge and the button window, DIPs (leaves the ViewCube free).</summary>
    public const double RightInset = 150;

    /// <summary>Gap between the drawing area's top edge and the button window, DIPs.</summary>
    public const double TopInset = 2;

    /// <summary>Smallest drawing area (pixels) that still shows the button.</summary>
    public const int MinAreaSize = 240;

    /// <summary>Top-left of the button window of <paramref name="width"/> pixels in <paramref name="area"/>.</summary>
    public static (int X, int Y) Button(ScreenRect area, int width, double scale)
    {
        var right = area.Right - (int)Math.Round(RightInset * scale);
        var x = Math.Max(area.Left, right - width);
        var y = area.Top + (int)Math.Round(TopInset * scale);
        return (x, y);
    }

    /// <summary>
    /// Top-left of the box window (<paramref name="width"/> x <paramref name="height"/> pixels): just below-right of
    /// <paramref name="cursorX"/>, <paramref name="cursorY"/>, flipped left / up when it would leave
    /// <paramref name="area"/>, and clamped inside it.
    /// </summary>
    public static (int X, int Y) Box(ScreenRect area, int cursorX, int cursorY, int width, int height, double scale)
    {
        var gap = (int)Math.Round(6 * scale);
        var x = cursorX + gap;
        var y = cursorY + gap;
        if (x + width > area.Right)
        {
            x = cursorX - width - gap;
        }

        if (y + height > area.Bottom)
        {
            y = cursorY - height - gap;
        }

        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        return (x, y);
    }

    /// <summary>Top-left of the box when it opens from the button: right-aligned under the button window.</summary>
    public static (int X, int Y) BoxUnderButton(ScreenRect area, ScreenRect button, int width, int height)
    {
        var x = Math.Clamp(button.Right - width, area.Left, Math.Max(area.Left, area.Right - width));
        var y = Math.Clamp(button.Bottom - 8, area.Top, Math.Max(area.Top, area.Bottom - height));
        return (x, y);
    }
}

/// <summary>Win32 helpers of the Quick Ask windows.</summary>
internal static class QuickAskNative
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>WM_MOUSEACTIVATE.</summary>
    public const int WmMouseActivate = 0x0021;

    /// <summary>MA_NOACTIVATE.</summary>
    public const int MaNoActivate = 3;

    /// <summary>Tool window (no taskbar button, not in Alt+Tab); <paramref name="noActivate"/> also never takes the keyboard.</summary>
    public static void SetToolStyle(IntPtr hwnd, bool noActivate)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style = (style | WsExToolWindow) & ~WsExAppWindow;
        if (noActivate)
        {
            style |= WsExNoActivate;
        }

        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    /// <summary>Moves <paramref name="hwnd"/> without resizing, activating or changing its z-order.</summary>
    public static void Move(IntPtr hwnd, int x, int y) => _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>The window's rectangle in screen pixels, or null.</summary>
    public static ScreenRect? WindowRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect) ? new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;

    /// <summary>The window's client area in screen pixels, or null.</summary>
    public static ScreenRect? ClientRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !GetClientRect(hwnd, out var client))
        {
            return null;
        }

        var origin = new NativePoint { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin))
        {
            return null;
        }

        return new ScreenRect(origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom);
    }

    /// <summary>True when the window exists, is visible and not minimised.</summary>
    public static bool IsShown(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    /// <summary>True when the window takes input (false while a modal dialog of AutoCAD is open).</summary>
    public static bool IsEnabled(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowEnabled(hwnd);

    /// <summary>The mouse position in screen pixels.</summary>
    public static (int X, int Y)? CursorPosition() => GetCursorPos(out var point) ? (point.X, point.Y) : null;

    /// <summary>True when the key is held down now (GetKeyState high bit).</summary>
    public static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}

/// <summary>
/// The small window that carries the floating AI button and the status bubble over the drawing area: owned by AutoCAD's
/// main window (so it stays above AutoCAD only, hides with it when minimised and drops behind other applications with
/// it), no taskbar button, and it never activates (WS_EX_NOACTIVATE + MA_NOACTIVATE), so clicking it never takes the
/// keyboard from the drawing. Its top-right corner is anchored; it grows to the left when the bubble shows.
/// </summary>
internal sealed class QuickAskOverlayWindow : Window
{
    private ScreenRect? _area;

    public QuickAskOverlayWindow(QuickAskController controller, PanelTheme theme, IntPtr owner)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Title = "AI Copilot Quick Ask";
        View = new QuickAskButtonView(theme) { DataContext = controller };
        Content = View;
        new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            QuickAskNative.SetToolStyle(handle, noActivate: true);
            HwndSource.FromHwnd(handle)?.AddHook(NoActivateHook);
        };
        SizeChanged += (_, _) => Reposition();
    }

    public QuickAskButtonView View { get; }

    /// <summary>The window's rectangle on screen (pixels), or null before it is shown.</summary>
    public ScreenRect? ScreenBounds => QuickAskNative.WindowRect(new WindowInteropHelper(this).Handle);

    /// <summary>Shows the window anchored in <paramref name="area"/>, or hides it (null).</summary>
    public void PlaceIn(ScreenRect? area)
    {
        _area = area;
        if (area is null)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        Reposition();
    }

    private void Reposition()
    {
        if (_area is not { } area || !IsVisible)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (QuickAskNative.WindowRect(handle) is not { } bounds)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (x, y) = QuickAskPlacement.Button(area, bounds.Width, scale);
        if (x != bounds.Left || y != bounds.Top)
        {
            QuickAskNative.Move(handle, x, y);
        }
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == QuickAskNative.WmMouseActivate)
        {
            handled = true;
            return new IntPtr(QuickAskNative.MaNoActivate);
        }

        return IntPtr.Zero;
    }
}

/// <summary>
/// The window of the Quick Ask box: owned by AutoCAD's main window, no taskbar button, activated while open (the user
/// types into it). Clicking elsewhere (it loses activation) closes it, except while the user picks a point.
/// </summary>
internal sealed class QuickAskBoxWindow : Window
{
    private readonly QuickAskBoxViewModel _box;
    private bool _closing;

    public QuickAskBoxWindow(QuickAskBoxViewModel box, PanelTheme theme, IntPtr owner)
    {
        _box = box;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Ask the AI";
        View = new QuickAskBoxView(theme) { DataContext = box };
        Content = View;
        new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) => QuickAskNative.SetToolStyle(new WindowInteropHelper(this).Handle, noActivate: false);
        Deactivated += OnDeactivated;
        Closing += (_, _) => _closing = true;
    }

    /// <summary>Raised when the user clicked away (the controller closes the box).</summary>
    public event EventHandler? ClickedAway;

    public QuickAskBoxView View { get; }

    /// <summary>Shows the box at <paramref name="place"/> (computed from its measured size) and gives it the keyboard.</summary>
    public void ShowAt(Func<int, int, (int X, int Y)> place)
    {
        // Measure first, invisible, so the position can use the real size.
        Opacity = 0;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (QuickAskNative.WindowRect(handle) is { } bounds)
        {
            var (x, y) = place(bounds.Width, bounds.Height);
            QuickAskNative.Move(handle, x, y);
        }

        Opacity = 1;
        Activate();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(View.FocusInput));
    }

    /// <summary>Hides the box while the user picks a point, or shows it again after.</summary>
    public void SetPicking(bool picking)
    {
        if (picking)
        {
            Hide();
        }
        else if (!_closing)
        {
            Show();
            Activate();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(View.FocusInput));
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing || _box.IsPicking || !IsVisible)
        {
            return;
        }

        try
        {
            ClickedAway?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: closing the box failed", ex);
        }
    }
}
