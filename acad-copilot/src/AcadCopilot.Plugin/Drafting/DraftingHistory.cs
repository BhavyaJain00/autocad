using AcadCopilot.Core.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>One finished run_commands call that changed a drawing (what the undo tool can take back).</summary>
internal sealed record RunRecord
{
    /// <summary>The drawing's database pointer (identity while the drawing is open).</summary>
    public required IntPtr Database { get; init; }

    /// <summary>The call's title.</summary>
    public required string Title { get; init; }

    /// <summary>Value of <see cref="DraftingHistory.ForeignChanges"/> when the call ended.</summary>
    public required long ForeignChangesAtEnd { get; init; }

    /// <summary>Objects the call created (still present when it ended).</summary>
    public IReadOnlyList<ObjectId> Created { get; init; } = [];

    /// <summary>Objects the call erased (pre-existing ones).</summary>
    public IReadOnlyList<ObjectId> Erased { get; init; } = [];

    /// <summary>Pre-existing objects the call modified.</summary>
    public IReadOnlyList<ObjectId> Modified { get; init; } = [];

    /// <summary>Number of pre-existing objects the call modified.</summary>
    public int ModifiedCount { get; init; }

    /// <summary>True when the call was one undo group (UNDO Begin/End worked).</summary>
    public bool OneUndoStep { get; init; }

    /// <summary>When the call ended (UTC).</summary>
    public DateTime EndedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// The copilot's finished calls per open drawing, newest last, and a count of "foreign" drawing changes per drawing:
/// entities and table records (layers, styles...) appended, modified or erased while no copilot call or undo was
/// running, i.e. by the user or another program, including the user's own Ctrl+Z. Zooms and pans (view records) do
/// not count. The undo tool only takes back copilot calls when nothing foreign happened after them. Main thread only.
/// </summary>
internal sealed class DraftingHistory
{
    private const int MaxRunsPerDrawing = 50;
    private readonly List<RunRecord> _runs = [];
    private readonly Dictionary<IntPtr, long> _foreign = [];
    private readonly HashSet<IntPtr> _watched = [];
    private int _suspended;
    private bool _documentsHooked;

    private DraftingHistory()
    {
    }

    /// <summary>The single instance (one AutoCAD session).</summary>
    public static DraftingHistory Instance { get; } = new();

    /// <summary>Starts counting foreign changes of <paramref name="document"/>'s drawing (idempotent).</summary>
    public void Watch(Document document)
    {
        var database = document.Database;
        var key = database.UnmanagedObject;
        if (!_documentsHooked)
        {
            AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            _documentsHooked = true;
        }

        if (!_watched.Add(key))
        {
            return;
        }

        _foreign[key] = 0;
        database.ObjectAppended += OnObjectChanged;
        database.ObjectModified += OnObjectChanged;
        database.ObjectUnappended += OnObjectChanged;
        database.ObjectReappended += OnObjectChanged;
        database.ObjectErased += OnObjectErased;
    }

    /// <summary>Foreign changes counted so far for <paramref name="database"/>.</summary>
    public long ForeignChanges(Database database) => _foreign.TryGetValue(database.UnmanagedObject, out var count) ? count : 0;

    /// <summary>While the returned scope is open, changes are the copilot's own and not counted as foreign.</summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Scope(this);
    }

    /// <summary>Remembers a finished call (only calls that changed the drawing).</summary>
    public void Add(RunRecord record)
    {
        _runs.Add(record);
        var ofDrawing = _runs.Where(r => r.Database == record.Database).ToList();
        if (ofDrawing.Count > MaxRunsPerDrawing)
        {
            _runs.Remove(ofDrawing[0]);
        }
    }

    /// <summary>The newest <paramref name="count"/> calls on <paramref name="database"/>, newest first.</summary>
    public IReadOnlyList<RunRecord> Latest(Database database, int count)
    {
        var key = database.UnmanagedObject;
        return _runs.Where(r => r.Database == key).Reverse().Take(count).ToList();
    }

    /// <summary>Forgets a call (after it was undone).</summary>
    public void Remove(RunRecord record) => _runs.Remove(record);

    private void OnObjectChanged(object? sender, ObjectEventArgs e) => Count(sender, e.DBObject);

    private void OnObjectErased(object? sender, ObjectErasedEventArgs e) => Count(sender, e.DBObject);

    private void Count(object? sender, DBObject? changed)
    {
        if (_suspended > 0)
        {
            return;
        }

        try
        {
            var database = sender as Database ?? changed?.Database;
            if (database is not null && (changed is Entity || (changed is SymbolTableRecord && changed is not ViewportTableRecord)))
            {
                var key = database.UnmanagedObject;
                _foreign[key] = _foreign.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Drafting history: could not classify a drawing change", ex);
        }
    }

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document?.Database is not { } database)
            {
                return;
            }

            var key = database.UnmanagedObject;
            _runs.RemoveAll(r => r.Database == key);
            _foreign.Remove(key);
            if (_watched.Remove(key))
            {
                database.ObjectAppended -= OnObjectChanged;
                database.ObjectModified -= OnObjectChanged;
                database.ObjectUnappended -= OnObjectChanged;
                database.ObjectReappended -= OnObjectChanged;
                database.ObjectErased -= OnObjectErased;
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Drafting history: forgetting a closing drawing failed", ex);
        }
    }

    private sealed class Scope(DraftingHistory owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner._suspended--;
            }
        }
    }
}
