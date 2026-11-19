namespace AcadCopilot.Core.Agents;

/// <summary>The AI providers the panel can drive, each through its official CLI.</summary>
public enum ProviderId
{
    /// <summary>Anthropic Claude via Claude Code ('claude').</summary>
    Claude,

    /// <summary>OpenAI ChatGPT via Codex CLI ('codex').</summary>
    Codex,

    /// <summary>Google Gemini via Gemini CLI ('gemini').</summary>
    Gemini,
}

/// <summary>Whether the CLI is signed in to the user's account.</summary>
public enum SignInState
{
    /// <summary>Could not determine (the CLI has no status command or it failed).</summary>
    Unknown,

    /// <summary>Signed in.</summary>
    SignedIn,

    /// <summary>Not signed in: the panel shows the "Sign in" button.</summary>
    SignedOut,
}

/// <summary>Result of <see cref="IAgentRunner.GetStatusAsync"/>.</summary>
public sealed record ProviderStatus
{
    /// <summary>Provider.</summary>
    public required ProviderId Provider { get; init; }

    /// <summary>True when the CLI executable was found.</summary>
    public bool Installed { get; init; }

    /// <summary>CLI version string, when installed.</summary>
    public string? Version { get; init; }

    /// <summary>Sign-in state.</summary>
    public SignInState SignIn { get; init; } = SignInState.Unknown;

    /// <summary>Account shown in the panel (e-mail / plan), when the CLI reports it.</summary>
    public string? Account { get; init; }

    /// <summary>Resolved executable path.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Human-readable detail or the reason it is unusable ("npm install -g @openai/codex").</summary>
    public string? Detail { get; init; }

    /// <summary>True when a turn can be started (installed and not signed out).</summary>
    public bool IsUsable => Installed && SignIn != SignInState.SignedOut;
}

/// <summary>A model the user can pick for a provider.</summary>
/// <param name="Id">Value passed to the CLI's model flag; null means "CLI default".</param>
/// <param name="DisplayName">Label in the model drop-down.</param>
public sealed record ModelChoice(string? Id, string DisplayName);

