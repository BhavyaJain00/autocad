using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Ipc;

/// <summary>
/// Content of a discovery file %LOCALAPPDATA%\AcadCopilot\instances\&lt;pid&gt;.json written by the add-in
/// so the bridge can find and authenticate to the running AutoCAD.
/// </summary>
public sealed record AcadInstanceInfo
{
    /// <summary>AutoCAD process id.</summary>
    public required int Pid { get; init; }

    /// <summary>Full pipe path, e.g. \\.\pipe\AcadCopilot-1234.</summary>
    public required string Pipe { get; init; }

    /// <summary>Random secret the client must send in auth.</summary>
    public required string Token { get; init; }

    /// <summary>AutoCAD version number ("2027").</summary>
    public string? AcadVersion { get; init; }

    /// <summary>Active document title, refreshed by the add-in when it changes.</summary>
    public string? DocumentTitle { get; init; }

    /// <summary>When the add-in started (UTC).</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Pipe naming helpers.</summary>
public static class PipeNames
{
    /// <summary>Prefix of the full Windows pipe path.</summary>
    public const string PipePathPrefix = @"\\.\pipe\";

    /// <summary>Short pipe name for a AutoCAD process ("AcadCopilot-1234"), as used by .NET pipe streams.</summary>
    public static string ForProcess(int pid) => $"AcadCopilot-{pid}";

    /// <summary>Full pipe path for a AutoCAD process (\\.\pipe\AcadCopilot-1234), as stored in discovery files.</summary>
    public static string FullPath(string shortName) => PipePathPrefix + shortName;

    /// <summary>Accepts a full path or a short name and returns the short name.</summary>
    public static string ToShortName(string pipe) =>
        pipe.StartsWith(PipePathPrefix, StringComparison.OrdinalIgnoreCase) ? pipe[PipePathPrefix.Length..] : pipe;

    /// <summary>Creates a new random URL-safe token (256 bits).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Reads and writes discovery files.</summary>
public static class InstanceDiscovery
{
    /// <summary>Writes (atomically replaces) the discovery file for <paramref name="info"/>; returns its path.</summary>
    /// <param name="info">Instance data.</param>
    /// <param name="directory">Folder; defaults to <see cref="AppPaths.Instances"/>.</param>
    public static string Write(AcadInstanceInfo info, string? directory = null)
    {
        var dir = AppPaths.Ensure(directory ?? AppPaths.Instances);
        var path = PathFor(info.Pid, dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, CopilotJson.Indented));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Deletes the discovery file of <paramref name="pid"/> if present.</summary>
    public static void Delete(int pid, string? directory = null)
    {
        try
        {
            File.Delete(PathFor(pid, directory ?? AppPaths.Instances));
        }
        catch (IOException ex)
        {
            CopilotLog.Warn($"Could not delete discovery file for pid {pid}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            CopilotLog.Warn($"Could not delete discovery file for pid {pid}", ex);
        }
    }

    /// <summary>Path of the discovery file of <paramref name="pid"/>.</summary>
    public static string PathFor(int pid, string? directory = null) =>
        Path.Combine(directory ?? AppPaths.Instances, $"{pid}.json");

    /// <summary>All readable discovery files whose process is alive, newest first.</summary>
    /// <param name="directory">Folder; defaults to <see cref="AppPaths.Instances"/>.</param>
    /// <param name="isAlive">Process liveness check (tests may replace it); defaults to <see cref="IsProcessAlive"/>.</param>
    public static IReadOnlyList<AcadInstanceInfo> ReadLive(string? directory = null, Func<int, bool>? isAlive = null)
    {
        var dir = directory ?? AppPaths.Instances;
        if (!Directory.Exists(dir))
        {
            return [];
        }

        isAlive ??= IsProcessAlive;
        var result = new List<AcadInstanceInfo>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var info = TryRead(file);
            if (info is not null && isAlive(info.Pid))
            {
                result.Add(info);
            }
        }

        return result.OrderByDescending(i => i.StartedAt).ToList();
    }

    /// <summary>
    /// Picks the instance to connect to: the one of <paramref name="pid"/> when given, otherwise the newest live one.
    /// Returns null when none is found.
    /// </summary>
    public static AcadInstanceInfo? Find(int? pid = null, string? directory = null, Func<int, bool>? isAlive = null)
    {
        var live = ReadLive(directory, isAlive);
        return pid is null ? live.FirstOrDefault() : live.FirstOrDefault(i => i.Pid == pid);
    }

    /// <summary>Deletes discovery files whose process is gone (called by the add-in at startup).</summary>
    public static int RemoveStale(string? directory = null, Func<int, bool>? isAlive = null)
    {
        var dir = directory ?? AppPaths.Instances;
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        isAlive ??= IsProcessAlive;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var info = TryRead(file);
            var pidFromName = int.TryParse(Path.GetFileNameWithoutExtension(file), out var p) ? p : (int?)null;
            var pid = info?.Pid ?? pidFromName;
            if (pid is null || !isAlive(pid.Value))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    // Someone else is using it; leave it.
                }
                catch (UnauthorizedAccessException)
                {
                    // Leave it.
                }
            }
        }

        return removed;
    }

    /// <summary>Reads one discovery file, or null when missing/corrupt.</summary>
    public static AcadInstanceInfo? TryRead(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<AcadInstanceInfo>(File.ReadAllText(file), CopilotJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a process with <paramref name="pid"/> exists and has not exited.</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied to an elevated process: it exists.
            return true;
        }
    }
}
