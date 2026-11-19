namespace AcadCopilot.Core.Agents;

/// <summary>
/// Normalises tool names reported by the CLIs so the panel can show "create_wall" instead of
/// "mcp__acad__create_wall" (Claude Code) or other prefixed forms.
/// </summary>
public static class ToolNameHelper
{
    /// <summary>
    /// Splits a CLI tool name into (server, bare name). Recognises "mcp__&lt;server&gt;__&lt;tool&gt;" (Claude Code, Codex)
    /// and "&lt;server&gt;__&lt;tool&gt;" (Gemini CLI's server-qualified form) when &lt;server&gt; equals
    /// <paramref name="serverName"/>. Other names are returned unchanged with a null server.
    /// </summary>
    public static (string? Server, string Name) Split(string rawName, string serverName = "acad")
    {
        ArgumentNullException.ThrowIfNull(rawName);
        var prefixes = new[] { $"mcp__{serverName}__", $"{serverName}__", $"mcp_{serverName}_" };
        foreach (var prefix in prefixes)
        {
            if (rawName.StartsWith(prefix, StringComparison.Ordinal) && rawName.Length > prefix.Length)
            {
                return (serverName, rawName[prefix.Length..]);
            }
        }

        return (null, rawName);
    }
}
