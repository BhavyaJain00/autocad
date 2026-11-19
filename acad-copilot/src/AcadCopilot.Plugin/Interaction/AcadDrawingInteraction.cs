using System.IO;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Drafting;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Interaction;

/// <summary>
/// "Click to point" and "select and ask" inside AutoCAD (one instance per AutoCAD session, created on the main thread
/// with <see cref="GetOrCreate"/>).
/// <para>
/// <b>Picking from a modeless palette.</b> The palette's buttons and the copilot's dispatcher items run in AutoCAD's
/// application context, where <c>Editor.GetPoint</c> cannot prompt (it needs a command). The pick therefore runs inside
/// <c>DocumentCollection.ExecuteInCommandContextAsync</c> (AutoCAD 2016+, accoremgd; verified by reflection in AutoCAD
/// 2027: <c>ExecutionResult ExecuteInCommandContextAsync(Func&lt;object, Task&gt;, object)</c>, awaitable), which runs the
/// callback like a command of the active drawing: GetPoint shows its prompt in the command line, the user clicks
/// (object snaps, ortho and transparent zoom/pan work), Enter finishes a multi-point pick and Esc cancels. Keyboard
/// focus is moved to the drawing first (<c>Internal.Utils.SetFocusToDwgView</c> through <see cref="AcadInput.FocusDrawing"/>).
/// A time limit or the panel's Cancel presses Esc for the user (<see cref="AcadInput.Escape"/>, typed input that the
/// waiting GetPoint takes as a cancel). Points come back in world coordinates (UCS -&gt; WCS).
/// </para>
/// <para>
/// <b>Selection.</b> Follows <c>Document.ImpliedSelectionChanged</c> (and the end of commands) of the active drawing,
/// debounced, and reads the pickfirst set with <c>Editor.SelectImplied</c>; the first
/// <see cref="SelectionSnapshot.MaxDescribed"/> objects are described with <see cref="EntitySummary"/>. The last non-empty
/// selection is kept for get_selection ("last").
/// </para>
/// Compile-verified only: nothing here has run inside AutoCAD yet.
/// </summary>
public sealed class AcadDrawingInteraction : IDrawingInteraction, IDisposable
{
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly Lock CreateGate = new();
    private static AcadDrawingInteraction? _shared;

    private readonly AcadDispatcher _acad;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _selectionTimer;
    private AcadTurnQueue? _queue;
    private Document? _document;
    private SelectionSnapshot? _selection;
    private PickInProgress? _activePick;
    private string? _cancelReason;
    private Document? _pickDocument;
    private ObjectId[] _lastSelection = [];
    private Document? _lastSelectionDocument;
    private bool _disposed;

    private AcadDrawingInteraction(AcadDispatcher dispatcher)
    {
        _acad = dispatcher;
        _ui = Dispatcher.CurrentDispatcher;
        _selectionTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = SelectionDebounce };
        _selectionTimer.Tick += (_, _) =>
        {
            _selectionTimer.Stop();
            RefreshSelection();
        };

