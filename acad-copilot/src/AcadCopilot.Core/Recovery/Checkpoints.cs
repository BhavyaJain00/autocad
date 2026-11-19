using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Recovery;

/// <summary>
/// A cheap summary of a drawing's content used to verify that AutoCAD's UNDO Back really reached a checkpoint: the
/// number of entities in model and paper space, an order-independent hash of their handles, and the number of layers.
/// Two states with the same fingerprint have exactly the same set of entities (by handle) and layer count.
/// </summary>
/// <param name="EntityCount">Entities (not erased) in model space and all layouts.</param>
/// <param name="HandleHash">Order-independent hash of their handle values (<see cref="Accumulate"/>).</param>
/// <param name="LayerCount">Layers in the layer table.</param>
public sealed record DrawingFingerprint(int EntityCount, long HandleHash, int LayerCount)
{
    /// <summary>Order-independent mix of one handle value into a running hash (sum of SplitMix64 values).</summary>
    public static long Accumulate(long hash, long handle)
    {
        unchecked
        {
            var z = (ulong)handle + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return hash + (long)z;
        }
    }

    /// <summary>The fingerprint of a set of entity handles and a layer count.</summary>
    public static DrawingFingerprint Of(IEnumerable<long> handles, int layerCount)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var count = 0;
        long hash = 0;
        foreach (var handle in handles)
        {
            count++;
            hash = Accumulate(hash, handle);
        }

        return new DrawingFingerprint(count, hash, layerCount);
    }

    /// <summary>"152 objects, 9 layers".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{EntityCount} objects, {LayerCount} layers");
}

/// <summary>Where a checkpoint came from.</summary>
public enum CheckpointOrigin
{
    /// <summary>The AI called create_checkpoint.</summary>
    Tool,

    /// <summary>run_commands created it before a stage (autoCheckpoint).</summary>
    Auto,
}

/// <summary>One checkpoint of a drawing.</summary>
public sealed record CheckpointInfo
{
    /// <summary>Its name ("before: Inner walls").</summary>
    public required string Name { get; init; }

    /// <summary>When it was created (UTC).</summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>The drawing content at that moment.</summary>
    public required DrawingFingerprint Fingerprint { get; init; }

    /// <summary>True while an AutoCAD UNDO mark of this checkpoint is believed to exist in the open drawing (exact restore).</summary>
    public bool HasUndoMark { get; init; }

    /// <summary>Full path of the copy of the drawing saved at that moment, when one was written.</summary>
    public string? SnapshotFile { get; init; }

    /// <summary>Who created it.</summary>
    public CheckpointOrigin Origin { get; init; } = CheckpointOrigin.Tool;

    /// <summary>The run_commands title it was made for (automatic checkpoints).</summary>
    public string? Note { get; init; }
}

/// <summary>What restoring a checkpoint with UNDO Back takes: how many marks to go back and what each one must show.</summary>
/// <param name="Name">The checkpoint.</param>
/// <param name="Expected">Fingerprints the drawing must have after the 1st, 2nd... UNDO Back (the last one is the checkpoint's).</param>
public sealed record UndoRestorePlan(string Name, IReadOnlyList<DrawingFingerprint> Expected)
{
    /// <summary>Number of UNDO Back commands.</summary>
    public int BackCount => Expected.Count;
}

/// <summary>Checkpoint names: trimmed, one line, at most <see cref="MaxLength"/> characters; compared case-insensitively.</summary>
public static class CheckpointNames
{
    /// <summary>Longest name.</summary>
    public const int MaxLength = 60;

    /// <summary>Prefix of the checkpoints run_commands creates before a stage.</summary>
    public const string AutoPrefix = "before: ";

    /// <summary>The clean form of <paramref name="name"/>, or null when it is empty or has control characters.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
        {
            return null;
        }

        var text = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length > MaxLength ? text[..MaxLength].TrimEnd() : text;
    }

    /// <summary>The automatic checkpoint name for a run_commands title ("before: Outer walls").</summary>
    public static string ForRun(string title) => Normalize(AutoPrefix + title) ?? AutoPrefix.Trim();

    /// <summary>A file-name-safe form of <paramref name="name"/> (letters, digits, '-', '_'; at most 40 characters).</summary>
    public static string FileSafe(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        var text = builder.ToString().Trim('_');
        while (text.Contains("__", StringComparison.Ordinal))
        {
            text = text.Replace("__", "_", StringComparison.Ordinal);
        }

        if (text.Length > 40)
        {
            text = text[..40];
        }

        return text.Length == 0 ? "checkpoint" : text;
    }
}

/// <summary>
/// The checkpoints of ONE open drawing in this AutoCAD session and the stack of AutoCAD UNDO marks the copilot placed
/// for them. Pure bookkeeping (the plug-in types the UNDO commands): it knows, for a restore, how many UNDO Back
/// commands reach a checkpoint and which fingerprint the drawing must show after each, so a Back that lands somewhere
/// else (the user undid past a mark, placed own marks, or the drawing was reopened) is detected and reversed with REDO.
/// Not thread-safe: used on AutoCAD's main thread.
/// </summary>
public sealed class CheckpointCatalog
{
    /// <summary>Most checkpoints kept per drawing (the oldest are forgotten first).</summary>
    public const int MaxCheckpoints = 30;

