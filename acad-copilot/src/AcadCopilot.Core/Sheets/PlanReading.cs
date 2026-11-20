using System.Text.RegularExpressions;
using AcadCopilot.Core.Accuracy;

namespace AcadCopilot.Core.Sheets;

/// <summary>
/// Reads the facts plan_full_set needs from a ground floor plan: from the drawing (walls on wall layers, the stair on
/// A-STRS, the plot on A-SITE, wet rooms by their names, opening tags) or from the plan validated by validate_plan
/// (the accuracy pack's <see cref="HousePlan"/>). Pure.
/// </summary>
public static partial class PlanReading
{
    /// <summary>Wall lines thinner than this (inches) bound rooms when a wet area is measured.</summary>
    public const double ThinWallInches = 12;

    /// <summary>
    /// The plan facts of <paramref name="plan"/> (a drawing found by <see cref="DrawingClusters"/>) from its objects:
    /// outline = wall extents (else the drawing's non-annotation extents), stair = A-STRS / *STAIR* objects, plot =
    /// A-SITE / *PLOT* objects, wet areas = rooms named TOILET, BATH, WC, KITCHEN, UTILITY, WASH (measured from the
    /// name to the nearest wall lines), openings = tags counted in the texts.
    /// </summary>
    public static FullSetInput FromDrawing(IReadOnlyList<ClusterItem> items, IReadOnlyList<string> layers, DrawingCluster plan, double inchesPerUnit)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(plan);
        var ipu = inchesPerUnit > 0 ? inchesPerUnit : 1;
        var outline = plan.Anchor;
        SheetRect? Union(Func<int, bool> select)
        {
            SheetRect? box = null;
            foreach (var i in plan.Items.Where(select))
            {
                box = box is { } b ? b.Union(items[i].Box) : items[i].Box;
            }

            return box;
        }

        string Upper(int i) => layers[i].ToUpperInvariant();
        var stair = Union(i => items[i].Text is null && (Upper(i).Contains("STR", StringComparison.Ordinal) || Upper(i).Contains("STAIR", StringComparison.Ordinal)));
        var plot = Union(i => items[i].Text is null && (Upper(i).Contains("SITE", StringComparison.Ordinal) || Upper(i).Contains("PLOT", StringComparison.Ordinal)));
        if (plot is { } p && !p.Contains(outline, 1e-6))
        {
            plot = null; // site objects that do not surround the building are not the plot boundary
        }

        var thin = plan.Items
            .Where(i => items[i].IsWall)
            .Select(i => items[i].Box)
            .Where(b => Math.Min(b.Width, b.Height) * ipu <= ThinWallInches && Math.Max(b.Width, b.Height) * ipu > 2 * ThinWallInches)
            .ToList();
        var wet = new List<NamedRect>();
        foreach (var i in plan.Items.Where(i => items[i].Text is not null))
        {
            var text = DrawingTitles.Clean(items[i].Text);
            var match = WetRoom().Match(text);
            if (match.Success && outline.Contains(items[i].Box.CenterX, items[i].Box.CenterY))
            {
                wet.Add(new NamedRect(match.Value, RoomAround(items[i].Box.CenterX, items[i].Box.CenterY, thin, outline)));
            }
        }

        return new FullSetInput
        {
            Outline = outline,
            PlanBox = plan.Box,
            Stair = stair,
            Plot = plot,
            WetAreas = wet,
            Openings = FullSetPlanner.CountTags(plan.Items.Select(i => items[i].Text)),
            InchesPerUnit = ipu,
            Source = "drawing",
        };
    }

    /// <summary>
    /// The room around a point: up to the nearest thin wall line above, below, left and right of it (the outline's
    /// faces when there is none).
    /// </summary>
    public static SheetRect RoomAround(double x, double y, IEnumerable<SheetRect> thinWalls, SheetRect outline)
    {
        ArgumentNullException.ThrowIfNull(thinWalls);
        double top = outline.MaxY, bottom = outline.MinY, left = outline.MinX, right = outline.MaxX;
        foreach (var w in thinWalls)
        {
            if (w.MinX <= x && w.MaxX >= x)
            {
                if (w.MinY >= y && w.MinY < top)
                {
                    top = w.MinY;
                }

                if (w.MaxY <= y && w.MaxY > bottom)
                {
                    bottom = w.MaxY;
                }
            }

            if (w.MinY <= y && w.MaxY >= y)
            {
                if (w.MinX >= x && w.MinX < right)
                {
                    right = w.MinX;
                }

                if (w.MaxX <= x && w.MaxX > left)
                {
                    left = w.MaxX;
                }
            }
        }

        return new SheetRect(left, bottom, right, top);
    }

    /// <summary>
    /// The plan facts from a validated plan (validate_plan): the ground floor's outline, stair box, wet rooms and
    /// openings moved by the floor's origin (where the plot's 0,0 sits in the drawing) and converted to drawing units,
    /// the plot, the road side, the floor height and the number of floors. Null when the plan has no ground floor.
    /// </summary>
    public static FullSetInput? FromHousePlan(HousePlan plan, double inchesPerUnit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ipu = inchesPerUnit > 0 ? inchesPerUnit : 1;
        var ground = plan.Floors.OrderBy(f => f.Index).FirstOrDefault();
        if (ground is null)
        {
            return null;
        }

        var ox = ground.Origin?.X ?? 0;
        var oy = ground.Origin?.Y ?? 0;
        SheetRect Map(Box b) => new((b.MinX + ox) / ipu, (b.MinY + oy) / ipu, (b.MaxX + ox) / ipu, (b.MaxY + oy) / ipu);
        var openings = ground.Doors.Concat(ground.Windows)
            .GroupBy(o => o.Tag.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Select(g => new OpeningCount(g.Key, g.Count(), g.Average(o => o.Width)))
            .ToList();
        return new FullSetInput
        {
            Outline = Map(ground.Outline.Bounds),
            Stair = ground.Stair?.Box is { } stair ? Map(stair) : null,
            Plot = plan.PlotWidth > 0 && plan.PlotDepth > 0 ? Map(new Box(0, 0, plan.PlotWidth, plan.PlotDepth)) : null,
            WetAreas = [.. ground.Rooms.Where(r => RoomTypes.IsWet(r.Type)).Select(r => new NamedRect(r.Name.ToUpperInvariant(), Map(r.Shape.Bounds)))],
            Openings = openings,
            UpperFloors = Math.Max(1, plan.Floors.Count - 1),
            FloorHeightInches = plan.FloorHeightGiven ? plan.FloorHeight : 126,
            FrontSide = plan.RoadSide ?? "south",
            InchesPerUnit = ipu,
            Source = "validated plan",
        };
    }

    [GeneratedRegex(@"\b(TOILET|BATH(ROOM)?|W\.?C\.?|KITCHEN|UTILITY|WASH|POWDER)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WetRoom();
}
