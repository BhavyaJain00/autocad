using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Tests.Fakes;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

public sealed class ClaudeStreamParserTests
{
    [Fact]
    public void Recorded_tool_call_turn_maps_to_events_in_order()
    {
        var events = ParseAll(new ClaudeStreamParser("acad"), CliSamples.ClaudeToolCallTurn);

        Assert.Equal(new SessionStarted("75a8ea39-878f-446a-a466-a09657aac33f"), events[0]);
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.Equal("toolu_017MW8wBTpSFJCYA59bCgf5c", started.CallId);
        Assert.Equal("acad_status", started.Name);
        Assert.Equal("acad", started.Server);
        var finished = Assert.Single(events.OfType<ToolCallFinished>());
        Assert.Equal(started.CallId, finished.CallId);
        Assert.True(finished.Success);
        Assert.StartsWith("No running AutoCAD Architecture 2027", finished.Summary, StringComparison.Ordinal);

        var streamed = string.Concat(events.OfType<TextDelta>().Select(d => d.Text));
        var done = Assert.Single(events.OfType<TextDone>());
        Assert.Equal(streamed, done.Text);
        Assert.StartsWith("AutoCAD isn't connected", done.Text, StringComparison.Ordinal);

        Assert.Equal(new Usage("claude-haiku-5-5", 4 + 5217 + 5083, 92), Assert.Single(events.OfType<Usage>()));
        Assert.Equal(new TurnCompleted("end_turn"), events[^1]);
        Assert.True(events.IndexOf(started) < events.IndexOf(finished));
        Assert.True(events.IndexOf(finished) < events.IndexOf(done));
        Assert.Single(events, e => e is SessionStarted);
    }

    [Fact]
    public void Signed_out_is_reported_as_not_signed_in_without_chat_text()
    {
        var events = ParseAll(new ClaudeStreamParser("acad"), CliSamples.ClaudeSignedOut);

        Assert.DoesNotContain(events, e => e is TextDone or TextDelta or Usage);
        var failed = Assert.IsType<TurnFailed>(events[^1]);
        Assert.Equal(AgentFailureKind.NotSignedIn, failed.Kind);
        Assert.Contains("Not logged in", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Sign in", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_resume_session_is_detected()
    {
        var parser = new ClaudeStreamParser("acad");
        var events = ParseAll(parser, CliSamples.ClaudeMissingSession);
        Assert.True(parser.IsMissingSession);
        Assert.IsType<TurnFailed>(events[^1]);
    }

    [Fact]
    public void Tool_errors_and_failed_acad_results_are_marked_failed()
    {
        var parser = new ClaudeStreamParser("acad");
        var events = ParseAll(parser, """
            {"type":"assistant","message":{"model":"claude-x","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"mcp__acad__create_wall","input":{"level":"Level 9","points":[[0,0],[20,0]],"height":10}},{"type":"tool_use","id":"t2","name":"mcp__acad__delete_elements","input":{"ids":[1,2,3]}}]},"parent_tool_use_id":null,"session_id":"s"}
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"t1","type":"tool_result","content":[{"type":"text","text":"{\"success\":false,\"message\":\"Level 'Level 9' not found\",\"errors\":[\"Level 'Level 9' not found\",\"Use get_model_info\"]}"}]}]},"parent_tool_use_id":null,"session_id":"s"}
            {"type":"user","message":{"role":"user","content":[{"tool_use_id":"t2","type":"tool_result","is_error":true,"content":"The user declined this action in the panel."}]},"parent_tool_use_id":null,"session_id":"s"}
            """);

        var starts = events.OfType<ToolCallStarted>().ToList();
        Assert.Equal(["create_wall", "delete_elements"], starts.Select(s => s.Name));
        Assert.Equal("level: Level 9, points: 2 items, height: 10", starts[0].Summary);
        var finishes = events.OfType<ToolCallFinished>().ToList();
        Assert.False(finishes[0].Success);
        Assert.Equal("Level 'Level 9' not found; Use get_model_info", finishes[0].Error);
        Assert.False(finishes[1].Success);
        Assert.Equal("The user declined this action in the panel.", finishes[1].Error);
    }

    [Fact]
    public void Subagent_messages_and_unknown_events_are_ignored()
    {
        var events = ParseAll(new ClaudeStreamParser("acad"), """
            not json at all
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"sub"}},"parent_tool_use_id":"toolu_parent","session_id":"s"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"from a subagent"}]},"parent_tool_use_id":"toolu_parent","session_id":"s"}
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}},"parent_tool_use_id":null,"session_id":"s"}
            {"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"error":"overloaded","session_id":"s"}
            {"type":"something_new","session_id":"s"}
            """);
        Assert.Empty(events);
    }

    [Fact]
    public void Resume_after_stop_skips_the_synthetic_placeholder_and_waits_for_the_real_answer()
    {
        var events = ParseAll(new ClaudeStreamParser("acad"), CliSamples.ClaudeResumeAfterStop);

        // ParseAll keeps every event; the first terminal one must be the real answer's result.
        var terminal = Assert.Single(events, e => e is TurnCompleted or TurnFailed);
        Assert.Equal(new TurnCompleted("end_turn"), terminal);
        Assert.Same(terminal, events[^1]);
        Assert.DoesNotContain(events, e => e is TextDone { Text: ClaudeStreamParser.NoResponseRequested } or TextDelta { Text: ClaudeStreamParser.NoResponseRequested });
        Assert.Equal("The plot is drawn; the walls come next.", Assert.Single(events.OfType<TextDone>()).Text);
        Assert.Equal("The plot is drawn; the walls come next.", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)));
    }

