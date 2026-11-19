using System.IO;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Drafting;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>
/// Loads the user's acad.pgp (found on AutoCAD's support path) into <see cref="CommandAliases"/>, so custom aliases of
/// blocked commands and every external (operating-system) command it defines are caught by the policy. Cached by path
/// and file time; falls back to the built-in default aliases when the file cannot be found or read. Main thread only.
/// </summary>
internal static class PgpAliases
{
    private static string? _cachedPath;
    private static DateTime _cachedStamp;
    private static CommandAliases? _cached;

    /// <summary>The aliases for <paramref name="database"/>'s session.</summary>
    public static CommandAliases Load(Database database)
    {
        string path;
        try
        {
            path = HostApplicationServices.Current.FindFile("acad.pgp", database, FindFileHint.Default);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException or InvalidOperationException)
        {
            CopilotLog.Info("Drafting: acad.pgp was not found on the support path; using the built-in aliases. " + ex.Message);
            return CommandAliases.Default;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cached is not null && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) && stamp == _cachedStamp)
            {
                return _cached;
            }

            var aliases = CommandAliases.FromPgp(File.ReadAllText(path));
            _cached = aliases;
            _cachedPath = path;
            _cachedStamp = stamp;
            CopilotLog.Info($"Drafting: loaded {aliases.AliasCount} aliases and {aliases.ExternalCommandCount} external commands from {path}.");
            return aliases;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            CopilotLog.Warn($"Drafting: could not read {path}; using the built-in aliases", ex);
            return CommandAliases.Default;
        }
    }
}
