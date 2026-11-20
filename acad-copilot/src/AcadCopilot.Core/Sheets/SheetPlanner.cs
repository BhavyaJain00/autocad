using System.Globalization;

namespace AcadCopilot.Core.Sheets;

/// <summary>One drawing to put on a sheet.</summary>
/// <param name="Number">The drawing's number (arrange_drawings / create_sheets numbering).</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Order">Sort order within the kind (floor level for plans).</param>
/// <param name="Title">Its title ("GROUND FLOOR PLAN").</param>
/// <param name="ModelBox">Its extents in model space (drawing units), title included.</param>
public sealed record SheetDrawing(int Number, DrawingKind Kind, double Order, string Title, SheetRect ModelBox);

/// <summary>Options of a sheet set.</summary>
public sealed record SheetOptions
{
    /// <summary>Paper (default ISO A3 landscape).</summary>
    public PaperSize Paper { get; init; } = PaperSizes.All[0];

    /// <summary>Scale notation (imperial for drawings in inches / feet).</summary>
    public ScaleSystem System { get; init; } = ScaleSystem.Imperial;

    /// <summary>Inches per drawing unit (1 for INSUNITS inches, 1/25.4 for millimetres).</summary>
    public double InchesPerUnit { get; init; } = 1;

    /// <summary>Put several small drawings (elevations, sections, schedules) on one sheet (default true).</summary>
    public bool Combine { get; init; } = true;

    /// <summary>One scale for every drawing instead of the automatic choice (null = automatic).</summary>
    public DrawingScale? Scale { get; init; }

    /// <summary>Sheet number prefix (default "A": A-101).</summary>
    public string Prefix { get; init; } = "A";
}

/// <summary>A viewport on a sheet.</summary>
/// <param name="DrawingNumber">The drawing shown.</param>
/// <param name="Title">The drawing's title.</param>
/// <param name="Scale">Its scale.</param>
/// <param name="Paper">The viewport rectangle on the sheet (paper mm).</param>
/// <param name="Model">The model-space rectangle it shows (drawing units; centred on the drawing).</param>
/// <param name="Cropped">True when the drawing did not fit even at the smallest scale and is shown cropped.</param>
public sealed record SheetViewport(int DrawingNumber, string Title, DrawingScale Scale, SheetRect Paper, SheetRect Model, bool Cropped);

/// <summary>One sheet (layout tab).</summary>
/// <param name="Number">Sheet number ("A-101").</param>
/// <param name="LayoutName">Layout tab name ("A-101 Ground Floor Plan").</param>
/// <param name="Title">Drawing title for the title block.</param>
/// <param name="ScaleText">Scale for the title block ("1/4\" = 1'-0\"" or "AS SHOWN").</param>
/// <param name="Viewports">Its viewports.</param>
public sealed record SheetSpec(string Number, string LayoutName, string Title, string ScaleText, IReadOnlyList<SheetViewport> Viewports);

/// <summary>A planned sheet set.</summary>
/// <param name="Sheets">The sheets in order.</param>
/// <param name="TitleBlock">The title block of the paper.</param>
/// <param name="Warnings">Problems (drawings that do not fit).</param>
public sealed record SheetSetPlan(IReadOnlyList<SheetSpec> Sheets, TitleBlockDesign TitleBlock, IReadOnlyList<string> Warnings);

/// <summary>
/// Plans the sheets of a drawing set: picks each drawing's standard scale (floor plans share one scale, so do the
/// elevations and the sections), numbers the sheets by series (A-001 site plan, A-101 floor plans, A-201 elevations,
/// A-301 sections, A-501 layouts, A-601 schedules, A-901 others), gives each plan its own sheet, packs small drawings
/// of the same series on shared sheets, and centres everything in the drawable area left of the title strip. Pure.
/// </summary>
public static class SheetPlanner
{
    /// <summary>Space between viewports on a sheet (mm).</summary>
    public const double ViewportGap = 10;

    /// <summary>Clear margin around a drawing inside its viewport (mm).</summary>
    public const double ViewportPad = 3;