    [Fact]
    public void A_placeholder_result_after_real_output_still_ends_the_turn()
    {
        // Only the placeholder BEFORE any model output is skipped; a model that answers exactly that ends normally.
        var events = ParseAll(new ClaudeStreamParser("acad"), """
            {"type":"assistant","message":{"model":"claude-x","role":"assistant","content":[{"type":"text","text":"No response requested."}]},"parent_tool_use_id":null,"session_id":"s"}
            {"type":"result","subtype":"success","is_error":false,"result":"No response requested.","stop_reason":"end_turn","session_id":"s"}
            """);
        Assert.Equal(new TurnCompleted("end_turn"), events[^1]);
        Assert.Equal("No response requested.", Assert.Single(events.OfType<TextDone>()).Text);
    }

    [Fact]
    public void Rate_limit_errors_are_classified()
    {
        var events = ParseAll(new ClaudeStreamParser("acad"), """
            {"type":"assistant","message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"You've hit your limit · resets 3pm"}]},"parent_tool_use_id":null,"session_id":"s","error":"rate_limit","is_api_error_message":true}
            {"type":"result","subtype":"success","is_error":true,"result":"You've hit your limit · resets 3pm","session_id":"s","usage":{"input_tokens":0,"output_tokens":0}}
            """);
        var failed = Assert.IsType<TurnFailed>(events[^1]);
        Assert.Equal(AgentFailureKind.RateLimited, failed.Kind);
        Assert.Contains("resets 3pm", failed.Message, StringComparison.Ordinal);
    }
}