        var documents = AcadApp.DocumentManager;
        documents.DocumentBecameCurrent += OnDocumentBecameCurrent;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        Attach(documents.MdiActiveDocument);
    }

    /// <inheritdoc />
    public event EventHandler? SelectionChanged;

    /// <inheritdoc />
    public event EventHandler? PickStateChanged;

    /// <inheritdoc />
    public SelectionSnapshot? CurrentSelection => _selection;

    /// <inheritdoc />
    public PickInProgress? ActivePick => _activePick;

    /// <summary>
    /// The session's instance, created on first use. Main thread only (the palette host, or a tool handler inside
    /// <see cref="AcadDispatcher.InvokeAsync{T}(Func{T}, CancellationToken)"/>).
    /// </summary>
    /// <param name="dispatcher">AutoCAD's main-thread dispatcher.</param>
    /// <param name="queue">The chats' AutoCAD turn queue: picks started in the panel wait for their turn (null keeps the current one).</param>
    public static AcadDrawingInteraction GetOrCreate(AcadDispatcher dispatcher, AcadTurnQueue? queue = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (!dispatcher.IsMainThread)
        {
            throw new InvalidOperationException("AcadDrawingInteraction must be created on AutoCAD's main thread.");
        }

        lock (CreateGate)
        {
            _shared ??= new AcadDrawingInteraction(dispatcher);
            if (queue is not null)
            {
                _shared._queue = queue;
            }

            return _shared;
        }
    }

    /// <inheritdoc />
    public async Task<PickResult> PickPointsAsync(PickRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (_queue is null)
            {
                return await PickInTurnAsync(request, cancellationToken).ConfigureAwait(true);
            }

            // AutoCAD has one command line: a click asked for by the panel waits while a chat is typing into AutoCAD.
            using var turn = await _queue.EnterAsync(null, "Waiting for your click", cancellationToken).ConfigureAwait(true);
            return await PickInTurnAsync(request, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new PickResult { Status = PickStatus.Cancelled, Message = "The pick was cancelled." };
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Pick point failed", ex);
            return PickResult.Fail("AutoCAD could not ask for a point: " + ex.Message);
        }
    }

    /// <summary>
    /// The pick itself, for a caller that already holds AutoCAD's turn (the pick_point tool runs inside the pipe handler's
    /// turn). Any thread. Expected problems come back as <see cref="PickStatus.Failed"/>.
    /// </summary>
    public Task<PickResult> PickInTurnAsync(PickRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.Normalized();
        return _acad.InvokeAsync(() => PickOnMainThreadAsync(normalized, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public void CancelPick() => RunOnUi(() => RequestCancel("Cancelled in the AI Copilot panel."));

    /// <summary>
    /// The current selection, or (when nothing is selected and <paramref name="allowLast"/>) the last one the user made in
    /// this drawing, as object ids. Main thread only.
    /// </summary>
    /// <returns>The ids and where they came from ("current", "last" or "none").</returns>
    public (ObjectId[] Ids, string Source) ReadSelectionIds(Document document, bool allowCurrent, bool allowLast)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (allowCurrent)
        {
            var current = SelectImplied(document);
            if (current.Length > 0)
            {
                return (current, "current");
            }
        }

        if (allowLast)
        {
            if (ReferenceEquals(_lastSelectionDocument, document))
            {
                var last = _lastSelection.Where(id => !id.IsNull && !id.IsErased).ToArray();
                if (last.Length > 0)
                {
                    return (last, "last");
                }
            }

            try
            {
                var previous = document.Editor.SelectPrevious();
                if (previous.Status == PromptStatus.OK && previous.Value is { } set)
                {
                    var ids = set.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).ToArray();
                    if (ids.Length > 0)
                    {
                        return (ids, "last");
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                CopilotLog.Info("SelectPrevious is not available here: " + ex.Message);
            }
        }

        return ([], "none");
    }

    /// <summary>Selects (grips + highlight) <paramref name="ids"/> for the user, replacing the selection. Main thread only.</summary>
    public void Highlight(Document document, ObjectId[] ids)
    {
        ArgumentNullException.ThrowIfNull(document);
        using (AcadDispatcher.LockDocument(document))
        {
            document.Editor.SetImpliedSelection(ids);
        }

        document.Editor.UpdateScreen();
        RememberSelection(document, ids);
        _selectionTimer.Stop();
        _selectionTimer.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _selectionTimer.Stop();
        try
        {
            var documents = AcadApp.DocumentManager;
            documents.DocumentBecameCurrent -= OnDocumentBecameCurrent;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            Detach();
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not detach the selection handlers", ex);
        }
    }

    private async Task<PickResult> PickOnMainThreadAsync(PickRequest request, CancellationToken cancellationToken)
    {
        if (_activePick is not null)
        {
            return PickResult.Fail("AutoCAD is already waiting for the user to click a point. Wait for that pick to finish.");
        }

        var documents = AcadApp.DocumentManager;
        var document = documents.MdiActiveDocument;
        if (document is null)
        {
            return PickResult.Fail("No drawing is open in AutoCAD, so there is nothing to click in.");
        }

        if (AcadInput.IsCommandActive())
        {
            var command = SafeCommandName(document);
            return PickResult.Fail($"AutoCAD is busy with a command{(command is null ? string.Empty : $" ({command})")}. Finish or cancel it (Esc), then try again.");
        }

        if (!documents.IsApplicationContext)
        {
            return PickResult.Fail("AutoCAD cannot ask for a point right now (a command is running). Try again in a moment.");
        }

        var units = ToolContext.GetUnits(document.Database);
        var title = Path.GetFileName(document.Name);
        var points = new List<PickedPoint>();
        _cancelReason = null;
        _pickDocument = document;
        SetActivePick(new PickInProgress(request.Prompt, request.Source, request.MaxPoints, 0));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(request.Timeout);
        using var registration = limit.Token.Register(() => RunOnUi(() => RequestCancel(
            cancellationToken.IsCancellationRequested ? "Cancelled." : "timeout")));
        try
        {
            await documents.ExecuteInCommandContextAsync(
                _ =>
                {
                    var editor = document.Editor;
                    AcadInput.FocusDrawing();
                    var lastUcs = default(Autodesk.AutoCAD.Geometry.Point3d);
                    while (points.Count < request.MaxPoints && _cancelReason is null)
                    {
                        var options = new PromptPointOptions("\n" + PromptLine(request, points.Count))
                        {
                            AllowNone = points.Count > 0,
                        };
                        if (points.Count > 0)
                        {
                            options.UseBasePoint = true;
                            options.BasePoint = lastUcs;
                            options.UseDashedLine = true;
                        }

                        var answer = editor.GetPoint(options);
                        if (answer.Status == PromptStatus.OK)
                        {
                            lastUcs = answer.Value;
                            var world = answer.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                            points.Add(new PickedPoint(
                                ToolContext.Round(world.X),
                                ToolContext.Round(world.Y),
                                ToolContext.Round(world.Z),
                                units.ToFeetInches(world.X),
                                units.ToFeetInches(world.Y)));
                            SetActivePick(new PickInProgress(request.Prompt, request.Source, request.MaxPoints, points.Count));
                            continue;
                        }

                        // Enter (None) finishes a multi-point pick; Esc (Cancel) or anything else ends it.
                        if (answer.Status != PromptStatus.None)
                        {
                            _cancelReason ??= "Esc";
                        }

                        break;
                    }

                    var done = points.Count switch
                    {
                        0 => "AI Copilot: no point picked.",
                        1 => "AI Copilot: point sent to the panel.",
                        _ => $"AI Copilot: {points.Count} points sent to the panel.",
                    };
                    editor.WriteMessage("\n" + done);
                    return Task.CompletedTask;
                },
                null);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Pick point: AutoCAD refused to prompt", ex);
            if (points.Count == 0)
            {
                return PickResult.Fail("AutoCAD could not ask for a point right now (" + ex.Message + "). Make sure no command is running and try again.");
            }
        }
        finally
        {
            _pickDocument = null;
            SetActivePick(null);
            try
            {
                document.Editor.PostCommandPrompt();
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            {
                // The command line refreshes by itself.
            }
        }

        if (points.Count > 0)
        {
            return new PickResult
            {
                Status = PickStatus.Picked,
                Points = points,
                DrawingTitle = title,
                Message = points.Count == 1 ? $"The user clicked 1 point in {title}." : $"The user clicked {points.Count} points in {title}.",
            };
        }

        return _cancelReason == "timeout"
            ? new PickResult { Status = PickStatus.TimedOut, DrawingTitle = title, Message = $"Nobody clicked within {request.Timeout.TotalSeconds:0} s." }
            : new PickResult { Status = PickStatus.Cancelled, DrawingTitle = title, Message = "The user pressed Esc (no point was picked)." };
    }

    private static string PromptLine(PickRequest request, int picked)
    {
        if (request.MaxPoints <= 1)
        {
            return request.Prompt + ": ";
        }

        return picked == 0
            ? $"{request.Prompt} (point 1 of up to {request.MaxPoints}): "
            : $"{request.Prompt} (point {picked + 1}, Enter to finish): ";
    }

    private static string? SafeCommandName(Document document)
    {
        try
        {
            return document.CommandInProgress is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // Main thread: ends the waiting GetPoint by typing Esc into the command line.
    private void RequestCancel(string reason)
    {
        if (_activePick is null || _pickDocument is not { } document)
        {
            return;
        }

        _cancelReason ??= reason;
        try
        {
            AcadInput.Escape(document);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Could not press Esc to end the pick", ex);
        }
    }

    private void SetActivePick(PickInProgress? pick)
    {
        _activePick = pick;
        Raise(PickStateChanged);
    }

    private void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e) => Attach(e.Document);

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        if (ReferenceEquals(e.Document, _lastSelectionDocument))
        {
            _lastSelectionDocument = null;
            _lastSelection = [];
        }

        if (ReferenceEquals(e.Document, _document))
        {
            Detach();
            SetSelection(null);
        }
    }

    private void Attach(Document? document)
    {
        if (_disposed || ReferenceEquals(document, _document))
        {
            return;
        }

        Detach();
        _document = document;
        if (document is not null)
        {
            document.ImpliedSelectionChanged += OnImpliedSelectionChanged;
            document.CommandEnded += OnCommandFinished;
            document.CommandCancelled += OnCommandFinished;
            document.CommandFailed += OnCommandFinished;
        }

        ScheduleRefresh();
    }

    private void Detach()
    {
        if (_document is { } previous)
        {
            try
            {
                previous.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
                previous.CommandEnded -= OnCommandFinished;
                previous.CommandCancelled -= OnCommandFinished;
                previous.CommandFailed -= OnCommandFinished;
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            {
                // The document is being destroyed.
            }
        }

        _document = null;
    }

    private void OnImpliedSelectionChanged(object? sender, EventArgs e) => ScheduleRefresh();

    private void OnCommandFinished(object? sender, CommandEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        // Many changes arrive per click (window selection, grips): read once they settle.
        _selectionTimer.Stop();
        _selectionTimer.Start();
    }

    private void RefreshSelection()
    {
        try
        {
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            if (document is null)
            {
                SetSelection(null);
                return;
            }

            if (_activePick is not null || AcadInput.IsCommandActive())
            {
                // During a command the pickfirst set is the command's business; read again when it ends.
                return;
            }

            var ids = SelectImplied(document);
            if (ids.Length == 0)
            {
                SetSelection(null);
                return;
            }

            RememberSelection(document, ids);
            SetSelection(Describe(document, ids));
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Could not read the selection", ex);
        }
    }

    private static ObjectId[] SelectImplied(Document document)
    {
        var result = document.Editor.SelectImplied();
        return result.Status == PromptStatus.OK && result.Value is { } set
            ? [.. set.GetObjectIds().Where(id => !id.IsNull && !id.IsErased)]
            : [];
    }

    private static SelectionSnapshot Describe(Document document, ObjectId[] ids)
    {
        var units = ToolContext.GetUnits(document.Database);
        var items = new List<SelectedObject>();
        using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var id in ids.Take(SelectionSnapshot.MaxDescribed))
            {
                if (transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
                {
                    continue;
                }

                var summary = EntitySummary.Summarize(entity, units);
                items.Add(new SelectedObject(
                    ToolContext.HandleOf(id),
                    EntitySummary.TypeName(entity),
                    entity.Layer,
                    InteractionText.ShortDetail(summary)));
            }

            transaction.Commit();
        }

        return new SelectionSnapshot { Count = ids.Length, Items = items, DrawingTitle = Path.GetFileName(document.Name) };
    }

    private void RememberSelection(Document document, ObjectId[] ids)
    {
        if (ids.Length > 0)
        {
            _lastSelection = ids;
            _lastSelectionDocument = document;
        }
    }

    private void SetSelection(SelectionSnapshot? selection)
    {
        if (selection?.Key == _selection?.Key)
        {
            return;
        }

        _selection = selection;
        Raise(SelectionChanged);
    }

    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("A panel handler of the drawing interaction failed", ex);
        }
    }

    private void RunOnUi(Action action)
    {
        if (_ui.CheckAccess())
        {
            action();
        }
        else if (!_ui.HasShutdownStarted)
        {
            _ = _ui.BeginInvoke(DispatcherPriority.Normal, action);
        }
    }
}