    /// <summary>Plans the sheets for <paramref name="drawings"/>.</summary>
    public static SheetSetPlan Plan(IReadOnlyList<SheetDrawing> drawings, SheetOptions options)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        ArgumentNullException.ThrowIfNull(options);
        var block = TitleBlockDesign.For(options.Paper);
        var area = block.Drawable;
        var warnings = new List<string>();
        var scales = ChooseScales(drawings, options);

        var sheets = new List<SheetSpec>();
        foreach (var series in drawings.GroupBy(d => SheetNames.Series(d.Kind)).OrderBy(g => g.Key))
        {
            var ordered = series.OrderBy(d => d.Order).ThenBy(d => d.Number).ToList();
            var single = !options.Combine || series.First().Kind is DrawingKind.FloorPlan or DrawingKind.SitePlan or DrawingKind.Layout;
            var groups = new List<List<(SheetDrawing Drawing, double W, double H)>>();
            foreach (var drawing in ordered)
            {
                var scale = scales[drawing.Number];
                var k = scale.PaperMmPerUnit(options.InchesPerUnit);
                var w = (drawing.ModelBox.Width * k) + (2 * ViewportPad);
                var h = (drawing.ModelBox.Height * k) + (2 * ViewportPad);
                if (w > area.Width + 1e-6 || h > area.Height + 1e-6)
                {
                    warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{drawing.Title} does not fit {options.Paper.Name} even at {scale.Text}; its viewport shows the middle of it. Use a larger paper (A2 or A1)."));
                    w = Math.Min(w, area.Width);
                    h = Math.Min(h, area.Height);
                }

                var item = (drawing, w, h);
                if (!single && groups.Count > 0 && Pack<SheetDrawing>([.. groups[^1], item], area) is not null)
                {
                    groups[^1].Add(item);
                }
                else
                {
                    groups.Add([item]);
                }
            }

            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var rects = Pack<SheetDrawing>(group, area) ??[.. group.Select(g => SheetRect.FromCenter(area.CenterX, area.CenterY, g.W, g.H))];
                var viewports = new List<SheetViewport>();
                for (var i = 0; i < group.Count; i++)
                {
                    var (drawing, _, _) = group[i];
                    var scale = scales[drawing.Number];
                    var k = scale.PaperMmPerUnit(options.InchesPerUnit);
                    var paper = rects[i];
                    var model = SheetRect.FromCenter(drawing.ModelBox.CenterX, drawing.ModelBox.CenterY, paper.Width / k, paper.Height / k);
                    var cropped = !model.Contains(drawing.ModelBox, 1e-6 * Math.Max(1, drawing.ModelBox.Width));
                    viewports.Add(new SheetViewport(drawing.Number, drawing.Title, scale, paper, model, cropped));
                }

                var number = SheetNames.Number(options.Prefix, series.Key, index);
                var title = SheetTitle(group.Select(g => g.Drawing).ToList());
                var scaleTexts = viewports.Select(v => v.Scale.Text).Distinct(StringComparer.Ordinal).ToList();
                sheets.Add(new SheetSpec(number, SheetNames.LayoutName(number, title), title, scaleTexts.Count == 1 ? scaleTexts[0] : "AS SHOWN", viewports));
            }
        }

        return new SheetSetPlan(sheets, block, warnings);
    }

    /// <summary>
    /// The scale of each drawing (by number): <see cref="SheetOptions.Scale"/> when given, else the largest standard
    /// scale at which it fits the sheet's drawable area (<see cref="DrawingScales.Choose"/>), shared within the floor
    /// plans, the elevations, the sections and the layouts (the smallest of the group wins).
    /// </summary>
    public static IReadOnlyDictionary<int, DrawingScale> ChooseScales(IReadOnlyList<SheetDrawing> drawings, SheetOptions options)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        ArgumentNullException.ThrowIfNull(options);
        var area = TitleBlockDesign.For(options.Paper).Drawable;
        var roomW = area.Width - (2 * ViewportPad);
        var roomH = area.Height - (2 * ViewportPad);
        var scales = new Dictionary<int, DrawingScale>();
        foreach (var drawing in drawings)
        {
            scales[drawing.Number] = options.Scale ?? DrawingScales.Choose(
                drawing.Kind, drawing.ModelBox.Width, drawing.ModelBox.Height, options.InchesPerUnit, roomW, roomH, options.System);
        }

        if (options.Scale is null)
        {
            foreach (var kind in new[] { DrawingKind.FloorPlan, DrawingKind.Elevation, DrawingKind.Section, DrawingKind.Layout })
            {
                var same = drawings.Where(d => d.Kind == kind).ToList();
                if (same.Count > 1)
                {
                    var common = same.Select(d => scales[d.Number]).MaxBy(s => s.Ratio)!;
                    same.ForEach(d => scales[d.Number] = common);
                }
            }
        }

        return scales;
    }

    /// <summary>
    /// The title of a sheet: the drawing's title, "FRONT &amp; REAR ELEVATIONS" for two elevations, the titles joined with
    /// " &amp; " when short, else the plural of the kind ("ELEVATIONS").
    /// </summary>
    public static string SheetTitle(IReadOnlyList<SheetDrawing> drawings)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        if (drawings.Count == 0)
        {
            return "DRAWINGS";
        }

        if (drawings.Count == 1)
        {
            return drawings[0].Title;
        }

        var kinds = drawings.Select(d => d.Kind).Distinct().ToList();
        if (kinds.Count == 1 && kinds[0] == DrawingKind.Elevation && drawings.Count == 2 &&
            drawings.All(d => d.Title.EndsWith(" ELEVATION", StringComparison.Ordinal)))
        {
            return string.Join(" & ", drawings.Select(d => d.Title[..^" ELEVATION".Length])) + " ELEVATIONS";
        }

        var joined = string.Join(" & ", drawings.Select(d => d.Title));
        if (joined.Length <= 40)
        {
            return joined;
        }

        return kinds.Count == 1
            ? kinds[0] switch
            {
                DrawingKind.Elevation => "ELEVATIONS",
                DrawingKind.Section => "SECTIONS",
                DrawingKind.Schedule => "SCHEDULES",
                DrawingKind.FloorPlan => "FLOOR PLANS",
                _ => "DRAWINGS",
            }
            : "DRAWINGS";
    }

    /// <summary>
    /// Shelf packing: the items in rows from the top (left to right, bottoms aligned in a row), every row centred
    /// horizontally and the rows centred vertically in <paramref name="area"/>. Null when they do not all fit.
    /// </summary>
    public static IReadOnlyList<SheetRect>? Pack<T>(IReadOnlyList<(T Item, double W, double H)> items, SheetRect area)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new List<List<int>>();
        var rowWidth = new List<double>();
        var rowHeight = new List<double>();
        for (var i = 0; i < items.Count; i++)
        {
            var (_, w, h) = items[i];
            if (w > area.Width + 1e-6 || h > area.Height + 1e-6)
            {
                return null;
            }

            if (rows.Count > 0 && rowWidth[^1] + ViewportGap + w <= area.Width + 1e-6 &&
                Total(rowHeight) - rowHeight[^1] + Math.Max(rowHeight[^1], h) <= area.Height + 1e-6)
            {
                rows[^1].Add(i);
                rowWidth[^1] += ViewportGap + w;
                rowHeight[^1] = Math.Max(rowHeight[^1], h);
                continue;
            }

            if (rows.Count > 0 && Total(rowHeight) + ViewportGap + h > area.Height + 1e-6)
            {
                return null;
            }

            rows.Add([i]);
            rowWidth.Add(w);
            rowHeight.Add(h);
        }

        var result = new SheetRect[items.Count];
        var top = area.CenterY + (Total(rowHeight) / 2);
        for (var r = 0; r < rows.Count; r++)
        {
            var bottom = top - rowHeight[r];
            var x = area.CenterX - (rowWidth[r] / 2);
            foreach (var i in rows[r])
            {
                result[i] = SheetRect.FromSize(x, bottom, items[i].W, items[i].H);
                x += items[i].W + ViewportGap;
            }

            top = bottom - ViewportGap;
        }

        return result;

        static double Total(List<double> heights) => heights.Count == 0 ? 0 : heights.Sum() + (ViewportGap * (heights.Count - 1));
    }
}
