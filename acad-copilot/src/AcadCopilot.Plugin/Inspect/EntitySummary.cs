using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadColor = Autodesk.AutoCAD.Colors.Color;

namespace AcadCopilot.Plugin.Inspect;

/// <summary>
/// Describes entities for the AI: handle, DXF type, layer and key geometry in drawing units with feet-inches text.
/// <see cref="Summarize(Entity, ToolContext)"/> is the compact form used by run_commands results and list_entities;
/// <see cref="Describe"/> is the full form used by get_entity. Points use <see cref="ToolContext.Point"/>
/// ({x, y, ftIn}), lengths <see cref="ToolContext.Length"/> ({du, ftIn}); angles are degrees (counter-clockwise from
/// +X); areas are {sqDu (square drawing units), sqFt}. Main thread only; the entity must be open (read is enough).
/// </summary>
public static class EntitySummary
{
    private const int CompactVertexLimit = 12;
    private const int DetailVertexLimit = 500;
    private const int CompactTextLimit = 160;
    private const int DetailTextLimit = 4000;
    private const int CompactAttributeLimit = 8;
    private const int HatchLoopLimit = 20;

    /// <summary>
    /// Compact summary: {handle, type, layer, key geometry..., box?, color?, linetype?}. Never throws for odd
    /// entities: geometry that cannot be read is reported in "note".
    /// </summary>
    /// <param name="entity">An open entity of the active drawing (erased entities work too).</param>
    /// <param name="context">The tool call context (supplies the drawing units).</param>
    public static JsonObject Summarize(Entity entity, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(context);
        return Summarize(entity, ToolContext.GetUnits(entity.Database ?? context.Database));
    }

    /// <summary>Compact summary with already-read drawing units (use this in loops).</summary>
    public static JsonObject Summarize(Entity entity, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(units);
        var json = Header(entity);
        try
        {
            AddGeometry(json, entity, units, transaction: null, detailed: false);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
        {
            json["note"] = "Geometry could not be read: " + ex.Message;
        }

        AddStyleOverrides(json, entity);
        return json;
    }

    /// <summary>
    /// Everything useful about one entity (get_entity): the compact fields plus colour, linetype, lineweight,
    /// transparency, owner space, and the full geometry (all polyline vertices with bulges and widths, text and its
    /// style, every dimension point, hatch loops, attributes and dynamic block properties).
    /// </summary>
    /// <param name="entity">An open entity.</param>
    /// <param name="transaction">The transaction it was opened with (used to open related objects for read).</param>
    /// <param name="units">Drawing units of its database.</param>
    public static JsonObject Describe(Entity entity, Transaction transaction, DrawingUnits units)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(units);
        var json = Header(entity);
        json["space"] = SpaceName(entity, transaction);
        json["color"] = ColorText(entity.Color);
        json["linetype"] = entity.Linetype;
        json["linetypeScale"] = ToolContext.Round(entity.LinetypeScale);
        json["lineweight"] = LineweightText(entity.LineWeight);
        json["transparency"] = TransparencyText(entity.Transparency);
        if (!entity.Visible)
        {
            json["visible"] = false;
        }

        if (entity.IsErased)
        {
            json["erased"] = true;
        }

        try
        {
            AddGeometry(json, entity, units, transaction, detailed: true);
        }
        catch (Exception ex) when (ex is Autodesk.AutoCAD.Runtime.Exception or InvalidOperationException or ArgumentException)
        {
            json["note"] = "Some geometry could not be read: " + ex.Message;
        }

        if (!json.ContainsKey("box") && Box(entity, units) is { } box)
        {
            json["box"] = box;
        }

        return json;
    }

    /// <summary>The DXF type name ("LINE", "LWPOLYLINE", "AEC_WALL"), or the class name when it has none.</summary>
    public static string TypeName(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var rxClass = entity.GetRXClass();
        return string.IsNullOrEmpty(rxClass.DxfName) ? rxClass.Name : rxClass.DxfName;
    }