    private readonly List<CheckpointInfo> _checkpoints = [];
    private readonly List<MarkSlot> _marks = [];

    /// <summary>The checkpoints, oldest first.</summary>
    public IReadOnlyList<CheckpointInfo> Checkpoints => _checkpoints;

    /// <summary>Number of UNDO marks the copilot believes are on AutoCAD's undo stack.</summary>
    public int MarkCount => _marks.Count;

    /// <summary>The checkpoint called <paramref name="name"/> (case-insensitive), or null.</summary>
    public CheckpointInfo? Find(string? name)
    {
        var clean = CheckpointNames.Normalize(name);
        return clean is null ? null : _checkpoints.FirstOrDefault(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a checkpoint (replacing an older one of the same name). When <see cref="CheckpointInfo.HasUndoMark"/> is
    /// true, an UNDO mark was just placed for it on top of the undo stack.
    /// </summary>
    public CheckpointInfo Add(CheckpointInfo checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var clean = CheckpointNames.Normalize(checkpoint.Name) ?? throw new ArgumentException("A checkpoint needs a name.", nameof(checkpoint));
        checkpoint = checkpoint with { Name = clean };
        if (Find(clean) is { } older)
        {
            _checkpoints.Remove(older);
            foreach (var slot in _marks.Where(s => string.Equals(s.Name, older.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Its mark stays on AutoCAD's undo stack; restores still have to pass it.
                slot.Name = null;
            }
        }

        _checkpoints.Add(checkpoint);
        if (checkpoint.HasUndoMark)
        {
            _marks.Add(new MarkSlot { Name = clean, Fingerprint = checkpoint.Fingerprint });
        }

        while (_checkpoints.Count > MaxCheckpoints)
        {
            var oldest = _checkpoints[0];
            _checkpoints.RemoveAt(0);
            foreach (var slot in _marks.Where(s => string.Equals(s.Name, oldest.Name, StringComparison.OrdinalIgnoreCase)))
            {
                slot.Name = null;
            }
        }

        return checkpoint;
    }

    /// <summary>
    /// The UNDO Back plan to reach <paramref name="name"/>, or null when it has no usable undo mark (then only its
    /// snapshot file can restore it).
    /// </summary>
    public UndoRestorePlan? PlanUndoRestore(string name)
    {
        var checkpoint = Find(name);
        if (checkpoint is null || !checkpoint.HasUndoMark)
        {
            return null;
        }

        var index = _marks.FindLastIndex(s => string.Equals(s.Name, checkpoint.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var expected = new List<DrawingFingerprint>();
        for (var i = _marks.Count - 1; i >= index; i--)
        {
            expected.Add(_marks[i].Fingerprint);
        }

        return new UndoRestorePlan(checkpoint.Name, expected);
    }

    /// <summary>
    /// Records that <paramref name="backsDone"/> UNDO Back commands succeeded: their marks are used up. Checkpoints whose
    /// mark was used up keep only their snapshot file.
    /// </summary>
    public void MarksUsed(int backsDone)
    {
        for (var i = 0; i < backsDone && _marks.Count > 0; i++)
        {
            var slot = _marks[^1];
            _marks.RemoveAt(_marks.Count - 1);
            if (slot.Name is not null && !_marks.Any(s => string.Equals(s.Name, slot.Name, StringComparison.OrdinalIgnoreCase)))
            {
                SetHasMark(slot.Name, false);
            }
        }
    }

    /// <summary>Records that a new UNDO mark was placed for the existing checkpoint <paramref name="name"/> (after a restore).</summary>
    public void Remarked(string name, DrawingFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var checkpoint = Find(name) ?? throw new ArgumentException($"No checkpoint '{name}'.", nameof(name));
        _marks.Add(new MarkSlot { Name = checkpoint.Name, Fingerprint = fingerprint });
        SetHasMark(checkpoint.Name, true);
    }

    /// <summary>
    /// Forgets every UNDO mark (AutoCAD's undo stack no longer matches: a restore found another state, UNDO was turned
    /// off, the drawing was reopened). The checkpoints stay, restorable from their snapshot files.
    /// </summary>
    public void InvalidateMarks()
    {
        _marks.Clear();
        for (var i = 0; i < _checkpoints.Count; i++)
        {
            _checkpoints[i] = _checkpoints[i] with { HasUndoMark = false };
        }
    }

    /// <summary>Records that the snapshot file of <paramref name="name"/> is gone.</summary>
    public void SnapshotRemoved(string name)
    {
        var index = _checkpoints.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _checkpoints[index] = _checkpoints[index] with { SnapshotFile = null };
        }
    }

    private void SetHasMark(string name, bool value)
    {
        var index = _checkpoints.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _checkpoints[index] = _checkpoints[index] with { HasUndoMark = value };
        }
    }

    private sealed class MarkSlot
    {
        public string? Name { get; set; }

        public required DrawingFingerprint Fingerprint { get; init; }
    }
}
