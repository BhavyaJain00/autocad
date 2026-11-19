using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Drafting;

/// <summary>What the copilot may do with a command it was asked to type.</summary>
public enum CommandDecision
{
    /// <summary>Typed without asking.</summary>
    Allowed,

    /// <summary>Typed only after the user approves it in the panel (saving, exporting, plotting, purging, ERASE ALL...).</summary>
    NeedsApproval,

    /// <summary>Never typed (other programs, program code, AutoLISP, opening/closing files, dialog-only commands...).</summary>
    Blocked,
}

/// <summary>The policy decision for a command or an input, with a human-readable reason.</summary>
public sealed record CommandVerdict
{
    /// <summary>The decision.</summary>
    public required CommandDecision Decision { get; init; }

    /// <summary>What the decision is about: the canonical command name ("SHELL", "ERASE") or a short input description.</summary>
    public required string Subject { get; init; }

    /// <summary>Why, in plain words ("runs another program..."); null when allowed.</summary>
    public string? Reason { get; init; }

    /// <summary>True when allowed.</summary>
    public bool IsAllowed => Decision == CommandDecision.Allowed;

    /// <summary>True when blocked.</summary>
    public bool IsBlocked => Decision == CommandDecision.Blocked;

    /// <summary>True when the user must approve it first.</summary>
    public bool NeedsApproval => Decision == CommandDecision.NeedsApproval;

    /// <summary>"SHELL: runs another program..." (or just the subject when allowed).</summary>
    public string Describe() => Reason is null ? Subject : $"{Subject}: {Reason}";

    internal static CommandVerdict Allow(string subject) => new() { Decision = CommandDecision.Allowed, Subject = subject };

    internal static CommandVerdict Block(string subject, string reason) => new() { Decision = CommandDecision.Blocked, Subject = subject, Reason = reason };

    internal static CommandVerdict Approve(string subject, string reason) => new() { Decision = CommandDecision.NeedsApproval, Subject = subject, Reason = reason };
}

/// <summary>A command as typed, split into prefixes and name, with its alias resolved.</summary>
public sealed record ParsedCommand
{
    /// <summary>The command as given, trimmed ("_.LA").</summary>
    public required string Typed { get; init; }

    /// <summary>Leading prefix characters other than '-' ("_.", "'", "+"), in their original order.</summary>
    public required string Prefix { get; init; }

    /// <summary>True when a '-' was among the leading characters (the command-line version of a command).</summary>
    public required bool Dash { get; init; }

    /// <summary>The name as typed, upper case, without prefixes ("LA").</summary>
    public required string Name { get; init; }

    /// <summary>The command the name stands for after alias resolution, without '-' ("LAYER").</summary>
    public required string Canonical { get; init; }

    /// <summary>True when the resolved command is the command-line ('-') version ("-LA" or "LO" -&gt; "-LAYOUT").</summary>
    public required bool CanonicalDash { get; init; }

    /// <summary>True when the command was typed transparently (with a leading apostrophe).</summary>
    public bool Transparent => Prefix.Contains('\'', StringComparison.Ordinal);

    /// <summary>True when <see cref="Name"/> is a well-formed AutoCAD command name.</summary>
    public bool IsWellFormed => CommandPolicy.IsCommandNameText(Name);
}

/// <summary>
/// Pure command policy for run_commands (no AutoCAD references). Every step's command and every input is checked
/// BEFORE anything is typed:
/// <list type="bullet">
/// <item><b>Blocked</b> (never typed): SHELL/SH and the operating-system commands of acad.pgp, APPLOAD, NETLOAD, ARX,
/// VBA*, ACTIVEX*, SCRIPT, VLISP, WEBLOAD, CUI/menu loading, QUIT, EXIT, CLOSE, CLOSEALL, OPEN, NEW, QNEW, RECOVER and
/// other file-opening commands, UNDO/U (the copilot has its own undo tool), security system variables, dialog-only
/// commands with no command-line version, and any command or input starting with "(" or "!" (AutoLISP).</item>
/// <item><b>Needs approval</b>: SAVE, QSAVE, SAVEAS, WBLOCK, EXPORT*, PLOT, PUBLISH, PURGE, LAYDEL, LAYMRG, AUDIT,
/// other file writers, and ERASE (or CUTCLIP) with the selection keyword ALL.</item>
/// </list>
/// Names are normalised first (leading _ . - + and the transparent ' stripped, upper case) and aliases resolved
/// (built-in default acad.pgp aliases plus the user's acad.pgp when given).
/// </summary>
public static partial class CommandPolicy
{
    private const string LispReason = "AutoLISP expressions are not allowed; type AutoCAD commands and plain values";

