using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Agents;

/// <summary>
/// Drives Claude Code ('claude', signed in with the user's Claude account) for the panel:
/// <c>claude -p --output-format stream-json --verbose --include-partial-messages</c> with the prompt on stdin,
/// the AutoCAD bridge as the only MCP server (<c>--mcp-config &lt;temp file&gt; --strict-mcp-config</c>), only the
/// AutoCAD tools pre-approved (<c>--allowedTools mcp__acad__*</c>, <c>--permission-mode dontAsk</c>), no built-in
/// tools (<c>--tools ""</c>, or only Read when files are attached), the user's own settings/plugins/connectors left
/// out, and <c>--resume &lt;session&gt;</c> for multi-turn chats. Nothing in the user's Claude configuration is changed.
/// </summary>
public sealed class ClaudeCodeRunner : IAgentRunner
{
    /// <summary>CLI facts.</summary>
    internal static readonly CliProfile Profile = new("Claude", "Claude Code", "claude", "@anthropic-ai/claude-code", "Claude");

    private readonly CliRunnerOptions _options;

    /// <summary>Creates the runner.</summary>
    public ClaudeCodeRunner()
        : this(new CliRunnerOptions())
    {
    }

    /// <summary>Creates the runner with test seams.</summary>
    internal ClaudeCodeRunner(CliRunnerOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public ProviderId ProviderId => ProviderId.Claude;

    /// <inheritdoc />
    public string DisplayName => "Claude";

    /// <inheritdoc />
    public IReadOnlyList<ModelChoice> Models { get; } =
    [
        new(null, "Default"),
        new("fable", "Fable"),
        new("opus", "Opus"),
        new("sonnet", "Sonnet"),
        new("haiku", "Haiku"),
    ];

    /// <inheritdoc />
    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var resolved = _options.Resolve(Profile.Command);
        if (resolved is null)
        {
            return new ProviderStatus { Provider = ProviderId.Claude, Installed = false, Detail = Profile.NotInstalledMessage };
        }

        var environment = StatusEnvironment();
        var versionResult = await CliCommands.RunAsync(resolved, ["--version"], _options, environment, cancellationToken).ConfigureAwait(false);
        var authResult = await CliCommands.RunAsync(resolved, ["auth", "status", "--json"], _options, environment, cancellationToken).ConfigureAwait(false);
        var (state, account, detail) = ParseAuthStatus(authResult);
        return new ProviderStatus
        {
            Provider = ProviderId.Claude,
            Installed = true,
            Version = CliCommands.ParseVersion(versionResult?.StandardOutput),
            SignIn = state,
            Account = account,
            ExecutablePath = CliCommands.ExecutableOf(resolved),
            Detail = detail,
        };
    }

    /// <inheritdoc />
    public CommandLine GetSignInCommand() => new("claude", ["auth", "login"]);

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(TurnRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = _options.Resolve(Profile.Command);
        if (resolved is null)
        {
            yield return new TurnFailed(Profile.NotInstalledMessage, AgentFailureKind.NotInstalled);
            yield break;
        }

        await foreach (var agentEvent in CliTurnEngine.RunAsync(
                           Profile,
                           request.ResumeSessionId,
                           resume => BuildInvocation(resolved, request, resume),
                           () => new ClaudeStreamParser(request.AcadMcp.ServerName),
                           _options,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    /// <summary>Writes the per-turn files (MCP config, instructions) and builds the process start.</summary>
    internal static CliInvocation BuildInvocation(ResolvedCommand command, TurnRequest request, string? resumeSessionId)
    {
        var workDirectory = AppPaths.Ensure(Path.Combine(request.WorkingDirectory, "claude"));
        var turnDirectory = AppPaths.Ensure(Path.Combine(request.WorkingDirectory, "claude-turns", Guid.NewGuid().ToString("N")));
        var mcpConfigPath = Path.Combine(turnDirectory, "mcp.json");
        var instructionsPath = Path.Combine(turnDirectory, "instructions.md");
        File.WriteAllText(mcpConfigPath, BuildMcpConfig(request.AcadMcp).ToJsonString(), ProcessRunner.Utf8NoBom);
        File.WriteAllText(instructionsPath, request.Instructions, ProcessRunner.Utf8NoBom);

        var attachments = CliText.ClassifyAttachments(request.Attachments);
        var readable = attachments.Where(a => a.Kind is AttachmentKind.Image or AttachmentKind.Pdf or AttachmentKind.Text).ToList();
        var unavailable = attachments.Where(a => a.Kind is AttachmentKind.Other or AttachmentKind.Missing).ToList();
        var prompt = CliText.ComposePrompt(request.Prompt, readable, "use the Read tool to open them", [], unavailable);
        var arguments = BuildArguments(request, resumeSessionId, mcpConfigPath, instructionsPath, CliText.AttachmentFolders(readable));
        var launch = command.ToLaunch(arguments, workDirectory) with { Environment = TurnEnvironment(request.AcadMcp) };
        return new CliInvocation(launch, prompt, [turnDirectory]);
    }

    /// <summary>Command-line arguments of one turn. The prompt is NOT among them (it goes to stdin).</summary>
    internal static IReadOnlyList<string> BuildArguments(
        TurnRequest request,
        string? resumeSessionId,
        string mcpConfigPath,
        string instructionsPath,
        IReadOnlyList<string> attachmentFolders)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--mcp-config", mcpConfigPath,
            "--strict-mcp-config",
            "--allowedTools", $"mcp__{request.AcadMcp.ServerName}__*",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
            // Built-in tools off (no shell, no file edits, no web); Read only to open attached files.
            "--tools", attachmentFolders.Count > 0 ? "Read" : string.Empty,
            // Leave out the user's own settings, hooks and plugins; the private folder has none.
            "--setting-sources", "project,local",
            "--disable-slash-commands",
            "--append-system-prompt-file", instructionsPath,
        };
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            arguments.Add("--resume");
            arguments.Add(resumeSessionId);
        }

        foreach (var folder in attachmentFolders)
        {
            arguments.Add("--add-dir");
            arguments.Add(folder);
        }

        return arguments;
    }

