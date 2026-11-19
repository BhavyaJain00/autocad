using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Core.Agents;

/// <summary>
/// Drives OpenAI Codex CLI ('codex', signed in with the user's ChatGPT account) for the panel:
/// <c>codex exec [resume &lt;thread&gt;] --json --skip-git-repo-check --ignore-user-config -c ... -</c> with the
/// prompt on stdin. Everything is configured per invocation with <c>-c key=value</c> overrides: the AutoCAD bridge as
/// the only MCP server (auto-approved, long tool timeout), no shell tool, a read-only sandbox, approvals never asked,
/// no ChatGPT connectors, and the AutoCAD copilot instructions as developer instructions. The user's
/// ~/.codex/config.toml is only read (for the default model) and never changed.
/// </summary>
public sealed partial class CodexRunner : IAgentRunner
{
    /// <summary>CLI facts.</summary>
    internal static readonly CliProfile Profile = new("ChatGPT", "Codex CLI", "codex", "@openai/codex", "ChatGPT");

    private static readonly IReadOnlyDictionary<string, string?> CliEnvironment = new Dictionary<string, string?> { ["NO_COLOR"] = "1" };
    private readonly CliRunnerOptions _options;
    private readonly Lazy<IReadOnlyList<ModelChoice>> _models;

    /// <summary>Creates the runner.</summary>
    public CodexRunner()
        : this(new CliRunnerOptions())
    {
    }

    /// <summary>Creates the runner with test seams.</summary>
    internal CodexRunner(CliRunnerOptions options)
    {
        _options = options;
        _models = new Lazy<IReadOnlyList<ModelChoice>>(() => LoadModels(CodexHome(options), ReadUserDefaults(CodexHome(options))));
    }

    /// <inheritdoc />
    public ProviderId ProviderId => ProviderId.Codex;

    /// <inheritdoc />
    public string DisplayName => "ChatGPT";

    /// <inheritdoc />
    /// <remarks>Read from Codex's own model list cache (~/.codex/models_cache.json) when available.</remarks>
    public IReadOnlyList<ModelChoice> Models => _models.Value;

    /// <inheritdoc />
    public async Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var resolved = _options.Resolve(Profile.Command);
        if (resolved is null)
        {
            return new ProviderStatus { Provider = ProviderId.Codex, Installed = false, Detail = Profile.NotInstalledMessage };
        }

