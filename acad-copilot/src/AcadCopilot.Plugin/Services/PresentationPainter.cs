using AcadCopilot.Core.Presentation;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Inspect;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using AcadTransparency = Autodesk.AutoCAD.Colors.Transparency;

namespace AcadCopilot.Plugin.Services;

/// <summary>One fill created by the painter.</summary>
/// <param name="Id">The hatch.</param>
/// <param name="Layer">Its layer.</param>
/// <param name="Area">Its area in square drawing units.</param>
/// <param name="Loops">Boundary loops (1 = no islands).</param>
internal sealed record PaintedFill(ObjectId Id, string Layer, double Area, int Loops);

/// <summary>
/// Creates the colour presentation hatches (true-colour solid, gradient and patterned fills on the A-ELEV-PRES-* layers)
/// and keeps them behind the linework. Main thread, inside the caller's write transaction (document locked).
/// </summary>
internal static class PresentationPainter
{
    /// <summary>Creates the presentation layers that are missing (true colour of their first finish, plottable).</summary>
    public static IReadOnlyList<string> EnsureLayers(Database database, Transaction transaction)
    {
        var created = new List<string>();
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach (var (name, color) in PresentationFinishes.Layers)
        {
            if (table.Has(name))
            {
                continue;
            }

            var record = new LayerTableRecord
            {
                Name = name,
                Color = AcadColor.FromRgb(color.R, color.G, color.B),
                Description = "Colour presentation fills (AI Copilot)",
            };
            if (!table.IsWriteEnabled)
            {
                table.UpgradeOpen();
            }

            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            created.Add(name);
        }

        return created;
    }

    /// <summary>
    /// Fills the closed area around the point <paramref name="x"/>, <paramref name="y"/> (like HATCH's pick point; it must be on screen). With
    /// <paramref name="islands"/> inner closed areas stay unfilled.
    /// </summary>
    public static PaintedFill FillAtPoint(Document document, Transaction transaction, PresentationFinish finish, double x, double y, bool islands)
    {
        var view = ViewGeometry.Read(document);
        var visible = view.Visible;
        if (x < visible.MinX || x > visible.MaxX || y < visible.MinY || y > visible.MaxY)
        {
            throw new ToolException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The point {x:0.##},{y:0.##} is not on screen (the view shows {visible.MinX:0.##},{visible.MinY:0.##} to {visible.MaxX:0.##},{visible.MaxY:0.##}). ZOOM W around the elevation first."));
        }

        var curves = document.Editor.TraceBoundary(new Point3d(x, y, 0), islands);
        if (curves is null || curves.Count == 0)
        {
            throw new ToolException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"No closed area around {x:0.##},{y:0.##}: close the gap in the lines, pick a point further inside, or -BOUNDARY then fill by handle."));
        }

        var loops = new List<Entity>();
        foreach (DBObject item in curves)
        {
            if (item is Entity entity)
            {
                loops.Add(entity);
            }
            else
            {
                item.Dispose();
            }
        }

