using System.Globalization;
using System.Text.Json;
using AcadCopilot.Core.Json;

namespace AcadCopilot.Core.Recovery;

/// <summary>
/// The checkpoint copies on disk: <c>%LOCALAPPDATA%\AcadCopilot\checkpoints\&lt;drawing key&gt;\</c> holds one DWG per
/// checkpoint (written by the plug-in with Database.Wblock + SaveAs, never the user's own file) and <c>index.json</c>
/// (name, file, time, fingerprint, drawing path). They outlive the AutoCAD session, so a checkpoint can still be restored
/// from its file after AutoCAD was closed or crashed (its UNDO mark is gone then). At most <see cref="MaxPerDrawing"/>
/// copies per drawing are kept; drawing folders untouched for <see cref="MaxAge"/> are deleted.
/// </summary>
public sealed class CheckpointStore
{
    /// <summary>Most snapshot files kept per drawing (oldest deleted first).</summary>
    public const int MaxPerDrawing = 20;

    /// <summary>Folders older than this are deleted by <see cref="CleanOld"/>.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const string IndexFile = "index.json";
    private readonly Lock _gate = new();

    /// <summary>Creates a store rooted at <paramref name="root"/>.</summary>
    public CheckpointStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    /// <summary>The store under %LOCALAPPDATA%\AcadCopilot\checkpoints (or ACADCOPILOT_HOME).</summary>
    public static CheckpointStore CreateDefault() => new(Path.Combine(AppPaths.Root, "checkpoints"));

    /// <summary>The root folder.</summary>
    public string Root { get; }

    /// <summary>
    /// The folder name for a drawing: its file name (safe characters) plus a hash of the full path, so two drawings with
    /// the same name in different folders never share checkpoints ("house-g1_3fa2c19b").
    /// </summary>
    public static string DrawingKey(string drawingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drawingPath);
        var full = drawingPath.Trim().Replace('/', '\\').ToUpperInvariant();
        uint hash = 2166136261;
        foreach (var c in full)
        {
            unchecked
            {
                hash = (hash ^ c) * 16777619;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(drawingPath.Trim()).ToLowerInvariant();
        return CheckpointNames.FileSafe(string.IsNullOrEmpty(stem) ? "drawing" : stem) + "_" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>The folder of a drawing's checkpoints (not created).</summary>
    public string FolderOf(string drawingKey) => Path.Combine(Root, drawingKey);

    /// <summary>A new, unused file path for a snapshot of checkpoint <paramref name="name"/> (the folder is created).</summary>
    public string NewSnapshotPath(string drawingKey, string name, DateTime utc)
    {
        var folder = AppPaths.Ensure(FolderOf(drawingKey));
        var stem = utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + CheckpointNames.FileSafe(name);
        var path = Path.Combine(folder, stem + ".dwg");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".dwg");
        }

        return path;
    }

    /// <summary>
    /// Records a checkpoint with a snapshot file in the drawing's index (replacing one of the same name, whose old file is
    /// deleted), then keeps at most <see cref="MaxPerDrawing"/>. Returns the names whose files were deleted to make room.
    /// </summary>
    public IReadOnlyList<string> Record(string drawingKey, string drawingPath, CheckpointInfo checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.SnapshotFile is null)
        {
            throw new ArgumentException("Only checkpoints with a snapshot file are stored.", nameof(checkpoint));
        }

        lock (_gate)
        {
            var entries = ReadIndex(drawingKey);
            foreach (var same in entries.Where(e => string.Equals(e.Name, checkpoint.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                entries.Remove(same);
                if (!string.Equals(same.File, Path.GetFileName(checkpoint.SnapshotFile), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(Path.Combine(FolderOf(drawingKey), same.File));
                }
            }

            entries.Add(new Entry
            {
                Name = checkpoint.Name,
                File = Path.GetFileName(checkpoint.SnapshotFile),
                CreatedUtc = checkpoint.CreatedUtc,
                DrawingPath = drawingPath,
                EntityCount = checkpoint.Fingerprint.EntityCount,
                HandleHash = checkpoint.Fingerprint.HandleHash,
                LayerCount = checkpoint.Fingerprint.LayerCount,
                Origin = checkpoint.Origin,
                Note = checkpoint.Note,
            });

            var removed = new List<string>();
            foreach (var old in entries.OrderBy(e => e.CreatedUtc).Take(Math.Max(0, entries.Count - MaxPerDrawing)).ToList())
            {
                entries.Remove(old);
                TryDelete(Path.Combine(FolderOf(drawingKey), old.File));
                removed.Add(old.Name);
            }

            WriteIndex(drawingKey, entries);
            return removed;
        }
    }

    /// <summary>The stored checkpoints of a drawing whose files still exist, oldest first (without undo marks).</summary>
    public IReadOnlyList<CheckpointInfo> List(string drawingKey)
    {
        lock (_gate)
        {
            return ReadIndex(drawingKey)
                .Select(e => (Entry: e, Path: Path.Combine(FolderOf(drawingKey), e.File)))
                .Where(x => File.Exists(x.Path))
                .OrderBy(x => x.Entry.CreatedUtc)
                .Select(x => new CheckpointInfo
                {
                    Name = x.Entry.Name,
                    CreatedUtc = x.Entry.CreatedUtc,
                    Fingerprint = new DrawingFingerprint(x.Entry.EntityCount, x.Entry.HandleHash, x.Entry.LayerCount),
                    HasUndoMark = false,
                    SnapshotFile = x.Path,
                    Origin = x.Entry.Origin,
                    Note = x.Entry.Note,
                })
                .ToList();
        }
    }

    /// <summary>Deletes drawing folders whose newest file is older than <paramref name="maxAge"/>; returns how many.</summary>
    public int CleanOld(TimeSpan maxAge, DateTime utcNow)
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var removed = 0;
        lock (_gate)
        {
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    var newest = Directory.EnumerateFiles(folder).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
                    if (utcNow - newest > maxAge)
                    {
                        Directory.Delete(folder, recursive: true);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file is in use (AutoCAD reading a snapshot): try again next time.
                }
            }
        }

        return removed;
    }

    private List<Entry> ReadIndex(string drawingKey)
    {
        var path = Path.Combine(FolderOf(drawingKey), IndexFile);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return CopilotJson.Deserialize<List<Entry>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void WriteIndex(string drawingKey, List<Entry> entries)
    {
        var folder = AppPaths.Ensure(FolderOf(drawingKey));
        AtomicFile.WriteAllText(Path.Combine(folder, IndexFile), JsonSerializer.Serialize(entries, CopilotJson.Indented));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use: CleanOld removes it later.
        }
    }

    internal sealed class Entry
    {
        public string Name { get; set; } = string.Empty;

        public string File { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        public string? DrawingPath { get; set; }

        public int EntityCount { get; set; }

        public long HandleHash { get; set; }

        public int LayerCount { get; set; }

        public CheckpointOrigin Origin { get; set; }

        public string? Note { get; set; }
    }
}

/// <summary>Writes a file atomically: a temporary file in the same folder, then a rename over the target.</summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="text"/> (UTF-8) to <paramref name="path"/> so readers see the old or the new file, never half of one.</summary>
    public static void WriteAllText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
