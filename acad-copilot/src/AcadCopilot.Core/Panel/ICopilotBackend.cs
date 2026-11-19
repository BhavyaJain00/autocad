using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Interaction;

namespace AcadCopilot.Core.Panel;

/// <summary>Information about the AutoCAD session hosting the panel (shown in the panel header).</summary>
/// <param name="Pid">AutoCAD process id.</param>
/// <param name="AcadVersion">AutoCAD version number ("2027").</param>
/// <param name="DocumentTitle">Active document title, or null when no model is open.</param>
public sealed record AcadSessionInfo(int Pid, string? AcadVersion, string? DocumentTitle)
{
    /// <summary>
    /// The host product ("AutoCAD 2027", "AutoCAD Architecture 2027"); null = unknown (treated as plain AutoCAD). The
    /// system instructions mention AutoCAD Architecture objects only when this names that product.
    /// </summary>
    public string? Product { get; init; }
}

/// <summary>
/// THE CONTRACT BETWEEN THE CHAT VIEW MODEL AND EVERYTHING ELSE. The add-in's PaneController creates one
/// <see cref="CopilotBackend"/> and passes it to the CopilotPane view; the view model:
/// <list type="number">
/// <item>calls <see cref="AttachUi"/> with its <see cref="ICopilotUi"/> implementation (questions, plan, approvals);</item>
/// <item>lists <see cref="Runners"/> for the provider picker and their <see cref="IAgentRunner.Models"/> for the model picker;</item>
/// <item>calls <see cref="GetStatusAsync"/> to show "Signed in / Sign in" per provider and <see cref="SignIn"/> for the button;</item>
/// <item>calls <see cref="SendAsync"/> for each user message and renders the <see cref="AgentEvent"/> stream
/// (TextDelta/TextDone = assistant text, ToolCallStarted/Finished = "CAD activity" rows, TurnFailed = error banner);
/// Stop = cancel the token passed to <see cref="SendAsync"/>;</item>
/// <item>calls <see cref="NewChat"/> for the New chat button.</item>
/// </list>
/// Events may be raised on any thread; marshal to the WPF dispatcher.
/// </summary>
public interface ICopilotBackend
{
    /// <summary>One runner per provider, in display order (Claude, ChatGPT, Gemini).</summary>
    IReadOnlyList<IAgentRunner> Runners { get; }

    /// <summary>Currently selected provider (persisted in settings).</summary>
    ProviderId SelectedProvider { get; set; }

    /// <summary>True while a turn is running.</summary>
    bool IsBusy { get; }

    /// <summary>AutoCAD host info (document title updates as the user switches models).</summary>
    AcadSessionInfo Acad { get; }

    /// <summary>Raised when <see cref="IsBusy"/>, <see cref="SelectedProvider"/>, a model choice or <see cref="Acad"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised after <see cref="NewChat"/> (the add-in resets "always approve" choices).</summary>
    event EventHandler? ChatReset;

    /// <summary>Selected model id for <paramref name="provider"/> (null = CLI default).</summary>
    string? GetModel(ProviderId provider);

    /// <summary>Selects a model for <paramref name="provider"/> (persisted). Null = CLI default.</summary>
    void SetModel(ProviderId provider, string? modelId);

    /// <summary>Install/sign-in status of a provider's CLI (read-only check).</summary>
    Task<ProviderStatus> GetStatusAsync(ProviderId provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a user message to the selected provider and streams the turn. The conversation continues across
    /// calls (CLI session resume) until <see cref="NewChat"/> or the provider changes. Bookkeeping tool calls
    /// (ask_user, update_plan) are filtered out: they arrive through <see cref="ICopilotUi"/> instead.
    /// Never throws for runner failures: the stream ends with exactly one TurnCompleted or TurnFailed.
    /// </summary>
    IAsyncEnumerable<AgentEvent> SendAsync(string text, IReadOnlyList<string> attachments, CancellationToken cancellationToken);

    /// <summary>Forgets the CLI sessions so the next message starts a new conversation.</summary>
    void NewChat();

    /// <summary>Opens the provider CLI's own login flow in a terminal window (user-initiated). False when the CLI is not installed.</summary>
    bool SignIn(ProviderId provider);

    /// <summary>Registers the panel UI that answers questions, shows the plan and approvals.</summary>
    void AttachUi(ICopilotUi ui);
}

/// <summary>
/// An AutoCAD drawing a chat tab works on: the drawing that was active when the tab was created. The id is assigned by
/// the plug-in (kept in the document's UserData for the document's lifetime), the title is its file name.
/// </summary>
/// <param name="Id">Plug-in id of the open document ("dwg-3").</param>
/// <param name="Title">File name ("Drawing2.dwg").</param>
public sealed record DrawingRef(string Id, string Title)
{
    /// <summary>True after the drawing was closed in AutoCAD (the tab's drawing actions then fail with a clear message).</summary>
    public bool IsClosed { get; init; }

    /// <summary>"Drawing2.dwg", or "Drawing2.dwg (closed)".</summary>
    public string DisplayTitle => IsClosed ? Title + " (closed)" : Title;
}

/// <summary>
/// One chat tab's conversation: its own CLI sessions (resume), provider and model, history, task progress, approvals
/// ("Always" choices) and Stop, bound to one drawing. It is the <see cref="ICopilotBackend"/> the tab's view model
/// talks to; several sessions run their turns in parallel (separate CLI processes) while their drawing actions take
/// turns in AutoCAD (<see cref="AcadTurnQueue"/>).
/// </summary>
public interface ICopilotSession : ICopilotBackend
{
    /// <summary>Session id, passed to the bridge as <c>--session &lt;id&gt;</c> (letters, digits, '-').</summary>
    string Id { get; }

    /// <summary>The drawing this chat works on; null when no drawing was open when it was created (it binds on first use).</summary>
    DrawingRef? Drawing { get; }

    /// <summary>This chat's place in the AutoCAD turn queue (raises <see cref="ICopilotBackend.StateChanged"/> when it changes).</summary>
    AcadTurnStatus AcadTurn { get; }

    /// <summary>False when the most chats that may run at once are already running (this one is not among them).</summary>
    bool CanStartTurn { get; }

    /// <summary>The most chats that may run at once (RAM: each runs its own CLI process).</summary>
    int MaxRunningTurns { get; }
}

/// <summary>
/// The panel's chat tabs: creates and closes <see cref="ICopilotSession"/>s and knows which one is shown (questions and
/// plans from clients outside the panel go there). Implemented by <see cref="CopilotBackend"/>.
/// </summary>
public interface ICopilotHub
{
    /// <summary>One runner per provider, in display order.</summary>
    IReadOnlyList<IAgentRunner> Runners { get; }

    /// <summary>Open chat sessions in creation order.</summary>
    IReadOnlyList<ICopilotSession> Sessions { get; }

    /// <summary>The session of the tab the user sees (calls without a session id use it).</summary>
    ICopilotSession? ActiveSession { get; set; }

    /// <summary>The most sessions that may run a turn at once.</summary>
    int MaxRunningSessions { get; }

    /// <summary>Sessions running a turn now.</summary>
    int RunningSessionCount { get; }

    /// <summary>Raised when sessions are added or removed or the number of running sessions changes (any thread).</summary>
    event EventHandler? SessionsChanged;

    /// <summary>Creates a session bound to the drawing that is active now (main/UI thread).</summary>
    ICopilotSession CreateSession();

    /// <summary>Removes a session (stop its turn first); calls for it fail from then on.</summary>
    void CloseSession(ICopilotSession session);
}
