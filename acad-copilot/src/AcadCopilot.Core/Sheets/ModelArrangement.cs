using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Sheets;

/// <summary>Where one drawing goes when model space is arranged.</summary>
/// <param name="Number">The drawing's number.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Name">Its title (or "DRAWING n").</param>
/// <param name="Row">1 = floor plans, 2 = elevations, sections, site plan, 3 = everything else.</param>
/// <param name="From">Its extents now.</param>
/// <param name="To">Its extents after the move.</param>
public sealed record ArrangedDrawing(int Number, DrawingKind Kind, string Name, int Row, SheetRect From, SheetRect To)
{
    /// <summary>Move in X (drawing units).</summary>
    public double Dx => To.MinX - From.MinX;

    /// <summary>Move in Y (drawing units).</summary>
    public double Dy => To.MinY - From.MinY;

    /// <summary>True when the drawing actually moves.</summary>
    public bool Moves => Math.Abs(Dx) > 1e-6 || Math.Abs(Dy) > 1e-6;
}

/// <summary>A planned arrangement.</summary>
/// <param name="Drawings">Every drawing with its move, in row order.</param>
/// <param name="Gap">The gap used between drawings (drawing units).</param>
/// <param name="Sketch">A small text picture of the result (each drawing is a box with its number).</param>
public sealed record ArrangementResult(IReadOnlyList<ArrangedDrawing> Drawings, double Gap, string Sketch);

/// <summary>A title and scale line to add under a drawing (model space).</summary>
/// <param name="X">Centre X of both lines.</param>
/// <param name="TitleY">Centre Y of the title.</param>
/// <param name="ScaleY">Centre Y of the scale line.</param>
/// <param name="TitleHeight">Title text height (drawing units).</param>
/// <param name="ScaleHeight">Scale text height.</param>
/// <param name="Title">Title text, with %%u (underlined) in front.</param>
/// <param name="ScaleLine">Scale line ("SCALE 1/4\" = 1'-0\"").</param>
/// <param name="Box">The space the two lines take.</param>
public sealed record PlannedTitle(double X, double TitleY, double ScaleY, double TitleHeight, double ScaleHeight, string? Title, string ScaleLine, SheetRect Box);

/// <summary>
/// Lines the drawings of model space up the professional way: the floor plans in one row (ground, first, second ...,
/// terrace) with the same spacing between their wall extents, so walls and stairs line up from floor to floor; the
/// elevations in a row below (front first, under the ground floor plan), then the sections and the site plan; anything
/// else in a third row. The gap between drawings is about one plan width (at least 20'). The first floor plan (or the
/// first drawing) stays where it is. Pure.
/// </summary>
public static class ModelArrangement
{
    /// <summary>Smallest gap between drawings: 20'-0".</summary>
    public const double MinGapInches = 240;

    /// <summary>Space between a drawing and its title (at 1/4" = 1'-0"): 4'-0".</summary>
    public const double TitleOffsetInches = 48;

    /// <summary>Title text height at 1/4" = 1'-0".</summary>
    public const double TitleHeightInches = 12;

    /// <summary>Scale line height at 1/4" = 1'-0".</summary>
    public const double ScaleHeightInches = 6;

