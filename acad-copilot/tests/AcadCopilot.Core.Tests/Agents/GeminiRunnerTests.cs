using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Tests.Fakes;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

public sealed class GeminiStreamParserTests
{
    /// <summary>
    /// Built from the event shapes Gemini CLI 0.60.0 writes in stream-json mode (init, message with delta, tool_use,
    /// tool_result, result with stats); MCP tools are named mcp_&lt;server&gt;_&lt;tool&gt;.
    /// </summary>
    private const string ToolTurn = """
        {"type":"init","timestamp":"2026-11-14T06:20:00.000Z","session_id":"5b1f2c3d-0000-4000-8000-000000000001","model":"auto"}
        {"type":"message","timestamp":"2026-11-14T06:20:00.010Z","role":"user","content":"Create walls"}
        {"type":"message","timestamp":"2026-11-14T06:20:01.000Z","role":"assistant","content":"I'll check the model ","delta":true}
        {"type":"message","timestamp":"2026-11-14T06:20:01.100Z","role":"assistant","content":"first.","delta":true}
        {"type":"tool_use","timestamp":"2026-11-14T06:20:01.200Z","tool_name":"mcp_acad_get_model_info","tool_id":"mcp_acad_get_model_info-1763101201200-a1","parameters":{}}
        {"type":"tool_result","timestamp":"2026-11-14T06:20:01.900Z","tool_id":"mcp_acad_get_model_info-1763101201200-a1","status":"success","output":"{\"success\":true,\"message\":\"Model 'Project1': 2 levels, feet\"}"}
        {"type":"tool_use","timestamp":"2026-11-14T06:20:02.000Z","tool_name":"mcp_acad_create_wall","tool_id":"mcp_acad_create_wall-1763101202000-b2","parameters":{"level":"Level 9","points":[[0,0],[20,0]],"height":10}}
        {"type":"tool_result","timestamp":"2026-11-14T06:20:02.500Z","tool_id":"mcp_acad_create_wall-1763101202000-b2","status":"error","output":"{\"success\":false,\"message\":\"Level 'Level 9' not found\",\"errors\":[\"Level 'Level 9' not found\"]}","error":{"type":"TOOL_EXECUTION_ERROR","message":"Level 'Level 9' not found"}}
        {"type":"message","timestamp":"2026-11-14T06:20:03.000Z","role":"assistant","content":"Done.","delta":true}
        {"type":"result","timestamp":"2026-11-14T06:20:03.100Z","status":"success","stats":{"total_tokens":1234,"input_tokens":1000,"output_tokens":234,"cached":0,"input":1000,"duration_ms":3100,"tool_calls":2,"models":{"gemini-3.1-flash-lite":{"total_tokens":34,"input_tokens":20,"output_tokens":14,"cached":0,"input":20},"gemini-3-flash-preview":{"total_tokens":1200,"input_tokens":980,"output_tokens":220,"cached":0,"input":980}}}}
        """;

    [Fact]
    public void Tool_turn_maps_to_events_in_order()
    {
        var events = ParseAll(new GeminiStreamParser("acad"), ToolTurn);

        Assert.Equal(new SessionStarted("5b1f2c3d-0000-4000-8000-000000000001"), events[0]);
        Assert.Equal(new TextDelta("I'll check the model "), events[1]);
        Assert.Equal(new TextDelta("first."), events[2]);
        Assert.Equal(new TextDone("I'll check the model first."), events[3]);
        var info = Assert.IsType<ToolCallStarted>(events[4]);
        Assert.Equal(("get_model_info", "acad"), (info.Name, info.Server));
        var infoDone = Assert.IsType<ToolCallFinished>(events[5]);
        Assert.Equal(info.CallId, infoDone.CallId);
        Assert.True(infoDone.Success);
        Assert.Equal("Model 'Project1': 2 levels, feet", infoDone.Summary);
        var wall = Assert.IsType<ToolCallStarted>(events[6]);
        Assert.Equal("create_wall", wall.Name);
        Assert.Equal("level: Level 9, points: 2 items, height: 10", wall.Summary);
        var wallDone = Assert.IsType<ToolCallFinished>(events[7]);
        Assert.False(wallDone.Success);
        Assert.Equal("Level 'Level 9' not found", wallDone.Error);
        Assert.Equal(new TextDelta("Done."), events[8]);
        Assert.Equal(new TextDone("Done."), events[9]);
        Assert.Equal(new Usage("gemini-3-flash-preview", 1000, 234), events[10]);
        Assert.Equal(new TurnCompleted("success"), events[11]);
        Assert.Equal(12, events.Count);
    }

