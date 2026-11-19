using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Agents;

/// <summary>
/// A command resolved to something <see cref="System.Diagnostics.Process"/> can start directly (no cmd.exe):
/// run <see cref="FileName"/> with <see cref="PrefixArguments"/> followed by your own arguments.
/// </summary>
/// <param name="FileName">Executable to start (claude.exe, node.exe, ...).</param>
/// <param name="PrefixArguments">Arguments that must come first (e.g. the CLI's .js entry point for node).</param>
/// <param name="SourcePath">What was found on disk (e.g. ...\npm\codex.cmd).</param>
public sealed record ResolvedCommand(string FileName, IReadOnlyList<string> PrefixArguments, string SourcePath)
{
    /// <summary>Builds a <see cref="ProcessLaunch"/> with <paramref name="arguments"/> appended.</summary>
    public ProcessLaunch ToLaunch(IEnumerable<string> arguments, string? workingDirectory = null) => new()
    {
        FileName = FileName,
        Arguments = [.. PrefixArguments, .. arguments],
        WorkingDirectory = workingDirectory,
    };
}

/// <summary>
/// Finds CLIs installed with npm -g on Windows ("claude", "codex", "gemini") even when AutoCAD's PATH is stale:
/// searches PATH, %APPDATA%\npm and %ProgramFiles%\nodejs. npm ".cmd" shims are unwrapped to the real target
/// (a native .exe, or node.exe + the package's .js entry point) so arguments never pass through cmd.exe quoting.
/// </summary>
public static partial class CommandLocator
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    /// <summary>Resolves <paramref name="command"/> (bare name or path); null when not found.</summary>
    public static ResolvedCommand? Resolve(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? FromFile(command) : null;
        }

        foreach (var directory in SearchDirectories())
        {
            var hasExtension = Path.HasExtension(command);
            foreach (var extension in hasExtension ? [string.Empty] : Extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return FromFile(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>Directories searched, in order (PATH first, then the usual npm/node folders), without duplicates.</summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length > 0 && seen.Add(trimmed) && Directory.Exists(trimmed))
            {
                result.Add(trimmed);
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            Add(directory);
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        return result;
    }

    /// <summary>Resolves a concrete file (exe or npm .cmd shim).</summary>
    public static ResolvedCommand FromFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return UnwrapShim(path) ?? new ResolvedCommand("cmd.exe", ["/d", "/c", path], path);
        }

        return new ResolvedCommand(path, [], path);
    }

    /// <summary>
    /// Reads an npm cmd-shim and returns its real target, or null when the file does not look like one.
    /// Handles both shim styles: "%dp0%\node_modules\pkg\bin\tool.exe" %* and node "%dp0%\node_modules\pkg\cli.js" %*.
    /// </summary>
    public static ResolvedCommand? UnwrapShim(string shimPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(shimPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var shimDirectory = Path.GetDirectoryName(shimPath)!;
        var targets = ShimTarget().Matches(text)
            .Select(m => m.Groups["path"].Value)
            .Where(p => !p.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (targets.Count == 0)
        {
            return null;
        }

        var target = Path.GetFullPath(Path.Combine(shimDirectory, targets[^1]));
        if (!File.Exists(target))
        {
            return null;
        }

        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedCommand(target, [], shimPath);
        }

        var localNode = Path.Combine(shimDirectory, "node.exe");
        var node = File.Exists(localNode) ? localNode : Resolve("node.exe")?.FileName;
        return node is null ? null : new ResolvedCommand(node, [target], shimPath);
    }

    [GeneratedRegex("\"%dp0%\\\\(?<path>[^\"]+?\\.(?:exe|js|cjs|mjs))\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShimTarget();
}
