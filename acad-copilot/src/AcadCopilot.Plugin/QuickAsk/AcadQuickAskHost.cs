using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Panel;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Drafting;
using AcadCopilot.Plugin.Interaction;
using AcadCopilot.Plugin.Tools;
using AcadCopilot.Plugin.ViewModels;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.QuickAsk;

/// <summary>
/// Quick Ask's link to AutoCAD and the panel (main thread only).
/// <list type="bullet">
/// <item><b>Cursor point</b>: <c>Editor.PointMonitor</c> of the active drawing (re-attached on
/// <c>DocumentBecameCurrent</c>) keeps the last cursor position in the drawing; the handler only stores a point, so the
/// mouse never slows down. <c>InputPointContext.ComputedPoint</c> is in world coordinates (as AcEdInputPoint in
/// ObjectARX); feet-inch text comes from the drawing's units when the box opens.</item>
/// <item><b>Selection, Pick</b>: the panel's <see cref="AcadDrawingInteraction"/> (pickfirst selection; picks wait for
/// their turn in the chats' AutoCAD queue).</item>
/// <item><b>Undo</b>: waits for its turn in <see cref="CopilotBackend.DrawingQueue"/> like a tool call, switches to the
/// chat's drawing when needed and runs the copilot's own undo of its last run_commands calls
/// (<see cref="DraftingCommands.UndoAsync"/>, which refuses when the user changed the drawing since).</item>
/// <item><b>Open chat</b>: shows the palette (<see cref="Palette.PaletteHost.Show"/>) on that tab.</item>
/// </list>
/// AutoCAD API names verified by reflection over the AutoCAD 2027 assemblies (Editor.PointMonitor,
/// PointMonitorEventArgs.Context, InputPointContext.ComputedPoint / RawPoint / PointComputed). Compile-verified only.
/// </summary>
internal sealed class AcadQuickAskHost : IQuickAskHost, IDisposable
{
    private readonly CopilotApp _app;
    private readonly AcadDispatcher _dispatcher;
    private readonly AcadDrawingInteraction? _interaction;
    private Editor? _watchedEditor;
    private Document? _watchedDocument;
    private Point3d? _cursor;
    private bool _disposed;

    public AcadQuickAskHost(CopilotApp app, AcadDispatcher dispatcher)
    {
        _app = app;
        _dispatcher = dispatcher;
        try
        {
            _interaction = AcadDrawingInteraction.GetOrCreate(dispatcher, app.Backend?.DrawingQueue);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Quick Ask: the selection and Pick are not available", ex);
        }

        var documents = AcadApp.DocumentManager;
        documents.DocumentBecameCurrent += OnDocumentBecameCurrent;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        WatchCursor(documents.MdiActiveDocument);
    }

    public CopilotPanelViewModel? Shell => _app.Palette?.Pane?.Shell;

    public string? ActiveDrawingId => DrawingDirectory.Active()?.Id;

    public string? ActiveDrawingTitle => DrawingDirectory.Active()?.Title;