        var versionResult = await CliCommands.RunAsync(resolved, ["--version"], _options, CliEnvironment, cancellationToken).ConfigureAwait(false);
        var loginResult = await CliCommands.RunAsync(resolved, ["login", "status"], _options, CliEnvironment, cancellationToken).ConfigureAwait(false);
        var (state, account, detail) = ParseLoginStatus(loginResult);
        return new ProviderStatus
        {
            Provider = ProviderId.Codex,
            Installed = true,
            Version = CliCommands.ParseVersion(versionResult?.StandardOutput + " " + versionResult?.StandardError),
            SignIn = state,
            Account = account,
            ExecutablePath = CliCommands.ExecutableOf(resolved),
            Detail = detail,
        };
    }

    /// <inheritdoc />
    public CommandLine GetSignInCommand() => new("codex", ["login"]);

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

        var defaults = ReadUserDefaults(CodexHome(_options));
        await foreach (var agentEvent in CliTurnEngine.RunAsync(
                           Profile,
                           request.ResumeSessionId,
                           resume => BuildInvocation(resolved, request, resume, defaults),
                           () => new CodexStreamParser(request.AcadMcp.ServerName, request.Model ?? defaults.Model),
                           _options,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    /// <summary>Builds the process start of one turn (the prompt goes to stdin).</summary>
    internal static CliInvocation BuildInvocation(ResolvedCommand command, TurnRequest request, string? resumeSessionId, CodexUserDefaults defaults)
    {
        var workDirectory = AppPaths.Ensure(Path.Combine(request.WorkingDirectory, "codex"));
        var attachments = CliText.ClassifyAttachments(request.Attachments);
        var images = attachments.Where(a => a.Kind == AttachmentKind.Image).Select(a => a.Path).ToList();
        var inlined = new List<(string Path, string Content)>();
        var unavailable = new List<Attachment>();
        foreach (var attachment in attachments.Where(a => a.Kind != AttachmentKind.Image))
        {
            var content = attachment.Kind == AttachmentKind.Text ? CliText.ReadInlineText(attachment.Path) : null;
            if (content is null)
            {
                unavailable.Add(attachment);
            }
            else
            {
                inlined.Add((attachment.Path, content));
            }
        }

        var prompt = CliText.ComposePrompt(request.Prompt, [], string.Empty, inlined, unavailable);
        var arguments = BuildArguments(request, resumeSessionId, images, defaults);
        var launch = command.ToLaunch(arguments, workDirectory) with { Environment = CliEnvironment };
        return new CliInvocation(launch, prompt, []);
    }

    /// <summary>Command-line arguments of one turn (all configuration as -c overrides; prompt on stdin via "-").</summary>
    internal static IReadOnlyList<string> BuildArguments(TurnRequest request, string? resumeSessionId, IReadOnlyList<string> images, CodexUserDefaults defaults)
    {
        var server = request.AcadMcp;
        var key = "mcp_servers." + CliText.TomlKey(server.ServerName);
        var resume = !string.IsNullOrWhiteSpace(resumeSessionId);
        var arguments = new List<string> { "exec" };
        if (resume)
        {
            arguments.Add("resume");
        }

        // "--image=<file>" keeps each image to exactly one value (the option is variadic).
        arguments.AddRange(images.Select(image => "--image=" + image));
        arguments.AddRange(
        [
            "--json",
            "--skip-git-repo-check",
            "--ignore-user-config",
            "-c", "approval_policy=\"never\"",
            "-c", "sandbox_mode=\"read-only\"",
            "-c", "features.shell_tool=false",
            "-c", "features.apps=false",
            "-c", "check_for_update_on_startup=false",
            "-c", $"{key}.command={CliText.TomlString(server.Command)}",
            "-c", $"{key}.args={CliText.TomlArray(server.Arguments)}",
        ]);
        if (server.Environment.Count > 0)
        {
            arguments.AddRange(["-c", $"{key}.env={CliText.TomlInlineTable(server.Environment)}"]);
        }

        arguments.AddRange(
        [
            "-c", $"{key}.tool_timeout_sec={server.ToolTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}",
            "-c", $"{key}.startup_timeout_sec=60",
            "-c", $"{key}.default_tools_approval_mode=\"approve\"",
            "-c", $"developer_instructions={CliText.TomlString(request.Instructions)}",
        ]);

        var model = string.IsNullOrWhiteSpace(request.Model) ? defaults.Model : request.Model;
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.AddRange(["-m", model]);
        }

        if (string.IsNullOrWhiteSpace(request.Model) && !string.IsNullOrWhiteSpace(defaults.ReasoningEffort))
        {
            arguments.AddRange(["-c", $"model_reasoning_effort={CliText.TomlString(defaults.ReasoningEffort)}"]);
        }

        if (resume)
        {
            arguments.Add(resumeSessionId!);
        }

        arguments.Add("-");
        return arguments;
    }

    /// <summary>Reads 'codex login status' output ("Logged in using ChatGPT" / "Not logged in", printed on stderr).</summary>
    internal static (SignInState State, string? Account, string? Detail) ParseLoginStatus(ProcessResult? result)
    {
        if (result is null)
        {
            return (SignInState.Unknown, null, "Could not run 'codex login status'.");
        }

        if (result.TimedOut)
        {
            return (SignInState.Unknown, null, "'codex login status' did not answer in time.");
        }

        var text = result.StandardOutput + "\n" + result.StandardError;
        if (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return (SignInState.SignedOut, null, "Not signed in. Click \"Sign in\" to log in with your ChatGPT account.");
        }

        if (text.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return (SignInState.SignedIn, "ChatGPT account", null);
        }

        if (text.Contains("API key", StringComparison.OrdinalIgnoreCase) && text.Contains("Logged in", StringComparison.OrdinalIgnoreCase))
        {
            return (SignInState.SignedIn, "OpenAI API key", null);
        }

        if (text.Contains("Logged in", StringComparison.OrdinalIgnoreCase))
        {
            return (SignInState.SignedIn, null, null);
        }

        return result.ExitCode != 0
            ? (SignInState.SignedOut, null, "Not signed in. Click \"Sign in\" to log in with your ChatGPT account.")
            : (SignInState.Unknown, null, "Could not read the sign-in state: " + CliText.Truncate(CliText.LastMeaningfulLines(text), 200));
    }

    /// <summary>Codex's home folder (CODEX_HOME or ~/.codex).</summary>
    internal static string CodexHome(CliRunnerOptions options)
    {
        var overridden = options.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(overridden) ? Path.Combine(options.Home, ".codex") : overridden;
    }

    /// <summary>The user's default model and reasoning effort from the top level of config.toml (read-only).</summary>
    internal static CodexUserDefaults ReadUserDefaults(string codexHome)
    {
        string? model = null;
        string? effort = null;
        try
        {
            var path = Path.Combine(codexHome, "config.toml");
            if (!File.Exists(path))
            {
                return new CodexUserDefaults(null, null);
            }

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    break;
                }

                var match = TopLevelString().Match(trimmed);
                if (!match.Success)
                {
                    continue;
                }

                var value = match.Groups["dq"].Success ? Regex.Unescape(match.Groups["dq"].Value) : match.Groups["sq"].Value;
                switch (match.Groups["key"].Value)
                {
                    case "model":
                        model = value;
                        break;
                    case "model_reasoning_effort":
                        effort = value;
                        break;
                }
            }
        }
        catch (IOException ex)
        {
            CopilotLog.Warn("Could not read the Codex config", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            CopilotLog.Warn("Could not read the Codex config", ex);
        }
        catch (ArgumentException ex)
        {
            CopilotLog.Warn("Could not read the Codex config", ex);
        }

        return new CodexUserDefaults(string.IsNullOrWhiteSpace(model) ? null : model, string.IsNullOrWhiteSpace(effort) ? null : effort);
    }

    /// <summary>Models listed in Codex's models_cache.json (visible ones, by priority), after "Default".</summary>
    internal static IReadOnlyList<ModelChoice> LoadModels(string codexHome, CodexUserDefaults defaults)
    {
        var defaultLabel = defaults.Model is null ? "Default" : $"Default ({defaults.Model})";
        var result = new List<ModelChoice> { new(null, defaultLabel) };
        try
        {
            var path = Path.Combine(codexHome, "models_cache.json");
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject cache || cache["models"] is not JsonArray models)
            {
                return result;
            }

            var listed = models.OfType<JsonObject>()
                .Where(m => CliText.Str(m, "slug") is { Length: > 0 } && CliText.Str(m, "visibility") is null or "list")
                .OrderBy(m => CliText.Long(m, "priority"))
                .Select(m => new ModelChoice(CliText.Str(m, "slug"), CliText.Str(m, "display_name") ?? CliText.Str(m, "slug")!));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            result.AddRange(listed.Where(m => seen.Add(m.Id!)));
        }
        catch (IOException ex)
        {
            CopilotLog.Warn("Could not read the Codex model list", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            CopilotLog.Warn("Could not read the Codex model list", ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            CopilotLog.Warn("Could not read the Codex model list", ex);
        }

        return result;
    }

    [GeneratedRegex("^(?<key>model|model_reasoning_effort)\\s*=\\s*(?:\"(?<dq>(?:[^\"\\\\]|\\\\.)*)\"|'(?<sq>[^']*)')\\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelString();
}

/// <summary>The user's own Codex defaults (top of ~/.codex/config.toml).</summary>
/// <param name="Model">Configured model, if any.</param>
/// <param name="ReasoningEffort">Configured reasoning effort, if any.</param>
internal sealed record CodexUserDefaults(string? Model, string? ReasoningEffort);

/// <summary>Maps 'codex exec --json' JSONL (thread.started, item.*, turn.completed/failed, error) to <see cref="AgentEvent"/>s.</summary>
internal sealed class CodexStreamParser : CliStreamParser
{
    private readonly string? _model;
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private string? _lastError;
    private int _unauthorizedErrors;
    private bool _threadStarted;
    private bool _missingSession;

    /// <summary>Creates the parser.</summary>
    /// <param name="serverName">AutoCAD MCP server name.</param>
    /// <param name="model">Model passed to the CLI (for usage reporting).</param>
    public CodexStreamParser(string serverName, string? model)
        : base(CodexRunner.Profile, serverName)
    {
        _model = model;
    }

    /// <inheritdoc />
    public override bool IsMissingSession => _missingSession && !_threadStarted;

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
            case "thread.started":
                if (CliText.Str(json, "thread_id") is { Length: > 0 } threadId)
                {
                    _threadStarted = true;
                    events.Add(new SessionStarted(threadId));
                }

                break;
            case "item.started":
            case "item.updated":
                if (json["item"] is JsonObject startedItem)
                {
                    OnItemStarted(startedItem, events);
                }

                break;
            case "item.completed":
                if (json["item"] is JsonObject completedItem)
                {
                    OnItemCompleted(completedItem, events);
                }

                break;
            case "turn.completed":
                if (json["usage"] is JsonObject usage)
                {
                    var input = CliText.Long(usage, "input_tokens");
                    var output = CliText.Long(usage, "output_tokens");
                    if (input + output > 0)
                    {
                        events.Add(new Usage(_model, input, output));
                    }
                }

                events.Add(new TurnCompleted("completed"));
                break;
            case "turn.failed":
                var failure = CliText.Str(json["error"], "message") ?? _lastError ?? "The turn failed.";
                events.Add(CliText.Failure(Profile, failure));
                break;
            case "error":
                OnError(CliText.Str(json, "message"), events);
                break;
        }

        return events;
    }

    /// <inheritdoc />
    public override void ObserveStdErr(string line)
    {
        base.ObserveStdErr(line);
        if (line.Contains("no rollout found", StringComparison.OrdinalIgnoreCase) || line.Contains("thread/resume failed", StringComparison.OrdinalIgnoreCase))
        {
            _missingSession = true;
        }
    }

    /// <inheritdoc />
    public override AgentEvent CreateFallbackTerminal(int exitCode)
    {
        var errorLines = StandardErrorTail
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var detail = _lastError ?? (errorLines.Count > 0 ? errorLines[^1] : null);
        if (detail is null)
        {
            return base.CreateFallbackTerminal(exitCode);
        }

        return CliText.Failure(Profile, detail);
    }

    private void OnError(string? message, List<AgentEvent> events)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _lastError = message;
        if (CliText.IsHardLimit(message))
        {
            // The plan's usage limit: Codex would only retry until it gives up; end the turn now (the panel may switch AI).
            events.Add(CliText.Failure(Profile, message, AgentFailureKind.RateLimited));
            return;
        }

        if (CliText.Classify(message) != AgentFailureKind.NotSignedIn)
        {
            return;
        }

        // Codex retries a 401 for half a minute; a missing token or repeated 401s mean "not signed in".
        _unauthorizedErrors++;
        if (message.Contains("missing bearer", StringComparison.OrdinalIgnoreCase) || _unauthorizedErrors >= 3)
        {
            events.Add(CliText.Failure(Profile, message, AgentFailureKind.NotSignedIn));
        }
    }

    private void OnItemStarted(JsonObject item, List<AgentEvent> events)
    {
        if (CliText.Str(item, "id") is not { } id || _started.Contains(id))
        {
            return;
        }

        var started = CreateStarted(id, item);
        if (started is not null)
        {
            _started.Add(id);
            events.Add(started);
        }
    }

    private void OnItemCompleted(JsonObject item, List<AgentEvent> events)
    {
        var id = CliText.Str(item, "id") ?? string.Empty;
        var type = CliText.Str(item, "type");
        switch (type)
        {
            case "agent_message":
                if (CliText.Str(item, "text") is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    events.Add(new TextDone(text));
                }

                return;
            case "reasoning" or "todo_list" or null:
                return;
            case "error":
                CopilotLog.Info("Codex: " + CliText.Str(item, "message"));
                return;
        }

        if (!_started.Contains(id))
        {
            OnItemStarted(item, events);
            if (!_started.Contains(id))
            {
                return;
            }
        }

        var callId = CallId(id);
        var status = CliText.Str(item, "status");
        switch (type)
        {
            case "mcp_tool_call":
                var errorText = CliText.Str(item["error"], "message");
                var resultText = item["result"] is JsonObject result ? CliText.ContentText(result["content"]) : null;
                events.Add(CliText.SummarizeResult(callId, resultText ?? errorText, status == "failed" || errorText is not null, errorText));
                break;
            case "command_execution":
                var exitCode = item["exit_code"] is JsonValue ? CliText.Long(item, "exit_code") : -1;
                var ok = status == "completed" && exitCode == 0;
                var output = CliText.Str(item, "aggregated_output");
                events.Add(ok
                    ? new ToolCallFinished(callId, true, CliText.Truncate(CliText.FirstLine(output) ?? "Done", 160))
                    : new ToolCallFinished(callId, false, status == "declined" ? "Declined" : "Failed", CliText.Truncate(output ?? status, 600)));
                break;
            default:
                var succeeded = status is null or "completed";
                events.Add(new ToolCallFinished(callId, succeeded, succeeded ? "Done" : "Failed", succeeded ? null : status));
                break;
        }
    }

    private ToolCallStarted? CreateStarted(string id, JsonObject item)
    {
        var callId = CallId(id);
        switch (CliText.Str(item, "type"))
        {
            case "mcp_tool_call":
                var server = CliText.Str(item, "server");
                var tool = CliText.Str(item, "tool") ?? "tool";
                var (prefixServer, name) = ToolNameHelper.Split(tool, ServerName);
                return new ToolCallStarted(callId, name, CliText.SummarizeArguments(item["arguments"]), server ?? prefixServer);
            case "command_execution":
                return new ToolCallStarted(callId, "shell", CliText.Truncate(CliText.Str(item, "command"), 100));
            case "file_change":
                var paths = item["changes"] is JsonArray changes
                    ? string.Join(", ", changes.OfType<JsonObject>().Select(c => Path.GetFileName(CliText.Str(c, "path") ?? string.Empty)))
                    : string.Empty;
                return new ToolCallStarted(callId, "file_change", CliText.Truncate(paths, 100));
            case "web_search":
                return new ToolCallStarted(callId, "web_search", CliText.Truncate(CliText.Str(item, "query"), 100));
            case "collab_tool_call":
                return new ToolCallStarted(callId, CliText.Str(item, "tool") ?? "agent", string.Empty);
            default:
                return null;
        }
    }
}
