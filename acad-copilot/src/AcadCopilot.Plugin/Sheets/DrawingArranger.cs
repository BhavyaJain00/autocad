using AcadCopilot.Core.Sheets;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadCopilot.Plugin.Sheets;

/// <summary>What arrange_drawings was asked.</summary>
internal sealed record ArrangeRequest(
    IReadOnlyList<DrawingLabel> Labels,
    double? Gap,
    double? ClusterGap,
    bool AddTitles,
    DrawingScale? Scale);

/// <summary>A title change for one drawing.</summary>
/// <param name="Number">Drawing number.</param>
/// <param name="What">"title and scale added", "scale line added", "scale line corrected".</param>
/// <param name="Planned">Texts to add (null when only an existing scale line is corrected).</param>
/// <param name="ScaleItem">Item index of an existing scale line to correct.</param>
/// <param name="Scale">The drawing's scale.</param>
internal sealed record TitleAction(int Number, string What, PlannedTitle? Planned, int? ScaleItem, DrawingScale Scale);

/// <summary>The result of arrange_drawings.</summary>
internal sealed record ArrangeOutcome(
    FoundDrawings Found,
    ArrangementResult Plan,
    IReadOnlyList<TitleAction> Titles,
    IReadOnlyDictionary<int, DrawingScale> Scales,
    IReadOnlyList<ObjectId> Created,
    IReadOnlyList<ObjectId> Moved,
    IReadOnlyList<string> Warnings);

/// <summary>
/// arrange_drawings in AutoCAD: finds the drawings, plans titles and the arrangement (Core), and with
/// <c>apply</c> moves whole drawings (TransformBy, locked layers unlocked for the move and locked again) and adds or
/// corrects the titles. Main thread, inside the caller's transaction (one undo step).
/// </summary>
internal static class DrawingArranger
{
    /// <summary>Plans and (when <paramref name="apply"/>) carries out the arrangement.</summary>
    public static ArrangeOutcome Run(Database database, Transaction transaction, ArrangeRequest request, bool apply)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        var found = DrawingFinder.Find(database, transaction, request.ClusterGap, request.Labels);
        var warnings = new List<string>(found.Warnings);
        warnings.AddRange(found.UnmatchedLabels.Select(l => $"Label \"{l.Title}\" at {l.X:0.##},{l.Y:0.##} is not inside or near any drawing."));
        var scales = SheetPlanner.ChooseScales(found.SheetDrawings(), new SheetOptions { System = found.System, InchesPerUnit = found.InchesPerUnit, Scale = request.Scale });

        // Titles first: an added title makes the drawing taller, and the arrangement must leave room for it.
        var titles = new List<TitleAction>();
        var boxes = new Dictionary<int, SheetRect>();
        if (request.AddTitles)
        {
            foreach (var cluster in found.Clusters.Where(c => c.Title is not null))
            {
                var scale = scales[cluster.Number];
                if (cluster.TitleItem is null)
                {
                    var planned = ModelArrangement.PlanTitle(cluster.Box, cluster.Title!.Title, scale, found.InchesPerUnit);
                    titles.Add(new TitleAction(cluster.Number, "title and scale line added", planned, null, scale));
                    boxes[cluster.Number] = planned.Box;
                }
                else if (cluster.ScaleItem is null)
                {
                    var planned = ModelArrangement.PlanTitle(cluster.Box, null, scale, found.InchesPerUnit, found.Scan.Items[cluster.TitleItem.Value].Box);
                    titles.Add(new TitleAction(cluster.Number, "scale line added", planned, null, scale));
                    boxes[cluster.Number] = planned.Box;
                }
                else if (DrawingTitles.Clean(found.Scan.Items[cluster.ScaleItem.Value].Text) != DrawingTitles.Clean(scale.ScaleLine))
                {
                    titles.Add(new TitleAction(cluster.Number, "scale line corrected", null, cluster.ScaleItem, scale));
                }
            }
        }

        var plan = ModelArrangement.Arrange(found.Clusters, found.InchesPerUnit, request.Gap, boxes);
        var created = new List<ObjectId>();
        var moved = new List<ObjectId>();
        if (!apply)
        {
            return new ArrangeOutcome(found, plan, titles, scales, created, moved, warnings);
        }

        using var unlocked = new LayerUnlocker(database, transaction);
        var byNumber = found.Clusters.ToDictionary(c => c.Number);
        foreach (var drawing in plan.Drawings.Where(d => d.Moves))
        {
            var displacement = Matrix3d.Displacement(new Vector3d(drawing.Dx, drawing.Dy, 0));
            foreach (var index in byNumber[drawing.Number].Items)
            {
                var id = found.Scan.Ids[index];
                unlocked.Unlock(found.Scan.Layers[index]);
                var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
                entity.TransformBy(displacement);
                moved.Add(id);
            }
        }

        if (titles.Count > 0)
        {
            var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
            var layer = DrawingFinder.LayerOrCurrent(database, transaction, "A-ANNO-TEXT");
            var style = DrawingFinder.StyleOrCurrent(database, transaction, "ARCH");
            var offsets = plan.Drawings.ToDictionary(d => d.Number, d => (d.Dx, d.Dy));
            foreach (var action in titles)
            {
                var (dx, dy) = offsets[action.Number];
                if (action.Planned is { } p)
                {
                    if (p.Title is not null)
                    {
                        created.Add(DrawingFinder.AddText(database, transaction, space, p.X + dx, p.TitleY + dy, p.TitleHeight, p.Title, layer, style));
                    }

                    created.Add(DrawingFinder.AddText(database, transaction, space, p.X + dx, p.ScaleY + dy, p.ScaleHeight, p.ScaleLine, layer, style));
                }
                else if (action.ScaleItem is { } item)
                {
                    unlocked.Unlock(found.Scan.Layers[item]);
                    if (DrawingFinder.SetText(transaction, found.Scan.Ids[item], action.Scale.ScaleLine))
                    {
                        moved.Add(found.Scan.Ids[item]);
                    }
                }
            }
        }

        return new ArrangeOutcome(found, plan, titles, scales, created, [.. moved.Distinct()], warnings);
    }
}

/// <summary>Unlocks layers on demand and locks them again when disposed (inside the same transaction).</summary>
internal sealed class LayerUnlocker : IDisposable
{
    private readonly Transaction _transaction;
    private readonly LayerTable _layers;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LayerTableRecord> _relock = [];

    /// <summary>Creates the unlocker for <paramref name="database"/>.</summary>
    public LayerUnlocker(Database database, Transaction transaction)
    {
        _transaction = transaction;
        _layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
    }

    /// <summary>Unlocks <paramref name="layer"/> when it is locked.</summary>
    public void Unlock(string layer)
    {
        if (!_checked.Add(layer) || !_layers.Has(layer))
        {
            return;
        }

        var record = (LayerTableRecord)_transaction.GetObject(_layers[layer], OpenMode.ForRead);
        if (record.IsLocked)
        {
            record.UpgradeOpen();
            record.IsLocked = false;
            _relock.Add(record);
        }
    }

    /// <summary>Locks the unlocked layers again.</summary>
    public void Dispose()
    {
        foreach (var record in _relock)
        {
            record.IsLocked = true;
        }
    }
}
