using AcadCopilot.Core.Agents;
using AcadCopilot.Tests.Fakes;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

public sealed class CodexStreamParserTests
{
    [Fact]
    public void Recorded_tool_call_turn_maps_to_events_in_order()
    {
        var events = ParseAll(new CodexStreamParser("acad", "gpt-test"), CliSamples.CodexToolCallTurn);

        Assert.Equal(new SessionStarted("01a1d8a9-93d6-7931-9de5-6270d894d90d"), events[0]);
        Assert.Equal(new TextDone("I’ll call acad_status once."), events[1]);
        var started = Assert.IsType<ToolCallStarted>(events[2]);
        Assert.Equal("acad_status", started.Name);
        Assert.Equal("acad", started.Server);
        Assert.EndsWith(":item_1", started.CallId, StringComparison.Ordinal);
        var finished = Assert.IsType<ToolCallFinished>(events[3]);
        Assert.Equal(started.CallId, finished.CallId);
        Assert.True(finished.Success);
        Assert.StartsWith("No running AutoCAD Architecture 2027", finished.Summary, StringComparison.Ordinal);
        Assert.Equal(new TextDone("OK"), events[4]);
        Assert.Equal(new Usage("gpt-test", 30403, 76), events[5]);
        Assert.Equal(new TurnCompleted("completed"), events[6]);
        Assert.Equal(7, events.Count);
    }

    [Fact]
    public void Item_ids_are_unique_across_turns()
    {
        var first = ParseAll(new CodexStreamParser("acad", null), CliSamples.CodexToolCallTurn).OfType<ToolCallStarted>().Single();
        var second = ParseAll(new CodexStreamParser("acad", null), CliSamples.CodexToolCallTurn).OfType<ToolCallStarted>().Single();
        Assert.NotEqual(first.CallId, second.CallId);
    }

    [Fact]
    public void Resumed_turn_reports_the_same_thread()
    {
        var events = ParseAll(new CodexStreamParser("acad", null), CliSamples.CodexResumeTurn);
        Assert.Equal(new SessionStarted("01a1d8a9-93d6-7931-9de5-6270d894d90d"), events[0]);
        Assert.Equal(new TextDone("OK"), events[1]);
        Assert.IsType<TurnCompleted>(events[^1]);
    }

    [Fact]
    public void Signed_out_fails_fast_as_not_signed_in()
    {
        var events = ParseAll(new CodexStreamParser("acad", null), CliSamples.CodexSignedOut);
        var firstTerminal = events.First(e => e is TurnFailed or TurnCompleted);
        var failed = Assert.IsType<TurnFailed>(firstTerminal);
        Assert.Equal(AgentFailureKind.NotSignedIn, failed.Kind);
        Assert.Contains("ChatGPT", failed.Message, StringComparison.Ordinal);
        // The first "Reconnecting... (401 Missing bearer)" line is enough: no 30 s of retries.
        Assert.IsType<SessionStarted>(events[0]);
        Assert.Equal(1, events.IndexOf(firstTerminal));
    }

    [Fact]
    public void Failures_and_limits_are_classified()
    {
        var limit = ParseAll(new CodexStreamParser("acad", null), """
            {"type":"thread.started","thread_id":"t"}
            {"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again in 2 hours."}}
            """);
        Assert.Equal(AgentFailureKind.RateLimited, Assert.IsType<TurnFailed>(limit[^1]).Kind);

        var other = ParseAll(new CodexStreamParser("acad", null), """{"type":"turn.failed","error":{"message":"stream disconnected before completion"}}""");
        var failed = Assert.IsType<TurnFailed>(other[^1]);
        Assert.Equal(AgentFailureKind.Other, failed.Kind);
        Assert.Contains("stream disconnected", failed.Message, StringComparison.Ordinal);

        var transient = ParseAll(new CodexStreamParser("acad", null), """{"type":"error","message":"Reconnecting... 1/5 (stream disconnected)"}""");
        Assert.Empty(transient);
    }

