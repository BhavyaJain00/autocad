using ModelContextProtocol.Server;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.McpBridge;

// AcadCopilotMcp.exe - stdio MCP server. stdout carries MCP JSON-RPC only; diagnostics go to the log file.
BridgeOptions options;
try
{
    options = BridgeOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    await Console.Error.WriteLineAsync(ex.Message);
    await Console.Error.WriteLineAsync(BridgeOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(BridgeOptions.Usage);
    return 0;
}

if (options.ShowVersion)
{
    Console.WriteLine(BridgeServer.Version);
    return 0;
}

if (!string.Equals(options.LogFile, "none", StringComparison.OrdinalIgnoreCase))
{
    CopilotLog.Initialize(options.LogFile ?? Path.Combine(AppPaths.Logs, "bridge.log"));
}

CopilotLog.Info($"Bridge {BridgeServer.Version} starting (pid {Environment.ProcessId}, target pid {options.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "newest"}, panel {options.Panel}, session {options.SessionId ?? "none"}).");
try
{
    await using var link = new AcadLink(options);
    var service = new BridgeToolService(link, options.Panel);
    var transport = new StdioServerTransport(BridgeServer.ServerName);
    await using var server = BridgeServer.Create(transport, service, link);
    await server.RunAsync(CancellationToken.None);
    CopilotLog.Info("Bridge stopped (client closed stdin).");
    return 0;
}
catch (Exception ex)
{
    CopilotLog.Error("Bridge crashed", ex);
    await Console.Error.WriteLineAsync("AcadCopilotMcp failed: " + ex.Message);
    return 1;
}
