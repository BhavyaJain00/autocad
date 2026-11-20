using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Panel;
using AcadCopilot.Tests.Fakes;
using static AcadCopilot.Core.Tests.Agents.CliTestSupport;

namespace AcadCopilot.Core.Tests.Agents;

/// <summary>The Web research switch: off = no web tools in any CLI, on = only the read-only web tools.</summary>
public sealed class WebResearchTests
{
    [Fact]
    public void Claude_code_gets_websearch_and_webfetch_only_when_on()
    {
        using var dir = new TestDirectory();
        var off = ClaudeCodeRunner.BuildArguments(Request(dir.Path), null, "m.json", "i.md", []);
        Assert.Equal("mcp__acad__*", ValueAfter(off, "--allowedTools"));
        Assert.Equal(string.Empty, ValueAfter(off, "--tools"));

        var on = ClaudeCodeRunner.BuildArguments(Request(dir.Path) with { WebResearch = true }, null, "m.json", "i.md", []);
        Assert.Equal("mcp__acad__*,WebSearch,WebFetch", ValueAfter(on, "--allowedTools"));
        Assert.Equal("WebSearch,WebFetch", ValueAfter(on, "--tools"));
        Assert.Equal("dontAsk", ValueAfter(on, "--permission-mode"));
        Assert.DoesNotContain(on, a => a.Contains("Bash", StringComparison.Ordinal) || a.Contains("Write", StringComparison.Ordinal) || a.Contains("Edit", StringComparison.Ordinal));

        // Attached files keep Read next to the web tools.
        var withFiles = ClaudeCodeRunner.BuildArguments(Request(dir.Path) with { WebResearch = true }, null, "m.json", "i.md", [@"C:\Sketches"]);
        Assert.Equal("Read,WebSearch,WebFetch", ValueAfter(withFiles, "--tools"));
        Assert.Equal("Read", ValueAfter(ClaudeCodeRunner.BuildArguments(Request(dir.Path), null, "m.json", "i.md", [@"C:\Sketches"]), "--tools"));
    }

    [Fact]
    public void Codex_searches_live_only_when_on_and_stays_read_only()
    {
        using var dir = new TestDirectory();
        var defaults = new CodexUserDefaults("gpt-5", "high");
        var off = Overrides(CodexRunner.BuildArguments(Request(dir.Path), null, [], defaults));
        Assert.Contains("web_search=\"disabled\"", off);
        Assert.DoesNotContain("web_search=\"live\"", off);

        var onArguments = CodexRunner.BuildArguments(Request(dir.Path) with { WebResearch = true }, null, [], defaults);
        var on = Overrides(onArguments);
        Assert.Contains("web_search=\"live\"", on);
        Assert.DoesNotContain("web_search=\"disabled\"", on);
        Assert.Contains("sandbox_mode=\"read-only\"", on);
        Assert.Contains("features.shell_tool=false", on);
        Assert.Contains("approval_policy=\"never\"", on);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", onArguments);
        Assert.Equal("-", onArguments[^1]);
    }

    [Fact]
    public void Gemini_allows_its_web_tools_only_when_on_and_keeps_shell_and_writes_denied()
    {
        var off = GeminiRunner.BuildPolicy();
        Assert.Contains("\"web_fetch\", \"google_web_search\"", off, StringComparison.Ordinal);
        Assert.DoesNotContain("decision = \"allow\"", off, StringComparison.Ordinal);

        var on = GeminiRunner.BuildPolicy(webResearch: true);
        var deny = on[..on.IndexOf("decision = \"deny\"", StringComparison.Ordinal)];
        Assert.DoesNotContain("google_web_search", deny, StringComparison.Ordinal);
        Assert.DoesNotContain("web_fetch", deny, StringComparison.Ordinal);
        Assert.Contains("\"run_shell_command\", \"write_file\", \"replace\"", deny, StringComparison.Ordinal);
        Assert.Contains("toolName = [\"google_web_search\", \"web_fetch\"]\ndecision = \"allow\"", on, StringComparison.Ordinal);

        var excludedOff = GeminiRunner.BuildSettings(Request(Path.GetTempPath()).AcadMcp, excludeToolsInSettings: true)["tools"]!["exclude"]!.AsArray().Select(a => (string?)a).ToList();
        Assert.Contains("google_web_search", excludedOff);
        var excludedOn = GeminiRunner.BuildSettings(Request(Path.GetTempPath()).AcadMcp, excludeToolsInSettings: true, webResearch: true)["tools"]!["exclude"]!.AsArray().Select(a => (string?)a).ToList();
        Assert.DoesNotContain("google_web_search", excludedOn);
        Assert.DoesNotContain("web_fetch", excludedOn);
        Assert.Contains("run_shell_command", excludedOn);
        Assert.Contains("write_file", excludedOn);
    }

