using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadCopilot.Plugin.Inspect;

/// <summary>The current view of the active viewport, converted from display coordinates (DCS) to world coordinates.</summary>
/// <param name="Center">View centre in WCS.</param>
/// <param name="Width">Width of the visible area in drawing units.</param>
/// <param name="Height">Height of the visible area in drawing units.</param>
/// <param name="Visible">Bounding box (WCS, XY) of the visible area; exact for plan views.</param>
/// <param name="IsPlan">True for a plan view (looking down the Z axis, no twist).</param>
/// <param name="Perspective">True when perspective projection is on.</param>
/// <param name="Viewport">CVPORT: 1 = paper space of a layout, 2+ = model space viewports.</param>
/// <param name="Space">"model", or "paper:&lt;layout&gt;" / "viewport:&lt;layout&gt;" inside a layout.</param>
public sealed record ViewFacts(Point3d Center, double Width, double Height, PlanBox Visible, bool IsPlan, bool Perspective, int Viewport, string Space)
{
    /// <summary>True when the view shows paper space (a layout sheet, not model space through a viewport).</summary>
    public bool IsPaperSpace => Space.StartsWith("paper:", StringComparison.Ordinal);

    /// <summary>JSON for results: centre, width, height, visible area (drawing units + feet-inches), plan flag, space.</summary>
    public JsonObject ToJson(DrawingUnits units)
    {
        var json = new JsonObject
        {
            ["center"] = ToolContext.Point(Center, units),
            ["width"] = ToolContext.Length(Width, units),
            ["height"] = ToolContext.Length(Height, units),
            ["visibleArea"] = BoxJson(Visible, units),
            ["plan"] = IsPlan,
            ["space"] = Space,
        };
        if (Perspective)
        {
            json["perspective"] = true;
        }

        return json;
    }

    /// <summary>A box as {min, max, size} in drawing units with feet-inches text.</summary>
    public static JsonObject BoxJson(PlanBox box, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var json = new JsonObject
        {
            ["min"] = ToolContext.Point(new Point3d(box.MinX, box.MinY, 0), units),
            ["max"] = ToolContext.Point(new Point3d(box.MaxX, box.MaxY, 0), units),
        };
        if (units.ToFeetInches(box.Width) is { } width && units.ToFeetInches(box.Height) is { } height)
        {
            json["size"] = width + " x " + height;
        }

        return json;
    }
}

/// <summary>Reads the current view. Main thread only.</summary>
public static class ViewGeometry
{
    /// <summary>Reads the active viewport's view of <paramref name="document"/> in world coordinates.</summary>
    public static ViewFacts Read(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var database = document.Database;
        var viewport = CurrentViewport();
        using var view = document.Editor.GetCurrentView();

        // DCS -> WCS, as in the AutoCAD .NET Developer's Guide ("Manipulate the Current View").
        var dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                       * Matrix3d.Displacement(view.Target - Point3d.Origin)
                       * Matrix3d.PlaneToWorld(view.ViewDirection);
        var cx = view.CenterPoint.X;
        var cy = view.CenterPoint.Y;
        var halfWidth = view.Width / 2.0;
        var halfHeight = view.Height / 2.0;
        var center = new Point3d(cx, cy, 0).TransformBy(dcsToWcs);
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var (dx, dy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
        {
            var corner = new Point3d(cx + (dx * halfWidth), cy + (dy * halfHeight), 0).TransformBy(dcsToWcs);
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }

        var twist = Math.Abs(Math.IEEERemainder(view.ViewTwist, 2 * Math.PI));
        var isPlan = view.ViewDirection.IsParallelTo(Vector3d.ZAxis) && view.ViewDirection.Z > 0 && twist < 1e-9;
        string space;
        if (database.TileMode)
        {
            space = "model";
        }
        else
        {
            var layout = CurrentLayoutName();
            space = viewport <= 1 ? "paper:" + layout : "viewport:" + layout;
        }

        return new ViewFacts(center, view.Width, view.Height, new PlanBox(minX, minY, maxX, maxY), isPlan, view.PerspectiveEnabled, viewport, space);
    }

    /// <summary>CVPORT (1 = paper space, 2+ = model space viewports); 2 when it cannot be read.</summary>
    public static int CurrentViewport()
    {
        try
        {
            return Convert.ToInt32(AcadApp.GetSystemVariable("CVPORT"), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidCastException or FormatException or OverflowException)
        {
            return 2;
        }
    }

    private static string CurrentLayoutName()
    {
        try
        {
            return Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.CurrentLayout;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return "?";
        }
    }
}
