using System.Globalization;
using System.IO;
using System.Text.Json;
using AcadCopilot.Core;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>
/// The system variables a run_commands call sets so typed input means exactly what the classic command line means,
/// and always restores. Main thread only.
/// <list type="table">
/// <item><term>SYSMON = 0</term><description>the system-variable monitor prints no "**** System Variable Changed ****" notices into the command line the engine reads (the settings below and the AI's own SETVARs would trigger them).</description></item>
/// <item><term>CMDDIA, FILEDIA, ATTDIA = 0</term><description>commands prompt on the command line instead of opening dialogs (plot, file names, attribute values).</description></item>
/// <item><term>TEXTED = 1</term><description>TEXT takes its text on the command line instead of the in-place editor.</description></item>
/// <item><term>TEXTEVAL = 0</term><description>text answers starting with "(" or "!" are taken literally, never evaluated as AutoLISP.</description></item>
/// <item><term>DYNPICOORDS = 1</term><description>with Dynamic Input on, "10,0" stays absolute and only "@10,0" is relative.</description></item>
/// <item><term>OSNAPCOORD = 1</term><description>typed coordinates win over running object snaps.</description></item>
/// <item><term>NOMUTT = 0</term><description>prompts and messages are shown (the user watches them, the engine reads them).</description></item>
/// <item><term>PICKAUTO = 0</term><description>a typed point that hits nothing at "Select objects:" selects nothing instead of starting a window.</description></item>
/// <item><term>PICKADD 0 -&gt; 2</term><description>several picks add up instead of replacing each other.</description></item>
/// </list>
/// The original values are also written to %LOCALAPPDATA%\AcadCopilot\drafting-sysvars.json until they are restored, so
/// a crash of AutoCAD in the middle of a call cannot leave the user's registry settings (FILEDIA, CMDDIA...) changed:
/// the next call puts them back first.
/// </summary>
internal sealed class SysvarScope
{
    // SYSMON first: it is set before the others (so changing them prints no "**** System Variable Changed ****" notices)
    // and restored last (Restore runs in reverse order), after the user's values are back.
    private static readonly (string Name, Func<int, int?> Desired)[] Settings =
    [
        ("SYSMON", _ => 0),
        ("CMDDIA", _ => 0),
        ("FILEDIA", _ => 0),
        ("ATTDIA", _ => 0),
        ("TEXTED", _ => 1),
        ("TEXTEVAL", _ => 0),
        ("DYNPICOORDS", _ => 1),
        ("OSNAPCOORD", _ => 1),
        ("NOMUTT", _ => 0),
        ("PICKAUTO", _ => 0),
        ("PICKADD", current => current == 0 ? 2 : null),
    ];

    private readonly List<(string Name, object Original, int Forced)> _changed = [];
    private readonly List<string> _problems = [];
    private bool _restored;

    private SysvarScope()
    {
    }

    /// <summary>Settings that could not be applied (for the report's warnings).</summary>
    public IReadOnlyList<string> Problems => _problems;

    private static string BackupFile => Path.Combine(AppPaths.Root, "drafting-sysvars.json");

    /// <summary>Restores values left behind by a crashed earlier call, then applies the drafting settings.</summary>
    public static SysvarScope Apply(Document document)
    {
        RecoverAfterCrash();
        var scope = new SysvarScope();
        var plan = new List<(string Name, object Original, int Forced)>();
        foreach (var (name, desired) in Settings)
        {
            var current = AcadInput.Get(name);
            if (current is null)
            {
                if (name != "SYSMON")
                {
                    scope._problems.Add($"{name} is not available in this AutoCAD");
                }

                continue;
            }

            int value;
            try
            {
                value = Convert.ToInt32(current, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                continue;
            }

            if (desired(value) is { } forced && forced != value)
            {
                plan.Add((name, current, forced));
            }
        }

        WriteBackup(plan);
        using (TryLock(document))
        {
            foreach (var (name, original, forced) in plan)
            {
                object value;
                try
                {
                    value = Convert.ChangeType(forced, original.GetType(), CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                {
                    scope._problems.Add($"{name} has an unexpected type ({original.GetType().Name})");
                    continue;
                }

                if (AcadInput.Set(name, value))
                {
                    scope._changed.Add((name, original, forced));
                }
                else
                {
                    scope._problems.Add($"{name} could not be set to {forced}");
                }
            }
        }

        if (scope._changed.Count == 0)
        {
            DeleteBackup();
        }

        return scope;
    }

    /// <summary>Puts every changed variable back to the user's value (once; later calls do nothing).</summary>
    public void Restore(Document? document)
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        var failed = false;
        using (document is null ? null : TryLock(document))
        {
            for (var i = _changed.Count - 1; i >= 0; i--)
            {
                var (name, original, _) = _changed[i];
                failed |= !AcadInput.Set(name, original);
            }
        }

        if (!failed)
        {
            DeleteBackup();
        }
    }

    private static IDisposable? TryLock(Document document)
    {
        try
        {
            return AcadDispatcher.LockDocument(document);
        }
        catch (Exception ex) when (ex is AcadBusyException or Autodesk.AutoCAD.Runtime.Exception or ObjectDisposedException or InvalidOperationException or NullReferenceException)
        {
            // Registry system variables need no lock; drawing ones are retried without it and reported if refused.
            return null;
        }
    }

    private static void WriteBackup(List<(string Name, object Original, int Forced)> plan)
    {
        if (plan.Count == 0)
        {
            return;
        }

        try
        {
            var values = plan.ToDictionary(p => p.Name, p => new[] { Convert.ToInt32(p.Original, CultureInfo.InvariantCulture), p.Forced });
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(values));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidCastException)
        {
            CopilotLog.Warn("Drafting: could not write the system-variable backup file", ex);
        }
    }

    private static void DeleteBackup()
    {
        try
        {
            File.Delete(BackupFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopilotLog.Warn("Drafting: could not delete the system-variable backup file", ex);
        }
    }

    // A backup file means an earlier call did not finish (AutoCAD crashed or was killed). Values that still hold what the
    // copilot forced are put back to the user's originals; values the user changed since are left alone.
    private static void RecoverAfterCrash()
    {
        Dictionary<string, int[]>? values;
        try
        {
            if (!File.Exists(BackupFile))
            {
                return;
            }

            values = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(BackupFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            CopilotLog.Warn("Drafting: the system-variable backup file could not be read; deleting it", ex);
            DeleteBackup();
            return;
        }

        foreach (var (name, pair) in values ?? new Dictionary<string, int[]>())
        {
            if (pair is not { Length: 2 } || AcadInput.Get(name) is not { } current)
            {
                continue;
            }

            if (AcadInput.GetInt(name) == pair[1])
            {
                AcadInput.Set(name, Convert.ChangeType(pair[0], current.GetType(), CultureInfo.InvariantCulture));
                CopilotLog.Info($"Drafting: restored {name} = {pair[0]} left over from an unfinished earlier call.");
            }
        }

        DeleteBackup();
    }
}