        // The outer loop is the largest.
        loops.Sort((a, b) => AreaOf(b).CompareTo(AreaOf(a)));
        return FillWithTemporaryLoops(document.Database, transaction, finish, loops);
    }

    /// <summary>Fills the closed boundary object with hex handle <paramref name="handle"/> (associative hatch).</summary>
    public static PaintedFill FillHandle(Database database, Transaction transaction, PresentationFinish finish, string handle)
    {
        var id = ToolContext.GetObjectId(database, handle);
        var boundary = transaction.GetObject(id, OpenMode.ForRead);
        var closed = boundary switch
        {
            Polyline p => p.Closed || p.StartPoint.IsEqualTo(p.EndPoint),
            Polyline2d p => p.Closed,
            Polyline3d p => p.Closed,
            Circle => true,
            Ellipse e => e.Closed,
            Spline s => s.Closed,
            Region => true,
            _ => false,
        };
        if (!closed)
        {
            throw new ToolException($"{handle} is a {boundary.GetRXClass().DxfName} that is not a closed boundary: use a closed polyline, circle, ellipse, spline or region (-BOUNDARY makes one from a point).");
        }

        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var hatch = NewHatch(database, finish);
        var hatchId = space.AppendEntity(hatch);
        transaction.AddNewlyCreatedDBObject(hatch, true);
        try
        {
            hatch.Associative = true;
            hatch.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { id });
            hatch.EvaluateHatch(true);
        }
        catch
        {
            hatch.Erase();
            throw;
        }

        return new PaintedFill(hatchId, hatch.Layer, SafeArea(hatch), 1);
    }

    /// <summary>Fills a polygon (sky, ground, shadow).</summary>
    public static PaintedFill FillPolygon(Database database, Transaction transaction, PresentationFinish finish, IReadOnlyList<PlanPoint> polygon)
    {
        if (polygon.Count < 3 || PresentationGeometry.Area(polygon) <= 1e-9)
        {
            throw new ToolException("The area to fill is empty (fewer than 3 corners or no area).");
        }

        var outline = new Polyline();
        for (var i = 0; i < polygon.Count; i++)
        {
            outline.AddVertexAt(i, new Point2d(polygon[i].X, polygon[i].Y), 0, 0, 0);
        }

        outline.Closed = true;
        return FillWithTemporaryLoops(database, transaction, finish, [outline]);
    }

    /// <summary>
    /// Sends every presentation hatch behind everything else, by rank: sky / ground lowest, then surfaces, planting and
    /// shadows; the linework stays on top.
    /// </summary>
    public static void Reorder(Database database, Transaction transaction)
    {
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
        var byRank = new SortedDictionary<int, ObjectIdCollection>();
        foreach (ObjectId id in space)
        {
            if (id.IsErased || !id.ObjectClass.DxfName.Equals("HATCH", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hatch = (Hatch)transaction.GetObject(id, OpenMode.ForRead);
            if (!PresentationFinishes.IsPresentationLayer(hatch.Layer))
            {
                continue;
            }

            var rank = PresentationFinishes.RankOfLayer(hatch.Layer);
            if (!byRank.TryGetValue(rank, out var ids))
            {
                byRank[rank] = ids = new ObjectIdCollection();
            }

            ids.Add(id);
        }

        if (byRank.Count == 0)
        {
            return;
        }

        var order = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
        foreach (var rank in byRank.Keys.Reverse())
        {
            // Each call puts its set at the very bottom, so the last (rank 0, the backdrop) ends lowest.
            order.MoveToBottom(byRank[rank]);
        }
    }

    // Appends the loops as temporary curves, hatches them (non-associative), then erases the temporary curves.
    private static PaintedFill FillWithTemporaryLoops(Database database, Transaction transaction, PresentationFinish finish, IReadOnlyList<Entity> loops)
    {
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        var temporary = new List<Entity>();
        Hatch? hatch = null;
        try
        {
            foreach (var loop in loops)
            {
                loop.SetDatabaseDefaults(database);
                space.AppendEntity(loop);
                transaction.AddNewlyCreatedDBObject(loop, true);
                temporary.Add(loop);
            }

            hatch = NewHatch(database, finish);
            var hatchId = space.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);
            hatch.Associative = false;
            for (var i = 0; i < loops.Count; i++)
            {
                hatch.AppendLoop(i == 0 ? HatchLoopTypes.External : HatchLoopTypes.Default, new ObjectIdCollection { loops[i].ObjectId });
            }

            hatch.EvaluateHatch(true);
            return new PaintedFill(hatchId, hatch.Layer, SafeArea(hatch), loops.Count);
        }
        catch
        {
            if (hatch is { IsErased: false } && !hatch.ObjectId.IsNull)
            {
                hatch.Erase();
            }

            throw;
        }
        finally
        {
            foreach (var loop in temporary)
            {
                if (!loop.IsErased)
                {
                    loop.Erase();
                }
            }
        }
    }

    private static Hatch NewHatch(Database database, PresentationFinish finish)
    {
        var hatch = new Hatch();
        hatch.SetDatabaseDefaults(database);
        hatch.Layer = finish.Layer;
        hatch.Normal = Vector3d.ZAxis;
        hatch.Elevation = 0;
        switch (finish.Kind)
        {
            case FillKind.Gradient:
                var end = finish.Color2 ?? finish.Color.Lighter(0.5);
                hatch.HatchObjectType = HatchObjectType.GradientObject;
                hatch.SetGradient(GradientPatternType.PreDefinedGradient, "LINEAR");
                hatch.GradientOneColorMode = false;
                hatch.SetGradientColors(
                [
                    new GradientColor(AcadColor.FromRgb(finish.Color.R, finish.Color.G, finish.Color.B), 0f),
                    new GradientColor(AcadColor.FromRgb(end.R, end.G, end.B), 1f),
                ]);
                hatch.GradientAngle = finish.GradientAngle * Math.PI / 180;
                hatch.GradientShift = 0f;
                break;
            case FillKind.Pattern when finish.Pattern is not null:
                var lines = finish.Color2 ?? finish.Color.Darker(0.3);
                hatch.PatternScale = 1;
                hatch.SetHatchPattern(HatchPatternType.PreDefined, finish.Pattern);
                hatch.Color = AcadColor.FromRgb(lines.R, lines.G, lines.B);
                hatch.BackgroundColor = AcadColor.FromRgb(finish.Color.R, finish.Color.G, finish.Color.B);
                break;
            default:
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                hatch.Color = AcadColor.FromRgb(finish.Color.R, finish.Color.G, finish.Color.B);
                break;
        }

        if (finish.TransparencyPercent > 0)
        {
            hatch.Transparency = new AcadTransparency((byte)Math.Round(255 * (100 - Math.Clamp(finish.TransparencyPercent, 0, 90)) / 100.0));
        }

        return hatch;
    }

    private static double AreaOf(Entity entity)
    {
        try
        {
            return entity switch
            {
                Curve curve => Math.Abs(curve.Area),
                Region region => Math.Abs(region.Area),
                _ => 0,
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return 0;
        }
    }

    private static double SafeArea(Hatch hatch)
    {
        try
        {
            return Math.Abs(hatch.Area);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return 0;
        }
    }
}
