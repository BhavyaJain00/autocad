using AcadCopilot.Core.Accuracy;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Accuracy;

/// <summary>
/// Reads the model-space objects of one area of the active drawing into a <see cref="DrawingSnapshot"/> (inches) for
/// check_drawing: lines and polyline pieces, arcs, text, linear dimensions and every object's layer. Main thread, inside
/// the caller's read transaction.
/// </summary>
internal static class AccuracyDrawingReader
{
    /// <summary>Most objects read in one area.</summary>
    public const int MaxObjects = 20_000;

    /// <summary>Reads the objects whose extents touch <paramref name="regionInches"/>.</summary>
    /// <returns>The snapshot and whether the drawing's unit scale was known (unitless drawings are read as inches).</returns>
    public static (DrawingSnapshot Snapshot, bool UnitsKnown) Read(Document document, Transaction transaction, Box regionInches)
    {
        var database = document.Database;
        var units = ToolContext.GetUnits(database);
        var k = units.InchesPerUnit is { } ipu && ipu > 0 ? ipu : 1;
        var snapshot = new DrawingSnapshot();
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
        var read = 0;
        foreach (ObjectId id in space)
        {
            if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            Box bounds;
            try
            {
                var extents = entity.GeometricExtents;
                bounds = new Box(extents.MinPoint.X * k, extents.MinPoint.Y * k, extents.MaxPoint.X * k, extents.MaxPoint.Y * k);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                continue;
            }

            if (!bounds.Inflate(0.01).Overlaps(regionInches, 0))
            {
                continue;
            }

            if (++read > MaxObjects)
            {
                snapshot.Truncated = true;
                break;
            }

            var handle = ToolContext.HandleOf(id);
            var layer = entity.Layer;
            try
            {
                switch (entity)
                {
                    case Line line:
                        snapshot.Segments.Add(new DrawnSegment(handle, layer, P(line.StartPoint, k), P(line.EndPoint, k)));
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "line"));
                        break;
                    case Polyline polyline:
                        AddPolyline(snapshot, polyline, handle, layer, k);
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "polyline"));
                        break;
                    case Arc arc:
                        snapshot.Arcs.Add(new DrawnArc(handle, layer, P(arc.Center, k), arc.Radius * k, arc.StartAngle, arc.EndAngle));
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "arc"));
                        break;
                    case DBText text:
                        snapshot.Texts.Add(new DrawnText(handle, layer, text.TextString ?? string.Empty, bounds, text.Height * k));
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "text"));
                        break;
                    case MText mtext:
                        snapshot.Texts.Add(new DrawnText(handle, layer, mtext.Text ?? string.Empty, bounds, mtext.TextHeight * k));
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "mtext"));
                        break;
                    case RotatedDimension rotated:
                        snapshot.Dimensions.Add(new DrawnDimension(handle, layer, P(rotated.XLine1Point, k), P(rotated.XLine2Point, k), P(rotated.DimLinePoint, k), rotated.Rotation, rotated.Measurement * k, rotated.DimensionText));
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "dimension"));
                        break;
                    case AlignedDimension aligned:
                        snapshot.Dimensions.Add(new DrawnDimension(handle, layer, P(aligned.XLine1Point, k), P(aligned.XLine2Point, k), P(aligned.DimLinePoint, k), null, aligned.Measurement * k, aligned.DimensionText));
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "dimension"));
                        break;
                    case Dimension:
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "dimension"));
                        break;
                    case Hatch:
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "hatch"));
                        break;
                    case BlockReference:
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "insert"));
                        break;
                    case Circle:
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "circle"));
                        break;
                    default:
                        snapshot.Objects.Add(new DrawnObject(handle, layer, "other"));
                        break;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // An object that cannot report its geometry is left out; the others are still checked.
            }
        }

        return (snapshot, units.InchesPerUnit is not null);
    }

    private static void AddPolyline(DrawingSnapshot snapshot, Polyline polyline, string handle, string layer, double k)
    {
        var count = polyline.NumberOfVertices;
        var segments = polyline.Closed ? count : count - 1;
        for (var i = 0; i < segments; i++)
        {
            if (polyline.GetSegmentType(i) != SegmentType.Line)
            {
                continue;
            }

            var segment = polyline.GetLineSegmentAt(i);
            snapshot.Segments.Add(new DrawnSegment(handle, layer, new Pt(segment.StartPoint.X * k, segment.StartPoint.Y * k), new Pt(segment.EndPoint.X * k, segment.EndPoint.Y * k)));
        }
    }

    private static Pt P(Autodesk.AutoCAD.Geometry.Point3d p, double k) => new(p.X * k, p.Y * k);
}
