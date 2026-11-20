using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Agents;

/// <summary>
/// Drives Google Gemini CLI ('gemini', signed in with the user's Google account) for the panel:
/// <c>gemini -o stream-json -e none --allowed-mcp-server-names acad --approval-mode default [--resume &lt;id&gt;]</c>
/// with the prompt on stdin, run in a private workspace folder whose project settings
/// (<c>.gemini/settings.json</c>) declare the AutoCAD bridge as a trusted MCP server, whose policy file (passed with
/// <c>--policy</c>) denies the shell, file-writing and web tools, and whose <c>GEMINI.md</c> carries the AutoCAD copilot
/// instructions. The user's ~/.gemini files are only read (to show the sign-in state) and never changed.
/// </summary>
public sealed class GeminiRunner : IAgentRunner
{
    /// <summary>CLI facts.</summary>
    internal static readonly CliProfile Profile = new(
        "Gemini",
        "Gemini CLI",
        "gemini",
        "@google/gemini-cli",
        "Google",
        "Click \"Sign in\"; in the Gemini window type /auth, choose \"Sign in with Google\" and finish in your browser");

    /// <summary>Built-in Gemini CLI tools the panel never needs (shell, file edits, web, memory, sub-agents).</summary>
    internal static readonly string[] ExcludedTools =
    [
        "run_shell_command", "write_file", "replace", "web_fetch", "google_web_search", "save_memory", "write_todos",
        "invoke_agent", "activate_skill", "enter_plan_mode", "exit_plan_mode",
    ];

    /// <summary>Gemini CLI's read-only web tools, allowed only while Web research is on.</summary>
    internal static readonly string[] WebTools = ["google_web_search", "web_fetch"];

    /// <summary>The built-in tools denied for a turn: all of <see cref="ExcludedTools"/>, without the web tools while Web research is on.</summary>
    internal static IReadOnlyList<string> DeniedTools(bool webResearch) =>
        webResearch ? [.. ExcludedTools.Except(WebTools, StringComparer.Ordinal)] : ExcludedTools;

    private static readonly TimeSpan WorkspaceRetention = TimeSpan.FromDays(14);
    private readonly CliRunnerOptions _options;

    /// <summary>Creates the runner.</summary>
    public GeminiRunner()
        : this(new CliRunnerOptions())
    {
    }

    /// <summary>Creates the runner with test seams.</summary>
    internal GeminiRunner(CliRunnerOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public ProviderId ProviderId => ProviderId.Gemini;

    /// <inheritdoc />
    public string DisplayName => "Gemini";

    /// <inheritdoc />
    public IReadOnlyList<ModelChoice> Models { get; } =
    [
        new(null, "Default"),
        new("auto", "Auto"),
        new("pro", "Pro"),
        new("flash", "Flash"),
        new("flash-lite", "Flash-Lite"),
    ];

    /// <inheritdoc />
    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var resolved = _options.Resolve(Profile.Command);
        if (resolved is null)
        {
            return new ProviderStatus { Provider = ProviderId.Gemini, Installed = false, Detail = Profile.NotInstalledMessage };
        }

        var versionResult = await CliCommands.RunAsync(resolved, ["--version"], _options, CliEnvironment(), cancellationToken).ConfigureAwait(false);
        var (state, account, detail) = ProbeSignIn(_options);
        return new ProviderStatus
        {
            Provider = ProviderId.Gemini,
            Installed = true,
            Version = CliCommands.ParseVersion(versionResult?.StandardOutput),
            SignIn = state,
            Account = account,
            ExecutablePath = CliCommands.ExecutableOf(resolved),
            Detail = detail,
        };
    }