    [Fact]
    public void Failed_mcp_calls_and_shell_items_are_reported()
    {
        var events = ParseAll(new CodexStreamParser("acad", null), """
            {"type":"item.completed","item":{"id":"item_3","type":"mcp_tool_call","server":"acad","tool":"create_wall","arguments":{"level":"Level 1"},"result":null,"error":{"message":"tool call timed out"},"status":"failed"}}
            {"type":"item.started","item":{"id":"item_4","type":"mcp_tool_call","server":"acad","tool":"create_floor","arguments":{},"result":null,"error":null,"status":"in_progress"}}
            {"type":"item.completed","item":{"id":"item_4","type":"mcp_tool_call","server":"acad","tool":"create_floor","arguments":{},"result":{"content":[{"type":"text","text":"{\"success\":false,\"message\":\"Boundary is not closed\",\"errors\":[\"Boundary is not closed\"]}"}],"structured_content":null},"error":null,"status":"completed"}}
            {"type":"item.started","item":{"id":"item_5","type":"command_execution","command":"dir","aggregated_output":"","exit_code":null,"status":"in_progress"}}
            {"type":"item.completed","item":{"id":"item_5","type":"command_execution","command":"dir","aggregated_output":"denied","exit_code":1,"status":"failed"}}
            {"type":"item.updated","item":{"id":"item_6","type":"todo_list","items":[{"text":"a","completed":false}]}}
            {"type":"item.completed","item":{"id":"item_7","type":"reasoning","text":"thinking"}}
            """);

        var starts = events.OfType<ToolCallStarted>().ToList();
        Assert.Equal(["create_wall", "create_floor", "shell"], starts.Select(s => s.Name));
        var finishes = events.OfType<ToolCallFinished>().ToList();
        Assert.Equal(3, finishes.Count);
        Assert.False(finishes[0].Success);
        Assert.Equal("tool call timed out", finishes[0].Error);
        Assert.False(finishes[1].Success);
        Assert.Equal("Boundary is not closed", finishes[1].Error);
        Assert.False(finishes[2].Success);
        Assert.DoesNotContain(events, e => e is TextDone);
    }

    [Fact]
    public void Unknown_thread_on_resume_is_detected_from_stderr()
    {
        var parser = new CodexStreamParser("acad", null);
        parser.ObserveStdErr("Error: thread/resume: thread/resume failed: no rollout found for thread id 00000000-0000-4000-8000-000000000000 (code -32600)");
        Assert.True(parser.IsMissingSession);
        var failed = Assert.IsType<TurnFailed>(parser.CreateFallbackTerminal(1));
        Assert.Contains("no rollout found", failed.Message, StringComparison.Ordinal);
    }
}

