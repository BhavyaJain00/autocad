using AcadCopilot.Core.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Acad;

/// <summary>
/// Drawing revision counter returned in every ToolResult.Revision: increments on every object appended, modified or
/// erased in any open drawing, including undo/redo (ObjectUnappended / ObjectReappended / un-erase), and when the active
/// drawing changes. The AI uses it to notice that the user changed the drawing between its calls.
/// Attach/detach on AutoCAD's main thread; <see cref="Current"/> is readable from any thread.
/// </summary>
public sealed class RevisionTracker : IDisposable
{
    private readonly HashSet<Database> _attached = [];
    private readonly ObjectEventHandler _onObject;
    private readonly ObjectErasedEventHandler _onErased;
    private DocumentCollection? _documents;
    private long _revision;

    /// <summary>Creates the tracker (call <see cref="Attach"/> on the main thread).</summary>
    public RevisionTracker()
    {
        _onObject = (_, _) => Bump();
        _onErased = (_, _) => Bump();
    }

    /// <summary>Current revision (any thread).</summary>
    public long Current => Interlocked.Read(ref _revision);

    /// <summary>Increments the revision.</summary>
    public long Bump() => Interlocked.Increment(ref _revision);

    /// <summary>Starts tracking every open drawing and the ones opened later. Main thread.</summary>
    public void Attach(DocumentCollection documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        foreach (Document document in documents)
        {
            AttachDatabase(document);
        }

        documents.DocumentCreated += OnDocumentCreated;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        documents.DocumentBecameCurrent += OnDocumentBecameCurrent;
    }

    /// <summary>Stops tracking. Main thread.</summary>
    public void Dispose()
    {
        if (_documents is { } documents)
        {
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            documents.DocumentBecameCurrent -= OnDocumentBecameCurrent;
            _documents = null;
        }

        foreach (var database in _attached.ToArray())
        {
            DetachDatabase(database);
        }
    }

    private void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e) => AttachDatabase(e.Document);

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        try
        {
            if (e.Document?.Database is { } database)
            {
                DetachDatabase(database);
            }
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Revision tracker: detaching a closing drawing failed", ex);
        }

        Bump();
    }

    private void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e) => Bump();

    private void AttachDatabase(Document? document)
    {
        try
        {
            if (document?.Database is not { } database || !_attached.Add(database))
            {
                return;
            }

            database.ObjectAppended += _onObject;
            database.ObjectModified += _onObject;
            database.ObjectUnappended += _onObject;
            database.ObjectReappended += _onObject;
            database.ObjectErased += _onErased;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Revision tracker: attaching to a drawing failed", ex);
        }
    }

    private void DetachDatabase(Database database)
    {
        if (!_attached.Remove(database))
        {
            return;
        }

        try
        {
            database.ObjectAppended -= _onObject;
            database.ObjectModified -= _onObject;
            database.ObjectUnappended -= _onObject;
            database.ObjectReappended -= _onObject;
            database.ObjectErased -= _onErased;
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Revision tracker: detaching from a drawing failed", ex);
        }
    }
}
