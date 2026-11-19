using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using InternalUtils = Autodesk.AutoCAD.Internal.Utils;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>
/// Exception-safe wrappers around the AutoCAD calls the drafting engine makes. Main thread only.
/// <para>
/// Public API: <see cref="Document.SendStringToExecute"/> (typing), system variables (CMDACTIVE, LASTPROMPT...).
/// Undocumented helpers of <c>Autodesk.AutoCAD.Internal.Utils</c> in acmgd.dll (present in AutoCAD 2027 26.0, verified
/// by reflection; not supported by Autodesk): GetLastCommandLines (command-line history), IsInputPending, IsTextEditorActive,
/// SetFocusToDwgView and IsCommandNameInUse. Each is called through its own non-inlined method so a missing member
/// cannot break the caller, and is switched off for the session after its first failure (the engine then falls back to
/// public signals only).
/// </para>
/// </summary>
internal static class AcadInput
{
    private const int WmNull = 0x0000;
    private static bool _historyBroken;
    private static bool _pendingBroken;
    private static bool _editorBroken;
    private static bool _focusBroken;
    private static bool _commandTypeBroken;

    /// <summary>Lines of command-line history read per snapshot.</summary>
    public const int HistoryLines = 24;

    /// <summary>
    /// Types <paramref name="text"/> into the command line of <paramref name="document"/> as if the user typed it.
    /// <paramref name="echo"/> shows it in the command line.
    /// </summary>
    public static void Type(Document document, string text, bool echo)
    {
        document.SendStringToExecute(text, true, false, echo);
        WakeUp();
    }

    /// <summary>Presses Esc twice (cancels the active command and a transparent one), without echo.</summary>
    public static void Escape(Document document)
    {
        document.SendStringToExecute("\x03\x03", false, false, false);
        WakeUp();
    }

    /// <summary>CMDACTIVE: 0 at the Command prompt; 1 command, 2 transparent, 4 script, 8 dialog, 32 AutoLISP, 64 ObjectARX.</summary>
    public static int CommandActive() => GetInt("CMDACTIVE");

    /// <summary>True when an ordinary or transparent command (or a dialog) is active.</summary>
    public static bool IsCommandActive() => (CommandActive() & (1 | 2 | 8)) != 0;

    /// <summary>LASTPROMPT, the last line shown in the command line, cleaned ("Specify next point or [Undo]:").</summary>
    public static string LastPrompt() => CommandLineText.Clean(GetString("LASTPROMPT"));

    /// <summary>A system variable, or null when it cannot be read.</summary>
    public static object? Get(string name)
    {
        try
        {
            return AcadApp.GetSystemVariable(name);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A system variable as an integer (0 when missing).</summary>
    public static int GetInt(string name)
    {
        try
        {
            return Get(name) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary>A system variable as text ("" when missing).</summary>
    public static string GetString(string name) => Get(name)?.ToString() ?? string.Empty;

    /// <summary>Sets a system variable; false (and a log line) when AutoCAD refuses.</summary>
    public static bool Set(string name, object value)
    {
        try
        {
            AcadApp.SetSystemVariable(name, value);
            return true;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidOperationException)
        {
            CopilotLog.Warn($"Drafting: could not set {name} to {value}", ex);
            return false;
        }
    }

    /// <summary>
    /// The last <paramref name="lines"/> lines of the command-line history (oldest first), or null when unavailable.
    /// GetLastCommandLines returns them newest first in AutoCAD 2027; <see cref="CommandLineText.Chronological"/> puts
    /// them in order (checked against LASTPROMPT).
    /// </summary>
    public static IReadOnlyList<string>? History(int lines = HistoryLines)
    {
        if (_historyBroken)
        {
            return null;
        }

        try
        {
            return CommandLineText.Chronological(ReadHistory(lines), GetString("LASTPROMPT"));
        }
        catch (Exception ex)
        {
            _historyBroken = true;
            CopilotLog.Warn("Drafting: command-line history (Internal.Utils.GetLastCommandLines) is not available; rejected inputs are detected from LASTPROMPT only", ex);
            return null;
        }
    }

    /// <summary>True while typed input is still queued (not yet taken by AutoCAD); null when unknown.</summary>
    public static bool? IsInputPending()
    {
        if (_pendingBroken)
        {
            return null;
        }

        try
        {
            return ReadInputPending();
        }
        catch (Exception ex)
        {
            _pendingBroken = true;
            CopilotLog.Warn("Drafting: Internal.Utils.IsInputPending is not available; waiting on prompt events and time only", ex);
            return null;
        }
    }

    /// <summary>True while an in-place text editor is open; null when unknown.</summary>
    public static bool? IsTextEditorActive()
    {
        if (_editorBroken)
        {
            return null;
        }

        try
        {
            return ReadTextEditorActive();
        }
        catch (Exception ex)
        {
            _editorBroken = true;
            CopilotLog.Warn("Drafting: Internal.Utils.IsTextEditorActive is not available", ex);
            return null;
        }
    }

    /// <summary>
    /// Moves keyboard focus to the drawing view (like clicking into the drawing), so AutoCAD processes typed input
    /// without the user moving the mouse. Only when AutoCAD is the foreground application.
    /// </summary>
    public static void FocusDrawing()
    {
        if (_focusBroken || !AutoCadIsForeground())
        {
            return;
        }

        try
        {
            SetFocusToDrawing();
        }
        catch (Exception ex)
        {
            _focusBroken = true;
            CopilotLog.Warn("Drafting: Internal.Utils.SetFocusToDwgView is not available", ex);
        }
    }

    /// <summary>
    /// What kind of command <paramref name="name"/> is (built-in, ObjectARX/.NET, AutoLISP, action macro, system variable),
    /// or null when unknown or not a command name.
    /// </summary>
    public static Autodesk.AutoCAD.Internal.CommandTypeFlags? CommandType(string name)
    {
        if (_commandTypeBroken || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return ReadCommandType(name);
        }
        catch (Exception ex)
        {
            _commandTypeBroken = true;
            CopilotLog.Warn("Drafting: Internal.Utils.IsCommandNameInUse is not available; AutoLISP-defined commands cannot be recognised", ex);
            return null;
        }
    }

    /// <summary>Posts a no-op message to AutoCAD's main window so its message loop runs and takes queued input.</summary>
    public static void WakeUp()
    {
        try
        {
            var window = AcadApp.MainWindow?.Handle ?? IntPtr.Zero;
            if (window != IntPtr.Zero)
            {
                PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            // The main window is not available (AutoCAD starting or closing): nothing to wake.
        }
    }

    /// <summary>True when the foreground window belongs to this AutoCAD process.</summary>
    public static bool AutoCadIsForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>The handle of AutoCAD's main window, or zero.</summary>
    public static IntPtr MainWindowHandle()
    {
        try
        {
            return AcadApp.MainWindow?.Handle ?? IntPtr.Zero;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Elapsed-time helper for log lines.</summary>
    public static string Ms(Stopwatch stopwatch) => stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<string> ReadHistory(int lines) => InternalUtils.GetLastCommandLines(lines, true) ?? [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadInputPending() => InternalUtils.IsInputPending();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReadTextEditorActive() => InternalUtils.IsTextEditorActive();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetFocusToDrawing() => InternalUtils.SetFocusToDwgView();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Autodesk.AutoCAD.Internal.CommandTypeFlags ReadCommandType(string name) => InternalUtils.IsCommandNameInUse(name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