    [Fact]
    public void Rejected_api_key_is_reported_as_not_signed_in_with_a_readable_reason()
    {
        var events = ParseAll(new GeminiStreamParser("acad"), CliSamples.GeminiApiKeyRejected);
        Assert.Equal(new SessionStarted("7a3460dc-ef68-4cad-a642-fb5f7a27f678"), events[0]);
        var failed = Assert.IsType<TurnFailed>(events[^1]);
        Assert.Equal(AgentFailureKind.NotSignedIn, failed.Kind);
        Assert.Contains("API key not valid. Please pass a valid API key.", failed.Message, StringComparison.Ordinal);
        Assert.Contains("/auth", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_without_result_uses_exit_code_and_stderr()
    {
        var auth = new GeminiStreamParser("acad");
        auth.ObserveStdErr(@"Please set an Auth method in your C:\Users\u\.gemini\settings.json or specify one of the following environment variables before running: GEMINI_API_KEY, GOOGLE_GENAI_USE_VERTEXAI, GOOGLE_GENAI_USE_GCA");
        Assert.Equal(AgentFailureKind.NotSignedIn, Assert.IsType<TurnFailed>(auth.CreateFallbackTerminal(41)).Kind);

        var resume = new GeminiStreamParser("acad");
        resume.ObserveStdErr("Error resuming session: Invalid session identifier \"abc\". Use --list-sessions to see available sessions.");
        Assert.True(resume.IsMissingSession);

        var noisy = new GeminiStreamParser("acad");
        noisy.ObserveStdErr("Warning: Windows 10 detected. Some UI features like smooth scrolling may be degraded.");
        Assert.Equal(new TurnCompleted("exit"), noisy.CreateFallbackTerminal(0));
    }

    [Fact]
    public void Pending_text_is_completed_when_the_stream_ends_early()
    {
        var parser = new GeminiStreamParser("acad");
        ParseAll(parser, """{"type":"message","role":"assistant","content":"partial","delta":true}""");
        Assert.Equal([new TextDone("partial")], parser.Flush());
        Assert.Empty(parser.Flush());
    }
}

public sealed class GeminiRunnerTests
{
    [Fact]
    public void Arguments_restrict_extensions_and_mcp_servers_without_the_prompt()
    {
        using var dir = new TestDirectory();
        var arguments = GeminiRunner.BuildArguments(Request(dir.Path, NastyPrompt, model: "flash", resume: "5b1f2c3d"), "5b1f2c3d", [@"C:\My Files"]);
        Assert.Equal("stream-json", ValueAfter(arguments, "-o"));
        Assert.Equal("none", ValueAfter(arguments, "-e"));
        Assert.Equal("acad", ValueAfter(arguments, "--allowed-mcp-server-names"));
        Assert.Equal("default", ValueAfter(arguments, "--approval-mode"));
        Assert.Equal("flash", ValueAfter(arguments, "-m"));
        Assert.Equal("5b1f2c3d", ValueAfter(arguments, "--resume"));
        Assert.Equal(@"C:\My Files", ValueAfter(arguments, "--include-directories"));
        Assert.DoesNotContain("-p", arguments);
        Assert.DoesNotContain("--yolo", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("big", StringComparison.Ordinal));
    }

    [Fact]
    public void Invocation_prepares_a_private_trusted_workspace()
    {
        using var dir = new TestDirectory();
        var request = Request(dir.Path, NastyPrompt) with { Instructions = "AutoCAD only." };
        var invocation = GeminiRunner.BuildInvocation(new ResolvedCommand("node.exe", ["gemini.js"], "gemini.cmd"), request, null);

        var work = invocation.Launch.WorkingDirectory!;
        Assert.StartsWith(Path.Combine(dir.Path, "gemini-"), work, StringComparison.Ordinal);
        Assert.Equal(NastyPrompt, invocation.StandardInput);
        Assert.Equal("AutoCAD only.", File.ReadAllText(Path.Combine(work, "GEMINI.md")));
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(work, ".gemini", "settings.json")))!;
        var server = settings["mcpServers"]!["acad"]!;
        Assert.Equal(BridgePath, (string?)server["command"]);
        Assert.True((bool)server["trust"]!);
        Assert.Equal(900_000, (long)server["timeout"]!);
        Assert.Equal(["--pid", "1234", "--panel"], server["args"]!.AsArray().Select(a => (string?)a));
        Assert.Null(settings["tools"]);
        var policyPath = ValueAfter(invocation.Launch.Arguments, "--policy");
        Assert.Equal(Path.Combine(work, ".gemini", "copilot-policy.toml"), policyPath);
        var policy = File.ReadAllText(policyPath);
        Assert.Contains("[[rule]]", policy, StringComparison.Ordinal);
        Assert.Contains("\"run_shell_command\", \"write_file\", \"replace\"", policy, StringComparison.Ordinal);
        Assert.Contains("decision = \"deny\"", policy, StringComparison.Ordinal);
        Assert.Contains("priority = 900", policy, StringComparison.Ordinal);
        Assert.Equal("GEMINI.md", (string?)settings["context"]!["fileName"]);
        Assert.Equal("true", invocation.Launch.Environment["GEMINI_CLI_TRUST_WORKSPACE"]);
        Assert.Equal("true", invocation.Launch.Environment["GEMINI_CLI_NO_RELAUNCH"]);
        Assert.Equal(["gemini.js", "-o"], invocation.Launch.Arguments.Take(2));
        Assert.Empty(Directory.GetFiles(Path.Combine(work, ".gemini"), "*.tmp"));
    }