    /// <summary>
    /// The normalised command name: trimmed, leading _ . - + and ' stripped, upper case ("_.-layer" -&gt; "LAYER",
    /// "'ZOOM" -&gt; "ZOOM"). Empty for null or blank input.
    /// </summary>
    public static string NormalizeName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        return command.Trim().TrimStart('_', '.', '-', '+', '\'').ToUpperInvariant();
    }

    /// <summary>True when <paramref name="text"/> starts (after leading spaces) with "(" or "!", i.e. would be evaluated as AutoLISP.</summary>
    public static bool IsAutoLisp(string? text)
    {
        var trimmed = text?.TrimStart();
        return !string.IsNullOrEmpty(trimmed) && trimmed[0] is '(' or '!';
    }

    /// <summary>True when <paramref name="name"/> (without prefixes) looks like an AutoCAD command name: a letter or digit followed by letters, digits, _ - or $ (at most 64 characters).</summary>
    public static bool IsCommandNameText(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 64 && CommandNamePattern().IsMatch(name);

    /// <summary>Splits <paramref name="command"/> into prefixes and name and resolves aliases.</summary>
    public static ParsedCommand Parse(string command, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        aliases ??= CommandAliases.Default;
        var typed = command.Trim();
        var index = 0;
        var prefix = new System.Text.StringBuilder();
        var dash = false;
        while (index < typed.Length && typed[index] is '_' or '.' or '-' or '+' or '\'')
        {
            if (typed[index] == '-')
            {
                dash = true;
            }
            else
            {
                prefix.Append(typed[index]);
            }

            index++;
        }

        var name = typed[index..].ToUpperInvariant();
        var lookup = dash ? "-" + name : name;
        var resolved = aliases.Resolve(lookup) ?? lookup;
        var canonicalDash = resolved.StartsWith('-');
        return new ParsedCommand
        {
            Typed = typed,
            Prefix = prefix.ToString(),
            Dash = dash,
            Name = name,
            Canonical = resolved.TrimStart('-'),
            CanonicalDash = canonicalDash,
        };
    }

    /// <summary>
    /// Classifies a step's command (as typed, with prefixes). A dialog command that has a command-line version is
    /// Blocked here with a hint; <see cref="CommandLineVersion"/> gives the version to type instead.
    /// </summary>
    public static CommandVerdict ClassifyCommand(string command, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        aliases ??= CommandAliases.Default;
        if (IsAutoLisp(command))
        {
            return CommandVerdict.Block("AutoLISP", LispReason);
        }

        var parsed = Parse(command, aliases);
        if (!parsed.IsWellFormed)
        {
            return CommandVerdict.Block(
                string.IsNullOrEmpty(parsed.Name) ? "(empty)" : parsed.Name,
                "is not an AutoCAD command name; give one command such as LINE, -LAYER or DIMLINEAR");
        }

        return ClassifyParsed(parsed, aliases);
    }

    /// <summary>
    /// Classifies one input of a step on its own: AutoLISP ("(" or "!"), a transparent command ("'NAME", classified
    /// like a command) or a protected system variable name. Anything else is Allowed (its effect depends on the prompt;
    /// the drafting engine checks spaces against the live prompt).
    /// </summary>
    public static CommandVerdict ClassifyInput(string input, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        aliases ??= CommandAliases.Default;
        var trimmed = input.Trim();
        if (IsAutoLisp(trimmed))
        {
            return CommandVerdict.Block("AutoLISP input", LispReason);
        }

        if (trimmed.StartsWith('\''))
        {
            var parsed = Parse(trimmed, aliases);
            if (parsed.IsWellFormed)
            {
                var verdict = ClassifyParsed(parsed, aliases);
                if (!verdict.IsAllowed)
                {
                    return verdict with { Subject = "'" + verdict.Subject };
                }
            }
        }

        var bare = NormalizeName(trimmed);
        if (CommandTables.ProtectedSystemVariables.Contains(bare))
        {
            return CommandVerdict.Block(bare, "changes AutoCAD's security settings");
        }

        // A lone word that names a forbidden command (SHELL, APPLOAD, OPEN, QUIT...) is no sensible answer to a prompt,
        // and it would start that command if the prompt had already ended. Words with spaces are text and stay allowed.
        if (bare.Length > 0 && !trimmed.Any(char.IsWhiteSpace) && IsCommandNameText(bare) && ClassifyRunningCommand(bare, aliases).IsBlocked)
        {
            return CommandVerdict.Block(bare, $"as an answer it could start the forbidden command {bare}; answer with a value, or write it as text with other words");
        }

        return CommandVerdict.Allow(trimmed.Length == 0 ? "Enter" : trimmed);
    }

    /// <summary>
    /// Every policy finding of a step (command and inputs) that is not Allowed, most severe first; empty when the
    /// whole step is allowed. Adds the ERASE ALL rule: ERASE or CUTCLIP with an input "ALL" needs approval.
    /// </summary>
    public static IReadOnlyList<CommandVerdict> CheckStep(string command, IReadOnlyList<string> inputs, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(inputs);
        aliases ??= CommandAliases.Default;
        var findings = new List<CommandVerdict>();
        var commandVerdict = ClassifyCommand(command, aliases);
        if (!commandVerdict.IsAllowed)
        {
            findings.Add(commandVerdict);
        }

        foreach (var input in inputs)
        {
            var verdict = ClassifyInput(input, aliases);
            if (!verdict.IsAllowed)
            {
                findings.Add(verdict);
            }
        }

        if (!IsAutoLisp(command))
        {
            var parsed = Parse(command, aliases);
            if (CommandTables.EraseCommands.Contains(parsed.Canonical) && inputs.Any(IsSelectAll))
            {
                findings.Add(CommandVerdict.Approve(parsed.Canonical + " ALL", "erases every object in the drawing (all layouts)"));
            }
        }

        return findings.OrderByDescending(f => f.Decision).ToList();
    }

    /// <summary>The most severe verdict for a whole step (Allowed when <see cref="CheckStep"/> finds nothing).</summary>
    public static CommandVerdict ClassifyStep(string command, IReadOnlyList<string> inputs, CommandAliases? aliases = null)
    {
        var findings = CheckStep(command, inputs, aliases);
        return findings.Count > 0 ? findings[0] : CommandVerdict.Allow(NormalizeName(command));
    }

    /// <summary>
    /// The command-line version to type instead of a dialog or palette command, keeping the other prefixes:
    /// "LAYER" -&gt; "-LAYER", "_.LA" -&gt; "_.-LAYER", "BH" -&gt; "-HATCH", "MTEXT" -&gt; "-MTEXT". Null when the command has
    /// no such version or is already the command-line version.
    /// </summary>
    public static string? CommandLineVersion(string command, CommandAliases? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsAutoLisp(command))
        {
            return null;
        }

        var parsed = Parse(command, aliases);
        if (parsed.CanonicalDash || !parsed.IsWellFormed || !CommandTables.CommandLineVersions.TryGetValue(parsed.Canonical, out var version))
        {
            return null;
        }

        return parsed.Prefix + version;
    }

    /// <summary>True when an input is the selection keyword ALL ("ALL", "_ALL", case-insensitive).</summary>
    public static bool IsSelectAll(string? input) =>
        input is not null && string.Equals(input.Trim().TrimStart('_'), "ALL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies a command AutoCAD is about to run while a copilot call is in progress (the global command name of
    /// DocumentCollection.DocumentLockModeChanged, which may carry a leading '#' or other prefixes), on the security rules
    /// only: Blocked for other programs, program code, opening/closing files and security settings; NeedsApproval for
    /// the approval commands; Allowed otherwise. Dialog rules and session-only blocks (UNDO, U, MULTIPLE...) are ignored,
    /// because AutoCAD and running commands invoke such commands internally and the copilot's undo group uses UNDO.
    /// </summary>
    public static CommandVerdict ClassifyRunningCommand(string? globalCommandName, CommandAliases? aliases = null)
    {
        aliases ??= CommandAliases.Default;
        var name = NormalizeName(globalCommandName?.Trim().TrimStart('#'));
        if (name.Length == 0 || !IsCommandNameText(name))
        {
            return CommandVerdict.Allow(name.Length == 0 ? "(none)" : name);
        }

        return ClassifyParsed(Parse(name, aliases), aliases, running: true);
    }

    private static CommandVerdict ClassifyParsed(ParsedCommand parsed, CommandAliases aliases, bool running = false)
    {
        var name = parsed.Canonical;
        if (aliases.IsExternalCommand(parsed.Name) || aliases.IsExternalCommand(name))
        {
            return CommandVerdict.Block(parsed.Name, "is an operating-system command defined in acad.pgp; it runs another program");
        }

        if (CommandTables.Blocked.TryGetValue(name, out var blocked) && !(running && CommandTables.NotVetoedWhileRunning.Contains(name)))
        {
            return CommandVerdict.Block(name, blocked);
        }

        foreach (var (prefix, reason) in CommandTables.BlockedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return CommandVerdict.Block(name, reason);
            }
        }

        if (!parsed.CanonicalDash && !running)
        {
            if (CommandTables.DialogOnly.TryGetValue(name, out var dialog))
            {
                return CommandVerdict.Block(name, dialog);
            }

            if (CommandTables.CommandLineVersions.TryGetValue(name, out var version))
            {
                return CommandVerdict.Block(name, $"opens a dialog box, palette or interactive mode; type {version} (its command-line version) instead");
            }
        }

        if (CommandTables.NeedsApproval.TryGetValue(name, out var approval))
        {
            return CommandVerdict.Approve(Display(parsed), approval);
        }

        foreach (var (prefix, reason) in CommandTables.NeedsApprovalPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return CommandVerdict.Approve(Display(parsed), reason);
            }
        }

        return CommandVerdict.Allow(Display(parsed));
    }

    private static string Display(ParsedCommand parsed) => parsed.CanonicalDash ? "-" + parsed.Canonical : parsed.Canonical;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_\\-$]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CommandNamePattern();
}