    /// <summary>The active document now (null when none).</summary>
    public static Document? ActiveDocument
    {
        get
        {
            try
            {
                return AcadApp.DocumentManager.MdiActiveDocument;
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            {
                return null;
            }
        }
    }

    public PickedPoint? CursorPoint
    {
        get
        {
            if (_cursor is not { } point || _watchedDocument is not { } document || !ReferenceEquals(document, ActiveDocument))
            {
                return null;
            }

            var units = ToolContext.GetUnits(document.Database);
            return new PickedPoint(
                ToolContext.Round(point.X),
                ToolContext.Round(point.Y),
                ToolContext.Round(point.Z),
                units.ToFeetInches(point.X),
                units.ToFeetInches(point.Y));
        }
    }

    public SelectionSnapshot? Selection => _interaction?.CurrentSelection;

    public Task<PickResult> PickPointAsync(CancellationToken cancellationToken) =>
        _interaction is { } interaction
            ? interaction.PickPointsAsync(
                new PickRequest { Prompt = "Click the point for your request", MaxPoints = 1, Source = PickSource.User, Timeout = TimeSpan.FromMinutes(2) },
                cancellationToken)
            : Task.FromResult(PickResult.Fail("Picking points is not available in this AutoCAD."));

    public void ShowChat(ChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (_app.Palette is not { } palette)
        {
            return;
        }

        palette.Show();
        if ((chat.Backend as ICopilotSession)?.Id is { } session)
        {
            palette.Pane?.ShowSession(session);
        }
    }

    public async Task<QuickAskUndoResult> UndoAsync(ChatViewModel chat, int calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (_app.Backend is not { } backend)
        {
            return new QuickAskUndoResult(false, "the AI Copilot did not start.");
        }

        var session = chat.Backend as ICopilotSession;
        var drawing = session?.Drawing;
        if (drawing is { IsClosed: true })
        {
            return new QuickAskUndoResult(false, $"'{drawing.Title}' was closed.");
        }

        using var turn = await backend.DrawingQueue.EnterAsync(session?.Id, "Undo (Quick Ask)", cancellationToken).ConfigureAwait(true);
        using var copilotWorking = AcadCopilot.Core.Learning.CopilotActivity.Shared.Begin();
        try
        {
            if (drawing is not null)
            {
                _ = await _dispatcher.InvokeAsync(() => DrawingDirectory.ActivateAsync(_dispatcher, drawing, cancellationToken), cancellationToken).ConfigureAwait(true);
            }

            AcadDispatcher.ExpectedDrawingId = drawing?.Id;
            var result = await _dispatcher.InvokeAsync(() => DraftingCommands.UndoAsync(_dispatcher, calls, cancellationToken), cancellationToken).ConfigureAwait(true);
            var message = result.Errors.Count > 0 ? string.Join(" ", result.Errors) : result.Message ?? string.Empty;
            CopilotLog.Info($"Quick Ask: undo of {calls} call(s): {(result.Success ? "done" : "failed")}. {message}");
            return new QuickAskUndoResult(result.Success, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CopilotLog.Warn("Quick Ask: undo failed", ex);
            return new QuickAskUndoResult(false, ex.Message);
        }
        finally
        {
            AcadDispatcher.ExpectedDrawingId = null;
        }
    }

    public void ReturnFocusToDrawing() => AcadInput.FocusDrawing();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var documents = AcadApp.DocumentManager;
            documents.DocumentBecameCurrent -= OnDocumentBecameCurrent;
            documents.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            // AutoCAD is closing.
        }

        WatchCursor(null);
    }

    private void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e) => WatchCursor(e.Document);

    private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        if (ReferenceEquals(e.Document, _watchedDocument))
        {
            WatchCursor(null);
        }
    }

    private void WatchCursor(Document? document)
    {
        if (ReferenceEquals(document, _watchedDocument) && !_disposed)
        {
            return;
        }

        if (_watchedEditor is { } previous)
        {
            try
            {
                previous.PointMonitor -= OnPointMonitor;
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            {
                // The drawing is closing.
            }
        }

        _watchedEditor = null;
        _watchedDocument = null;
        _cursor = null;
        if (document is null || _disposed)
        {
            return;
        }

        try
        {
            var editor = document.Editor;
            editor.PointMonitor += OnPointMonitor;
            _watchedEditor = editor;
            _watchedDocument = document;
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Quick Ask: cannot follow the cursor in this drawing", ex);
        }
    }

    // Runs on every mouse move over the drawing: store the point, nothing else.
    private void OnPointMonitor(object? sender, PointMonitorEventArgs e)
    {
        try
        {
            var context = e.Context;
            _cursor = context.PointComputed ? context.ComputedPoint : context.RawPoint;
        }
        catch (Exception)
        {
            // Never disturb the cursor.
        }
    }
}
