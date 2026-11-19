using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Recovery;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Drafting;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Recovery;

/// <summary>
/// Checkpoints of open drawings (create_checkpoint, list_checkpoints, restore_checkpoint, run_commands' autoCheckpoint).
/// <para><b>Design (chosen after comparing the options).</b> A checkpoint is TWO things:</para>
/// <list type="number">
/// <item><b>An AutoCAD UNDO mark</b> (<c>_.UNDO _Mark</c>, typed like the user would). Restoring types
/// <c>_.UNDO _Back</c> once per mark placed since then. This is exact: AutoCAD's own undo puts back every object with its
/// handle, every layer, style and setting, and paper space. It lives only while the drawing stays open in this AutoCAD
/// session, and marks are anonymous, so the copilot keeps the stack of marks it placed (<see cref="CheckpointCatalog"/>)
/// with a fingerprint of the drawing at each (entities by handle, layer count). After EVERY Back the drawing must match the
/// expected fingerprint; if it does not (the user undid past a mark, placed own marks, or undo was reset) the Backs are
/// reversed with REDO/MREDO and the restore stops, so nothing is lost. When no mark is left AutoCAD asks "This will undo
/// everything. OK?" and the copilot answers No.</item>
/// <item><b>A copy of the drawing</b> (<c>Database.Wblock()</c> = WBLOCK *, saved with <c>Database.SaveAs</c> to
/// %LOCALAPPDATA%\AcadCopilot\checkpoints\&lt;drawing&gt;\, never the user's file, at most 20 per drawing, deleted after
/// 7 days). It survives closing or crashing AutoCAD. Restoring from it (fromFile) erases the model-space objects and
/// inserts the copy's model space (Database.Insert): the geometry comes back, handles change, and layouts, layers and
/// styles of the current drawing stay as they are (only missing ones are added). Because that is less exact, it is only
/// used when asked for explicitly, and a checkpoint of the current state is taken first.</item>
/// </list>
/// Rejected alternatives: copying handles and properties of model-space objects (misses table records, dictionaries,
/// paper space, and cannot recreate erased objects faithfully); reopening the copy as the drawing (would close the
/// user's document). Everything here runs on AutoCAD's main thread inside the dispatcher.
/// </summary>
internal static class CheckpointService
{
    private const int PollMs = 30;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackTimeout = TimeSpan.FromSeconds(120);
    private static readonly Dictionary<IntPtr, CheckpointCatalog> Catalogs = [];
    private static readonly Lazy<CheckpointStore> LazyStore = new(CheckpointStore.CreateDefault);
    private static bool _documentsHooked;

    private enum BackResult
    {
        Done,
        NoMark,
        Timeout,
    }

    /// <summary>The checkpoint store.</summary>
    public static CheckpointStore Store => LazyStore.Value;