    /// <inheritdoc />
    /// <remarks>Interactive Gemini CLI shows its sign-in dialog when needed; /auth switches to "Sign in with Google".</remarks>
    public CommandLine GetSignInCommand() => new("gemini", []);

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
                           () => new GeminiStreamParser(request.AcadMcp.ServerName),
                           _options,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    /// <summary>
    /// Prepares the private workspace (settings + GEMINI.md) and builds the process start. The folder is keyed by the
    /// bridge launch (AutoCAD process), so two AutoCAD sessions never share one and Gemini's per-folder sessions resume.
    /// </summary>
    internal static CliInvocation BuildInvocation(ResolvedCommand command, TurnRequest request, string? resumeSessionId)
    {
        var folderName = "gemini-" + LaunchKey(request.AcadMcp);
        RemoveStaleWorkspaces(request.WorkingDirectory, folderName);
        var workDirectory = AppPaths.Ensure(Path.Combine(request.WorkingDirectory, folderName));
        var settingsDirectory = AppPaths.Ensure(Path.Combine(workDirectory, ".gemini"));
        var policyPath = Path.Combine(settingsDirectory, "copilot-policy.toml");
        // --policy takes a comma-separated list; a path with a comma falls back to the (deprecated) tools.exclude.
        var usePolicy = !policyPath.Contains(',', StringComparison.Ordinal);
        if (usePolicy)
        {
            WriteAtomically(policyPath, BuildPolicy(request.WebResearch));
        }

        WriteAtomically(Path.Combine(settingsDirectory, "settings.json"), BuildSettings(request.AcadMcp, excludeToolsInSettings: !usePolicy, request.WebResearch).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        WriteAtomically(Path.Combine(workDirectory, "GEMINI.md"), request.Instructions);

        var attachments = CliText.ClassifyAttachments(request.Attachments);
        var readable = new List<Attachment>();
        var inlined = new List<(string Path, string Content)>();
        var unavailable = new List<Attachment>();
        foreach (var attachment in attachments)
        {
            var folder = Path.GetDirectoryName(attachment.Path) ?? string.Empty;
            if (attachment.Kind is AttachmentKind.Image or AttachmentKind.Pdf or AttachmentKind.Text && !folder.Contains(',', StringComparison.Ordinal))
            {
                readable.Add(attachment);
            }
            else if (attachment.Kind == AttachmentKind.Text && CliText.ReadInlineText(attachment.Path) is { } content)
            {
                inlined.Add((attachment.Path, content));
            }
            else
            {
                unavailable.Add(attachment);
            }
        }

        var prompt = CliText.ComposePrompt(request.Prompt, readable, "use the read_file tool to open them", inlined, unavailable);
        var arguments = BuildArguments(request, resumeSessionId, CliText.AttachmentFolders(readable), usePolicy ? policyPath : null);
        var launch = command.ToLaunch(arguments, workDirectory) with { Environment = CliEnvironment() };
        return new CliInvocation(launch, prompt, []);
    }

    /// <summary>Command-line arguments of one turn (the prompt goes to stdin).</summary>
    /// <param name="request">The turn.</param>
    /// <param name="resumeSessionId">Session to resume, or null.</param>
    /// <param name="attachmentFolders">Folders of attached files (added as workspace directories).</param>
    /// <param name="policyPath">Policy file that denies the unneeded built-in tools, or null.</param>
    internal static IReadOnlyList<string> BuildArguments(TurnRequest request, string? resumeSessionId, IReadOnlyList<string> attachmentFolders, string? policyPath = null)
    {
        var arguments = new List<string>
        {
            "-o", "stream-json",
            // No extensions, and no MCP servers except the AutoCAD bridge.
            "-e", "none",
            "--allowed-mcp-server-names", request.AcadMcp.ServerName,
            // Anything that would need a confirmation (other than the trusted AutoCAD tools) is refused headless.
            "--approval-mode", "default",
        };
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.AddRange(["-m", request.Model]);
        }

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            arguments.AddRange(["--resume", resumeSessionId]);
        }

        if (policyPath is not null)
        {
            // Used instead of the user's ~/.gemini/policies for this run only.
            arguments.AddRange(["--policy", policyPath]);
        }

        foreach (var folder in attachmentFolders)
        {
            arguments.AddRange(["--include-directories", folder]);
        }