    /// <summary>The automatic gap: the widest floor plan's wall extents, at least 20'; 20' when there are no plans.</summary>
    public static double AutomaticGap(IReadOnlyList<DrawingCluster> clusters, double inchesPerUnit)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var minimum = MinGapInches / PositiveOr1(inchesPerUnit);
        var widest = clusters.Where(c => c.Kind == DrawingKind.FloorPlan).Select(c => c.Anchor.Width).DefaultIfEmpty(0).Max();
        return Math.Max(minimum, widest);
    }

    /// <summary>
    /// Plans the arrangement. <paramref name="boxes"/> may give a drawing's extents including a title still to be added
    /// (by number); otherwise its cluster extents are used. <paramref name="gap"/> in drawing units (null = automatic).
    /// </summary>
    public static ArrangementResult Arrange(
        IReadOnlyList<DrawingCluster> clusters, double inchesPerUnit, double? gap = null, IReadOnlyDictionary<int, SheetRect>? boxes = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var g = gap is > 0 ? gap.Value : AutomaticGap(clusters, inchesPerUnit);
        SheetRect BoxOf(DrawingCluster c) => boxes is not null && boxes.TryGetValue(c.Number, out var b) ? b.Union(c.Box) : c.Box;

        var rows = new List<List<DrawingCluster>>
        {
            [.. clusters.Where(c => c.Kind == DrawingKind.FloorPlan).OrderBy(c => c.Order).ThenBy(c => c.Box.MinX)],
            [.. clusters.Where(c => c.Kind == DrawingKind.Elevation).OrderBy(c => c.Order).ThenBy(c => c.Number)
                .Concat(clusters.Where(c => c.Kind == DrawingKind.Section).OrderBy(c => c.Order).ThenBy(c => c.Number))
                .Concat(clusters.Where(c => c.Kind == DrawingKind.SitePlan).OrderBy(c => c.Number))],
            [.. clusters.Where(c => c.Kind is DrawingKind.Layout or DrawingKind.Schedule or DrawingKind.Other)
                .OrderBy(c => c.Kind == DrawingKind.Other ? 1 : 0).ThenBy(c => c.Order).ThenBy(c => c.Number)],
        };

        var result = new List<ArrangedDrawing>();
        double? startX = null;
        double? previousBottom = null;
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0)
            {
                continue;
            }

            double baseline;
            if (startX is null)
            {
                // The first drawing of the first row stays where it is.
                startX = row[0].Anchor.MinX;
                baseline = row[0].Anchor.MinY;
            }
            else
            {
                var headroom = row.Max(c => BoxOf(c).MaxY - c.Anchor.MinY);
                baseline = previousBottom!.Value - g - headroom;
            }

            var placed = new List<SheetRect>();
            if (r == 0)
            {
                // Floor plans: equal spacing of their wall extents (same origin spacing between floors).
                var pitch = row.Max(c => BoxOf(c).MaxX - c.Anchor.MinX) + row.Max(c => c.Anchor.MinX - BoxOf(c).MinX) + g;
                for (var i = 0; i < row.Count; i++)
                {
                    placed.Add(Move(row[i], BoxOf(row[i]), startX.Value + (i * pitch), baseline));
                }
            }
            else
            {
                var x = startX.Value;
                for (var i = 0; i < row.Count; i++)
                {
                    var box = BoxOf(row[i]);
                    if (i > 0)
                    {
                        x = placed[^1].MaxX + g + (row[i].Anchor.MinX - box.MinX);
                    }

                    placed.Add(Move(row[i], box, x, baseline));
                }
            }

            for (var i = 0; i < row.Count; i++)
            {
                result.Add(new ArrangedDrawing(row[i].Number, row[i].Kind, row[i].Name, r + 1, BoxOf(row[i]), placed[i]));
            }

            previousBottom = placed.Min(p => p.MinY);
        }

        return new ArrangementResult(result, g, Sketch([.. result.Select(d => (d.Number, d.To))]));
    }

    /// <summary>
    /// The title and scale line to add under a drawing whose extents are <paramref name="box"/>: title 12 and scale 6
    /// high at 1/4" = 1'-0" (twice that at 1/8"), centred, 4'-0" (at 1/4") below it. <paramref name="title"/> null = scale
    /// line only, placed under the existing title <paramref name="existingTitle"/>.
    /// </summary>
    public static PlannedTitle PlanTitle(SheetRect box, string? title, DrawingScale scale, double inchesPerUnit, SheetRect? existingTitle = null)
    {
        ArgumentNullException.ThrowIfNull(scale);
        var k = scale.TextFactor / PositiveOr1(inchesPerUnit);
        var titleHeight = TitleHeightInches * k;
        var scaleHeight = ScaleHeightInches * k;
        double x, titleY, scaleY;
        if (title is null && existingTitle is { } t)
        {
            x = t.CenterX;
            titleY = t.CenterY;
            scaleY = t.MinY - (6 * k) - (scaleHeight / 2);
        }
        else
        {
            x = box.CenterX;
            titleY = box.MinY - (TitleOffsetInches * k) - (titleHeight / 2);
            scaleY = titleY - (15 * k);
        }

        var shown = title is null ? null : "%%u" + DrawingTitles.Clean(title);
        var width = Math.Max((title ?? string.Empty).Length * titleHeight, scale.ScaleLine.Length * scaleHeight) * 0.8;
        var top = title is null ? scaleY + (scaleHeight / 2) : titleY + (titleHeight / 2);
        var area = SheetRect.FromCorners(x - (width / 2), scaleY - (scaleHeight / 2), x + (width / 2), top);
        return new PlannedTitle(x, titleY, scaleY, titleHeight, scaleHeight, shown, scale.ScaleLine, area);
    }

    /// <summary>
    /// A small text picture of drawings (one box per drawing, its number inside), at most <paramref name="width"/>
    /// characters wide and <paramref name="height"/> lines high; Y up.
    /// </summary>
    public static string Sketch(IReadOnlyList<(int Number, SheetRect Box)> drawings, int width = 64, int height = 20)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        if (drawings.Count == 0)
        {
            return string.Empty;
        }

        var all = drawings.Select(d => d.Box).Aggregate((a, b) => a.Union(b));
        var w = Math.Max(all.Width, 1e-9);
        var h = Math.Max(all.Height, 1e-9);

        // Characters are about twice as tall as wide.
        var sx = (width - 1) / w;
        var sy = Math.Min((height - 1) / h, sx / 2);
        var rows = Math.Max(2, (int)Math.Ceiling(h * sy) + 1);
        var grid = new char[rows][];
        for (var i = 0; i < rows; i++)
        {
            grid[i] = Enumerable.Repeat(' ', width).ToArray();
        }

        foreach (var (number, box) in drawings)
        {
            var c0 = (int)Math.Round((box.MinX - all.MinX) * sx);
            var c1 = Math.Max(c0 + 2, (int)Math.Round((box.MaxX - all.MinX) * sx));
            var r0 = (int)Math.Round((all.MaxY - box.MaxY) * sy);
            var r1 = Math.Max(r0 + 1, (int)Math.Round((all.MaxY - box.MinY) * sy));
            c1 = Math.Min(c1, width - 1);
            r1 = Math.Min(r1, rows - 1);
            for (var c = c0; c <= c1; c++)
            {
                grid[r0][c] = grid[r0][c] == '|' ? '+' : '-';
                grid[r1][c] = grid[r1][c] == '|' ? '+' : '-';
            }

            for (var r = r0; r <= r1; r++)
            {
                grid[r][c0] = r == r0 || r == r1 ? '+' : '|';
                grid[r][c1] = r == r0 || r == r1 ? '+' : '|';
            }

            var label = number.ToString(CultureInfo.InvariantCulture);
            var labelRow = r1 - r0 >= 2 ? (r0 + r1) / 2 : r0;
            var labelCol = Math.Max(c0 + 1, ((c0 + c1) / 2) - (label.Length / 2));
            for (var i = 0; i < label.Length && labelCol + i < c1; i++)
            {
                grid[labelRow][labelCol + i] = label[i];
            }
        }

        var builder = new StringBuilder();
        foreach (var line in grid)
        {
            builder.Append(new string(line).TrimEnd()).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static SheetRect Move(DrawingCluster cluster, SheetRect box, double anchorX, double anchorY) =>
        box.Offset(anchorX - cluster.Anchor.MinX, anchorY - cluster.Anchor.MinY);

    private static double PositiveOr1(double value) => value > 0 && double.IsFinite(value) ? value : 1;
}
