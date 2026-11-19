using System.Runtime.InteropServices;
using System.Text;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>
/// Finds and cancels a modal dialog box that a typed command opened on AutoCAD's main thread (the copilot cannot type
/// into dialogs and the user does not want pop-ups). The dialog is the visible, enabled window of the main thread whose
/// owner is disabled (Windows disables the owner of every modal MFC, Win32, task or WPF dialog). Cancelling sends
/// IDCANCEL (the Cancel button / Esc), then WM_CLOSE. Main thread only.
/// </summary>
internal static class DialogCloser
{
    private const int WmCommand = 0x0111;
    private const int WmClose = 0x0010;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int VkEscape = 0x1B;
    private const int IdCancel = 2;
    private const uint GwOwner = 4;

    /// <summary>
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> of the copilot's last <see cref="PressEscapeKey"/> (0 = never),
    /// so <see cref="RunWatch"/> does not take that key press for the user's.
    /// </summary>
    public static long LastPressTimestamp { get; private set; }

    /// <summary>The title of the open modal dialog ("Text Style"), "" for an untitled one, or null when none is found.</summary>
    public static string? ActiveDialogTitle() => FindDialog() is { } dialog ? TitleOf(dialog) : null;

    /// <summary>
    /// Asks the open modal dialog to cancel: <paramref name="attempt"/> 0 sends IDCANCEL, later attempts WM_CLOSE.
    /// Returns false when no dialog was found.
    /// </summary>
    public static bool TryCancel(int attempt)
    {
        if (FindDialog() is not { } dialog)
        {
            return false;
        }

        return attempt == 0
            ? PostMessage(dialog, WmCommand, (IntPtr)IdCancel, IntPtr.Zero)
            : PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Presses the Esc key in the window of AutoCAD's main thread that has the keyboard focus (an in-place text editor
    /// reads the keyboard, not the command line). Does nothing when no window of this thread has the focus.
    /// </summary>
    public static void PressEscapeKey()
    {
        var focus = GetFocus();
        if (focus == IntPtr.Zero)
        {
            return;
        }

        LastPressTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        PostMessage(focus, WmKeyDown, new IntPtr(VkEscape), new IntPtr(0x00010001L));
        PostMessage(focus, WmKeyUp, new IntPtr(VkEscape), new IntPtr(0xC0010001L));
    }

    private static IntPtr? FindDialog()
    {
        IntPtr? found = null;
        var mainWindow = AcadInput.MainWindowHandle();
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            if (window == mainWindow || !IsWindowVisible(window) || !IsWindowEnabled(window))
            {
                return true;
            }

            var owner = GetWindow(window, GwOwner);
            if (owner != IntPtr.Zero && !IsWindowEnabled(owner))
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string TitleOf(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(length + 1);
        _ = GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