public sealed class CodexRunnerTests
{
    [Fact]
    public void New_turn_arguments_configure_everything_with_overrides()
    {
        using var dir = new TestDirectory();
        var request = Request(dir.Path, NastyPrompt, model: "gpt-5.6-sol") with { Instructions = "Use \"acad\" tools only.\nC:\\path\tTab" };
        var arguments = CodexRunner.BuildArguments(request, null, [], new CodexUserDefaults("gpt-6-astra", "high"));

        Assert.Equal("exec", arguments[0]);
        Assert.Equal("--json", arguments[1]);
        Assert.Equal("-", arguments[^1]);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Contains("--ignore-user-config", arguments);
        var overrides = Overrides(arguments);
        Assert.Contains("approval_policy=\"never\"", overrides);
        Assert.Contains("sandbox_mode=\"read-only\"", overrides);
        Assert.Contains("features.shell_tool=false", overrides);
        Assert.Contains("features.apps=false", overrides);
        Assert.Contains("mcp_servers.acad.command=\"C:\\\\Program Files\\\\AutoCAD Copilot\\\\bridge\\\\AcadCopilotMcp.exe\"", overrides);
        Assert.Contains("mcp_servers.acad.args=[\"--pid\", \"1234\", \"--panel\"]", overrides);
        Assert.Contains("mcp_servers.acad.env={ACADCOPILOT_HOME = \"C:\\\\Users\\\\Zoë\\\\Copilot \\\"home\\\"\"}", overrides);
        Assert.Contains("mcp_servers.acad.tool_timeout_sec=900", overrides);
        Assert.Contains("mcp_servers.acad.default_tools_approval_mode=\"approve\"", overrides);
        Assert.Contains("developer_instructions=\"Use \\\"acad\\\" tools only.\\nC:\\\\path\\tTab\"", overrides);
        Assert.Equal("gpt-5.6-sol", ValueAfter(arguments, "-m"));
        Assert.DoesNotContain(overrides, o => o.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
        Assert.DoesNotContain("resume", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("big", StringComparison.Ordinal));
    }

    [Fact]
    public void Resume_arguments_put_the_thread_id_before_the_stdin_marker()
    {
        using var dir = new TestDirectory();
        var image = dir.File("plan 1.png");
        var arguments = CodexRunner.BuildArguments(Request(dir.Path), "01a1d8a9-93d6-7931-9de5-6270d894d90d", [image], new CodexUserDefaults("gpt-6-astra", "high"));

        Assert.Equal(["exec", "resume", "--image=" + image, "--json"], arguments.Take(4));
        Assert.Equal(["01a1d8a9-93d6-7931-9de5-6270d894d90d", "-"], arguments.TakeLast(2));
        Assert.Equal("gpt-6-astra", ValueAfter(arguments, "-m"));
        Assert.Contains("model_reasoning_effort=\"high\"", Overrides(arguments));
    }

    [Fact]
    public void Invocation_inlines_text_attachments_and_passes_images()
    {
        using var dir = new TestDirectory();
        File.WriteAllText(dir.File("plan.png"), "x");
        File.WriteAllText(dir.File("rooms.csv"), "name,area\nBed 1,12");
        File.WriteAllText(dir.File("spec.pdf"), "x");
        var request = Request(dir.Path, NastyPrompt, attachments: [dir.File("plan.png"), dir.File("rooms.csv"), dir.File("spec.pdf")]);

        var invocation = CodexRunner.BuildInvocation(new ResolvedCommand("node.exe", ["codex.js"], "codex.cmd"), request, null, new CodexUserDefaults(null, null));

        Assert.Equal(["codex.js", "exec", "--image=" + dir.File("plan.png")], invocation.Launch.Arguments.Take(3));
        Assert.StartsWith(NastyPrompt, invocation.StandardInput, StringComparison.Ordinal);
        Assert.Contains("Bed 1,12", invocation.StandardInput, StringComparison.Ordinal);
        Assert.Contains("spec.pdf", invocation.StandardInput, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(dir.Path, "codex"), invocation.Launch.WorkingDirectory);
        Assert.Empty(invocation.TempPaths);
        Assert.DoesNotContain("-m", invocation.Launch.Arguments);
    }

    [Fact]
    public void Login_status_is_read_from_either_stream()
    {
        Assert.Equal((SignInState.SignedIn, "ChatGPT account", (string?)null),
            CodexRunner.ParseLoginStatus(new ProcessResult(0, string.Empty, "Logged in using ChatGPT\n", false)));
        Assert.Equal(SignInState.SignedOut, CodexRunner.ParseLoginStatus(new ProcessResult(1, string.Empty, "WARNING: proceeding\nNot logged in\n", false)).State);
        Assert.Equal("OpenAI API key", CodexRunner.ParseLoginStatus(new ProcessResult(0, "Logged in using an API key - sk-***\n", string.Empty, false)).Account);
        Assert.Equal(SignInState.Unknown, CodexRunner.ParseLoginStatus(null).State);
        Assert.Equal(SignInState.Unknown, CodexRunner.ParseLoginStatus(new ProcessResult(-1, string.Empty, string.Empty, true)).State);
    }

    [Fact]
    public void User_defaults_and_model_list_are_read_from_codex_home()
    {
        using var dir = new TestDirectory();
        var home = Directory.CreateDirectory(dir.File(".codex")).FullName;
        File.WriteAllText(Path.Combine(home, "config.toml"), """
            model = "gpt-6-astra"
            model_reasoning_effort = 'xhigh' # comment
            [projects.'c:\x']
            model = "not-top-level"
            """);
        File.WriteAllText(Path.Combine(home, "models_cache.json"), """
            {"fetched_at":"x","models":[
              {"slug":"gpt-b","display_name":"GPT B","visibility":"list","priority":2},
              {"slug":"hidden","display_name":"Hidden","visibility":"hide","priority":0},
              {"slug":"gpt-a","display_name":"GPT A","visibility":"list","priority":1}
            ]}
            """);
        var options = new CliRunnerOptions { UserHome = dir.Path, GetEnvironmentVariable = _ => null };

        var defaults = CodexRunner.ReadUserDefaults(CodexRunner.CodexHome(options));
        Assert.Equal(new CodexUserDefaults("gpt-6-astra", "xhigh"), defaults);

        var models = new CodexRunner(options).Models;
        Assert.Equal([null, "gpt-a", "gpt-b"], models.Select(m => m.Id));
        Assert.Equal("Default (gpt-6-astra)", models[0].DisplayName);
        Assert.Equal("GPT A", models[1].DisplayName);

        var empty = new CodexRunner(new CliRunnerOptions { UserHome = dir.File("nobody"), GetEnvironmentVariable = _ => null }).Models;
        Assert.Equal(new ModelChoice(null, "Default"), Assert.Single(empty));

        var overridden = new CliRunnerOptions { UserHome = dir.File("nobody"), GetEnvironmentVariable = n => n == "CODEX_HOME" ? home : null };
        Assert.Equal(home, CodexRunner.CodexHome(overridden));
    }

    [Fact]
    public async Task Missing_cli_is_reported_as_not_installed()
    {
        var runner = new CodexRunner(new CliRunnerOptions { Resolve = _ => null, UserHome = Path.GetTempPath() });
        var status = await runner.GetStatusAsync();
        Assert.False(status.Installed);
        Assert.Contains("npm install -g @openai/codex", status.Detail, StringComparison.Ordinal);
        Assert.Equal(["login"], runner.GetSignInCommand().Arguments);
    }

    [Fact]
    public async Task Real_cli_status_check_is_read_only_and_does_not_throw()
    {
        using var dir = new TestDirectory();
        var runner = new CodexRunner(new CliRunnerOptions { StatusWorkingDirectory = dir.Path });
        var status = await runner.GetStatusAsync();
        Assert.Equal(ProviderId.Codex, status.Provider);
        Assert.Equal(CommandLocator.Resolve("codex") is not null, status.Installed);
        if (status.Installed)
        {
            Assert.NotNull(status.Version);
        }
    }

    private static List<string> Overrides(IReadOnlyList<string> arguments) =>
        [.. arguments.Select((a, i) => (a, i)).Where(p => p.a == "-c" && p.i + 1 < arguments.Count).Select(p => arguments[p.i + 1])];
}