    /// <summary>
    /// Creates checkpoint <paramref name="name"/> of <paramref name="document"/>: an UNDO mark plus a copy of the drawing.
    /// Returns the checkpoint (null when neither could be made) and the problems met (null when none). Main thread.
    /// </summary>
    public static async Task<(CheckpointInfo? Checkpoint, string? Problem)> CreateAsync(
        AcadDispatcher dispatcher,
        Document document,
        string name,
        CheckpointOrigin origin,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(document);
        var clean = CheckpointNames.Normalize(name);
        if (clean is null)
        {
            return (null, "a checkpoint needs a short one-line name");
        }

        if (DraftingEngine.BusyReason(document, dispatcher) is { } busy)
        {
            return (null, "AutoCAD is busy: " + busy);
        }

        var database = document.Database;
        var catalog = CatalogOf(document);
        var fingerprint = Fingerprint(database);
        var created = DateTime.UtcNow;
        var problems = new List<string>();

        var marked = false;
        var undoctl = AcadInput.GetInt("UNDOCTL");
        if ((undoctl & 1) == 0)
        {
            problems.Add("UNDO is turned off in AutoCAD (UNDOCTL 0), so only a saved copy was made");
        }
        else if ((undoctl & 2) != 0)
        {
            problems.Add("UNDO is limited to one step (UNDO Control One), so only a saved copy was made");
        }
        else if ((undoctl & 8) != 0)
        {
            problems.Add("an UNDO group is open in AutoCAD, so no undo mark was placed; only a saved copy was made");
        }
        else
        {
            using (DraftingHistory.Instance.Suspend())
            {
                marked = await TypeAndWaitAsync(document, "_.UNDO _Mark\n", echo: false, CommandTimeout).ConfigureAwait(true);
            }

            if (!marked)
            {
                problems.Add("AutoCAD did not take UNDO Mark in time, so only a saved copy was made");
            }
        }

        var key = DrawingKey(document);
        string? file = null;
        try
        {
            file = WriteSnapshot(document, key, clean, created);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CopilotLog.Warn($"Checkpoint '{clean}': writing the copy failed", ex);
            problems.Add("the copy of the drawing could not be written (" + ex.Message + "), so it can only be restored while this drawing stays open");
        }

        if (!marked && file is null)
        {
            return (null, string.Join("; ", problems));
        }

        var checkpoint = catalog.Add(new CheckpointInfo
        {
            Name = clean,
            CreatedUtc = created,
            Fingerprint = fingerprint,
            HasUndoMark = marked,
            SnapshotFile = file,
            Origin = origin,
            Note = note,
        });

        if (file is not null)
        {
            try
            {
                foreach (var removed in Store.Record(key, document.Name, checkpoint))
                {
                    catalog.SnapshotRemoved(removed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CopilotLog.Warn($"Checkpoint '{clean}': updating the index failed", ex);
            }
        }

        CopilotLog.Info($"Checkpoint '{clean}' created ({fingerprint.Describe()}, undo mark {(marked ? "yes" : "no")}, copy {file ?? "none"}).");
        return (checkpoint, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <summary>The create_checkpoint result. Main thread.</summary>
    public static async Task<ToolResult> CreateToolAsync(AcadDispatcher dispatcher, string name)
    {
        var document = AcadDispatcher.RequireActiveDocument();
        var (checkpoint, problem) = await CreateAsync(dispatcher, document, name, CheckpointOrigin.Tool, null).ConfigureAwait(true);
        if (checkpoint is null)
        {
            return ToolResult.Fail("No checkpoint was created: " + problem + ".");
        }

        var message = $"Checkpoint '{checkpoint.Name}' saved ({checkpoint.Fingerprint.Describe()}). restore_checkpoint '{checkpoint.Name}' goes back to it.";
        var result = ToolResult.Ok(problem is null ? message : message + " Note: " + problem + ".", Describe(checkpoint));
        return problem is null ? result : result with { Warnings = [problem] };
    }

    /// <summary>The list_checkpoints result for the active drawing. Main thread.</summary>
    public static ToolResult ListTool()
    {
        var document = AcadDispatcher.RequireActiveDocument();
        var all = AllOf(document);
        var title = Path.GetFileName(document.Name);
        var list = new JsonArray(all.OrderByDescending(c => c.CreatedUtc).Select(c => (JsonNode?)Describe(c)).ToArray());
        if (all.Count == 0)
        {
            return ToolResult.Ok($"No checkpoints for {title} yet. create_checkpoint makes one (run_commands makes one before calls of 10+ steps).", new JsonObject { ["checkpoints"] = list });
        }

        var names = string.Join(", ", all.OrderByDescending(c => c.CreatedUtc).Take(10).Select(c => $"'{c.Name}' ({How(c)})"));
        return ToolResult.Ok($"{all.Count} checkpoint(s) for {title}, newest first: {names}.", new JsonObject { ["checkpoints"] = list });
    }

    /// <summary>The restore_checkpoint body (approved already: the tool is Destructive). Main thread.</summary>
    public static async Task<ToolResult> RestoreToolAsync(AcadDispatcher dispatcher, string name, bool fromFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var document = AcadDispatcher.RequireActiveDocument();
        if (DraftingEngine.BusyReason(document, dispatcher) is { } busy)
        {
            return ToolResult.Fail("AutoCAD is busy: " + busy);
        }

        var all = AllOf(document);
        var clean = CheckpointNames.Normalize(name);
        var checkpoint = all.FirstOrDefault(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
        if (checkpoint is null)
        {
            return ToolResult.Fail(all.Count == 0
                ? $"There is no checkpoint '{name}' (this drawing has none)."
                : $"There is no checkpoint '{name}'. Known: {string.Join(", ", all.Select(c => $"'{c.Name}'"))}.");
        }

        if (fromFile)
        {
            return await RestoreFromFileAsync(dispatcher, document, checkpoint, cancellationToken).ConfigureAwait(true);
        }

        var catalog = CatalogOf(document);
        var plan = catalog.PlanUndoRestore(checkpoint.Name);
        if (plan is null)
        {
            return ToolResult.Fail(
                $"Checkpoint '{checkpoint.Name}' has no undo mark in this AutoCAD session (AutoCAD was restarted, the drawing reopened, or its mark was used up), so nothing was undone. " +
                (checkpoint.SnapshotFile is not null && File.Exists(checkpoint.SnapshotFile)
                    ? "Its saved copy exists: call restore_checkpoint again with fromFile true to replace the model-space objects with the copy's (handles change; layouts and layers stay). Tell the user first."
                    : "Its saved copy is gone too."));
        }

        if ((AcadInput.GetInt("UNDOCTL") & 1) == 0)
        {
            catalog.InvalidateMarks();
            return ToolResult.Fail("UNDO is turned off in AutoCAD (UNDOCTL 0), so the checkpoint cannot be restored with undo; nothing was changed.");
        }

        var database = document.Database;
        var start = Fingerprint(database);
        var backs = 0;
        string? problem = null;
        using (DraftingHistory.Instance.Suspend())
        {
            // 'next' = the mark the next UNDO Back should reach. A Back may land on a deeper mark when an earlier one was
            // used up meanwhile (the undo tool's U passes marks): the state then matches a later expected fingerprint.
            var next = 0;
            while (next < plan.BackCount)
            {
                if (cancellationToken.IsCancellationRequested && backs > 0)
                {
                    problem = "Stopped by the user part way";
                    break;
                }

                var back = await UndoBackAsync(document).ConfigureAwait(true);
                if (back == BackResult.NoMark)
                {
                    problem = "AutoCAD had no undo mark left at this point (the undo history was cleared or undone past the mark); the copilot answered No to 'undo everything'";
                    break;
                }

                if (back == BackResult.Timeout)
                {
                    problem = "AutoCAD did not finish UNDO Back in time";
                    break;
                }

                backs++;
                var now = Fingerprint(database);
                var reached = -1;
                for (var j = next; j < plan.BackCount; j++)
                {
                    if (plan.Expected[j] == now)
                    {
                        reached = j;
                        break;
                    }
                }

                if (reached < 0)
                {
                    problem = "after UNDO Back the drawing did not match the checkpoint (the undo history no longer lines up with the copilot's marks)";
                    break;
                }

                next = reached + 1;
            }

            if (problem is not null)
            {
                // Put the drawing back the way it was before the restore started.
                var reverted = backs == 0 || await RedoAsync(document, backs).ConfigureAwait(true);
                var now = Fingerprint(database);
                catalog.InvalidateMarks();
                var state = now == start
                    ? "The drawing is unchanged."
                    : reverted ? "The drawing may differ from before the restore; look at it with capture_view." : "REDO did not complete; look at the drawing with capture_view.";
                return ToolResult.Fail(
                    $"Checkpoint '{checkpoint.Name}' was not restored: {problem}. {state} " +
                    (checkpoint.SnapshotFile is not null && File.Exists(checkpoint.SnapshotFile)
                        ? "Its saved copy can still be restored with fromFile true."
                        : string.Empty)) with
                {
                    Data = new JsonObject { ["restored"] = false, ["undoBacks"] = backs, ["unchanged"] = now == start },
                };
            }

            catalog.MarksUsed(plan.BackCount);
            if ((AcadInput.GetInt("UNDOCTL") & 8) == 0 && await TypeAndWaitAsync(document, "_.UNDO _Mark\n", echo: false, CommandTimeout).ConfigureAwait(true))
            {
                // Keep the checkpoint restorable: a new mark at the restored state.
                catalog.Remarked(checkpoint.Name, plan.Expected[^1]);
            }
        }

        var forgotten = DraftingHistory.Instance.ForgetAfter(database, checkpoint.CreatedUtc);
        var restored = Fingerprint(database);
        CopilotLog.Info($"Checkpoint '{checkpoint.Name}' restored with {backs} UNDO Back(s) ({restored.Describe()}); {forgotten} copilot call(s) forgotten.");
        return ToolResult.Ok(
            $"Restored checkpoint '{checkpoint.Name}' with AutoCAD's undo ({backs} UNDO Back): the drawing is exactly as it was then ({restored.Describe()}). " +
            "Everything done after it is undone; the user can bring it back with REDO right away.",
            new JsonObject
            {
                ["restored"] = true,
                ["method"] = "undo",
                ["name"] = checkpoint.Name,
                ["undoBacks"] = backs,
                ["objects"] = restored.EntityCount,
                ["layers"] = restored.LayerCount,
            });
    }

    /// <summary>All checkpoints of <paramref name="document"/>: this session's plus saved copies from earlier sessions.</summary>
    public static IReadOnlyList<CheckpointInfo> AllOf(Document document)
    {
        var catalog = CatalogOf(document);
        var all = catalog.Checkpoints.ToList();
        try
        {
            foreach (var stored in Store.List(DrawingKey(document)))
            {
                if (!all.Any(c => string.Equals(c.Name, stored.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    all.Add(stored);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CopilotLog.Warn("Checkpoints: reading the saved copies failed", ex);
        }

        return all;
    }

    /// <summary>The fingerprint of a drawing: entities of model space and all layouts by handle, and the layer count.</summary>
    public static DrawingFingerprint Fingerprint(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var handles = new List<long>();
        var layers = 0;
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blocks)
        {
            if (transaction.GetObject(blockId, OpenMode.ForRead) is BlockTableRecord { IsLayout: true } layout)
            {
                foreach (ObjectId id in layout)
                {
                    if (!id.IsErased)
                    {
                        handles.Add(id.Handle.Value);
                    }
                }
            }
        }

        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in layerTable)
        {
            if (!id.IsErased)
            {
                layers++;
            }
        }

        transaction.Commit();
        return DrawingFingerprint.Of(handles, layers);
    }

    private static async Task<ToolResult> RestoreFromFileAsync(AcadDispatcher dispatcher, Document document, CheckpointInfo checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint.SnapshotFile is not { } file || !File.Exists(file))
        {
            return ToolResult.Fail($"Checkpoint '{checkpoint.Name}' has no saved copy (it was not written or was deleted), so it cannot be restored from a file.");
        }

        if (document.IsReadOnly)
        {
            return ToolResult.Fail($"The drawing '{Path.GetFileName(document.Name)}' is open read-only; nothing was changed.");
        }

        // A safety net first: the current state can be restored with undo or from its own copy.
        var safetyName = CheckpointNames.ForRun("restore of " + checkpoint.Name);
        var (safety, _) = await CreateAsync(dispatcher, document, safetyName, CheckpointOrigin.Auto, "restore_checkpoint fromFile").ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        var database = document.Database;
        var undoOn = (AcadInput.GetInt("UNDOCTL") & 1) != 0 && (AcadInput.GetInt("UNDOCTL") & 8) == 0;
        var erased = 0;
        using (DraftingHistory.Instance.Suspend())
        {
            if (undoOn)
            {
                await TypeAndWaitAsync(document, "_.UNDO _BEgin\n", echo: false, CommandTimeout).ConfigureAwait(true);
            }

            try
            {
                using var source = new Database(false, true);
                source.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                source.CloseInput(true);
                using var documentLock = AcadDispatcher.LockDocument(document);
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
                    foreach (ObjectId id in modelSpace)
                    {
                        if (!id.IsErased && transaction.GetObject(id, OpenMode.ForWrite, false, true) is Entity entity)
                        {
                            entity.Erase();
                            erased++;
                        }
                    }

                    transaction.Commit();
                }

                database.Insert(Matrix3d.Identity, source, false);
            }
            finally
            {
                if (undoOn)
                {
                    await TypeAndWaitAsync(document, "_.UNDO _End\n", echo: false, CommandTimeout).ConfigureAwait(true);
                }
            }
        }

        try
        {
            document.Editor.Regen();
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
        {
            CopilotLog.Warn("Checkpoint restore: REGEN failed", ex);
        }

        CatalogOf(document).InvalidateMarks();
        DraftingHistory.Instance.ForgetAfter(database, DateTime.MinValue);
        var now = Fingerprint(database);
        CopilotLog.Info($"Checkpoint '{checkpoint.Name}' restored from {file}: {erased} objects erased, now {now.Describe()}.");
        return ToolResult.Ok(
            $"Restored checkpoint '{checkpoint.Name}' from its saved copy: {erased} model-space objects were replaced by the copy's {now.EntityCount} objects " +
            "(new handles; layouts, layers and styles of the drawing were kept). " +
            (safety is null ? string.Empty : $"The state before the restore is checkpoint '{safety.Name}'. ") +
            (undoOn ? "Ctrl+Z (one step) takes the restore back." : string.Empty),
            new JsonObject
            {
                ["restored"] = true,
                ["method"] = "file",
                ["name"] = checkpoint.Name,
                ["file"] = file,
                ["erased"] = erased,
                ["objects"] = now.EntityCount,
                ["safetyCheckpoint"] = safety?.Name,
            });
    }

    private static JsonObject Describe(CheckpointInfo checkpoint) => new()
    {
        ["name"] = checkpoint.Name,
        ["created"] = checkpoint.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        ["objects"] = checkpoint.Fingerprint.EntityCount,
        ["layers"] = checkpoint.Fingerprint.LayerCount,
        ["restore"] = How(checkpoint),
        ["origin"] = checkpoint.Origin == CheckpointOrigin.Auto ? "auto" : "tool",
        ["note"] = checkpoint.Note,
    };

    private static string How(CheckpointInfo checkpoint)
    {
        var file = checkpoint.SnapshotFile is not null && File.Exists(checkpoint.SnapshotFile);
        return checkpoint.HasUndoMark ? (file ? "undo+file" : "undo") : file ? "file" : "none";
    }

    private static CheckpointCatalog CatalogOf(Document document)
    {
        HookDocuments();
        var key = document.Database.UnmanagedObject;
        if (!Catalogs.TryGetValue(key, out var catalog))
        {
            catalog = new CheckpointCatalog();
            Catalogs[key] = catalog;
        }

        return catalog;
    }

    private static string DrawingKey(Document document) =>
        CheckpointStore.DrawingKey(document.IsNamedDrawing
            ? document.Name
            : $"unsaved-{ProcessIdentity.Current.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Path.GetFileName(document.Name)}");

    private static string WriteSnapshot(Document document, string key, string name, DateTime created)
    {
        var path = Store.NewSnapshotPath(key, name, created);
        using var documentLock = AcadDispatcher.LockDocument(document);
        using var copy = document.Database.Wblock();
        copy.SaveAs(path, DwgVersion.Current);
        return path;
    }

    // UNDO Back to the newest mark. Without a mark AutoCAD asks "This will undo everything. OK? <Y>:"; the copilot answers No.
    private static async Task<BackResult> UndoBackAsync(Document document)
    {
        AcadInput.Type(document, "_.UNDO _Back\n", echo: true);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < BackTimeout)
        {
            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
            if (AcadInput.IsCommandActive() && AcadInput.LastPrompt().Contains("undo everything", StringComparison.OrdinalIgnoreCase))
            {
                await TypeAndWaitAsync(document, "_N\n", echo: true, CommandTimeout).ConfigureAwait(true);
                return BackResult.NoMark;
            }

            if (stopwatch.ElapsedMilliseconds >= 150 && !AcadInput.IsCommandActive() && AcadInput.IsInputPending() != true)
            {
                return BackResult.Done;
            }
        }

        return BackResult.Timeout;
    }

    // Reverses the last 'count' UNDO Backs: REDO for one, MREDO for several.
    private static Task<bool> RedoAsync(Document document, int count) =>
        count == 1
            ? TypeAndWaitAsync(document, "_.REDO\n", echo: true, BackTimeout)
            : TypeAndWaitAsync(document, "_.MREDO " + count.ToString(CultureInfo.InvariantCulture) + "\n", echo: true, BackTimeout);

    // Types copilot bookkeeping and waits until AutoCAD is back at the Command prompt with nothing queued.
    private static async Task<bool> TypeAndWaitAsync(Document document, string text, bool echo, TimeSpan timeout)
    {
        AcadInput.Type(document, text, echo);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
            if (stopwatch.ElapsedMilliseconds >= 150 && !AcadInput.IsCommandActive() && AcadInput.IsInputPending() != true)
            {
                return true;
            }
        }

        return false;
    }

    private static void HookDocuments()
    {
        if (_documentsHooked)
        {
            return;
        }

        AcadApp.DocumentManager.DocumentToBeDestroyed += (_, e) =>
        {
            try
            {
                if (e.Document?.Database is { } database)
                {
                    Catalogs.Remove(database.UnmanagedObject);
                }
            }
            catch (Exception ex)
            {
                CopilotLog.Warn("Checkpoints: forgetting a closing drawing failed", ex);
            }
        };
        _documentsHooked = true;
    }
}