    [Fact]
    public void Without_a_policy_file_the_tools_are_excluded_in_settings()
    {
        var settings = GeminiRunner.BuildSettings(Request(Path.GetTempPath()).AcadMcp, excludeToolsInSettings: true);
        var excluded = settings["tools"]!["exclude"]!.AsArray().Select(a => (string?)a).ToList();
        Assert.Contains("run_shell_command", excluded);
        Assert.Contains("write_file", excluded);
        Assert.DoesNotContain("--policy", GeminiRunner.BuildArguments(Request(Path.GetTempPath()), null, []));
    }

    [Fact]
    public void Workspace_folder_is_stable_per_acad_session_and_distinct_between_sessions()
    {
        using var dir = new TestDirectory();
        var a = GeminiRunner.LaunchKey(Request(dir.Path, pid: "100").AcadMcp);
        var again = GeminiRunner.LaunchKey(Request(dir.Path, pid: "100").AcadMcp);
        var b = GeminiRunner.LaunchKey(Request(dir.Path, pid: "200").AcadMcp);
        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void Sign_in_state_is_read_from_gemini_files()
    {
        using var dir = new TestDirectory();
        var gemini = Directory.CreateDirectory(dir.File(".gemini")).FullName;
        var environment = new Dictionary<string, string>();
        var options = new CliRunnerOptions { UserHome = dir.Path, GetEnvironmentVariable = n => environment.GetValueOrDefault(n) };

        Assert.Equal(SignInState.SignedOut, GeminiRunner.ProbeSignIn(options).State);

        File.WriteAllText(Path.Combine(gemini, "settings.json"), """{ "security": { "auth": { "selectedType": "oauth-personal" } } }""");
        Assert.Equal(SignInState.SignedOut, GeminiRunner.ProbeSignIn(options).State);

        File.WriteAllText(Path.Combine(gemini, "oauth_creds.json"), "{}");
        File.WriteAllText(Path.Combine(gemini, "google_accounts.json"), """{ "active": "someone@example.com", "old": [] }""");
        Assert.Equal((SignInState.SignedIn, "someone@example.com", (string?)null), GeminiRunner.ProbeSignIn(options));

        File.WriteAllText(Path.Combine(gemini, "settings.json"), """
            // comments are allowed in Gemini settings
            { "security": { "auth": { "selectedType": "gemini-api-key" } }, }
            """);
        var apiKey = GeminiRunner.ProbeSignIn(options);
        Assert.Equal(SignInState.Unknown, apiKey.State);
        Assert.Contains("Sign in with Google", apiKey.Detail, StringComparison.Ordinal);

        environment["GEMINI_API_KEY"] = "x";
        Assert.Equal(SignInState.SignedIn, GeminiRunner.ProbeSignIn(options).State);

        environment.Clear();
        File.WriteAllText(Path.Combine(gemini, ".env"), "# keys\nexport GEMINI_API_KEY=\"abc\"\n");
        Assert.Equal(SignInState.SignedIn, GeminiRunner.ProbeSignIn(options).State);

        var elsewhere = Directory.CreateDirectory(dir.File("other-home")).FullName;
        environment["GEMINI_CLI_HOME"] = elsewhere;
        Assert.Equal(SignInState.SignedOut, GeminiRunner.ProbeSignIn(options).State);
        environment["GOOGLE_GENAI_USE_GCA"] = "true";
        Assert.Equal(SignInState.SignedOut, GeminiRunner.ProbeSignIn(options).State);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(elsewhere, ".gemini")).FullName, "oauth_creds.json"), "{}");
        Assert.Equal((SignInState.SignedIn, "Google account", (string?)null), GeminiRunner.ProbeSignIn(options));
    }

    [Fact]
    public async Task Missing_cli_is_reported_as_not_installed()
    {
        var runner = new GeminiRunner(new CliRunnerOptions { Resolve = _ => null });
        var status = await runner.GetStatusAsync();
        Assert.False(status.Installed);
        Assert.Contains("npm install -g @google/gemini-cli", status.Detail, StringComparison.Ordinal);
        Assert.Equal("gemini", runner.GetSignInCommand().FileName);
        Assert.Equal([null, "auto", "pro", "flash", "flash-lite"], runner.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task Fake_cli_exit_41_becomes_not_signed_in()
    {
        using var dir = new TestDirectory();
        var runner = new GeminiRunner(new CliRunnerOptions
        {
            Resolve = _ => Cmd("echo", "Please set an Auth method in your settings.json", "1>&2", "&", "exit", "/b", "41"),
        });
        var events = await CollectAsync(runner.RunTurnAsync(Request(dir.Path), CancellationToken.None));
        var failed = Assert.IsType<TurnFailed>(Assert.Single(events));
        Assert.Equal(AgentFailureKind.NotSignedIn, failed.Kind);
    }

    [Fact]
    public async Task Real_cli_status_check_is_read_only_and_does_not_throw()
    {
        using var dir = new TestDirectory();
        var runner = new GeminiRunner(new CliRunnerOptions { StatusWorkingDirectory = dir.Path });
        var status = await runner.GetStatusAsync();
        Assert.Equal(ProviderId.Gemini, status.Provider);
        Assert.Equal(CommandLocator.Resolve("gemini") is not null, status.Installed);
        if (status.Installed)
        {
            Assert.NotNull(status.Version);
        }
    }
}
