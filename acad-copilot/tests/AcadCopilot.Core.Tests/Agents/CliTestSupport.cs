using AcadCopilot.Core.Agents;
using AcadCopilot.Tests.Fakes;

namespace AcadCopilot.Core.Tests.Agents;

/// <summary>Helpers for the CLI runner tests: requests, sample parsing and fake CLIs built from cmd.exe.</summary>
internal static class CliTestSupport
{
    /// <summary>A bridge path with spaces, like a real install under Program Files.</summary>
    public const string BridgePath = @"C:\Program Files\AutoCAD Copilot\bridge\AcadCopilotMcp.exe";

    /// <summary>A prompt with quotes, shell metacharacters, newlines and non-ASCII text.</summary>
    public const string NastyPrompt = "Make a \"big\" room — 5 m × 4 m 🏠 & del *.* | echo %PATH% ^ $(rm -rf /) `x`\nsecond line: Größe";

    public static TurnRequest Request(
        string workingDirectory,
        string prompt = "Hello",
        string? model = null,
        string? resume = null,
        IReadOnlyList<string>? attachments = null,
        string pid = "1234") => new()
    {
        Prompt = prompt,
        Attachments = attachments ?? [],
        Model = model,
        ResumeSessionId = resume,
        WorkingDirectory = workingDirectory,
        AcadMcp = new McpServerLaunch
        {
            Command = BridgePath,
            Arguments = ["--pid", pid, "--panel"],
            Environment = new Dictionary<string, string> { ["ACADCOPILOT_HOME"] = "C:\\Users\\Zoë\\Copilot \"home\"" },
        },
    };

    public static List<AgentEvent> ParseAll(CliStreamParser parser, string sample)
    {
        var events = new List<AgentEvent>();
        foreach (var line in sample.Split('\n'))
        {
            events.AddRange(parser.ParseLine(line.TrimEnd('\r')));
        }

        return events;
    }

    /// <summary>Text after a flag ("--model" -> "opus").</summary>
    public static string ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Flag {flag} missing in: {string.Join(" ", arguments)}");
        return arguments[index + 1];
    }

    /// <summary>A fake CLI that prints <paramref name="file"/> to stdout and ignores the runner's arguments.</summary>
    public static ResolvedCommand PrintFile(string file) => new("cmd.exe", ["/d", "/c", "type", file, "&", "rem"], "fake-cli.cmd");

    /// <summary>A fake CLI running a cmd.exe command line (arguments appended by the runner are commented out).</summary>
    public static ResolvedCommand Cmd(params string[] command) => new("cmd.exe", ["/d", "/c", .. command, "&", "rem"], "fake-cli.cmd");

    public static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events, CancellationToken cancellationToken = default)
    {
        var list = new List<AgentEvent>();
        await foreach (var agentEvent in events.WithCancellation(cancellationToken))
        {
            list.Add(agentEvent);
        }

        return list;
    }

    public static string WriteSample(TestDirectory directory, string name, string sample)
    {
        var path = directory.File(name);
        File.WriteAllText(path, sample.ReplaceLineEndings("\n") + "\n", new System.Text.UTF8Encoding(false));
        return path;
    }
}