public sealed class ClaudeCodeRunnerTests
{
    [Fact]
    public void Arguments_configure_mcp_permissions_and_resume_without_the_prompt()
    {
        using var dir = new TestDirectory();
        var request = Request(dir.Path, NastyPrompt, model: "opus", resume: "75a8ea39-878f-446a-a466-a09657aac33f");
        var arguments = ClaudeCodeRunner.BuildArguments(request, request.ResumeSessionId, @"C:\t 1\mcp.json", @"C:\t 1\instructions.md", []);

        Assert.Equal("-p", arguments[0]);
        Assert.Equal("stream-json", ValueAfter(arguments, "--output-format"));
        Assert.Contains("--verbose", arguments);
        Assert.Contains("--include-partial-messages", arguments);
        Assert.Equal(@"C:\t 1\mcp.json", ValueAfter(arguments, "--mcp-config"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal("mcp__acad__*", ValueAfter(arguments, "--allowedTools"));
        Assert.Equal("dontAsk", ValueAfter(arguments, "--permission-mode"));
        Assert.Equal("none", ValueAfter(arguments, "--permission-prompts"));
        Assert.Equal(string.Empty, ValueAfter(arguments, "--tools"));
        Assert.Equal("project,local", ValueAfter(arguments, "--setting-sources"));
        Assert.Equal(@"C:\t 1\instructions.md", ValueAfter(arguments, "--append-system-prompt-file"));
        Assert.Equal("opus", ValueAfter(arguments, "--model"));
        Assert.Equal("75a8ea39-878f-446a-a466-a09657aac33f", ValueAfter(arguments, "--resume"));
        Assert.DoesNotContain(arguments, a => a.Contains("big", StringComparison.Ordinal) || a.Contains("🏠", StringComparison.Ordinal));
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
    }

    [Fact]
    public void New_conversation_without_model_has_no_resume_or_model_flags()
    {
        using var dir = new TestDirectory();
        var arguments = ClaudeCodeRunner.BuildArguments(Request(dir.Path), null, "m.json", "i.md", []);
        Assert.DoesNotContain("--resume", arguments);
        Assert.DoesNotContain("--model", arguments);
    }

    [Fact]
    public void Invocation_writes_private_mcp_config_and_pipes_the_prompt()
    {
        using var dir = new TestDirectory();
        var request = Request(dir.Path, NastyPrompt) with { Instructions = "Use only the \"acad\" tools.\nLine two." };
        var invocation = ClaudeCodeRunner.BuildInvocation(new ResolvedCommand("claude.exe", [], "claude.cmd"), request, null);
        try
        {
            Assert.Equal(NastyPrompt, invocation.StandardInput);
            Assert.Equal(Path.Combine(dir.Path, "claude"), invocation.Launch.WorkingDirectory);
            Assert.Equal("claude.exe", invocation.Launch.FileName);

            var config = JsonNode.Parse(File.ReadAllText(ValueAfter(invocation.Launch.Arguments, "--mcp-config")))!;
            var server = config["mcpServers"]!["acad"]!;
            Assert.Equal("stdio", (string?)server["type"]);
            Assert.Equal(BridgePath, (string?)server["command"]);
            Assert.Equal(["--pid", "1234", "--panel"], server["args"]!.AsArray().Select(a => (string?)a));
            Assert.Equal(900_000, (long)server["timeout"]!);
            Assert.Equal(@"C:\Users\Zoë\Copilot ""home""", (string?)server["env"]!["ACADCOPILOT_HOME"]);
            Assert.Single(config["mcpServers"]!.AsObject());
            Assert.Equal(request.Instructions, File.ReadAllText(ValueAfter(invocation.Launch.Arguments, "--append-system-prompt-file")));

            var environment = invocation.Launch.Environment;
            Assert.Equal("0", environment["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"]);
            Assert.Equal("900000", environment["MCP_TOOL_TIMEOUT"]);
            Assert.Equal("false", environment["ENABLE_CLAUDEAI_MCP_SERVERS"]);
            Assert.Equal("false", environment["ENABLE_TOOL_SEARCH"]);
            Assert.True(environment.ContainsKey("CLAUDECODE") && environment["CLAUDECODE"] is null);
            Assert.All(invocation.TempPaths, p => Assert.True(Directory.Exists(p)));
        }
        finally
        {
            CliTurnEngine.DeleteTempPaths(invocation.TempPaths);
        }

        Assert.All(invocation.TempPaths, p => Assert.False(Directory.Exists(p)));
    }

    [Fact]
    public void Attachments_enable_only_the_read_tool_for_their_folders()
    {
        using var dir = new TestDirectory();
        var files = Directory.CreateDirectory(dir.File("my files")).FullName;
        File.WriteAllText(Path.Combine(files, "plan.png"), "x");
        File.WriteAllText(Path.Combine(files, "notes.txt"), "rooms: 3");
        File.WriteAllText(Path.Combine(files, "model.dwg"), "x");
        var missing = Path.Combine(files, "gone.pdf");
        var request = Request(dir.Path, "Use these", attachments: [Path.Combine(files, "plan.png"), Path.Combine(files, "notes.txt"), Path.Combine(files, "model.dwg"), missing]);

        var invocation = ClaudeCodeRunner.BuildInvocation(new ResolvedCommand("claude.exe", [], "claude.cmd"), request, null);
        CliTurnEngine.DeleteTempPaths(invocation.TempPaths);

        Assert.Equal("Read", ValueAfter(invocation.Launch.Arguments, "--tools"));
        Assert.Equal(files, ValueAfter(invocation.Launch.Arguments, "--add-dir"));
        Assert.Contains(Path.Combine(files, "plan.png"), invocation.StandardInput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(files, "notes.txt"), invocation.StandardInput, StringComparison.Ordinal);
        Assert.Contains("cannot be opened", invocation.StandardInput, StringComparison.Ordinal);
        Assert.Contains("gone.pdf (not found)", invocation.StandardInput, StringComparison.Ordinal);
        Assert.StartsWith("Use these", invocation.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_status_json_is_read()
    {
        var signedIn = ClaudeCodeRunner.ParseAuthStatus(new ProcessResult(0, """
            {
              "loggedIn": true,
              "authMethod": "claude.ai",
              "apiProvider": "firstParty",
              "email": "someone@example.com",
              "subscriptionType": "max"
            }
            """, string.Empty, false));
        Assert.Equal((SignInState.SignedIn, "someone@example.com · Claude Max", (string?)null), signedIn);

        var signedOut = ClaudeCodeRunner.ParseAuthStatus(new ProcessResult(1, """{ "loggedIn": false, "authMethod": "none" }""", string.Empty, false));
        Assert.Equal(SignInState.SignedOut, signedOut.State);

        Assert.Equal(SignInState.Unknown, ClaudeCodeRunner.ParseAuthStatus(null).State);
        Assert.Equal(SignInState.Unknown, ClaudeCodeRunner.ParseAuthStatus(new ProcessResult(-1, string.Empty, string.Empty, true)).State);
        Assert.Equal(SignInState.Unknown, ClaudeCodeRunner.ParseAuthStatus(new ProcessResult(1, string.Empty, "error: unknown command 'auth'", false)).State);
    }

    [Fact]
    public async Task Missing_cli_is_reported_as_not_installed()
    {
        var runner = new ClaudeCodeRunner(new CliRunnerOptions { Resolve = _ => null });
        var status = await runner.GetStatusAsync();
        Assert.False(status.Installed);
        Assert.False(status.IsUsable);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", status.Detail, StringComparison.Ordinal);

        using var dir = new TestDirectory();
        var events = await CollectAsync(runner.RunTurnAsync(Request(dir.Path), CancellationToken.None));
        Assert.Equal(AgentFailureKind.NotInstalled, Assert.IsType<TurnFailed>(Assert.Single(events)).Kind);
        Assert.Equal(new CommandLine("claude", ["auth", "login"]).FileName, runner.GetSignInCommand().FileName);
        Assert.Equal(["auth", "login"], runner.GetSignInCommand().Arguments);
    }

    [Fact]
    public async Task Runs_a_fake_cli_end_to_end_and_cleans_up_the_turn_files()
    {
        using var dir = new TestDirectory();
        var sample = WriteSample(dir, "claude.jsonl", CliSamples.ClaudeToolCallTurn);
        var runner = new ClaudeCodeRunner(new CliRunnerOptions { Resolve = _ => PrintFile(sample) });
        var work = Directory.CreateDirectory(dir.File("work")).FullName;

        var events = await CollectAsync(runner.RunTurnAsync(Request(work, NastyPrompt), CancellationToken.None));

        Assert.IsType<SessionStarted>(events[0]);
        Assert.Equal(new TurnCompleted("end_turn"), events[^1]);
        Assert.Single(events, e => e is TurnCompleted or TurnFailed);
        Assert.Contains(events, e => e is ToolCallFinished { Success: true });
        Assert.Empty(Directory.GetDirectories(Path.Combine(work, "claude-turns")));
    }

    [Fact]
    public async Task Resumed_turn_after_stop_runs_until_the_answer_to_the_new_prompt()
    {
        using var dir = new TestDirectory();
        var sample = WriteSample(dir, "claude-resume.jsonl", CliSamples.ClaudeResumeAfterStop);
        var runner = new ClaudeCodeRunner(new CliRunnerOptions { Resolve = _ => PrintFile(sample) });
        var work = Directory.CreateDirectory(dir.File("work")).FullName;

        var events = await CollectAsync(runner.RunTurnAsync(
            Request(work, "Draw it", resume: "3d14789e-7bd9-4b56-afdd-cf3e4c310175"), CancellationToken.None));

        Assert.Equal(new TurnCompleted("end_turn"), events[^1]);
        Assert.Single(events, e => e is TurnCompleted or TurnFailed);
        Assert.Equal("The plot is drawn; the walls come next.", Assert.Single(events.OfType<TextDone>()).Text);
        Assert.DoesNotContain(events, e => e is TextDone { Text: ClaudeStreamParser.NoResponseRequested });
        Assert.Equal(new SessionStarted("3d14789e-7bd9-4b56-afdd-cf3e4c310175"), Assert.Single(events.OfType<SessionStarted>()));
    }

    [Fact]
    public async Task Real_cli_status_check_is_read_only_and_does_not_throw()
    {
        using var dir = new TestDirectory();
        var runner = new ClaudeCodeRunner(new CliRunnerOptions { StatusWorkingDirectory = dir.Path });
        var status = await runner.GetStatusAsync();
        Assert.Equal(ProviderId.Claude, status.Provider);
        Assert.Equal(CommandLocator.Resolve("claude") is not null, status.Installed);
        if (status.Installed)
        {
            Assert.NotNull(status.Version);
        }
    }
}
