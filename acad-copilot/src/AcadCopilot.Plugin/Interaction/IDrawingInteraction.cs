namespace AcadCopilot.Plugin.Interaction;

/// <summary>Who asked for a point: the user (the composer's Point button) or the AI (the pick_point tool).</summary>
public enum PickSource
{
    /// <summary>The user clicked "Point" in the composer.</summary>
    User,

    /// <summary>The AI called pick_point.</summary>
    Ai,
}

/// <summary>How a pick ended.</summary>
public enum PickStatus
{
    /// <summary>At least one point was clicked (Enter or the point limit finished it).</summary>
    Picked,

    /// <summary>The user pressed Esc (or Cancel in the panel) before clicking a point.</summary>
    Cancelled,

    /// <summary>Nobody clicked within the time limit.</summary>
    TimedOut,

    /// <summary>AutoCAD could not ask (a command is running, no drawing is open ...); see the message.</summary>
    Failed,
}

/// <summary>A request to let the user click one or more points in the drawing.</summary>
public sealed record PickRequest
{
    /// <summary>Most points one request may collect.</summary>
    public const int MaxPointsLimit = 20;

    /// <summary>Longest time limit, seconds.</summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>What the user is asked to click ("Click where the kitchen should go"); shown in the command line and the panel.</summary>
    public string Prompt { get; init; } = "Click a point in the drawing";

    /// <summary>Most points (1 = a single click; more = click several, Enter finishes).</summary>
    public int MaxPoints { get; init; } = 1;

    /// <summary>How long to wait for the clicks.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Who asks.</summary>
    public PickSource Source { get; init; } = PickSource.User;

    /// <summary>A copy with the limits applied (1-20 points, 5-600 s, a non-empty one-line prompt).</summary>
    public PickRequest Normalized()
    {
        var prompt = string.Join(' ', (Prompt ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(':');
        if (prompt.Length == 0)
        {
            prompt = "Click a point in the drawing";
        }

        if (prompt.Length > 160)
        {
            prompt = prompt[..160].TrimEnd();
        }

        var seconds = Math.Clamp(Timeout.TotalSeconds, 5, MaxTimeoutSeconds);
        return this with
        {
            Prompt = prompt,
            MaxPoints = Math.Clamp(MaxPoints, 1, MaxPointsLimit),
            Timeout = TimeSpan.FromSeconds(seconds),
        };
    }
}

/// <summary>One clicked point in world coordinates (drawing units), with feet-inch text when the drawing's units allow it.</summary>
/// <param name="X">X in drawing units (WCS).</param>
/// <param name="Y">Y in drawing units (WCS).</param>
/// <param name="Z">Z in drawing units (WCS).</param>
/// <param name="XText">X in feet-inches ("12'-6\""), or null when the units are not architectural / engineering.</param>
/// <param name="YText">Y in feet-inches, or null.</param>
public sealed record PickedPoint(double X, double Y, double Z = 0, string? XText = null, string? YText = null);

/// <summary>The outcome of a pick.</summary>
public sealed record PickResult
{
    /// <summary>How it ended.</summary>
    public PickStatus Status { get; init; }

    /// <summary>The points clicked, in order (empty unless <see cref="PickStatus.Picked"/>).</summary>
    public IReadOnlyList<PickedPoint> Points { get; init; } = [];

    /// <summary>A sentence for the user or the AI (why it failed, what was picked).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The drawing the points were picked in ("House.dwg"), when known.</summary>
    public string? DrawingTitle { get; init; }

    /// <summary>A failed result.</summary>
    public static PickResult Fail(string message) => new() { Status = PickStatus.Failed, Message = message };
}

/// <summary>The pick that is waiting for clicks now (shown as a banner in the panel).</summary>
/// <param name="Prompt">What the user is asked to click.</param>
/// <param name="Source">Who asked.</param>
/// <param name="MaxPoints">How many points it collects at most.</param>
/// <param name="PointsSoFar">Points already clicked.</param>
public sealed record PickInProgress(string Prompt, PickSource Source, int MaxPoints, int PointsSoFar);

/// <summary>One selected object (compact).</summary>
/// <param name="Handle">Hex handle ("2A3F").</param>
/// <param name="Type">DXF type ("LWPOLYLINE").</param>
/// <param name="Layer">Layer name.</param>
/// <param name="Detail">A short detail ("length 12'-0\"", "\"KITCHEN\"", "block DOOR-36"), or empty.</param>
public sealed record SelectedObject(string Handle, string Type, string Layer, string Detail = "");

/// <summary>The user's selection in the active drawing (AutoCAD's pickfirst / implied selection).</summary>
public sealed record SelectionSnapshot
{
    /// <summary>Objects in the selection set described in <see cref="Items"/>.</summary>
    public const int MaxDescribed = 60;

    /// <summary>Number of selected objects.</summary>
    public int Count { get; init; }

    /// <summary>The first <see cref="MaxDescribed"/> objects.</summary>
    public IReadOnlyList<SelectedObject> Items { get; init; } = [];

    /// <summary>The drawing ("House.dwg").</summary>
    public string? DrawingTitle { get; init; }

    /// <summary>True when the selection is empty.</summary>
    public bool IsEmpty => Count <= 0;

    /// <summary>A stable key of what is selected (changes when the set changes).</summary>
    public string Key => DrawingTitle + "|" + Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + string.Join(",", Items.Select(i => i.Handle));
}

/// <summary>
/// The panel's link to the drawing for "click to point" and "select and ask" (implemented inside AutoCAD by
/// <see cref="AcadDrawingInteraction"/>; tests use a fake). Free of AutoCAD types so the view models stay testable.
/// Events are raised on the panel's (AutoCAD's main) thread.
/// </summary>
public interface IDrawingInteraction
{
    /// <summary>Raised when <see cref="CurrentSelection"/> changed.</summary>
    event EventHandler? SelectionChanged;

    /// <summary>Raised when a pick starts, gets a point or ends (<see cref="ActivePick"/>).</summary>
    event EventHandler? PickStateChanged;

    /// <summary>The user's current selection in the active drawing (null or empty when nothing is selected).</summary>
    SelectionSnapshot? CurrentSelection { get; }

    /// <summary>The pick that waits for clicks now, or null.</summary>
    PickInProgress? ActivePick { get; }

    /// <summary>
    /// Asks the user to click points in the active drawing (prompt in AutoCAD's command line and in the panel), waiting for
    /// its turn when a chat is typing into AutoCAD. Never throws for expected problems: they come back as
    /// <see cref="PickStatus.Failed"/>. Cancelling <paramref name="cancellationToken"/> ends the pick like Esc.
    /// </summary>
    Task<PickResult> PickPointsAsync(PickRequest request, CancellationToken cancellationToken);

    /// <summary>Ends the active pick as if the user pressed Esc (the panel's Cancel button).</summary>
    void CancelPick();
}