    /// <summary>The --mcp-config document: only the AutoCAD bridge, with a long per-call timeout (ask_user waits for a person).</summary>
    internal static JsonObject BuildMcpConfig(McpServerLaunch server)
    {
        var entry = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = server.Command,
            ["args"] = new JsonArray([.. server.Arguments.Select(a => (JsonNode?)JsonValue.Create(a))]),
            ["timeout"] = (long)server.ToolTimeoutSeconds * 1000,
        };
        if (server.Environment.Count > 0)
        {
            var environment = new JsonObject();
            foreach (var (name, value) in server.Environment)
            {
                environment[name] = value;
            }

            entry["env"] = environment;
        }

        return new JsonObject { ["mcpServers"] = new JsonObject { [server.ServerName] = entry } };
    }

    /// <summary>Environment of a turn: per-invocation switches only (documented Claude Code variables).</summary>
    internal static IReadOnlyDictionary<string, string?> TurnEnvironment(McpServerLaunch server)
    {
        var environment = new Dictionary<string, string?>(StatusEnvironment())
        {
            // ask_user can wait for minutes: never move AutoCAD calls to the background or time them out early.
            ["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"] = "0",
            ["MCP_TOOL_TIMEOUT"] = ((long)server.ToolTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture),
            ["MCP_TIMEOUT"] = "60000",
            // Load the AutoCAD tools up front and keep the user's claude.ai connectors out of the panel.
            ["ENABLE_TOOL_SEARCH"] = "false",
            ["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false",
            ["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1",
            ["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1",
        };
        return environment;
    }

    /// <summary>Environment for status commands.</summary>
    internal static IReadOnlyDictionary<string, string?> StatusEnvironment() => new Dictionary<string, string?>
    {
        ["NO_COLOR"] = "1",
        ["DISABLE_AUTOUPDATER"] = "1",
        // AutoCAD may have been started from a Claude Code terminal; this is a separate, independent session.
        ["CLAUDECODE"] = null,
    };

    /// <summary>Reads 'claude auth status --json' output.</summary>
    internal static (SignInState State, string? Account, string? Detail) ParseAuthStatus(ProcessResult? result)
    {
        if (result is null)
        {
            return (SignInState.Unknown, null, "Could not run 'claude auth status'.");
        }

        if (result.TimedOut)
        {
            return (SignInState.Unknown, null, "'claude auth status' did not answer in time.");
        }

        var text = result.StandardOutput;
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start && CliText.TryParseObject(text[start..(end + 1)], out var json))
        {
            if (CliText.Bool(json, "loggedIn") == true)
            {
                return (SignInState.SignedIn, DescribeAccount(json), null);
            }

            if (CliText.Bool(json, "loggedIn") == false)
            {
                return (SignInState.SignedOut, null, "Not signed in. Click \"Sign in\" to log in with your Claude account.");
            }
        }

        var all = text + "\n" + result.StandardError;
        if (all.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return (SignInState.SignedOut, null, "Not signed in. Click \"Sign in\" to log in with your Claude account.");
        }

        return (SignInState.Unknown, null, "Could not read the sign-in state: " + CliText.Truncate(CliText.LastMeaningfulLines(all), 200));
    }

    private static string? DescribeAccount(JsonObject json)
    {
        var email = CliText.Str(json, "email");
        var plan = CliText.Str(json, "subscriptionType") switch
        {
            null or "" => null,
            var p => "Claude " + char.ToUpperInvariant(p[0]) + p[1..],
        };
        var method = CliText.Str(json, "authMethod");
        if (plan is null && method is not null && !method.Equals("claude.ai", StringComparison.OrdinalIgnoreCase))
        {
            plan = method;
        }

        var parts = new[] { email, plan }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>
/// Maps Claude Code's stream-json output (system/init, stream_event text deltas, assistant and user messages,
/// result) to <see cref="AgentEvent"/>s.
/// </summary>
internal sealed class ClaudeStreamParser : CliStreamParser
{
    /// <summary>
    /// Claude Code's placeholder reply (model "&lt;synthetic&gt;"). On --resume of a session whose last turn was cut off
    /// (the user pressed Stop), Claude Code inserts it after the dangling user message and writes it to the output before
    /// it reads the new prompt from stdin. It is not an answer to anything the user asked now.
    /// </summary>
    internal const string NoResponseRequested = "No response requested.";

    private readonly HashSet<string> _startedCalls = new(StringComparer.Ordinal);
    private bool _realOutput;
    private string? _sessionId;
    private string? _model;
    private string? _apiError;
    private string? _apiErrorText;
    private bool _missingSession;

    /// <summary>Creates the parser.</summary>
    public ClaudeStreamParser(string serverName)
        : base(ClaudeCodeRunner.Profile, serverName)
    {
    }

    /// <inheritdoc />
    public override bool IsMissingSession => _missingSession;

    /// <inheritdoc />
    public override IEnumerable<AgentEvent> ParseLine(string line)
    {
        if (!CliText.TryParseObject(line, out var json))
        {
            return [];
        }

        var events = new List<AgentEvent>();
        switch (CliText.Str(json, "type"))
        {
            case "system":
                OnSystem(json, events);
                break;
            case "stream_event":
                OnStreamEvent(json, events);
                break;
            case "assistant":
                OnAssistant(json, events);
                break;
            case "user":
                OnUser(json, events);
                break;
            case "result":
                OnResult(json, events);
                break;
        }

        return events;
    }

    /// <inheritdoc />
    public override void ObserveStdErr(string line)
    {
        base.ObserveStdErr(line);
        if (line.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
        {
            _missingSession = true;
        }
    }

    private static bool IsSubagent(JsonObject json) => json["parent_tool_use_id"] is JsonValue;

    private void ReportSession(string? sessionId, List<AgentEvent> events)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionId != _sessionId)
        {
            _sessionId = sessionId;
            events.Add(new SessionStarted(sessionId));
        }
    }

    private void OnSystem(JsonObject json, List<AgentEvent> events)
    {
        if (CliText.Str(json, "subtype") != "init")
        {
            return;
        }

        _model = CliText.Str(json, "model") ?? _model;
        ReportSession(CliText.Str(json, "session_id"), events);
        if (json["mcp_servers"] is JsonArray servers)
        {
            foreach (var server in servers.OfType<JsonObject>())
            {
                if (CliText.Str(server, "name") == ServerName && CliText.Str(server, "status") is { } status && status != "connected")
                {
                    CopilotLog.Warn($"Claude Code reports the '{ServerName}' MCP server as '{status}'.");
                }
            }
        }
    }

    private void OnStreamEvent(JsonObject json, List<AgentEvent> events)
    {
        if (IsSubagent(json) || json["event"] is not JsonObject streamEvent)
        {
            return;
        }

        // Any streamed model output (message_start, a thinking block...) means the model is answering the prompt.
        _realOutput = true;
        if (CliText.Str(streamEvent, "type") != "content_block_delta")
        {
            return;
        }

        if (streamEvent["delta"] is JsonObject delta && CliText.Str(delta, "type") == "text_delta" && CliText.Str(delta, "text") is { Length: > 0 } text)
        {
            events.Add(new TextDelta(text));
        }
    }

    /// <summary>True for Claude Code's synthetic "No response requested." placeholder (not an API error message).</summary>
    internal static bool IsSyntheticPlaceholder(JsonObject json, JsonObject message, JsonArray? content) =>
        string.Equals(CliText.Str(message, "model"), "<synthetic>", StringComparison.Ordinal)
        && CliText.Str(json, "error") is null
        && CliText.Bool(json, "is_api_error_message") != true
        && string.Equals(CliText.ContentText(content)?.Trim(), NoResponseRequested, StringComparison.Ordinal);

    private void OnAssistant(JsonObject json, List<AgentEvent> events)
    {
        if (IsSubagent(json) || json["message"] is not JsonObject message)
        {
            return;
        }

        ReportSession(CliText.Str(json, "session_id"), events);
        _model = CliText.Str(message, "model") is { } model && !model.StartsWith('<') ? model : _model;
        var content = message["content"] as JsonArray;
        if (CliText.Str(json, "error") is not null || CliText.Bool(json, "is_api_error_message") == true)
        {
            // A synthetic message carrying an API/auth error ("Not logged in · Please run /login"): it belongs in the
            // failure banner, not in the chat.
            _apiError = CliText.Str(json, "error") ?? "api_error";
            _apiErrorText = CliText.ContentText(content);
            return;
        }

        if (content is null)
        {
            return;
        }

        if (IsSyntheticPlaceholder(json, message, content))
        {
            // Resume of a stopped turn: never shown as the reply, and not the end of this turn.
            return;
        }

        _realOutput = true;
        var text = new StringBuilder();
        var calls = new List<AgentEvent>();
        foreach (var block in content.OfType<JsonObject>())
        {
            switch (CliText.Str(block, "type"))
            {
                case "text":
                    if (CliText.Str(block, "text") is { } part)
                    {
                        if (text.Length > 0)
                        {
                            text.Append("\n\n");
                        }

                        text.Append(part);
                    }

                    break;
                case "tool_use":
                    var id = CliText.Str(block, "id");
                    var rawName = CliText.Str(block, "name");
                    if (id is null || rawName is null || !_startedCalls.Add(id))
                    {
                        break;
                    }

                    var (server, name) = ToolNameHelper.Split(rawName, ServerName);
                    calls.Add(new ToolCallStarted(id, name, CliText.SummarizeArguments(block["input"]), server));
                    break;
            }
        }

        if (text.Length > 0)
        {
            events.Add(new TextDone(text.ToString()));
        }

        events.AddRange(calls);
    }

    private void OnUser(JsonObject json, List<AgentEvent> events)
    {
        if (IsSubagent(json) || json["message"] is not JsonObject message || message["content"] is not JsonArray content)
        {
            return;
        }

        foreach (var block in content.OfType<JsonObject>())
        {
            if (CliText.Str(block, "type") != "tool_result" || CliText.Str(block, "tool_use_id") is not { } id || !_startedCalls.Contains(id))
            {
                continue;
            }

            var text = CliText.ContentText(block["content"]);
            var isError = CliText.Bool(block, "is_error") == true;
            events.Add(CliText.SummarizeResult(id, text, isError, isError ? text : null));
        }
    }

    private void OnResult(JsonObject json, List<AgentEvent> events)
    {
        if (!_realOutput && CliText.Bool(json, "is_error") != true
            && string.Equals(CliText.Str(json, "result")?.Trim(), NoResponseRequested, StringComparison.Ordinal))
        {
            // The result of the synthetic placeholder written on resume of a stopped turn, not of the prompt on stdin:
            // the turn goes on until the result of the real answer (or the process ends).
            return;
        }

        if (CliText.Bool(json, "is_error") != true)
        {
            // A failed --resume echoes the unknown id here; only a successful result confirms the session.
            ReportSession(CliText.Str(json, "session_id"), events);
        }
        if (json["usage"] is JsonObject usage)
        {
            var input = CliText.Long(usage, "input_tokens") + CliText.Long(usage, "cache_creation_input_tokens") + CliText.Long(usage, "cache_read_input_tokens");
            var output = CliText.Long(usage, "output_tokens");
            if (input + output > 0)
            {
                var model = json["modelUsage"] is JsonObject modelUsage && modelUsage.Count > 0 ? modelUsage.First().Key : _model;
                events.Add(new Usage(model, input, output));
            }
        }

        var errors = json["errors"] is JsonArray errorArray
            ? string.Join("; ", errorArray.Select(e => e is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null).OfType<string>())
            : string.Empty;
        if (errors.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
        {
            _missingSession = true;
        }

        if (CliText.Bool(json, "is_error") != true)
        {
            events.Add(new TurnCompleted(CliText.Str(json, "stop_reason") ?? CliText.Str(json, "subtype") ?? "success"));
            return;
        }

        var subtype = CliText.Str(json, "subtype");
        var detail = new[] { CliText.Str(json, "result"), errors, _apiErrorText }
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? subtype switch
            {
                "error_max_turns" => "Claude stopped after reaching the maximum number of steps for one message.",
                "error_max_budget_usd" => "Claude stopped after reaching the spending limit for one message.",
                _ => "Claude Code reported an error.",
            };
        AgentFailureKind? kind = _apiError switch
        {
            "authentication_failed" or "oauth_org_not_allowed" => AgentFailureKind.NotSignedIn,
            "rate_limit" => AgentFailureKind.RateLimited,
            _ => null,
        };
        events.Add(CliText.Failure(Profile, detail, kind));
    }
}