    /// <summary>The XY bounding box of an entity, or null when it has none (rays, xlines, empty text).</summary>
    public static PlanBox? PlanBounds(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        try
        {
            if (entity.Bounds is { } bounds)
            {
                var box = new PlanBox(bounds.MinPoint.X, bounds.MinPoint.Y, bounds.MaxPoint.X, bounds.MaxPoint.Y);
                return box.IsValid ? box : null;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // No measurable extents.
        }

        return null;
    }

    /// <summary>Colour as text: "ByLayer", "ByBlock", an ACI number ("1", "7"), or "r,g,b".</summary>
    public static string ColorText(AcadColor color)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (color.IsByLayer)
        {
            return "ByLayer";
        }

        if (color.IsByBlock)
        {
            return "ByBlock";
        }

        if (color.IsByAci)
        {
            return color.ColorIndex.ToString(CultureInfo.InvariantCulture);
        }

        return color.IsByColor
            ? string.Create(CultureInfo.InvariantCulture, $"{color.Red},{color.Green},{color.Blue}")
            : color.ColorNameForDisplay;
    }

    /// <summary>Lineweight as text: "ByLayer", "ByBlock", "Default" or millimetres ("0.30 mm").</summary>
    public static string LineweightText(LineWeight lineweight) => lineweight switch
    {
        LineWeight.ByLayer => "ByLayer",
        LineWeight.ByBlock => "ByBlock",
        LineWeight.ByLineWeightDefault => "Default",
        LineWeight.ByDIPs => "ByDIPs",
        _ => ((int)lineweight / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " mm",
    };

    private static string TransparencyText(Autodesk.AutoCAD.Colors.Transparency transparency) =>
        transparency.IsByLayer ? "ByLayer"
        : transparency.IsByBlock ? "ByBlock"
        : transparency.IsByAlpha ? ((int)Math.Round((255 - transparency.Alpha) * 100.0 / 255.0)).ToString(CultureInfo.InvariantCulture) + "%"
        : transparency.ToString();

    private static JsonObject Header(Entity entity) => new()
    {
        ["handle"] = entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
        ["type"] = TypeName(entity),
        ["layer"] = entity.Layer,
    };

    private static void AddStyleOverrides(JsonObject json, Entity entity)
    {
        try
        {
            if (!entity.Color.IsByLayer)
            {
                json["color"] = ColorText(entity.Color);
            }

            if (!string.Equals(entity.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
            {
                json["linetype"] = entity.Linetype;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Style is optional information.
        }
    }

    private static void AddGeometry(JsonObject json, Entity entity, DrawingUnits units, Transaction? transaction, bool detailed)
    {
        switch (entity)
        {
            case Line line:
                json["start"] = ToolContext.Point(line.StartPoint, units);
                json["end"] = ToolContext.Point(line.EndPoint, units);
                json["length"] = ToolContext.Length(line.Length, units);
                json["angle"] = Degrees(line.Angle);
                break;
            case Polyline polyline:
                AddPolyline(json, polyline, units, detailed);
                break;
            case Circle circle:
                json["center"] = ToolContext.Point(circle.Center, units);
                json["radius"] = ToolContext.Length(circle.Radius, units);
                json["diameter"] = ToolContext.Length(circle.Diameter, units);
                break;
            case Arc arc:
                json["center"] = ToolContext.Point(arc.Center, units);
                json["radius"] = ToolContext.Length(arc.Radius, units);
                json["startAngle"] = Degrees(arc.StartAngle);
                json["endAngle"] = Degrees(arc.EndAngle);
                json["start"] = ToolContext.Point(arc.StartPoint, units);
                json["end"] = ToolContext.Point(arc.EndPoint, units);
                json["length"] = ToolContext.Length(arc.Length, units);
                break;
            case Ellipse ellipse:
                json["center"] = ToolContext.Point(ellipse.Center, units);
                json["majorRadius"] = ToolContext.Length(ellipse.MajorRadius, units);
                json["minorRadius"] = ToolContext.Length(ellipse.MinorRadius, units);
                json["rotation"] = Degrees(Math.Atan2(ellipse.MajorAxis.Y, ellipse.MajorAxis.X));
                if (detailed)
                {
                    json["startAngle"] = Degrees(ellipse.StartAngle);
                    json["endAngle"] = Degrees(ellipse.EndAngle);
                }

                break;
            case AttributeReference attribute:
                json["tag"] = attribute.Tag;
                AddText(json, attribute, units, detailed);
                break;
            case DBText text:
                AddText(json, text, units, detailed);
                break;
            case MText mtext:
                AddMText(json, mtext, units, detailed);
                break;
            case Dimension dimension:
                AddDimension(json, dimension, units, detailed);
                break;
            case Hatch hatch:
                AddHatch(json, hatch, units, detailed);
                break;
            case Table table:
                json["rows"] = table.Rows.Count;
                json["columns"] = table.Columns.Count;
                json["position"] = ToolContext.Point(table.Position, units);
                json["width"] = ToolContext.Length(table.Width, units);
                json["height"] = ToolContext.Length(table.Height, units);
                json["style"] = table.TableStyleName;
                break;
            case BlockReference reference:
                AddBlockReference(json, reference, units, transaction, detailed);
                break;
            case DBPoint point:
                json["position"] = ToolContext.Point(point.Position, units);
                break;
            case Solid solid:
                var corners = new JsonArray();
                for (short i = 0; i < 4; i++)
                {
                    corners.Add(Pair(solid.GetPointAt(i)));
                }

                json["corners"] = corners;
                break;
            case Leader leader:
                var leaderPoints = new JsonArray();
                for (var i = 0; i < leader.NumVertices && i < (detailed ? DetailVertexLimit : CompactVertexLimit); i++)
                {
                    leaderPoints.Add(Pair(leader.VertexAt(i)));
                }

                json["vertexCount"] = leader.NumVertices;
                json["points"] = leaderPoints;
                json["arrow"] = leader.HasArrowHead;
                break;
            case MLeader mleader:
                AddMLeader(json, mleader, units);
                break;
            case Xline xline:
                json["basePoint"] = ToolContext.Point(xline.BasePoint, units);
                json["direction"] = Degrees(Math.Atan2(xline.UnitDir.Y, xline.UnitDir.X));
                break;
            case Ray ray:
                json["basePoint"] = ToolContext.Point(ray.BasePoint, units);
                json["direction"] = Degrees(Math.Atan2(ray.UnitDir.Y, ray.UnitDir.X));
                break;
            case Region region:
                json["area"] = Area(region.Area, units);
                json["perimeter"] = ToolContext.Length(region.Perimeter, units);
                break;
            case Viewport viewport:
                json["center"] = ToolContext.Point(viewport.CenterPoint, units);
                json["width"] = ToolContext.Length(viewport.Width, units);
                json["height"] = ToolContext.Length(viewport.Height, units);
                json["number"] = viewport.Number;
                json["on"] = viewport.On;
                json["locked"] = viewport.Locked;
                json["scale"] = ToolContext.Round(viewport.CustomScale);
                json["viewCenter"] = ToolContext.Point(new Point3d(viewport.ViewCenter.X, viewport.ViewCenter.Y, 0), units);
                json["viewHeight"] = ToolContext.Length(viewport.ViewHeight, units);
                break;
            case Curve curve:
                // Splines, old-style 2D/3D polylines and other curves: length and closure.
                json["closed"] = curve.Closed;
                json["length"] = ToolContext.Length(CurveLength(curve), units);
                json["start"] = ToolContext.Point(curve.StartPoint, units);
                json["end"] = ToolContext.Point(curve.EndPoint, units);
                if (curve is Spline spline)
                {
                    json["degree"] = spline.Degree;
                    json["controlPoints"] = spline.NumControlPoints;
                    json["fitPoints"] = spline.NumFitPoints;
                }

                if (curve.Closed)
                {
                    json["area"] = Area(curve.Area, units);
                }

                break;
        }

        // A bounding box is key geometry for everything without explicit end points (text, blocks, hatches, AEC objects...).
        if (entity is not (Line or Circle or Arc or DBPoint or Xline or Ray) && !json.ContainsKey("box") && Box(entity, units) is { } box)
        {
            json["box"] = box;
        }
    }

    private static void AddPolyline(JsonObject json, Polyline polyline, DrawingUnits units, bool detailed)
    {
        var count = polyline.NumberOfVertices;
        json["closed"] = polyline.Closed;
        json["vertexCount"] = count;
        if (detailed)
        {
            var vertices = new JsonArray();
            for (var i = 0; i < count && i < DetailVertexLimit; i++)
            {
                var p = polyline.GetPoint2dAt(i);
                var vertex = ToolContext.Point(new Point3d(p.X, p.Y, 0), units);
                var bulge = polyline.GetBulgeAt(i);
                if (Math.Abs(bulge) > 1e-12)
                {
                    vertex["bulge"] = ToolContext.Round(bulge);
                }

                var startWidth = polyline.GetStartWidthAt(i);
                var endWidth = polyline.GetEndWidthAt(i);
                if (startWidth > 0 || endWidth > 0)
                {
                    vertex["startWidth"] = ToolContext.Round(startWidth);
                    vertex["endWidth"] = ToolContext.Round(endWidth);
                }

                vertices.Add(vertex);
            }

            json["vertices"] = vertices;
            if (count > DetailVertexLimit)
            {
                json["verticesTruncated"] = true;
            }

            if (Math.Abs(polyline.Elevation) > 1e-9)
            {
                json["elevation"] = ToolContext.Length(polyline.Elevation, units);
            }
        }
        else
        {
            var points = new JsonArray();
            for (var i = 0; i < count && i < CompactVertexLimit; i++)
            {
                var p = polyline.GetPoint2dAt(i);
                points.Add(Pair(p.X, p.Y));
            }

            json["points"] = points;
            if (count > CompactVertexLimit)
            {
                json["pointsTruncated"] = true;
            }
        }

        if (polyline.HasBulges)
        {
            json["hasArcs"] = true;
        }

        if (polyline.ConstantWidth > 0)
        {
            json["width"] = ToolContext.Length(polyline.ConstantWidth, units);
        }

        json["length"] = ToolContext.Length(polyline.Length, units);
        if (polyline.Closed)
        {
            json["area"] = Area(polyline.Area, units);
        }
    }

    private static void AddText(JsonObject json, DBText text, DrawingUnits units, bool detailed)
    {
        json["text"] = Truncate(text.TextString, detailed ? DetailTextLimit : CompactTextLimit);
        json["position"] = ToolContext.Point(text.Position, units);
        json["height"] = ToolContext.Length(text.Height, units);
        json["rotation"] = Degrees(text.Rotation);
        json["style"] = text.TextStyleName;
        if (detailed)
        {
            json["justify"] = text.Justify.ToString();
            if (!text.IsDefaultAlignment)
            {
                json["alignmentPoint"] = ToolContext.Point(text.AlignmentPoint, units);
            }

            json["widthFactor"] = ToolContext.Round(text.WidthFactor);
            if (Math.Abs(text.Oblique) > 1e-12)
            {
                json["oblique"] = Degrees(text.Oblique);
            }
        }
    }

    private static void AddMText(JsonObject json, MText mtext, DrawingUnits units, bool detailed)
    {
        json["text"] = Truncate(mtext.Text, detailed ? DetailTextLimit : CompactTextLimit);
        json["position"] = ToolContext.Point(mtext.Location, units);
        json["height"] = ToolContext.Length(mtext.TextHeight, units);
        json["rotation"] = Degrees(mtext.Rotation);
        json["attachment"] = mtext.Attachment.ToString();
        json["style"] = mtext.TextStyleName;
        if (mtext.Width > 0)
        {
            json["width"] = ToolContext.Length(mtext.Width, units);
        }

        if (detailed)
        {
            json["contents"] = Truncate(mtext.Contents, DetailTextLimit);
            json["actualWidth"] = ToolContext.Length(mtext.ActualWidth, units);
            json["actualHeight"] = ToolContext.Length(mtext.ActualHeight, units);
            json["lineSpacing"] = ToolContext.Round(mtext.LineSpacingFactor);
        }
    }

    private static void AddDimension(JsonObject json, Dimension dimension, DrawingUnits units, bool detailed)
    {
        var angular = dimension is LineAngularDimension2 or Point3AngularDimension;
        json["kind"] = dimension switch
        {
            RotatedDimension => "linear",
            AlignedDimension => "aligned",
            ArcDimension => "arc length",
            RadialDimension => "radius",
            DiametricDimension => "diameter",
            OrdinateDimension => "ordinate",
            LineAngularDimension2 or Point3AngularDimension => "angular",
            _ => dimension.GetType().Name,
        };
        if (angular)
        {
            json["measurement"] = Degrees(dimension.Measurement);
        }
        else
        {
            json["measurement"] = ToolContext.Length(dimension.Measurement, units);
        }

        var text = dimension.DimensionText;
        if (!string.IsNullOrEmpty(text) && text != "<>")
        {
            json["textOverride"] = text;
        }

        json["style"] = dimension.DimensionStyleName;
        switch (dimension)
        {
            case RotatedDimension rotated:
                json["point1"] = ToolContext.Point(rotated.XLine1Point, units);
                json["point2"] = ToolContext.Point(rotated.XLine2Point, units);
                json["dimLine"] = ToolContext.Point(rotated.DimLinePoint, units);
                json["rotation"] = Degrees(rotated.Rotation);
                break;
            case AlignedDimension aligned:
                json["point1"] = ToolContext.Point(aligned.XLine1Point, units);
                json["point2"] = ToolContext.Point(aligned.XLine2Point, units);
                json["dimLine"] = ToolContext.Point(aligned.DimLinePoint, units);
                break;
            case ArcDimension arcDimension when detailed:
                json["point1"] = ToolContext.Point(arcDimension.XLine1Point, units);
                json["point2"] = ToolContext.Point(arcDimension.XLine2Point, units);
                json["arcPoint"] = ToolContext.Point(arcDimension.ArcPoint, units);
                break;
            case RadialDimension radial:
                json["center"] = ToolContext.Point(radial.Center, units);
                json["chordPoint"] = ToolContext.Point(radial.ChordPoint, units);
                break;
            case DiametricDimension diametric:
                json["chordPoint"] = ToolContext.Point(diametric.ChordPoint, units);
                json["farChordPoint"] = ToolContext.Point(diametric.FarChordPoint, units);
                break;
            case OrdinateDimension ordinate when detailed:
                json["definingPoint"] = ToolContext.Point(ordinate.DefiningPoint, units);
                json["leaderEnd"] = ToolContext.Point(ordinate.LeaderEndPoint, units);
                json["axis"] = ordinate.UsingXAxis ? "X" : "Y";
                break;
            case LineAngularDimension2 lineAngular when detailed:
                json["line1Start"] = ToolContext.Point(lineAngular.XLine1Start, units);
                json["line1End"] = ToolContext.Point(lineAngular.XLine1End, units);
                json["line2Start"] = ToolContext.Point(lineAngular.XLine2Start, units);
                json["line2End"] = ToolContext.Point(lineAngular.XLine2End, units);
                json["arcPoint"] = ToolContext.Point(lineAngular.ArcPoint, units);
                break;
            case Point3AngularDimension pointAngular when detailed:
                json["center"] = ToolContext.Point(pointAngular.CenterPoint, units);
                json["point1"] = ToolContext.Point(pointAngular.XLine1Point, units);
                json["point2"] = ToolContext.Point(pointAngular.XLine2Point, units);
                json["arcPoint"] = ToolContext.Point(pointAngular.ArcPoint, units);
                break;
        }

        if (detailed)
        {
            json["textPosition"] = ToolContext.Point(dimension.TextPosition, units);
            json["textRotation"] = Degrees(dimension.TextRotation);
            json["dimscale"] = ToolContext.Round(dimension.Dimscale);
            json["textHeight"] = ToolContext.Length(dimension.Dimtxt, units);
        }
    }

    private static void AddHatch(JsonObject json, Hatch hatch, DrawingUnits units, bool detailed)
    {
        json["pattern"] = hatch.PatternName;
        if (!hatch.IsSolidFill)
        {
            json["patternScale"] = ToolContext.Round(hatch.PatternScale);
            json["patternAngle"] = Degrees(hatch.PatternAngle);
        }

        json["loops"] = hatch.NumberOfLoops;
        json["associative"] = hatch.Associative;
        try
        {
            json["area"] = Area(hatch.Area, units);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Self-intersecting or open boundaries have no area.
        }

        if (!detailed)
        {
            return;
        }

        json["patternType"] = hatch.PatternType.ToString();
        json["style"] = hatch.HatchStyle.ToString();
        var loops = new JsonArray();
        for (var i = 0; i < hatch.NumberOfLoops && i < HatchLoopLimit; i++)
        {
            var loop = hatch.GetLoopAt(i);
            var item = new JsonObject { ["type"] = loop.LoopType.ToString() };
            if (loop.IsPolyline)
            {
                var points = new JsonArray();
                var vertexCount = loop.Polyline.Count;
                for (var v = 0; v < vertexCount && v < DetailVertexLimit; v++)
                {
                    var vertex = loop.Polyline[v];
                    var point = Pair(vertex.Vertex.X, vertex.Vertex.Y);
                    if (Math.Abs(vertex.Bulge) > 1e-12)
                    {
                        point.Add(ToolContext.Round(vertex.Bulge));
                    }

                    points.Add(point);
                }

                item["points"] = points;
            }
            else
            {
                item["edges"] = loop.Curves.Count;
            }

            loops.Add(item);
        }

        json["loopDetails"] = loops;
    }

    private static void AddBlockReference(JsonObject json, BlockReference reference, DrawingUnits units, Transaction? transaction, bool detailed)
    {
        json["block"] = EffectiveName(reference, transaction);
        if (reference.IsDynamicBlock && !string.Equals(reference.Name, json["block"]?.GetValue<string>(), StringComparison.Ordinal))
        {
            json["anonymousName"] = reference.Name;
        }

        json["position"] = ToolContext.Point(reference.Position, units);
        json["rotation"] = Degrees(reference.Rotation);
        var scale = reference.ScaleFactors;
        json["scale"] = new JsonArray(ToolContext.Round(scale.X), ToolContext.Round(scale.Y), ToolContext.Round(scale.Z));

        var attributes = ReadAttributes(reference, transaction, detailed ? int.MaxValue : CompactAttributeLimit);
        if (attributes.Count > 0)
        {
            json["attributes"] = attributes;
        }

        if (detailed && reference.IsDynamicBlock)
        {
            var properties = new JsonObject();
            foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
            {
                if (!property.Show || string.Equals(property.PropertyName, "Origin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                properties[property.PropertyName] = PropertyValue(property.Value);
            }

            if (properties.Count > 0)
            {
                json["dynamicProperties"] = properties;
            }
        }
    }

    private static void AddMLeader(JsonObject json, MLeader mleader, DrawingUnits units)
    {
        json["leaders"] = mleader.LeaderCount;
        json["content"] = mleader.ContentType.ToString();
        if (mleader.ContentType == ContentType.MTextContent)
        {
            using var text = mleader.MText;
            if (text is not null)
            {
                json["text"] = Truncate(text.Text, CompactTextLimit);
                json["position"] = ToolContext.Point(mleader.TextLocation, units);
            }
        }
    }

    private static string EffectiveName(BlockReference reference, Transaction? transaction)
    {
        if (!reference.IsDynamicBlock)
        {
            return reference.Name;
        }

        try
        {
            return WithTransaction(reference.Database, transaction, tr =>
                ((BlockTableRecord)tr.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead)).Name);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return reference.Name;
        }
    }

    private static JsonObject ReadAttributes(BlockReference reference, Transaction? transaction, int limit)
    {
        var result = new JsonObject();
        if (reference.AttributeCollection.Count == 0 || reference.Database is null)
        {
            return result;
        }

        WithTransaction(reference.Database, transaction, tr =>
        {
            foreach (ObjectId id in reference.AttributeCollection)
            {
                if (result.Count >= limit)
                {
                    break;
                }

                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is AttributeReference attribute && !result.ContainsKey(attribute.Tag))
                {
                    result[attribute.Tag] = Truncate(attribute.TextString, CompactTextLimit);
                }
            }

            return true;
        });
        return result;
    }

    private static T WithTransaction<T>(Database? database, Transaction? transaction, Func<Transaction, T> read)
    {
        if (transaction is not null)
        {
            return read(transaction);
        }

        ArgumentNullException.ThrowIfNull(database);
        using var local = database.TransactionManager.StartOpenCloseTransaction();
        var result = read(local);
        local.Commit();
        return result;
    }

    private static string SpaceName(Entity entity, Transaction transaction)
    {
        try
        {
            var owner = transaction.GetObject(entity.OwnerId, OpenMode.ForRead) as BlockTableRecord;
            if (owner is null)
            {
                return "?";
            }

            if (SymbolUtilityServices.IsBlockModelSpaceName(owner.Name))
            {
                return "model";
            }

            return owner.IsLayout && transaction.GetObject(owner.LayoutId, OpenMode.ForRead) is Layout layout
                ? "paper:" + layout.LayoutName
                : "block:" + owner.Name;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return "?";
        }
    }

    private static JsonNode? PropertyValue(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag,
        double number => ToolContext.Round(number),
        float number => ToolContext.Round(number),
        short or int or long or byte or ushort or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        Point3d point => new JsonArray(ToolContext.Round(point.X), ToolContext.Round(point.Y), ToolContext.Round(point.Z)),
        Point2d point => new JsonArray(ToolContext.Round(point.X), ToolContext.Round(point.Y)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static JsonObject? Box(Entity entity, DrawingUnits units) =>
        PlanBounds(entity) is { } box ? ViewFacts.BoxJson(box, units) : null;

    private static JsonObject Area(double area, DrawingUnits units)
    {
        var json = new JsonObject { ["sqDu"] = ToolContext.Round(area) };
        if (units.ToSquareFeet(area) is { } squareFeet)
        {
            json["sqFt"] = squareFeet;
        }

        return json;
    }

    private static double CurveLength(Curve curve)
    {
        try
        {
            return Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return curve.StartPoint.DistanceTo(curve.EndPoint);
        }
    }

    private static JsonArray Pair(Point3d point) => Pair(point.X, point.Y);

    private static JsonArray Pair(double x, double y) => new(ToolContext.Round(x), ToolContext.Round(y));

    private static double Degrees(double radians)
    {
        var degrees = radians * 180.0 / Math.PI % 360.0;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        var rounded = Math.Round(degrees, 2, MidpointRounding.AwayFromZero);
        return rounded >= 360.0 ? 0 : rounded;
    }

    private static string Truncate(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit), "…");
    }
}
