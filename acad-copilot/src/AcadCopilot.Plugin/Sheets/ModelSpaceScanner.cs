using AcadCopilot.Core.Sheets;
using AcadCopilot.Plugin.Inspect;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadCopilot.Plugin.Sheets;

/// <summary>
/// Model space as the drawing-set tools see it: every object with extents as a <see cref="ClusterItem"/> (box, text,
/// annotation / wall flags), with its ObjectId and layer at the same index. Main thread, inside the caller's transaction.
/// </summary>
internal sealed class ModelSpaceScan
{
    /// <summary>The items for <see cref="DrawingClusters.Find"/>.</summary>
    public List<ClusterItem> Items { get; } = [];

    /// <summary>The object of each item.</summary>
    public List<ObjectId> Ids { get; } = [];

    /// <summary>The layer of each item.</summary>
    public List<string> Layers { get; } = [];

    /// <summary>Reads model space of <paramref name="database"/>.</summary>
    public static ModelSpaceScan Read(Database database, Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        var scan = new ModelSpaceScan();
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
        foreach (ObjectId id in space)
        {
            if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (EntitySummary.PlanBounds(entity) is not { } bounds)
            {
                continue;
            }

            var layer = entity.Layer ?? string.Empty;
            var upper = layer.ToUpperInvariant();
            string? text = null;
            double height = 0;
            switch (entity)
            {
                case DBText dbText:
                    text = dbText.TextString;
                    height = dbText.Height;
                    break;
                case MText mText:
                    text = mText.Contents;
                    height = mText.TextHeight;
                    break;
            }

            var annotation = text is not null || entity is Dimension or Leader or MLeader ||
                upper.Contains("ANNO", StringComparison.Ordinal) || upper.Contains("DIM", StringComparison.Ordinal) ||
                upper.Contains("TEXT", StringComparison.Ordinal);
            var wall = upper.Contains("WALL", StringComparison.Ordinal) && text is null;
            scan.Items.Add(new ClusterItem(new SheetRect(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY), text, height, annotation, wall));
            scan.Ids.Add(id);
            scan.Layers.Add(layer);
        }

        return scan;
    }

    /// <summary>The objects of <paramref name="cluster"/>.</summary>
    public IEnumerable<ObjectId> IdsOf(DrawingCluster cluster) => cluster.Items.Select(i => Ids[i]);
}
