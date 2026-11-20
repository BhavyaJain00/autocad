using AcadCopilot.Core.Diagnostics;
using AcadCopilot.Plugin.Acad;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.ViewModels;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Drafting;

/// <summary>
/// "Follow the AI's drawing" (panel setting, default on): after a run_commands call that created objects, the view
/// zooms to them with a 10% margin when they are outside the visible area or too small to see (<see cref="ViewFraming"/>).
/// Only in a plan view of model space (never through a layout viewport, whose scale must not change), never while a
/// command, dialog or text editor is active (the user may be in the middle of his own command), and through
/// <c>Editor.SetCurrentView</c>, so nothing is typed on the command line and nothing enters the undo history. Main
/// thread only; never throws.
/// </summary>
internal static class ViewFollow
{
    /// <summary>Most new objects measured for one call (more are rare and the first ones show where the work is).</summary>
    public const int MaxObjects = 20000;

    /// <summary>True when the setting is on (panel.json, read for each call so a change applies at once).</summary>
    public static bool IsEnabled() => new PanelSettingsStore().Load().FollowAiDrawing;

    /// <summary>
    /// Zooms <paramref name="document"/>'s view to <paramref name="created"/> when they are off screen or too small.
    /// Returns what was done for the log ("zoomed to 12 new objects (outside the view)"), or null when the view stayed.
    /// </summary>
    public static string? FrameCreated(Document document, IReadOnlyList<ObjectId> created, AcadDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (created.Count == 0)
        {
            return null;
        }

        try
        {
            if (dispatcher.IsModal || AcadInput.IsCommandActive() || !string.IsNullOrEmpty(document.CommandInProgress) || AcadInput.IsTextEditorActive() == true)
            {
                return null; // the user is in a command (or a dialog): never move the view under him
            }

            var facts = ViewGeometry.Read(document);
            if (!facts.IsPlan || facts.Perspective || facts.Space != "model")
            {
                return null;
            }

            if (Extents(document.Database, created) is not { } extents || ViewFraming.Decide(facts.Visible, extents) is not { } decision)
            {
                return null;
            }

            using (AcadDispatcher.LockDocument(document))
            {
                var editor = document.Editor;
                using var view = editor.GetCurrentView();
                var wcsToDcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                                * Matrix3d.Displacement(view.Target - Point3d.Origin)
                                * Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                var window = decision.Window;
                var center = new Point3d(window.CenterX, window.CenterY, view.Target.Z).TransformBy(wcsToDcs);
                view.CenterPoint = new Point2d(center.X, center.Y);
                view.Width = window.Width;
                view.Height = window.Height;
                editor.SetCurrentView(view);
            }

            var why = decision.Reason == FramingReason.Outside ? "outside the view" : "too small to see";
            return $"zoomed to {created.Count} new object(s) ({why})";
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or AcadBusyException or ArgumentException)
        {
            CopilotLog.Warn("Follow the AI's drawing: could not zoom to the new objects", ex);
            return null;
        }
    }

    // World XY extents of the new objects in the current space (objects inside blocks or other layouts are skipped).
    private static PlanBox? Extents(Database database, IReadOnlyList<ObjectId> ids)
    {
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var space = database.CurrentSpaceId;
        var boxes = new List<PlanBox>();
        foreach (var id in ids.Take(MaxObjects))
        {
            if (id.IsNull || !id.IsValid || id.IsErased || transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity || entity.OwnerId != space)
            {
                continue;
            }

            if (entity.Bounds is { } bounds)
            {
                boxes.Add(PlanBox.FromCorners(bounds.MinPoint.X, bounds.MinPoint.Y, bounds.MaxPoint.X, bounds.MaxPoint.Y));
            }
        }

        transaction.Commit();
        return ViewFraming.Union(boxes);
    }
}
