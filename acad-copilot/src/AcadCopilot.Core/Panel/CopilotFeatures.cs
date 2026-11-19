using AcadCopilot.Core.Agents;

namespace AcadCopilot.Core.Panel;

/// <summary>
/// The panel's feature switches (persisted in settings.json, shared by all chat tabs, applied from the next turn on):
/// AI switching, "Ask before drawing", automatic second-opinion review and tutor mode.
/// </summary>
public sealed record CopilotFeatures
{
    /// <summary>Continue a job with the next signed-in provider when the current one hits a limit or fails (default on).</summary>
    public bool AutoSwitch { get; init; } = true;

    /// <summary>Takeover order of the providers (default Claude, ChatGPT, Gemini).</summary>
    public IReadOnlyList<ProviderId> ProviderOrder { get; init; } = CopilotSettings.DefaultProviderOrder;

    /// <summary>Ask "Shall I draw this?" once before drawing a new plan (default on).</summary>
    public bool AskBeforeDrawing { get; init; } = true;

    /// <summary>Second-opinion review after a finished drawing (default off; "Review" works on demand anyway).</summary>
    public bool AutoReview { get; init; }

    /// <summary>The AI explains each AutoCAD command it uses in one short line (default off).</summary>
    public bool TutorMode { get; init; }

    /// <summary>True when <paramref name="other"/> has the same values (the order compared item by item).</summary>
    public bool SameAs(CopilotFeatures? other) =>
        other is not null
        && AutoSwitch == other.AutoSwitch
        && AskBeforeDrawing == other.AskBeforeDrawing
        && AutoReview == other.AutoReview
        && TutorMode == other.TutorMode
        && NormalizeOrder(ProviderOrder).SequenceEqual(NormalizeOrder(other.ProviderOrder));

    /// <summary>The per-turn instruction switches of these features.</summary>
    public TurnOptions TurnOptions => new() { AskBeforeDrawing = AskBeforeDrawing, TutorMode = TutorMode };

    /// <summary>
    /// <paramref name="order"/> without duplicates, followed by the providers it leaves out (in the default order), so every
    /// provider has a place.
    /// </summary>
    public static IReadOnlyList<ProviderId> NormalizeOrder(IEnumerable<ProviderId>? order)
    {
        var result = new List<ProviderId>();
        foreach (var provider in (order ?? []).Concat(CopilotSettings.DefaultProviderOrder).Concat(Enum.GetValues<ProviderId>()))
        {
            if (Enum.IsDefined(provider) && !result.Contains(provider))
            {
                result.Add(provider);
            }
        }

        return result;
    }
}

/// <summary>
/// Extra abilities of the panel backend and its chat sessions beyond <see cref="ICopilotSession"/> (kept in a separate
/// interface so other implementations of the session contract stay valid). The panel finds them with
/// <c>session as ICopilotAssist</c>.
/// </summary>
public interface ICopilotAssist
{
    /// <summary>The feature switches (shared by all tabs, persisted); setting them raises StateChanged.</summary>
    CopilotFeatures Features { get; set; }

    /// <summary>
    /// When true (default) the session also writes each <see cref="ProviderSwitched"/> / <see cref="ReviewCompleted"/>
    /// as a plain assistant message (TextDone), for chat views that do not render those events. A view that shows them
    /// itself (switch notice, review card) sets this to false.
    /// </summary>
    bool EmitNoticesAsText { get; set; }

    /// <summary>
    /// Runs a second-opinion review of this chat's drawing as a turn of its own: a separate CLI call (another signed-in
    /// provider when there is one, else the chat's provider in a fresh session) whose AutoCAD bridge is read-only. Streams
    /// the reviewer's tool activity, then <see cref="ReviewCompleted"/>, then exactly one TurnCompleted / TurnFailed.
    /// </summary>
    /// <param name="focus">What to look at ("the first floor plan"), or null for the drawing as a whole.</param>
    /// <param name="cancellationToken">Stop.</param>
    IAsyncEnumerable<AgentEvent> ReviewAsync(string? focus, CancellationToken cancellationToken);
}
