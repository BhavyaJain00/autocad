namespace AcadCopilot.Core.Drafting;

/// <summary>
/// Command aliases and external (operating-system) commands, as defined by acad.pgp. <see cref="Default"/> holds the
/// aliases of the default acad.pgp that matter to <see cref="CommandPolicy"/>; <see cref="FromPgp"/> adds the
/// user's own file so a custom alias of a blocked command (for example <c>XX,*SHELL</c>) and every external command
/// (<c>DEL, DEL, 8, File to delete: ,</c>) are caught too. Immutable and thread-safe.
/// </summary>
public sealed class CommandAliases
{
    private readonly Dictionary<string, string> _aliases;
    private readonly HashSet<string> _externalCommands;

    private CommandAliases(Dictionary<string, string> aliases, HashSet<string> externalCommands)
    {
        _aliases = aliases;
        _externalCommands = externalCommands;
    }

    /// <summary>The built-in aliases (default acad.pgp entries that lead to blocked, approval or dialog commands).</summary>
    public static CommandAliases Default { get; } = new(
        new Dictionary<string, string>(CommandTables.DefaultAliases, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Number of aliases known.</summary>
    public int AliasCount => _aliases.Count;

    /// <summary>Number of external (operating-system) commands known from the pgp file.</summary>
    public int ExternalCommandCount => _externalCommands.Count;

    /// <summary>
    /// The built-in aliases plus every alias and external command defined in <paramref name="pgpText"/> (the text of
    /// acad.pgp). Lines starting with ';' are comments. Aliases are <c>NAME,*COMMAND</c>; external commands are
    /// <c>NAME,[OS command],flags,[*]prompt,</c>. Entries of the file win over the built-in ones.
    /// </summary>
    public static CommandAliases FromPgp(string? pgpText)
    {
        var aliases = new Dictionary<string, string>(CommandTables.DefaultAliases, StringComparer.OrdinalIgnoreCase);
        var external = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(pgpText))
        {
            return new CommandAliases(aliases, external);
        }

        foreach (var rawLine in pgpText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            var fields = line.Split(',');
            var name = fields[0].Trim().ToUpperInvariant();
            if (!CommandPolicy.IsCommandNameText(name.TrimStart('-')))
            {
                continue;
            }

            if (fields.Length >= 2 && fields[1].TrimStart().StartsWith('*'))
            {
                var target = fields[1].Trim()[1..].Trim().ToUpperInvariant();
                if (CommandPolicy.IsCommandNameText(target.TrimStart('-')))
                {
                    aliases[name] = target;
                }

                continue;
            }

            if (fields.Length >= 3 && int.TryParse(fields[2].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                external.Add(name);
            }
        }

        return new CommandAliases(aliases, external);
    }

    /// <summary>
    /// The command an alias stands for (it may start with '-', e.g. "LO" -&gt; "-LAYOUT"), or null when
    /// <paramref name="name"/> is not an alias. <paramref name="name"/> is upper case without the prefixes _ . + '
    /// (a leading '-' is part of the alias: "-LA" -&gt; "-LAYER").
    /// </summary>
    public string? Resolve(string name)
    {
        if (_aliases.TryGetValue(name, out var target))
        {
            return target;
        }

        if (name.StartsWith('-') && _aliases.TryGetValue(name[1..], out var plain))
        {
            return plain.StartsWith('-') ? plain : "-" + plain;
        }

        return null;
    }

    /// <summary>True when <paramref name="name"/> is an external (operating-system) command of the pgp file.</summary>
    public bool IsExternalCommand(string name) => _externalCommands.Contains(name.TrimStart('-'));
}