        return arguments;
    }

    /// <summary>
    /// Policy (Gemini CLI policy engine, TOML) that denies the built-in tools the panel never needs; denied tools are
    /// not offered to the model at all. While Web research is on, google_web_search and web_fetch are allowed instead
    /// (headless runs refuse anything that would ask); shell and file-writing tools stay denied.
    /// </summary>
    internal static string BuildPolicy(bool webResearch = false)
    {
        var policy =
            "# AI Copilot for AutoCAD 2027: built-in Gemini CLI tools that the AutoCAD panel never uses.\n" +
            "[[rule]]\n" +
            "toolName = " + CliText.TomlArray(DeniedTools(webResearch)) + "\n" +
            "decision = \"deny\"\n" +
            "priority = 900\n";
        if (webResearch)
        {
            policy +=
                "\n# Web research is on: the read-only web tools may run without asking.\n" +
                "[[rule]]\n" +
                "toolName = " + CliText.TomlArray(WebTools) + "\n" +
                "decision = \"allow\"\n" +
                "priority = 900\n";
        }

        return policy;
    }

    /// <summary>Project-level settings of the private workspace: the trusted AutoCAD bridge (and, without a policy file, the excluded tools).</summary>
    internal static JsonObject BuildSettings(McpServerLaunch server, bool excludeToolsInSettings = false, bool webResearch = false)
    {
        var entry = new JsonObject
        {
            ["command"] = server.Command,
            ["args"] = new JsonArray([.. server.Arguments.Select(a => (JsonNode?)JsonValue.Create(a))]),
            ["timeout"] = (long)server.ToolTimeoutSeconds * 1000,
            ["trust"] = true,
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

        var settings = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [server.ServerName] = entry },
            ["context"] = new JsonObject { ["fileName"] = "GEMINI.md" },
        };
        if (excludeToolsInSettings)
        {
            settings["tools"] = new JsonObject { ["exclude"] = new JsonArray([.. DeniedTools(webResearch).Select(t => (JsonNode?)JsonValue.Create(t))]) };
        }

        return settings;
    }

    /// <summary>Environment of every Gemini CLI process.</summary>
    internal static IReadOnlyDictionary<string, string?> CliEnvironment() => new Dictionary<string, string?>
    {
        // One node process instead of a relaunched child (less memory), and trust the private workspace folder so
        // its .gemini/settings.json is used in headless mode.
        ["GEMINI_CLI_NO_RELAUNCH"] = "true",
        ["GEMINI_CLI_TRUST_WORKSPACE"] = "true",
        ["NO_COLOR"] = "1",
    };

    /// <summary>
    /// Sign-in state from Gemini CLI's own files (read-only): the auth type selected in ~/.gemini/settings.json and
    /// the cached Google login (oauth_creds.json, google_accounts.json) or API-key environment variables.
    /// </summary>
    internal static (SignInState State, string? Account, string? Detail) ProbeSignIn(CliRunnerOptions options)
    {
        var environment = options.GetEnvironmentVariable;
        var root = environment("GEMINI_CLI_HOME") is { Length: > 0 } overridden ? overridden : options.Home;
        var directory = Path.Combine(root, ".gemini");
        var selected = ReadSelectedAuthType(Path.Combine(directory, "settings.json"));
        if (selected is null)
        {
            if (IsTrue(environment("GOOGLE_GENAI_USE_GCA")))
            {
                selected = "oauth-personal";
            }
            else if (IsTrue(environment("GOOGLE_GENAI_USE_VERTEXAI")))
            {
                selected = "vertex-ai";
            }
            else if (!string.IsNullOrWhiteSpace(environment("GEMINI_API_KEY")))
            {
                selected = "gemini-api-key";
            }
        }

        switch (selected)
        {
            case "oauth-personal":
                var account = ReadActiveAccount(Path.Combine(directory, "google_accounts.json")) ?? "Google account";
                if (File.Exists(Path.Combine(directory, "oauth_creds.json")))
                {
                    return (SignInState.SignedIn, account, null);
                }

                if (IsTrue(environment("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE")))
                {
                    return File.Exists(Path.Combine(directory, "gemini-credentials.json"))
                        ? (SignInState.SignedIn, account, null)
                        : (SignInState.Unknown, account, "The Google login is kept in the system keychain; it is checked when you send a message.");
                }

                return (SignInState.SignedOut, null, "Gemini CLI has no saved Google login. " + Profile.SignInSteps + ".");
            case "gemini-api-key":
                if (!string.IsNullOrWhiteSpace(environment("GEMINI_API_KEY")) || EnvFileDefines(root, "GEMINI_API_KEY"))
                {
                    return (SignInState.SignedIn, "Gemini API key", null);
                }

                return (SignInState.Unknown, null,
                    "Gemini CLI is set to use a Gemini API key, not your Google account. To use your Google account: " + Profile.SignInSteps + ".");
            case "vertex-ai":
                var vertexReady = !string.IsNullOrWhiteSpace(environment("GOOGLE_API_KEY")) ||
                                  (!string.IsNullOrWhiteSpace(environment("GOOGLE_CLOUD_PROJECT")) && !string.IsNullOrWhiteSpace(environment("GOOGLE_CLOUD_LOCATION")));
                return vertexReady ? (SignInState.SignedIn, "Vertex AI", null) : (SignInState.Unknown, "Vertex AI", null);
            case null:
                return (SignInState.SignedOut, null, "Gemini CLI has not been signed in yet. " + Profile.SignInSteps + ".");
            default:
                return (SignInState.Unknown, selected, null);
        }
    }

    /// <summary>Short stable key of a bridge launch (command, arguments, environment).</summary>
    internal static string LaunchKey(McpServerLaunch server)
    {
        var text = new StringBuilder(server.Command);
        foreach (var argument in server.Arguments)
        {
            text.Append('\0').Append(argument);
        }

        foreach (var (name, value) in server.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append('\n').Append(name).Append('=').Append(value);
        }

        text.Append('\n').Append(server.ServerName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..12].ToLowerInvariant();
    }

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string? ReadSelectedAuthType(string settingsPath)
    {
        var json = TryReadJson(settingsPath);
        return CliText.Str(json?["security"]?["auth"], "selectedType") ?? CliText.Str(json, "selectedAuthType");
    }

    private static string? ReadActiveAccount(string accountsPath) => CliText.Str(TryReadJson(accountsPath), "active");

    private static JsonObject? TryReadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            return JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool EnvFileDefines(string root, string name)
    {
        foreach (var path in new[] { Path.Combine(root, ".gemini", ".env"), Path.Combine(root, ".env") })
        {
            try
            {
                if (File.Exists(path) && File.ReadLines(path).Any(line => IsDefinition(line, name)))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Unreadable: treat as not defined.
            }
            catch (UnauthorizedAccessException)
            {
                // Unreadable: treat as not defined.
            }
        }

        return false;

        static bool IsDefinition(string line, string name)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmed = trimmed[7..].TrimStart();
            }

            if (!trimmed.StartsWith(name, StringComparison.Ordinal))
            {
                return false;
            }

            var rest = trimmed[name.Length..].TrimStart();
            return rest.StartsWith('=') && rest[1..].Trim().Trim('"', '\'').Length > 0;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, content, ProcessRunner.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    private static void RemoveStaleWorkspaces(string workspace, string current)
    {
        try
        {
            if (!Directory.Exists(workspace))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(workspace, "gemini-*"))
            {
                if (string.Equals(Path.GetFileName(directory), current, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > WorkspaceRetention)
                {
                    CliTurnEngine.DeleteTempPaths([directory]);
                }
            }
        }
        catch (IOException ex)
        {
            CopilotLog.Warn("Could not clean old Gemini workspaces", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            CopilotLog.Warn("Could not clean old Gemini workspaces", ex);
        }
    }
}