/// <summary>A command line: executable/command name plus arguments (no shell quoting involved).</summary>
/// <param name="FileName">Executable path or a bare command resolved with <see cref="CommandLocator"/>.</param>
/// <param name="Arguments">Arguments, one per element.</param>
public sealed record CommandLine(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// How a CLI must launch the AutoCAD MCP server (the bridge, AcadCopilotMcp.exe) for this turn.
/// Runners turn this into per-invocation MCP configuration and never touch the user's global config:
/// Claude Code: --mcp-config &lt;file or JSON&gt; --strict-mcp-config;
/// Codex: -c mcp_servers.&lt;ServerName&gt;.command=... -c mcp_servers.&lt;ServerName&gt;.args=[...] (TOML values);
/// Gemini: settings.json "mcpServers" in a private workspace folder.
/// </summary>
public sealed record McpServerLaunch
{
    /// <summary>MCP server name as the CLI sees it; tools appear as e.g. mcp__acad__create_wall in Claude Code.</summary>
    public string ServerName { get; init; } = "acad";

    /// <summary>Absolute path of AcadCopilotMcp.exe.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments, e.g. ["--pid", "1234", "--panel"].</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment variables for the bridge process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Per-call timeout the CLI should allow for AutoCAD tools, in seconds. ask_user waits for a human,
    /// so this is long; configure the CLI's MCP tool timeout to at least this.
    /// </summary>
    public int ToolTimeoutSeconds { get; init; } = 900;
}

/// <summary>Everything a runner needs for one user turn.</summary>
public sealed record TurnRequest
{
    /// <summary>The user's message.</summary>
    public required string Prompt { get; init; }

    /// <summary>Absolute paths of files the user attached (images, PDFs, text).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>Model id from <see cref="IAgentRunner.Models"/>; null = CLI default.</summary>
    public string? Model { get; init; }

    /// <summary>Session id from a previous <see cref="SessionStarted"/> of the same provider to continue that conversation; null = new conversation.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>The AutoCAD MCP server to attach.</summary>
    public required McpServerLaunch AcadMcp { get; init; }

    /// <summary>Private working directory (exists) under %LOCALAPPDATA%\AcadCopilot\workspace.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Extra system instructions (AutoCAD copilot behaviour); pass via the CLI's system-prompt mechanism.</summary>
    public string Instructions { get; init; } = CopilotInstructions.Default;
}

/// <summary>Why a turn failed.</summary>
public enum AgentFailureKind
{
    /// <summary>The CLI is not signed in (show "Sign in").</summary>
    NotSignedIn,

    /// <summary>The CLI is not installed / not found.</summary>
    NotInstalled,

    /// <summary>Usage limit or rate limit reached.</summary>
    RateLimited,

    /// <summary>The user pressed Stop.</summary>
    Cancelled,

    /// <summary>Anything else (message explains).</summary>
    Other,
}

/// <summary>
/// One event of a turn's stream. Order: optional <see cref="SessionStarted"/>, then any mix of text/tool/usage
/// events, then exactly one terminal event (<see cref="TurnCompleted"/> or <see cref="TurnFailed"/>).
/// </summary>
public abstract record AgentEvent;

/// <summary>The CLI reported its session id (store it to resume the conversation next turn).</summary>
/// <param name="SessionId">CLI session/thread id.</param>
public sealed record SessionStarted(string SessionId) : AgentEvent;

/// <summary>Streamed assistant text fragment (append to the current message).</summary>
/// <param name="Text">Fragment.</param>
public sealed record TextDelta(string Text) : AgentEvent;

/// <summary>A complete assistant message (replaces the fragments streamed for it, if any).</summary>
/// <param name="Text">Full message text (Markdown).</param>
public sealed record TextDone(string Text) : AgentEvent;

/// <summary>A tool call started (one "CAD activity" row with a spinner).</summary>
/// <param name="CallId">CLI tool-call id (matches <see cref="ToolCallFinished.CallId"/>).</param>
/// <param name="Name">Bare tool name ("create_wall"), with any MCP prefix like "mcp__acad__" removed.</param>
/// <param name="Summary">Short human summary of the arguments ("Level 1, 4 walls").</param>
/// <param name="Server">MCP server name ("acad") for MCP tools; null for the CLI's own built-in tools.</param>
public sealed record ToolCallStarted(string CallId, string Name, string Summary, string? Server = null) : AgentEvent;

/// <summary>A tool call finished (check or cross).</summary>
/// <param name="CallId">Id from <see cref="ToolCallStarted"/>.</param>
/// <param name="Success">True when the tool succeeded.</param>
/// <param name="Summary">Short result summary ("Created 4 walls").</param>
/// <param name="Error">Error text when it failed.</param>
public sealed record ToolCallFinished(string CallId, bool Success, string Summary, string? Error = null) : AgentEvent;

/// <summary>Token usage reported by the CLI.</summary>
/// <param name="Model">Model that answered.</param>
/// <param name="InputTokens">Input tokens.</param>
/// <param name="OutputTokens">Output tokens.</param>
public sealed record Usage(string? Model, long InputTokens, long OutputTokens) : AgentEvent;

/// <summary>The turn finished normally.</summary>
/// <param name="StopReason">CLI stop reason ("end_turn", "success", ...).</param>
public sealed record TurnCompleted(string StopReason) : AgentEvent;

/// <summary>The turn failed (terminal event).</summary>
/// <param name="Message">User-facing explanation.</param>
/// <param name="Kind">Failure category.</param>
public sealed record TurnFailed(string Message, AgentFailureKind Kind) : AgentEvent;

/// <summary>Why the panel continued a job with another provider.</summary>
public enum SwitchReason
{
    /// <summary>The provider's usage or rate limit was reached.</summary>
    Limit,

    /// <summary>The CLI's sign-in expired or is missing.</summary>
    SignIn,

    /// <summary>The CLI failed again after a retry (or is not installed).</summary>
    Failure,
}

/// <summary>
/// Not terminal: the selected provider stopped (limit, sign-in, repeated failure) and the same job continues with
/// <paramref name="To"/> in the same turn; the events that follow come from the new provider. The chat's provider is now
/// <paramref name="To"/> (the session raises StateChanged). <paramref name="Message"/> is the line for the chat
/// ("Claude reached its limit - continuing with ChatGPT.").
/// </summary>
/// <param name="From">Provider that stopped.</param>
/// <param name="To">Provider that continues.</param>
/// <param name="Reason">Why.</param>
/// <param name="Message">User-facing line.</param>
/// <param name="Detail">The original failure text of <paramref name="From"/>.</param>
public sealed record ProviderSwitched(ProviderId From, ProviderId To, SwitchReason Reason, string Message, string Detail) : AgentEvent;

/// <summary>Not terminal: a second-opinion review finished; show <paramref name="Report"/> as a review card.</summary>
/// <param name="Report">Findings, with <see cref="ReviewReport.FixPrompt"/> for the card's "Fix these" button.</param>
public sealed record ReviewCompleted(ReviewReport Report) : AgentEvent;

/// <summary>
/// Drives one provider's official CLI (Claude Code, Codex, Gemini CLI) in non-interactive JSON streaming mode,
/// signed in with the user's own account (no API keys). Implementations live in
/// AcadCopilot.Core.Agents (ClaudeCodeRunner, CodexRunner, GeminiRunner).
/// </summary>
public interface IAgentRunner
{
    /// <summary>Which provider this runner drives.</summary>
    ProviderId ProviderId { get; }

    /// <summary>Display name ("Claude", "ChatGPT", "Gemini").</summary>
    string DisplayName { get; }

    /// <summary>Models offered in the panel; the first entry is the default (Id may be null = CLI default).</summary>
    IReadOnlyList<ModelChoice> Models { get; }

    /// <summary>Checks installation, version and sign-in state without side effects (no login flows, no config writes).</summary>
    Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The CLI's own interactive login command (e.g. claude + ["auth", "login"]). The panel opens it in a
    /// visible terminal only when the user clicks "Sign in".
    /// </summary>
    CommandLine GetSignInCommand();

    /// <summary>
    /// Runs one turn: starts the CLI (no window), streams its JSON output as <see cref="AgentEvent"/>s and ends
    /// with exactly one <see cref="TurnCompleted"/> or <see cref="TurnFailed"/>. Cancelling
    /// <paramref name="cancellationToken"/> kills the CLI process tree and yields
    /// <see cref="TurnFailed"/> with <see cref="AgentFailureKind.Cancelled"/> (it must not throw for Stop).
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, CancellationToken cancellationToken);
}
