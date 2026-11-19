using System.Diagnostics;
using System.Text;

namespace AcadCopilot.Core.Agents;

/// <summary>
/// Opens a VISIBLE terminal window running a CLI's own login command. This is the only window the add-in
/// opens, and only when the user clicks "Sign in". The terminal stays open (cmd /k) so the user can read it.
/// </summary>
public static class TerminalLauncher
{
    /// <summary>Opens a console window running <paramref name="command"/>; returns false when the CLI is not installed.</summary>
    /// <param name="command">The login command (from <see cref="IAgentRunner.GetSignInCommand"/>).</param>
    /// <param name="title">Window title.</param>
    /// <param name="workingDirectory">Working directory of the terminal.</param>
    public static bool OpenSignInTerminal(CommandLine command, string title, string? workingDirectory = null)
    {
        var resolved = CommandLocator.Resolve(command.FileName);
        if (resolved is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = true,
            Arguments = BuildCmdArguments(resolved, command.Arguments, title),
            WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    /// <summary>Builds the cmd.exe argument string: /s /k ""title" &amp;&amp; "exe" "args""..</summary>
    internal static string BuildCmdArguments(ResolvedCommand resolved, IEnumerable<string> arguments, string title)
    {
        var inner = new StringBuilder();
        inner.Append("title ").Append(SanitizeTitle(title)).Append(" && ");
        inner.Append(Quote(resolved.FileName));
        foreach (var argument in resolved.PrefixArguments.Concat(arguments))
        {
            inner.Append(' ').Append(Quote(argument));
        }

        return "/s /k \"" + inner + "\"";
    }

    private static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '\\' or ':' or '/' or '@' or '=')
            ? value
            : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string SanitizeTitle(string title) =>
        new(title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.').ToArray());
}