/// <summary>
/// Maps Gemini CLI's stream-json output (init, message deltas, tool_use, tool_result, error, result) to
/// <see cref="AgentEvent"/>s. Gemini only streams deltas, so the text of each assistant segment is completed with a
/// <see cref="TextDone"/> when a tool call starts or the turn ends.
/// </summary>
internal sealed class GeminiStreamParser : CliStreamParser
{
    private readonly StringBuilder _pendingText = new();
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private string? _model;
    private string? _lastError;
    private bool _initialized;

    /// <summary>Creates the parser.</summary>
    public GeminiStreamParser(string serverName)
        : base(GeminiRunner.Profile, serverName)
    {
    }

    /// <inheritdoc />
    public override bool IsMissingSession =>
        !_initialized && StandardErrorTail.Contains("Error resuming session", StringComparison.OrdinalIgnoreCase);

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
            case "init":
                _initialized = true;
                _model = CliText.Str(json, "model") ?? _model;
                if (CliText.Str(json, "session_id") is { Length: > 0 } sessionId)
                {
                    events.Add(new SessionStarted(sessionId));
                }

                break;
            case "message":
                if (CliText.Str(json, "role") == "assistant" && CliText.Str(json, "content") is { Length: > 0 } text)
                {
                    _pendingText.Append(text);
                    events.Add(new TextDelta(text));
                }

