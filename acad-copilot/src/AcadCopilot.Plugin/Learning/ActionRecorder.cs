using System.IO;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Learning;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Drafting;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Learning;

/// <summary>
/// "Learn from me": records the user's own manual work in AutoCAD while the user lets it (Start learning in the panel),
/// into a <see cref="UserRecording"/>. AutoCAD 2027 events used (public API): Document.CommandWillStart / CommandEnded /
/// CommandCancelled / CommandFailed (command name), Database.ObjectAppended / ObjectModified / ObjectErased (the objects of
/// each command), Application.SystemVariableChanged (CLAYER), DocumentCollection.DocumentCreated / DocumentActivated /
/// DocumentToBeDestroyed. The typed answers come from the command-line history (undocumented
/// Internal.Utils.GetLastCommandLines through <see cref="AcadInput.History"/>, which puts AutoCAD 2027's newest-first lines
/// into time order) read when a command starts and ends. Everything the copilot does is ignored: the plug-in marks its own
/// tool calls with <see cref="CopilotActivity.Shared"/>, and commands that start while it is active are not recorded.
/// Handlers run on AutoCAD's main thread, never throw and are attached only while a recording exists.
/// </summary>
public sealed class ActionRecorder : IUserActionRecorder, IDisposable
{
    /// <summary>Command-line lines read per history snapshot.</summary>
    private const int HistoryLines = 200;

    /// <summary>Most new objects remembered per drawing for the style analysis.</summary>
    private const int MaxStyleObjects = 20_000;

    private readonly AcadDispatcher _dispatcher;
    private readonly KnowledgeStore _knowledge;
    private readonly CopilotActivity _copilot;
    private readonly HashSet<Document> _documents = [];
    private readonly Stack<OpenCommand> _open = new();
    private readonly Dictionary<IntPtr, (string Drawing, HashSet<ObjectId> Ids)> _newObjects = [];
    private UserRecording? _recording;
    private bool _appHooked;
    private bool _disposed;

