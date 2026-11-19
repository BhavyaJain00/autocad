using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using AcadCopilot.Core.Agents;

namespace AcadCopilot.Plugin.Acad;

/// <summary>
/// Finds the MCP bridge (AcadCopilotMcp.exe) shipped next to the plug-in in its bridge\ sub-folder (the build copies it
/// there) and builds the launch information the CLI runners pass to Claude Code / Codex / Gemini for each turn.
/// </summary>
public static class BridgeLocator
{
    /// <summary>Environment variable that points at another AcadCopilotMcp.exe (development).</summary>
    public const string OverrideEnvironmentVariable = "ACADCOPILOT_BRIDGE";

    /// <summary>File name of the bridge executable.</summary>
    public const string ExecutableName = "AcadCopilotMcp.exe";

    /// <summary>Folder of AcadCopilot.Plugin.dll.</summary>
    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(BridgeLocator).Assembly.Location) ?? AppContext.BaseDirectory;

    /// <summary>Path the bridge is expected at: the override when set, else &lt;plug-in folder&gt;\bridge\AcadCopilotMcp.exe.</summary>
    public static string ExpectedPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(overridden)
                ? Path.GetFullPath(overridden.Trim().Trim('"'))
                : Path.Combine(PluginDirectory, "bridge", ExecutableName);
        }
    }

    /// <summary>True when the bridge executable exists.</summary>
    public static bool Exists => File.Exists(ExpectedPath);

    /// <summary>
    /// The .NET installation AutoCAD itself runs on (the folder holding host\fxr and shared\), so the framework-dependent
    /// bridge starts on the same runtime; null when it cannot be determined (the bridge then uses the normal lookup).
    /// </summary>
    public static string? DotnetRoot
    {
        get
        {
            try
            {
                // ...\dotnet\shared\Microsoft.NETCore.App\10.0.x\
                var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.FullName;
                return root is not null && Directory.Exists(Path.Combine(root, "host", "fxr")) ? root : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The bridge launch for one panel turn: connect to AutoCAD process <paramref name="acadPid"/> and expose the panel
    /// tools (ask_user, update_plan).
    /// </summary>
    /// <exception cref="FileNotFoundException">The bridge is not installed next to the plug-in.</exception>
    public static McpServerLaunch CreateLaunch(int acadPid)
    {
        var path = ExpectedPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The AutoCAD MCP bridge was not found at '{path}'. Reinstall AI Copilot (the bridge folder is missing).", path);
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (DotnetRoot is { } dotnetRoot)
        {
            environment["DOTNET_ROOT"] = dotnetRoot;
        }

        return new McpServerLaunch
        {
            Command = path,
            Arguments = ["--pid", acadPid.ToString(CultureInfo.InvariantCulture), "--panel"],
            Environment = environment,
        };
    }
}