    [Fact]
    public void Gemini_invocation_writes_the_policy_of_the_switch()
    {
        using var dir = new TestDirectory();
        var command = new ResolvedCommand("node.exe", ["gemini.js"], "gemini.cmd");
        var off = GeminiRunner.BuildInvocation(command, Request(dir.Path), null);
        Assert.DoesNotContain("decision = \"allow\"", File.ReadAllText(ValueAfter(off.Launch.Arguments, "--policy")), StringComparison.Ordinal);

        var on = GeminiRunner.BuildInvocation(command, Request(dir.Path) with { WebResearch = true }, null);
        Assert.Contains("decision = \"allow\"", File.ReadAllText(ValueAfter(on.Launch.Arguments, "--policy")), StringComparison.Ordinal);
        Assert.Equal("default", ValueAfter(on.Launch.Arguments, "--approval-mode"));
    }

    [Fact]
    public void Instructions_explain_web_research_only_when_on_and_stay_within_the_limits()
    {
        Assert.True(CopilotInstructions.Default.Length < 15_500, $"{CopilotInstructions.Default.Length} characters");
        Assert.Contains("No web unless TURN SETTINGS say WEB RESEARCH is ON", CopilotInstructions.Default, StringComparison.Ordinal);
        Assert.Contains("globe \"Web\" button", CopilotInstructions.Default, StringComparison.Ordinal);
        Assert.DoesNotContain(CopilotInstructions.WebResearchNote, CopilotInstructions.Compose(null, null, TurnOptions.Default), StringComparison.Ordinal);

        var options = new TurnOptions { WebResearch = true };
        Assert.False(options.IsDefault);
        var on = CopilotInstructions.Compose("AutoCAD Architecture 2027", new string('k', 6_000), options with { TutorMode = true, AskBeforeDrawing = false });
        Assert.True(on.Length <= CopilotInstructions.MaxComposedLength, $"{on.Length} characters");
        Assert.Contains(CopilotInstructions.WebResearchNote, on, StringComparison.Ordinal);
        foreach (var part in new[] { "3-6 relevant sources", "2-3 distinct layout ideas", "Vastu", "source links", "ask_user", "copyright", "minimum sizes" })
        {
            Assert.Contains(part, CopilotInstructions.WebResearchNote, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Setting_round_trips_and_reaches_the_turn_options()
    {
        using var dir = new TestDirectory();
        var store = new CopilotSettingsStore(Path.Combine(dir.Path, "settings.json"));
        Assert.False(store.Load().WebResearch);
        Assert.False(store.Load().Features.WebResearch);

        var features = store.Load().Features with { WebResearch = true };
        store.Save(store.Load().With(features));
        var loaded = store.Load();
        Assert.True(loaded.WebResearch);
        Assert.True(loaded.Features.WebResearch);
        Assert.True(loaded.Features.TurnOptions.WebResearch);
        Assert.Contains("\"webResearch\": true", File.ReadAllText(store.Path), StringComparison.OrdinalIgnoreCase);
        Assert.False(loaded.Features.SameAs(loaded.Features with { WebResearch = false }));

        store.Save(loaded.With(loaded.Features with { WebResearch = false }));
        Assert.False(store.Load().WebResearch);
    }

    private static List<string> Overrides(IReadOnlyList<string> arguments) =>
        [.. arguments.Select((a, i) => (a, i)).Where(x => x.a == "-c" && x.i + 1 < arguments.Count).Select(x => arguments[x.i + 1])];
}
