using System.Globalization;
using AcadCopilot.Core.Ipc;

namespace AcadCopilot.McpBridge;

/// <summary>Command-line options of AcadCopilotMcp.exe.</summary>
public sealed record BridgeOptions
{
    /// <summary>Usage text printed by --help.</summary>
    public const string Usage =
        """
        AcadCopilotMcp - MCP server (stdio) that controls Autodesk AutoCAD 2027 through the AI Copilot add-in.

        Usage: AcadCopilotMcp [--pid <acadPid>] [--panel] [--session <id>] [--instances-dir <dir>] [--connect-timeout <seconds>] [--log <file>|none]

          --pid <n>               Connect to the AutoCAD process <n> (default: the newest running AutoCAD with the add-in).
          --panel                 Also expose ask_user and update_plan, answered in the AI Copilot panel.
                                  The panel passes this; leave it out when using the server from your own
                                  Claude Desktop / Claude Code / Codex / Gemini session.
          --session <id>          The panel chat tab this server works for (the panel passes it): questions, the task
                                  list and approvals appear in that tab, and drawing tools act on that tab's drawing.
          --instances-dir <dir>   Folder with discovery files (default %LOCALAPPDATA%\AcadCopilot\instances).
          --connect-timeout <s>   Pipe connect timeout in seconds (default 3).
          --log <file>|none       Log file (default %LOCALAPPDATA%\AcadCopilot\logs\bridge.log).
          --version               Print the version and exit.
          --help                  Print this help and exit.

        Example MCP configuration (Claude Desktop / Claude Code):
          { "mcpServers": { "acad": { "command": "<add-in folder>\\bridge\\AcadCopilotMcp.exe", "args": [] } } }
        """;

    /// <summary>AutoCAD process to connect to; null = newest live instance.</summary>
    public int? Pid { get; init; }

    /// <summary>Expose the panel-only tools ask_user and update_plan.</summary>
    public bool Panel { get; init; }

    /// <summary>Panel chat session (tab) id, sent with every call to the add-in; null = not a panel tab.</summary>
    public string? SessionId { get; init; }

    /// <summary>Discovery folder override (tests); null = default.</summary>
    public string? InstancesDirectory { get; init; }

    /// <summary>Pipe connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Log file path; null = default location; "none" disables logging.</summary>
    public string? LogFile { get; init; }

    /// <summary>--help was given.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>--version was given.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>Parses the command line; throws <see cref="ArgumentException"/> with a readable message on bad input.</summary>
    public static BridgeOptions Parse(IReadOnlyList<string> args)
    {
        var options = new BridgeOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--pid":
                    options = options with { Pid = int.Parse(Next(args, ref i, arg), NumberStyles.None, CultureInfo.InvariantCulture) };
                    break;
                case "--panel":
                    options = options with { Panel = true };
                    break;
                case "--session":
                    var session = Next(args, ref i, arg);
                    if (!RpcSessions.IsValid(session))
                    {
                        throw new ArgumentException($"--session must be 1-{RpcSessions.MaxLength} letters, digits, '-' or '_'.");
                    }

                    options = options with { SessionId = session };
                    break;
                case "--instances-dir":
                    options = options with { InstancesDirectory = Next(args, ref i, arg) };
                    break;
                case "--connect-timeout":
                    var seconds = double.Parse(Next(args, ref i, arg), NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (seconds <= 0)
                    {
                        throw new ArgumentException("--connect-timeout must be positive.");
                    }

                    options = options with { ConnectTimeout = TimeSpan.FromSeconds(seconds) };
                    break;
                case "--log":
                    options = options with { LogFile = Next(args, ref i, arg) };
                    break;
                case "--help" or "-h" or "/?":
                    options = options with { ShowHelp = true };
                    break;
                case "--version" or "-v":
                    options = options with { ShowVersion = true };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'. Use --help.");
            }
        }

        return options;
    }

    private static string Next(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count)
        {
            throw new ArgumentException($"{name} needs a value.");
        }

        i++;
        return args[i];
    }
}
