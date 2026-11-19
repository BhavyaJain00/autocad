using System.Diagnostics;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Agents;
using AcadCopilot.Tests.Fakes;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

public sealed class CliTurnEngineTests
{
    [Fact]
    public async Task Stop_kills_the_cli_and_ends_with_cancelled()
    {
        using var dir = new TestDirectory();
        var runner = new ClaudeCodeRunner(new CliRunnerOptions { Resolve = _ => Cmd("ping", "-n", "30", "127.0.0.1", ">nul") });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var stopwatch = Stopwatch.StartNew();

        var events = await CollectAsync(runner.RunTurnAsync(Request(dir.Path), cts.Token));

        var failed = Assert.IsType<TurnFailed>(Assert.Single(events));
        Assert.Equal(AgentFailureKind.Cancelled, failed.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Stop took {stopwatch.Elapsed}");
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir.Path, "claude-turns")));
    }

    [Fact]
    public async Task Already_cancelled_turn_does_not_start_a_process()
    {
        using var dir = new TestDirectory();
        var built = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var events = await CollectAsync(CliTurnEngine.RunAsync(
            ClaudeCodeRunner.Profile,
            null,
            _ =>
            {
                built++;
                return new CliInvocation(new ProcessLaunch { FileName = "cmd.exe" }, string.Empty, []);
            },
            () => new ClaudeStreamParser("acad"),
            new CliRunnerOptions(),
            cts.Token));
        Assert.Equal(AgentFailureKind.Cancelled, Assert.IsType<TurnFailed>(Assert.Single(events)).Kind);
        Assert.Equal(0, built);
    }

    [Fact]
    public async Task Unknown_session_is_retried_once_as_a_new_conversation()
    {
        using var dir = new TestDirectory();
        var missing = WriteSample(dir, "missing.jsonl", CliSamples.ClaudeMissingSession);
        var ok = WriteSample(dir, "ok.jsonl", CliSamples.ClaudeToolCallTurn);
        var attempts = new List<string?>();

        var events = await CollectAsync(CliTurnEngine.RunAsync(
            ClaudeCodeRunner.Profile,
            "00000000-0000-4000-8000-000000000000",
            resume =>
            {
                attempts.Add(resume);
                return new CliInvocation(PrintFile(resume is null ? ok : missing).ToLaunch([], dir.Path), "prompt", []);
            },
            () => new ClaudeStreamParser("acad"),
            new CliRunnerOptions(),
            CancellationToken.None));

        Assert.Equal(["00000000-0000-4000-8000-000000000000", null], attempts);
        Assert.Equal(new SessionStarted("75a8ea39-878f-446a-a466-a09657aac33f"), events[0]);
        Assert.Equal(new TurnCompleted("end_turn"), events[^1]);
        Assert.Single(events, e => e is TurnCompleted or TurnFailed);
    }

    [Fact]
    public async Task Setup_and_start_failures_become_turn_failures()
    {
        using var dir = new TestDirectory();
        var setup = await CollectAsync(CliTurnEngine.RunAsync(
            CodexRunner.Profile, null, _ => throw new IOException("disk full"), () => new CodexStreamParser("acad", null), new CliRunnerOptions(), CancellationToken.None));
        Assert.Contains("disk full", Assert.IsType<TurnFailed>(Assert.Single(setup)).Message, StringComparison.Ordinal);

        var start = await CollectAsync(CliTurnEngine.RunAsync(
            CodexRunner.Profile,
            null,
            _ => new CliInvocation(new ProcessLaunch { FileName = dir.File("no-such-cli.exe") }, "x", []),
            () => new CodexStreamParser("acad", null),
            new CliRunnerOptions(),
            CancellationToken.None));
        Assert.Equal(AgentFailureKind.NotInstalled, Assert.IsType<TurnFailed>(Assert.Single(start)).Kind);
    }

    [Fact]
    public async Task Output_after_the_final_event_is_ignored_and_a_silent_crash_is_explained()
    {
        using var dir = new TestDirectory();

        // The fake CLI is cmd.exe (8191-character command line); the real Codex starts as node.exe/codex.exe (32K limit).
        var request = Request(dir.Path) with { Instructions = "Short test instructions." };
        var sample = WriteSample(dir, "codex.jsonl", CliSamples.CodexToolCallTurn + "\n" + """{"type":"item.completed","item":{"id":"late","type":"agent_message","text":"late"}}""");
        var events = await CollectAsync(new CodexRunner(new CliRunnerOptions { Resolve = _ => PrintFile(sample), UserHome = dir.Path, GetEnvironmentVariable = _ => null })
            .RunTurnAsync(request, CancellationToken.None));
        Assert.Equal(new TurnCompleted("completed"), events[^1]);
        Assert.DoesNotContain(new TextDone("late"), events);

        var crash = await CollectAsync(new CodexRunner(new CliRunnerOptions { Resolve = _ => Cmd("echo", "Error: something broke", "1>&2", "&", "exit", "/b", "3"), UserHome = dir.Path, GetEnvironmentVariable = _ => null })
            .RunTurnAsync(request, CancellationToken.None));
        var failed = Assert.IsType<TurnFailed>(Assert.Single(crash));
        Assert.Contains("something broke", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_command_line_with_the_real_instructions_fits_the_windows_limit()
    {
        using var dir = new TestDirectory();
        var arguments = CodexRunner.BuildArguments(Request(dir.Path), "5b1f2c3d-0000-4000-8000-000000000000", [], new CodexUserDefaults("gpt-5", "high"));

        // CreateProcess allows 32,767 characters; quoting adds a little, so keep a safety margin.
        var length = arguments.Sum(a => a.Length + 3) + 200;
        Assert.True(length < 30_000, $"Codex command line is {length} characters");
    }

    [Fact]
    public void Codex_command_line_with_the_largest_learned_section_still_fits()
    {
        using var dir = new TestDirectory();

        // Worst case: the product note plus a full "WHAT YOU HAVE LEARNED" section full of characters TOML must escape.
        var learned = AcadCopilot.Core.Learning.KnowledgeDigest.Heading + "\n" + string.Concat(Enumerable.Repeat("- 9\" walls, C:\\x \"q\"\n", AcadCopilot.Core.Learning.KnowledgeDigest.MaxCharacters / 20));
        learned = learned[..AcadCopilot.Core.Learning.KnowledgeDigest.MaxCharacters];
        var instructions = CopilotInstructions.Compose("AutoCAD Architecture 2027", learned);
        var arguments = CodexRunner.BuildArguments(Request(dir.Path) with { Instructions = instructions }, "5b1f2c3d-0000-4000-8000-000000000000", [], new CodexUserDefaults("gpt-5", "high"));

        // Windows quoting doubles the backslashes before quotes and escapes the quotes: count the escapes twice.
        var length = arguments.Sum(a => a.Length + 3 + a.Count(c => c is '"' or '\\')) + 200;
        Assert.True(length < 32_000, $"Codex command line is {length} characters");
    }
}

public sealed class CliTextTests
{
    [Theory]
    [InlineData("Not logged in · Please run /login", AgentFailureKind.NotSignedIn)]
    [InlineData("unexpected status 401 Unauthorized: Missing bearer or basic authentication in header", AgentFailureKind.NotSignedIn)]
    [InlineData("Please set an Auth method in your settings.json", AgentFailureKind.NotSignedIn)]
    [InlineData("OAuth token has expired. Please obtain a new token or refresh your existing token.", AgentFailureKind.NotSignedIn)]
    [InlineData("You've hit your usage limit. Upgrade or try again later.", AgentFailureKind.RateLimited)]
    [InlineData("429 Too Many Requests", AgentFailureKind.RateLimited)]
    [InlineData("[API Error: RESOURCE_EXHAUSTED quota exceeded]", AgentFailureKind.RateLimited)]
    [InlineData("stream disconnected before completion", AgentFailureKind.Other)]
    [InlineData("Created 401 walls", AgentFailureKind.Other)]
    [InlineData("", AgentFailureKind.Other)]
    public void Errors_are_classified(string text, AgentFailureKind expected) => Assert.Equal(expected, CliText.Classify(text));

    [Fact]
    public void Technical_error_details_are_cleaned_up()
    {
        Assert.Equal(
            "unexpected status 401 Unauthorized: Missing bearer or basic authentication in header",
            CliText.CleanDetail("Reconnecting... 2/5 (unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: wss://api.openai.com/v1/responses, cf-ray: a5a3f7c6ca40a806-DEL)"));
        Assert.Equal("plain (with parens)", CliText.CleanDetail("plain (with parens)"));
        var failed = CliText.Failure(CodexRunner.Profile, "unexpected status 401 Unauthorized: Missing bearer, url: https://x, request id: req_1");
        Assert.Equal(AgentFailureKind.NotSignedIn, failed.Kind);
        Assert.DoesNotContain("url:", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_cli_versions_get_an_update_hint()
    {
        var failed = CliText.Failure(ClaudeCodeRunner.Profile, "error: unknown option '--permission-prompts'");
        Assert.Contains("npm install -g @anthropic-ai/claude-code@latest", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Toml_strings_escape_everything_that_needs_it()
    {
        Assert.Equal("\"a\\\"b\\\\c\\nd\\te\\u0001ü🏠\"", CliText.TomlString("a\"b\\c\nd\te\u0001ü🏠"));
        Assert.Equal("[\"x\", \"y z\"]", CliText.TomlArray(["x", "y z"]));
        Assert.Equal("{A_B = \"1\", \"odd.key\" = \"2\"}", CliText.TomlInlineTable([new("A_B", "1"), new("odd.key", "2")]));
    }

    [Fact]
    public void Argument_summaries_are_short()
    {
        var args = JsonNode.Parse("""{"name":"A very long room name that goes on and on and on","rooms":[1,2,3],"options":{"a":1},"height":10,"extra":true}""");
        Assert.Equal("name: A very long room name that goes…, rooms: 3 items, options: {…}, …", CliText.SummarizeArguments(args));
        Assert.Equal(string.Empty, CliText.SummarizeArguments(new JsonObject()));
        Assert.Equal(string.Empty, CliText.SummarizeArguments(null));
    }

    [Fact]
    public void Tool_results_use_the_acad_message()
    {
        var ok = CliText.SummarizeResult("c1", """{"success":true,"message":"Created 4 walls","createdIds":[1,2,3,4]}""", false, null);
        Assert.Equal(new ToolCallFinished("c1", true, "Created 4 walls"), ok);

        var plain = CliText.SummarizeResult("c2", "line one\nline two", false, null);
        Assert.Equal("line one", plain.Summary);

        var cliError = CliText.SummarizeResult("c3", null, true, "MCP error -32001: Request timed out");
        Assert.False(cliError.Success);
        Assert.Equal("MCP error -32001: Request timed out", cliError.Error);
    }
}
