using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Drafting;

/// <summary>
/// How fast the copilot types AutoCAD commands (process-wide, thread-safe).
/// <para>
/// <b>Watch mode</b> (the default) pauses between typed inputs (<see cref="InputDelayMs"/>) and before each new
/// command (<see cref="CommandDelayMs"/>) so the user can follow every command and number in the command line and
/// see the objects appear one by one. <b>Fast mode</b> (<see cref="Watch"/> = false) types without pauses; AutoCAD
/// still finishes each input before the next one is typed.
/// </para>
/// <para>
/// The panel binds its Watch/Fast switch to <see cref="Watch"/>; the drafting engine reads the delays before every
/// pause, so a change applies to the very next input, also in the middle of a running call. <see cref="Changed"/>
/// is raised on the thread that changed the mode (any thread) after the new value is visible, and only when the
/// value really changed; handlers that touch WPF must marshal to the dispatcher themselves.
/// </para>
/// </summary>
public static class DraftingPace
{
    /// <summary>Pause between two typed inputs in watch mode, in milliseconds.</summary>
    public const int WatchInputDelay = 450;

    /// <summary>Pause before each new command in watch mode, in milliseconds.</summary>
    public const int WatchCommandDelay = 900;

    private static readonly Lock Gate = new();
    private static volatile bool _watch = true;

    /// <summary>Raised after <see cref="Watch"/> changed (sender is null). Handler exceptions are logged, never thrown.</summary>
    public static event EventHandler? Changed;

    /// <summary>True = watch mode (pauses so the user can follow), false = fast mode (no pauses). Default true.</summary>
    public static bool Watch
    {
        get => _watch;
        set
        {
            lock (Gate)
            {
                if (_watch == value)
                {
                    return;
                }

                _watch = value;
            }

            RaiseChanged();
        }
    }

    /// <summary>Milliseconds to wait between two typed inputs: <see cref="WatchInputDelay"/> in watch mode, 0 in fast mode.</summary>
    public static int InputDelayMs => _watch ? WatchInputDelay : 0;

    /// <summary>Milliseconds to wait before typing a new command: <see cref="WatchCommandDelay"/> in watch mode, 0 in fast mode.</summary>
    public static int CommandDelayMs => _watch ? WatchCommandDelay : 0;

    /// <summary>Short label of the current mode for logs and status lines ("watch" or "fast").</summary>
    public static string ModeName => _watch ? "watch" : "fast";

    private static void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("A DraftingPace.Changed handler failed", ex);
            }
        }
    }
}
