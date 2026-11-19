using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Acad;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>
/// The bodies of cancel_command (Esc Esc) and undo (take back the copilot's last run_commands calls). Both run on
/// AutoCAD's main thread inside the dispatcher and type visibly, like the user would.
/// </summary>
internal static class DraftingCommands
{
    private const int PollMs = 30;
    private const int MaxExtraUndoSteps = 3;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Cancels the active command with Esc (twice). Main thread.</summary>
    public static async Task<ToolResult> CancelAsync()
    {
        var document = AcadDispatcher.RequireActiveDocument();
        var command = document.CommandInProgress;
        var prompt = AcadInput.LastPrompt();
        var editorOpen = AcadInput.IsTextEditorActive() == true;
        if (AcadInput.CommandActive() == 0 && string.IsNullOrEmpty(command) && !editorOpen)
        {
            return ToolResult.Ok("No command was running; AutoCAD is at the Command prompt.", new JsonObject { ["cancelled"] = false });
        }

        AcadInput.Escape(document);
        var stopwatch = Stopwatch.StartNew();
        var keyPressed = false;
        while (stopwatch.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
            if (AcadInput.CommandActive() == 0 && AcadInput.IsTextEditorActive() != true)
            {
                var what = string.IsNullOrEmpty(command) ? "the active command" : command;
                return ToolResult.Ok(
                    $"Cancelled {what}{(prompt.Length > 0 ? $" (it was waiting at '{prompt}')" : string.Empty)}. Nothing was undone.",
                    new JsonObject { ["cancelled"] = true, ["command"] = command, ["prompt"] = prompt });
            }

            if (!keyPressed && stopwatch.ElapsedMilliseconds > 800)
            {
                keyPressed = true;
                DialogCloser.PressEscapeKey();
            }
        }

        return ToolResult.Fail(
            $"AutoCAD did not cancel {(string.IsNullOrEmpty(command) ? "the active command" : command)} within 3 s (it may be showing a dialog box or busy). " +
            "Ask the user to press Esc in AutoCAD or close the dialog.");
    }

    /// <summary>
    /// Undoes the copilot's last <paramref name="count"/> run_commands calls in the active drawing, newest first, with
    /// AutoCAD's U command (each call is one UNDO group). Refuses when the drawing changed after the newest call. Each U is
    /// checked: it must change the call's own objects; a U that only took back a zoom or pan is followed by another
    /// (at most three), and a U that changed other objects is reversed with REDO and the undo stops. Main thread.
    /// </summary>
    public static async Task<ToolResult> UndoAsync(AcadDispatcher dispatcher, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var document = AcadDispatcher.RequireActiveDocument();
        if (DraftingEngine.BusyReason(document, dispatcher) is { } busy)
        {
            return ToolResult.Fail("AutoCAD is busy: " + busy);
        }

        var database = document.Database;
        DraftingHistory.Instance.Watch(document);
        var runs = DraftingHistory.Instance.Latest(database, count);
        if (runs.Count == 0)
        {
            return ToolResult.Fail(
                "There is no copilot drawing to undo in this drawing (nothing was drawn by run_commands since AutoCAD started, or it was " +
                "already undone). To undo the user's own work, ask them to press Ctrl+Z.");
        }

        var foreign = DraftingHistory.Instance.ForeignChanges(database);
        if (foreign != runs[0].ForeignChangesAtEnd)
        {
            return ToolResult.Fail(
                $"The drawing was changed after the copilot's last call '{runs[0].Title}' (by the user, an undo, or another program), so " +
                "undoing now could take back their work instead. Ask the user to press Ctrl+Z themselves, or draw the correction instead.");
        }

        var undone = new JsonArray();
        var removed = 0;
        var restored = 0;
        string? problem = null;
        using (DraftingHistory.Instance.Suspend())
        {
            foreach (var run in runs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    problem = "Stopped by the user before all calls were undone.";
                    break;
                }

                var ours = new HashSet<ObjectId>(run.Created.Concat(run.Erased).Concat(run.Modified));
                var done = false;
                for (var attempt = 0; attempt <= MaxExtraUndoSteps && !done; attempt++)
                {
                    var touched = await TypeAndWatchAsync(document, "_.U\n").ConfigureAwait(true);
                    if (touched.Overlaps(ours))
                    {
                        done = true;
                    }
                    else if (touched.Count > 0)
                    {
                        // This undo step was not the copilot's (the user's own work): put it back and stop.
                        await TypeAndWatchAsync(document, "_.REDO\n").ConfigureAwait(true);
                        problem = $"The newest undo step in AutoCAD was not the copilot's call '{run.Title}' (it changed {touched.Count} other objects), " +
                                  "so it was restored with REDO and nothing more was undone. Ask the user to undo with Ctrl+Z.";
                        break;
                    }
                }

                if (!done)
                {
                    problem ??= $"Could not find the undo step of '{run.Title}' (AutoCAD's U changed none of its objects after {MaxExtraUndoSteps + 1} tries).";
                    break;
                }

                DraftingHistory.Instance.Remove(run);
                removed += run.Created.Count;
                restored += run.Erased.Count;
                undone.Add(run.Title);
            }
        }

        var data = new JsonObject
        {
            ["undone"] = undone,
            ["objectsRemoved"] = removed,
            ["objectsRestored"] = restored,
            ["remainingCopilotCalls"] = DraftingHistory.Instance.Latest(database, int.MaxValue).Count,
        };
        if (undone.Count == 0)
        {
            return ToolResult.Fail(problem ?? "Nothing was undone.") with { Data = data };
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Undid {undone.Count} copilot call(s): {string.Join(", ", undone.Select(t => $"'{t}'"))} ({removed} objects removed, {restored} restored).");
        CopilotLog.Info("undo: " + message + (problem is null ? string.Empty : " " + problem));
        return problem is null
            ? ToolResult.Ok(message, data)
            : new ToolResult { Success = true, Message = message + " " + problem, Data = data, Warnings = [problem] };
    }

    // Types one command (visibly) and returns the drawing content it appended, modified, erased, un-appended or
    // re-appended: entities and table records, not view records (an undone zoom or pan touches only those).
    private static async Task<HashSet<ObjectId>> TypeAndWatchAsync(Document document, string text)
    {
        var touched = new HashSet<ObjectId>();
        var database = document.Database;
        void OnObject(object? sender, ObjectEventArgs e) => Add(e.DBObject);
        void OnErased(object? sender, ObjectErasedEventArgs e) => Add(e.DBObject);
        void Add(DBObject? dbObject)
        {
            try
            {
                if (dbObject is Entity || (dbObject is SymbolTableRecord && dbObject is not ViewportTableRecord))
                {
                    touched.Add(dbObject.ObjectId);
                }
            }
            catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException)
            {
                // The object cannot be identified; it is not one of the copilot's.
            }
        }

        database.ObjectAppended += OnObject;
        database.ObjectModified += OnObject;
        database.ObjectUnappended += OnObject;
        database.ObjectReappended += OnObject;
        database.ObjectErased += OnErased;
        try
        {
            AcadInput.Type(document, text, echo: true);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < CommandTimeout)
            {
                await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(true);
                if (stopwatch.ElapsedMilliseconds >= 150 && AcadInput.CommandActive() == 0 && AcadInput.IsInputPending() != true)
                {
                    break;
                }
            }
        }
        finally
        {
            database.ObjectAppended -= OnObject;
            database.ObjectModified -= OnObject;
            database.ObjectUnappended -= OnObject;
            database.ObjectReappended -= OnObject;
            database.ObjectErased -= OnErased;
        }

        return touched;
    }
}