    /// <summary>Creates the recorder (idle: no AutoCAD event is attached until <see cref="Start"/>).</summary>
    /// <param name="dispatcher">AutoCAD's main-thread dispatcher.</param>
    /// <param name="knowledge">The knowledge folder (recordings go to its recordings\ folder, the style to style.md).</param>
    /// <param name="copilot">The copilot's activity flag (default <see cref="CopilotActivity.Shared"/>).</param>
    public ActionRecorder(AcadDispatcher dispatcher, KnowledgeStore knowledge, CopilotActivity? copilot = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _copilot = copilot ?? CopilotActivity.Shared;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public RecorderState State => _recording?.State ?? RecorderState.Off;

    /// <inheritdoc />
    public int ActionCount => _recording?.ActionCount ?? 0;

    /// <inheritdoc />
    public TimeSpan Elapsed => _recording?.Elapsed ?? TimeSpan.Zero;

    /// <inheritdoc />
    public string? Start()
    {
        if (!_dispatcher.IsMainThread)
        {
            return "Learning can only be started from the panel.";
        }

        if (_recording is { State: not RecorderState.Off })
        {
            return null;
        }

        var active = AcadApp.DocumentManager.MdiActiveDocument;
        if (active is null)
        {
            return "Open a drawing first, then start learning.";
        }

        var recording = new UserRecording();
        try
        {
            _recording = recording;
            _open.Clear();
            _newObjects.Clear();
            foreach (Document document in AcadApp.DocumentManager)
            {
                Hook(document);
            }

            var documents = AcadApp.DocumentManager;
            documents.DocumentCreated += OnDocumentCreated;
            documents.DocumentActivated += OnDocumentActivated;
            documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            AcadApp.SystemVariableChanged += OnSystemVariableChanged;
            _appHooked = true;

            var name = DrawingName(active);
            recording.SetUnits(name, ToolContext.GetUnits(active.Database));
            recording.SetCurrentLayer(AcadInput.GetString("CLAYER"));
            CopilotLog.Info($"Learn from me: recording started in {name} by the user.");
        }
        catch (Exception ex)
        {
            CopilotLog.Error("Learn from me: could not start recording", ex);
            Unhook();
            _recording = null;
            return "Could not start learning: " + ex.Message;
        }

        RaiseChanged();
        return null;
    }

    /// <inheritdoc />
    public void Pause()
    {
        _recording?.Pause();
        RaiseChanged();
    }

    /// <inheritdoc />
    public void Resume()
    {
        _recording?.Resume();
        RaiseChanged();
    }

    /// <inheritdoc />
    public async Task<RecordingOutcome> StopAndSaveAsync(CancellationToken cancellationToken = default)
    {
        if (_recording is not { } recording || recording.State == RecorderState.Off)
        {
            return new RecordingOutcome(null, 0, null, "Learning is not on.");
        }

        recording.Stop();
        Unhook();
        _open.Clear();
        var newObjects = _newObjects.ToDictionary(p => p.Key, p => p.Value);
        _newObjects.Clear();
        RaiseChanged();
        CopilotLog.Info($"Learn from me: recording stopped ({recording.ActionCount} actions, {recording.TotalCreated} objects created).");

        if (recording.ActionCount == 0)
        {
            _recording = null;
            RaiseChanged();
            return new RecordingOutcome(null, 0, null, "Nothing was recorded: no commands of yours ran while learning was on.");
        }

        string? path = null;
        string? problem = null;
        try
        {
            var markdown = RecordingMarkdown.Write(recording);
            var library = new RecordingLibrary(_knowledge.Directory);
            path = await Task.Run(() => library.Save(recording.Started, recording.Drawings.FirstOrDefault(), markdown), cancellationToken).ConfigureAwait(true);
            CopilotLog.Info("Learn from me: saved " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopilotLog.Error("Learn from me: could not save the recording", ex);
            problem = "Could not save the recording in " + _knowledge.Directory + ": " + ex.Message;
        }

        var style = await LearnStyleAsync(newObjects, cancellationToken).ConfigureAwait(true);
        var actions = recording.ActionCount;
        _recording = null;
        RaiseChanged();
        return new RecordingOutcome(path, actions, style, problem);
    }

    /// <inheritdoc />
    public void Discard()
    {
        if (_recording is null)
        {
            return;
        }

        var actions = _recording.ActionCount;
        _recording.Stop();
        Unhook();
        _open.Clear();
        _newObjects.Clear();
        _recording = null;
        CopilotLog.Info($"Learn from me: recording discarded by the user ({actions} actions, nothing saved).");
        RaiseChanged();
    }

    /// <summary>Detaches every handler (AutoCAD is closing).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recording?.Stop();
        Unhook();
    }

    private static string DrawingName(Document document)
    {
        try
        {
            return Path.GetFileName(document.Name);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or ArgumentException)
        {
            return "drawing";
        }
    }

    // Reads the user's new objects of the drawing where they made the most (main thread), then analyses them and merges
    // the style into style.md (off the main thread).
    private async Task<string?> LearnStyleAsync(Dictionary<IntPtr, (string Drawing, HashSet<ObjectId> Ids)> newObjects, CancellationToken cancellationToken)
    {
        var best = newObjects.Values.Where(v => v.Ids.Count > 0).OrderByDescending(v => v.Ids.Count).FirstOrDefault();
        if (best.Ids is null)
        {
            return null;
        }

        try
        {
            var sample = await _dispatcher.InvokeAsync<StyleSample?>(
                () =>
                {
                    var document = AcadApp.DocumentManager.Cast<Document>().FirstOrDefault(d => string.Equals(DrawingName(d), best.Drawing, StringComparison.OrdinalIgnoreCase));
                    if (document is null)
                    {
                        return null;
                    }

                    var ids = best.Ids.Where(id => id.Database == document.Database).ToList();
                    using var transaction = document.Database.TransactionManager.StartOpenCloseTransaction();
                    var read = ToolRegistry.ReadStyleSample(document, transaction, ids, best.Drawing + " (Learn from me recording)");
                    transaction.Commit();
                    return read;
                },
                cancellationToken).ConfigureAwait(true);
            if (sample is null)
            {
                return null;
            }

            return await Task.Run(
                () =>
                {
                    var profile = StyleAnalyzer.Analyze(sample);
                    return profile.Sections.Count == 0 ? null : _knowledge.MergeStyle(profile).Message;
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CopilotLog.Warn("Learn from me: the style of the recorded objects could not be learned", ex);
            return null;
        }
    }

    private void Hook(Document document)
    {
        if (!_documents.Add(document))
        {
            return;
        }

        document.CommandWillStart += OnCommandWillStart;
        document.CommandEnded += OnCommandEnded;
        document.CommandCancelled += OnCommandCancelled;
        document.CommandFailed += OnCommandFailed;
        var database = document.Database;
        database.ObjectAppended += OnObjectAppended;
        database.ObjectModified += OnObjectModified;
        database.ObjectErased += OnObjectErased;
    }

    private void Unhook(Document document)
    {
        if (!_documents.Remove(document))
        {
            return;
        }

        Try(() =>
        {
            document.CommandWillStart -= OnCommandWillStart;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandCancelled;
            document.CommandFailed -= OnCommandFailed;
        });
        Try(() =>
        {
            var database = document.Database;
            database.ObjectAppended -= OnObjectAppended;
            database.ObjectModified -= OnObjectModified;
            database.ObjectErased -= OnObjectErased;
        });
    }

    private void Unhook()
    {
        foreach (var document in _documents.ToList())
        {
            Unhook(document);
        }

        if (_appHooked)
        {
            _appHooked = false;
            Try(() =>
            {
                var documents = AcadApp.DocumentManager;
                documents.DocumentCreated -= OnDocumentCreated;
                documents.DocumentActivated -= OnDocumentActivated;
                documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            });
            Try(() => AcadApp.SystemVariableChanged -= OnSystemVariableChanged);
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Learn from me: detaching an AutoCAD event handler failed", ex);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Learn from me: the panel could not refresh", ex);
        }
    }

    private void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e) => Guard(() =>
    {
        if (e.Document is { } document)
        {
            Hook(document);
        }
    });

    private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e) => Guard(() =>
    {
        if (_recording is not { } recording || e.Document is not { } document)
        {
            return;
        }

        var name = DrawingName(document);
        if (recording.UnitsOf(name) is null)
        {
            recording.SetUnits(name, ToolContext.GetUnits(document.Database));
        }

        recording.DrawingSwitched(name, _copilot.IsActive);
        RaiseChanged();
    });

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e) => Guard(() =>
    {
        if (e.Document is { } document)
        {
            Unhook(document);
            _newObjects.Remove(document.Database.UnmanagedObject);
        }
    });

    private void OnSystemVariableChanged(object? sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e) => Guard(() =>
    {
        if (_recording is not { } recording || !string.Equals(e.Name, "CLAYER", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var document = AcadApp.DocumentManager.MdiActiveDocument;
        var before = recording.ActionCount;
        recording.LayerChanged(document is null ? string.Empty : DrawingName(document), AcadInput.GetString("CLAYER"), _copilot.IsActive);
        if (recording.ActionCount != before)
        {
            RaiseChanged();
        }
    });

    private void OnCommandWillStart(object? sender, CommandEventArgs e) => Guard(() =>
    {
        if (_recording is not { } recording || sender is not Document document)
        {
            return;
        }

        var name = e.GlobalCommandName ?? string.Empty;
        var action = recording.CommandStarted(DrawingName(document), name, AcadInput.GetString("CLAYER"), _copilot.IsActive);

        // Every start is pushed (also ignored ones) so the end events pair up with their own start.
        _open.Push(new OpenCommand(document, name, action, action is null ? null : AcadInput.History(HistoryLines)));
        if (action is not null)
        {
            RaiseChanged();
        }
    });

    private void OnCommandEnded(object? sender, CommandEventArgs e) => Finish(sender, e, CommandOutcome.Ended);

    private void OnCommandCancelled(object? sender, CommandEventArgs e) => Finish(sender, e, CommandOutcome.Cancelled);

    private void OnCommandFailed(object? sender, CommandEventArgs e) => Finish(sender, e, CommandOutcome.Failed);

    private void Finish(object? sender, CommandEventArgs e, CommandOutcome outcome) => Guard(() =>
    {
        if (_open.Count == 0 || sender is not Document document)
        {
            return;
        }

        // Pair with the newest open start of this command (transparent commands nest).
        var name = e.GlobalCommandName ?? string.Empty;
        var open = _open.Pop();
        if (!string.Equals(open.Name, name, StringComparison.OrdinalIgnoreCase) && _open.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            while (_open.Count > 0 && !string.Equals(open.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                open = _open.Pop();
            }
        }

        if (open.Action is null || _recording is not { } recording)
        {
            return;
        }

        IReadOnlyList<string> lines = [];
        if (open.HistoryBefore is { } before && AcadInput.History(HistoryLines) is { } after)
        {
            lines = AcadCopilot.Core.Drafting.CommandLineText.NewLines(before, after);
        }

        var objects = Describe(open, document);
        recording.CommandFinished(open.Action, outcome, lines, objects);
        RaiseChanged();
    });

    // One line per object the command created (still present), modified or erased; new objects are also kept for the
    // style analysis.
    private List<RecordedObject> Describe(OpenCommand open, Document document)
    {
        var appended = new HashSet<ObjectId>(open.Appended);
        var created = open.Appended.Where(id => !open.Erased.Contains(id)).Distinct().ToList();
        var modified = open.Modified.Where(id => !appended.Contains(id) && !open.Erased.Contains(id)).ToList();
        var erased = open.Erased.Where(id => !appended.Contains(id)).ToList();
        var result = new List<RecordedObject>(created.Count + modified.Count + erased.Count);
        if (created.Count + modified.Count + erased.Count == 0)
        {
            return result;
        }

        var database = document.Database;
        var key = database.UnmanagedObject;
        if (!_newObjects.TryGetValue(key, out var bucket))
        {
            bucket = (DrawingName(document), []);
            _newObjects[key] = bucket;
        }

        foreach (var id in created)
        {
            if (bucket.Ids.Count < MaxStyleObjects)
            {
                bucket.Ids.Add(id);
            }
        }

        try
        {
            var units = ToolContext.GetUnits(database);
            using var transaction = database.TransactionManager.StartOpenCloseTransaction();
            var described = 0;
            void Add(ObjectId id, ObjectChange change)
            {
                if (described >= UserRecording.MaxObjectsPerCommand)
                {
                    result.Add(new RecordedObject(change, "object", string.Empty, string.Empty));
                    return;
                }

                if (transaction.GetObject(id, OpenMode.ForRead, true) is not Entity entity)
                {
                    return;
                }

                described++;
                var summary = EntitySummary.Summarize(entity, units);
                var type = summary["type"]?.GetValue<string>() ?? EntitySummary.TypeName(entity);
                result.Add(new RecordedObject(change, type, entity.Layer, RecordingText.DescribeObject(summary)));
            }

            foreach (var id in created)
            {
                Add(id, ObjectChange.Created);
            }

            foreach (var id in modified)
            {
                Add(id, ObjectChange.Modified);
            }

            foreach (var id in erased)
            {
                Add(id, ObjectChange.Erased);
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Learn from me: the objects of a command could not be read", ex);
        }

        return result;
    }

    private void OnObjectAppended(object? sender, ObjectEventArgs e)
    {
        if (Top(sender) is { } open && EntityId(e.DBObject) is { } id)
        {
            open.Appended.Add(id);
        }
    }

    private void OnObjectModified(object? sender, ObjectEventArgs e)
    {
        if (Top(sender) is { } open && EntityId(e.DBObject) is { } id)
        {
            open.Modified.Add(id);
        }
    }

    private void OnObjectErased(object? sender, ObjectErasedEventArgs e)
    {
        if (Top(sender) is { } open && EntityId(e.DBObject) is { } id)
        {
            if (e.Erased)
            {
                open.Erased.Add(id);
            }
            else
            {
                open.Erased.Remove(id);
                open.Appended.Add(id);
            }
        }
    }

    // The recorded command that is running in the drawing of 'sender' (null when none or when it is not recorded).
    private OpenCommand? Top(object? sender)
    {
        if (_open.Count == 0)
        {
            return null;
        }

        var open = _open.Peek();
        try
        {
            return open.Action is not null && sender is Database database && open.Document.Database == database ? open : null;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static ObjectId? EntityId(DBObject? dbObject)
    {
        try
        {
            return dbObject is Entity ? dbObject.ObjectId : null;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Never disturb AutoCAD: a recording gap is better than an error in the user's command.
            CopilotLog.Warn("Learn from me: an AutoCAD event could not be recorded", ex);
        }
    }

    private sealed class OpenCommand(Document document, string name, RecordedAction? action, IReadOnlyList<string>? historyBefore)
    {
        public Document Document { get; } = document;

        public string Name { get; } = name;

        public RecordedAction? Action { get; } = action;

        public IReadOnlyList<string>? HistoryBefore { get; } = historyBefore;

        public List<ObjectId> Appended { get; } = [];

        public HashSet<ObjectId> Modified { get; } = [];

        public HashSet<ObjectId> Erased { get; } = [];
    }
}