                break;
            case "tool_use":
                FlushText(events);
                var rawId = CliText.Str(json, "tool_id") ?? Guid.NewGuid().ToString("N");
                var callId = CallId(rawId);
                if (_started.Add(callId))
                {
                    var (server, name) = ToolNameHelper.Split(CliText.Str(json, "tool_name") ?? "tool", ServerName);
                    events.Add(new ToolCallStarted(callId, name, CliText.SummarizeArguments(json["parameters"]), server));
                }

                break;
            case "tool_result":
                var resultId = CallId(CliText.Str(json, "tool_id") ?? string.Empty);
                if (_started.Contains(resultId))
                {
                    var isError = CliText.Str(json, "status") is { } status && status != "success";
                    var errorText = CliText.Str(json["error"], "message");
                    events.Add(CliText.SummarizeResult(resultId, CliText.Str(json, "output") ?? errorText, isError, errorText));
                }

                break;
            case "error":
                var message = CliText.Str(json, "message");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _lastError = message;
                    var severity = CliText.Str(json, "severity") ?? "error";
                    CopilotLog.Info($"Gemini CLI {severity}: {message}");
                    if (severity == "error" && CliText.IsHardLimit(message))
                    {
                        // The account's quota is used up (not a short 429 burst it retries): end the turn now.
                        events.Add(CliText.Failure(Profile, message, AgentFailureKind.RateLimited));
                    }
                }

                break;
            case "result":
                FlushText(events);
                OnResult(json, events);
                break;
        }

        return events;
    }

    /// <inheritdoc />
    public override IEnumerable<AgentEvent> Flush()
    {
        var events = new List<AgentEvent>();
        FlushText(events);
        return events;
    }

    /// <inheritdoc />
    public override AgentEvent CreateFallbackTerminal(int exitCode)
    {
        var detail = CliText.LastMeaningfulLines(StandardErrorTail);
        if (detail.Length == 0)
        {
            detail = _lastError ?? string.Empty;
        }

        if (detail.Contains("Please set an Auth method", StringComparison.OrdinalIgnoreCase))
        {
            detail = "no login has been set up yet";
        }

        return exitCode switch
        {
            0 when detail.Length == 0 => new TurnCompleted("exit"),
            41 => CliText.Failure(Profile, detail.Length > 0 ? detail : "authentication failed", AgentFailureKind.NotSignedIn),
            130 => CliText.Failure(Profile, "Gemini CLI cancelled the request."),
            _ => CliText.Failure(Profile, detail.Length > 0 ? detail : $"Gemini CLI exited with code {exitCode}."),
        };
    }

    private void FlushText(List<AgentEvent> events)
    {
        if (_pendingText.Length > 0)
        {
            events.Add(new TextDone(_pendingText.ToString()));
            _pendingText.Clear();
        }
    }

    private void OnResult(JsonObject json, List<AgentEvent> events)
    {
        if (json["stats"] is JsonObject stats)
        {
            var input = CliText.Long(stats, "input_tokens");
            var output = CliText.Long(stats, "output_tokens");
            if (input + output > 0)
            {
                var model = stats["models"] is JsonObject models && models.Count > 0
                    ? models.OrderByDescending(m => CliText.Long(m.Value, "input_tokens") + CliText.Long(m.Value, "output_tokens")).First().Key
                    : _model;
                events.Add(new Usage(model, input, output));
            }
        }

        if (CliText.Str(json, "status") == "success")
        {
            events.Add(new TurnCompleted("success"));
            return;
        }

        var error = json["error"];
        var type = CliText.Str(error, "type");
        var message = CliText.Str(error, "message") ?? _lastError ?? "Gemini CLI reported an error.";
        AgentFailureKind? kind = type switch
        {
            "FatalAuthenticationError" => AgentFailureKind.NotSignedIn,
            "FatalCancellationError" => AgentFailureKind.Other,
            _ => null,
        };
        events.Add(CliText.Failure(Profile, message, kind));
    }
}
