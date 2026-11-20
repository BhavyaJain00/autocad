using AcadCopilot.Core.Drafting;
using AcadCopilot.Core.Sheets;
using AcadCopilot.Plugin.Tools;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Sheets;

/// <summary>The drawings found in model space with everything the drawing-set tools need about them.</summary>
/// <param name="Scan">Model space objects.</param>
/// <param name="Clusters">The drawings (labels applied).</param>
/// <param name="Warnings">Problems found while separating them.</param>
/// <param name="UnmatchedLabels">Labels that named no drawing.</param>
/// <param name="Units">The drawing's units.</param>
/// <param name="InchesPerUnit">Inches per drawing unit (1 when unknown).</param>
/// <param name="System">Scale notation for these units.</param>
/// <param name="ClusterGap">The gap used to separate drawings (drawing units).</param>
internal sealed record FoundDrawings(
    ModelSpaceScan Scan,
    IReadOnlyList<DrawingCluster> Clusters,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<DrawingLabel> UnmatchedLabels,
    DrawingUnits Units,
    double InchesPerUnit,
    ScaleSystem System,
    double ClusterGap)
{
    /// <summary>The drawings as the sheet planner sees them.</summary>
    public List<SheetDrawing> SheetDrawings() =>
        [.. Clusters.Select(c => new SheetDrawing(c.Number, c.Kind, c.Order, c.Name, c.Box))];
}

/// <summary>Finds the drawings of model space (main thread, inside the caller's transaction).</summary>
internal static class DrawingFinder
{
    /// <summary>Scans model space, separates the drawings and applies the AI's labels.</summary>
    public static FoundDrawings Find(Database database, Transaction transaction, double? clusterGap, IReadOnlyList<DrawingLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        var units = ToolContext.GetUnits(database);
        var ipu = units.InchesPerUnit is { } inches && inches > 0 ? inches : 1;
        var system = units.LUnits is 3 or 4 ? ScaleSystem.Imperial : DrawingScales.SystemFor(units.InsUnits);
        var gap = clusterGap is > 0 ? clusterGap.Value : DrawingClusters.DefaultGapInches / ipu;
        var scan = ModelSpaceScan.Read(database, transaction);
        var found = DrawingClusters.Find(scan.Items, gap);
        var (clusters, unmatched) = DrawingClusters.ApplyLabels(found.Clusters, labels ?? [], gap);
        return new FoundDrawings(scan, clusters, found.Warnings, unmatched, units, ipu, system, gap);
    }

    /// <summary>Layer <paramref name="preferred"/> when it exists, else the current layer.</summary>
    public static ObjectId LayerOrCurrent(Database database, Transaction transaction, string preferred)
    {
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        return layers.Has(preferred) ? layers[preferred] : database.Clayer;
    }

    /// <summary>Text style <paramref name="preferred"/> when it exists, else the current style.</summary>
    public static ObjectId StyleOrCurrent(Database database, Transaction transaction, string preferred)
    {
        var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
        return styles.Has(preferred) ? styles[preferred] : database.Textstyle;
    }

    /// <summary>Adds a middle-centred single-line text to <paramref name="space"/>; returns its id.</summary>
    public static ObjectId AddText(Database database, Transaction transaction, BlockTableRecord space, double x, double y, double height, string text, ObjectId layer, ObjectId style)
    {
        var entity = new DBText();
        entity.SetDatabaseDefaults(database);
        entity.LayerId = layer;
        entity.TextStyleId = style;
        entity.Height = height;
        entity.TextString = text;
        var point = new Point3d(x, y, 0);
        entity.Position = point;
        entity.HorizontalMode = TextHorizontalMode.TextCenter;
        entity.VerticalMode = TextVerticalMode.TextVerticalMid;
        entity.AlignmentPoint = point;
        var id = space.AppendEntity(entity);
        transaction.AddNewlyCreatedDBObject(entity, true);
        entity.AdjustAlignment(database);
        return id;
    }

    /// <summary>Replaces the text of a TEXT or MTEXT object; true when it changed.</summary>
    public static bool SetText(Transaction transaction, ObjectId id, string text)
    {
        switch (transaction.GetObject(id, OpenMode.ForRead))
        {
            case DBText dbText when dbText.TextString != text:
                dbText.UpgradeOpen();
                dbText.TextString = text;
                return true;
            case MText mText when mText.Contents != text:
                mText.UpgradeOpen();
                mText.Contents = text;
                return true;
            default:
                return false;
        }
    }
}
