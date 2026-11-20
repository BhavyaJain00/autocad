using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Sheets;

/// <summary>A named rectangle of the ground floor plan (a toilet, the kitchen).</summary>
/// <param name="Name">Its name ("TOILET", "KITCHEN").</param>
/// <param name="Box">Its extents (drawing units).</param>
public sealed record NamedRect(string Name, SheetRect Box);

/// <summary>How many openings carry a tag (D, D1, MD, W, W1, V ...), and their width when known.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Count">How many on the ground floor.</param>
/// <param name="WidthInches">Clear width in inches, or null for the standard width of the tag.</param>
public sealed record OpeningCount(string Tag, int Count, double? WidthInches = null);

/// <summary>What plan_full_set knows about the approved ground floor plan and the set wanted.</summary>
public sealed record FullSetInput
{
    /// <summary>Outer wall extents of the ground floor (drawing units).</summary>
    public required SheetRect Outline { get; init; }

    /// <summary>Extents of the whole ground floor drawing with dimensions and title (null = outline + 12').</summary>
    public SheetRect? PlanBox { get; init; }

    /// <summary>Plot boundary (null = unknown).</summary>
    public SheetRect? Plot { get; init; }

    /// <summary>Stair extents (null = unknown).</summary>
    public SheetRect? Stair { get; init; }

    /// <summary>Toilets, kitchen, utility: stacked on every floor.</summary>
    public IReadOnlyList<NamedRect> WetAreas { get; init; } = [];

    /// <summary>Openings of the ground floor by tag.</summary>
    public IReadOnlyList<OpeningCount> Openings { get; init; } = [];

    /// <summary>Floors above the ground floor (1 for G+1).</summary>
    public int UpperFloors { get; init; } = 1;

    /// <summary>Add a terrace plan with the stair cabin (default true).</summary>
    public bool Terrace { get; init; } = true;

    /// <summary>Floor to floor height in inches (default 10'-6").</summary>
    public double FloorHeightInches { get; init; } = 126;

    /// <summary>Plinth height in inches (default 1'-6").</summary>
    public double PlinthInches { get; init; } = 18;

    /// <summary>Window sill height above the floor in inches (default 3'-0").</summary>
    public double SillInches { get; init; } = 36;

    /// <summary>Lintel / door head height above the floor in inches (default 7'-0").</summary>
    public double LintelInches { get; init; } = 84;

    /// <summary>Parapet height in inches (default 3'-0").</summary>
    public double ParapetInches { get; init; } = 36;

    /// <summary>Slab thickness in inches (default 5").</summary>
    public double SlabInches { get; init; } = 5;

    /// <summary>The road / front side of the plan: south (bottom, default), north, east or west.</summary>
    public string FrontSide { get; init; } = "south";

    /// <summary>Inches per drawing unit (1 for INSUNITS inches).</summary>
    public double InchesPerUnit { get; init; } = 1;

    /// <summary>Gap between drawings (drawing units; null = one plan width, at least 20').</summary>
    public double? Gap { get; init; }

    /// <summary>Where the plan facts came from ("drawing", "arguments", "validated plan").</summary>
    public string Source { get; init; } = "drawing";
}

/// <summary>One stage of the full drawing set.</summary>
/// <param name="Step">1-based order.</param>
/// <param name="Id">Stable id ("first-floor", "front-elevation").</param>
/// <param name="Title">Drawing title ("FIRST FLOOR PLAN").</param>
/// <param name="Kind">Drawing kind.</param>
/// <param name="Origin">Its reference point: the outline's lower-left corner for plans, the left end of the ground line for elevations and sections, the plot's lower-left for the site plan, the top-left corner for tables.</param>
/// <param name="Area">The model-space area it should take (drawing units).</param>
/// <param name="Tasks">What to draw, in order, with coordinates.</param>
/// <param name="Guide">recall_knowledge query for its built-in guide, or null.</param>
public sealed record FullSetStage(int Step, string Id, string Title, DrawingKind Kind, (double X, double Y) Origin, SheetRect Area, IReadOnlyList<string> Tasks, string? Guide);

/// <summary>A level of the elevations and the section.</summary>
/// <param name="Name">"GL", "GF FFL", "FF SILL" ...</param>
/// <param name="Inches">Height above ground level in inches.</param>
/// <param name="Text">Level text ("+12'-0\" FF FFL").</param>
public sealed record LevelMark(string Name, double Inches, string Text);

/// <summary>A row of the door / window schedule.</summary>
/// <param name="Tag">Tag (D, MD, W ...).</param>
/// <param name="Type">Door, window or ventilator.</param>
/// <param name="WidthInches">Width.</param>
/// <param name="HeightInches">Height.</param>
/// <param name="SillInches">Sill above floor (0 for doors).</param>
/// <param name="PerFloor">Count on the ground floor.</param>
public sealed record ScheduleRow(string Tag, string Type, double WidthInches, double HeightInches, double SillInches, int PerFloor);

/// <summary>The area statement (square feet and square metres, from the outer wall extents).</summary>
/// <param name="FloorSqFt">Built-up area of one floor.</param>
/// <param name="Floors">Number of full floors (ground + upper).</param>
/// <param name="CabinSqFt">Stair cabin on the terrace (0 when none).</param>
/// <param name="PlotSqFt">Plot area, or null.</param>
public sealed record AreaStatement(double FloorSqFt, int Floors, double CabinSqFt, double? PlotSqFt)
{
    /// <summary>Square metres per square foot.</summary>
    public const double SqmPerSqFt = 0.09290304;

    /// <summary>Total built-up area.</summary>
    public double TotalSqFt => (FloorSqFt * Floors) + CabinSqFt;

    /// <summary>Ground coverage in percent of the plot, or null.</summary>
    public double? CoveragePercent => PlotSqFt is > 0 ? FloorSqFt / PlotSqFt.Value * 100 : null;

    /// <summary>Floor area ratio (total / plot), or null.</summary>
    public double? Far => PlotSqFt is > 0 ? TotalSqFt / PlotSqFt.Value : null;
}

/// <summary>The planned drawing set.</summary>
public sealed record FullSetPlan
{
    /// <summary>The stages in drafting order.</summary>
    public required IReadOnlyList<FullSetStage> Stages { get; init; }

    /// <summary>Levels for the elevations and the section.</summary>
    public required IReadOnlyList<LevelMark> Levels { get; init; }

    /// <summary>Door and window schedule.</summary>
    public required IReadOnlyList<ScheduleRow> Schedule { get; init; }

    /// <summary>Area statement.</summary>
    public required AreaStatement Areas { get; init; }

    /// <summary>The section cut on the ground floor plan (start and end, drawing units).</summary>
    public required (double X0, double Y0, double X1, double Y1) Cut { get; init; }

    /// <summary>Assumptions to state to the user.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>Gap between drawings (drawing units).</summary>
    public double Gap { get; init; }

    /// <summary>Spacing between the floor plans' outlines (drawing units).</summary>
    public double Pitch { get; init; }

    /// <summary>Y of the ground line of the elevations and the section (drawing units).</summary>
    public double GroundY { get; init; }
}

/// <summary>
/// Plans a full drawing set from an approved ground floor plan: the upper floors (same outer walls, stair and wet
/// areas stacked, at the floor-plan spacing arrange_drawings uses), the terrace, front / rear / side elevations with
/// their levels (plinth 1'-6", floors 10'-6", sill 3', lintel 7', parapet 3' unless told otherwise), a section through
/// the stair, the site plan, furniture layouts, the door / window schedule and the area statement, each with its place
/// in model space and its tasks, then the sheets and the PDF. The AI drafts each stage with run_commands. Pure.
/// </summary>
public static partial class FullSetPlanner
{
    /// <summary>Space taken around a plan's outline by dimensions and title when the plan's extents are unknown: 12'.</summary>
    public const double PlanMarginInches = 144;

    /// <summary>Height of the stair cabin (mumty) above the terrace floor, slab included: 8'-6".</summary>
    public const double CabinInches = 102;

    /// <summary>Standard opening sizes by tag (width, height, sill) in inches.</summary>
    public static IReadOnlyDictionary<string, (string Type, double Width, double Height, double Sill)> StandardOpenings { get; } =
        new Dictionary<string, (string, double, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["MD"] = ("Door", 42, 84, 0),
            ["D"] = ("Door", 36, 84, 0),
            ["D1"] = ("Door", 30, 84, 0),
            ["D2"] = ("Door", 30, 84, 0),
            ["SD"] = ("Sliding door", 72, 84, 0),
            ["W"] = ("Window", 48, 48, 36),
            ["W1"] = ("Window", 36, 48, 36),
            ["W2"] = ("Window", 60, 48, 36),
            ["KW"] = ("Window", 36, 36, 42),
            ["V"] = ("Ventilator", 24, 18, 66),
            ["V1"] = ("Ventilator", 18, 18, 66),
        };

    /// <summary>Counts opening tags (D, D1, MD, W, W1, V, KW, SD ...) among plan texts.</summary>
    public static IReadOnlyList<OpeningCount> CountTags(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return [.. texts
            .Select(t => DrawingTitles.Clean(t))
            .Where(t => OpeningTag().IsMatch(t))
            .GroupBy(t => t, StringComparer.Ordinal)
            .OrderBy(g => TagOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OpeningCount(g.Key, g.Count()))];
    }

    /// <summary>Plans the set.</summary>
    public static FullSetPlan Plan(FullSetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Outline.IsValid || input.Outline.Width <= 0 || input.Outline.Height <= 0)
        {
            throw new ArgumentException("The ground floor outline needs a width and a depth.", nameof(input));
        }

        var ipu = input.InchesPerUnit > 0 && double.IsFinite(input.InchesPerUnit) ? input.InchesPerUnit : 1;
        var u = 1 / ipu; // drawing units per inch
        var f = new Fmt(ipu);
        var outline = input.Outline;
        var planBox = (input.PlanBox ?? outline.Inflate(PlanMarginInches * u)).Union(outline);
        var gap = input.Gap is > 0 ? input.Gap.Value : Math.Max(ModelArrangement.MinGapInches * u, outline.Width);
        var pitch = planBox.Width + gap;
        var upper = Math.Clamp(input.UpperFloors, 0, 6);
        var front = (input.FrontSide ?? "south").Trim().ToLowerInvariant();
        var frontIsX = front is "south" or "north"; // the front faces along X: its width is the plan's width
        var frontWidth = frontIsX ? outline.Width : outline.Height;
        var sideWidth = frontIsX ? outline.Height : outline.Width;

        // Levels (inches above ground level).
        var levels = new List<LevelMark> { new("GL", 0, "%%p0'-0\" GL") };
        var floorNames = new List<(string Short, string Title)> { ("GF", "GROUND FLOOR PLAN") };
        for (var i = 1; i <= upper; i++)
        {
            var title = DrawingTitles.FloorPlanTitle(i);
            floorNames.Add((ShortName(i), title));
        }

        for (var i = 0; i < floorNames.Count; i++)
        {
            var ffl = input.PlinthInches + (i * input.FloorHeightInches);
            var name = floorNames[i].Short;
            levels.Add(new LevelMark(name + " FFL", ffl, "+" + FeetInches.Format(ffl) + " " + name + " FFL"));
            levels.Add(new LevelMark(name + " SILL", ffl + input.SillInches, "+" + FeetInches.Format(ffl + input.SillInches) + " SILL"));
            levels.Add(new LevelMark(name + " LINTEL", ffl + input.LintelInches, "+" + FeetInches.Format(ffl + input.LintelInches) + " LINTEL"));
        }

        var terraceLevel = input.PlinthInches + (floorNames.Count * input.FloorHeightInches);
        levels.Add(new LevelMark("TERRACE", terraceLevel, "+" + FeetInches.Format(terraceLevel) + " TERRACE"));
        levels.Add(new LevelMark("PARAPET", terraceLevel + input.ParapetInches, "+" + FeetInches.Format(terraceLevel + input.ParapetInches) + " PARAPET TOP"));
        var hasCabin = input.Terrace && input.Stair is not null;
        if (hasCabin)
        {
            levels.Add(new LevelMark("CABIN", terraceLevel + CabinInches, "+" + FeetInches.Format(terraceLevel + CabinInches) + " STAIR CABIN TOP"));
        }

        var buildingTop = levels.Max(l => l.Inches);

        // Row 2 (elevations, section, site plan) sits one gap below the plans; its ground line is the common baseline.
        var siteBox = input.Plot ?? outline.Inflate(120 * u);
        var elevationHeadroom = (buildingTop + 48) * u;
        var siteHeadroom = siteBox.Height + (120 * u);
        var groundY = planBox.MinY - gap - Math.Max(elevationHeadroom, siteHeadroom);
        var belowGround = 14 * 12 * u; // dimensions, title and scale under the ground line
        var row2Bottom = groundY - belowGround;
        var row3Top = row2Bottom - gap;

        var stages = new List<FullSetStage>();
        void Add(string id, string title, DrawingKind kind, (double, double) origin, SheetRect area, string? guide, params string[] tasks) =>
            stages.Add(new FullSetStage(stages.Count + 1, id, title, kind, origin, area, tasks, guide));

        string Window(SheetRect r) => f.P(r.MinX - (12 * u), r.MinY - (12 * u)) + " to " + f.P(r.MaxX + (12 * u), r.MaxY + (12 * u));

        // Upper floors.
        var wet = input.WetAreas.Count == 0
            ? "the toilets and the kitchen"
            : string.Join(", ", input.WetAreas.Select(w => w.Name.ToLowerInvariant()));
        for (var i = 1; i <= upper; i++)
        {
            var dx = i * pitch;
            var moved = outline.Offset(dx, 0);
            var stackedWet = input.WetAreas.Count == 0
                ? string.Empty
                : " Wet areas stacked at: " + string.Join("; ", input.WetAreas.Select(w => w.Name + " " + f.R(w.Box.Offset(dx, 0)))) + ".";
            var stair = input.Stair is { } s ? " Stair at " + f.R(s.Offset(dx, 0)) + ": same outline, break line across the cut flight, \"DN\" on the lower one." : string.Empty;
            Add(
                IdOf(floorNames[i].Title),
                floorNames[i].Title,
                DrawingKind.FloorPlan,
                (moved.MinX, moved.MinY),
                planBox.Offset(dx, 0),
                "stair",
                $"COPY the ground floor's outer walls, stair and {wet} walls (window {Window(outline)}) with base point {f.P(0, 0)} and second point {f.P(dx, 0)}: the outline lands at {f.R(moved)}.",
                "Erase what does not continue upstairs (main door, parking and verandah openings, steps); keep the outer walls, the stair and the wet areas exactly above the ground floor ones." + stair + stackedWet,
                "Ask the user for this floor's rooms if they were not given (bedrooms, toilets, balcony over the entrance); then inner walls, openings, doors and windows, room names with sizes, dimensions.",
                $"Title \"%%u{floorNames[i].Title}\" and the scale line, 4' below the lowest dimension, like the ground floor.");
        }

        if (input.Terrace)
        {
            var dx = (upper + 1) * pitch;
            var moved = outline.Offset(dx, 0);
            var cabin = input.Stair is { } s
                ? $"Stair cabin (mumty) around the stair at {f.R(s.Offset(dx, 0))} with 9\" walls and a 3' door onto the terrace; overhead water tank over the toilets."
                : "Stair cabin (mumty) around the stair with 9\" walls and a 3' door; overhead water tank over the toilets.";
            Add(
                "terrace",
                "TERRACE PLAN",
                DrawingKind.FloorPlan,
                (moved.MinX, moved.MinY),
                planBox.Offset(dx, 0),
                "stair",
                $"COPY the outer walls of the top floor to {f.R(moved)} (base point {f.P(0, 0)}, second point {f.P(dx, 0)}) as the 4-1/2\" or 9\" parapet ({FeetInches.Format(input.ParapetInches)} high).",
                cabin,
                "Slope arrows (1:100) to rainwater outlets at the corners, dimensions, title \"%%uTERRACE PLAN\" and the scale line.");
        }

        // Elevations and section in row 2, left to right; the front one under the ground floor plan (projected from it).
        var levelsText = string.Join(", ", levels.Select(l => l.Text.Replace("%%p", "+-", StringComparison.Ordinal)));
        var x = outline.MinX;
        (double, double) Ground(double left) => (left, groundY);
        SheetRect ElevationArea(double left, double width) =>
            new(left - (72 * u), row2Bottom, left + width + (72 * u), groundY + elevationHeadroom);

        var elevations = new (string Id, string Title, double Width, string From)[]
        {
            ("front-elevation", "FRONT ELEVATION", frontWidth, $"the {front} side of the ground floor plan (project its corners, openings and the stair window straight down)"),
            ("rear-elevation", "REAR ELEVATION", frontWidth, $"the side opposite the front, seen from behind (mirror the order of openings)"),
            ("side-elevation", "SIDE ELEVATION", sideWidth, "the left side seen from the front (the side with the stair when possible)"),
        };
        foreach (var (id, title, width, source) in elevations)
        {
            Add(
                id,
                title,
                DrawingKind.Elevation,
                Ground(x),
                ElevationArea(x, width),
                "elevation",
                $"Ground line from {f.P(x - (60 * u), groundY)} to {f.P(x + width + (60 * u), groundY)} (PLINE width 2); building {FeetInches.Format(width * ipu)} wide from x = {f.L(x)}, drawn from {source}.",
                $"Horizontal lines at the levels (y = {f.L(groundY)} + level): {levelsText}.",
                $"Windows from sill +{FeetInches.Format(input.SillInches)} to lintel +{FeetInches.Format(input.LintelInches)} above each FFL, doors up to the lintel, slab bands {FeetInches.Format(input.SlabInches)}, parapet {FeetInches.Format(input.ParapetInches)} with coping" + (hasCabin ? ", the stair cabin above the terrace." : "."),
                $"Level markers and vertical dimensions on one side, title \"%%u{title}\" and the scale line under the ground line.");
            x += width + (72 * u) + gap + (72 * u);
        }

        // Section through the stair.
        var stairBox = input.Stair ?? outline;
        (double X0, double Y0, double X1, double Y1) cut = frontIsX
            ? (stairBox.CenterX, outline.MinY - (60 * u), stairBox.CenterX, outline.MaxY + (60 * u))
            : (outline.MinX - (60 * u), stairBox.CenterY, outline.MaxX + (60 * u), stairBox.CenterY);
        var sectionWidth = frontIsX ? outline.Height : outline.Width;
        var mapping = frontIsX
            ? $"section x = {f.L(x)} + (plan y - {f.L(outline.MinY)})"
            : $"section x = {f.L(x)} + (plan x - {f.L(outline.MinX)})";
        Add(
            "section",
            "SECTION A-A",
            DrawingKind.Section,
            Ground(x),
            ElevationArea(x, sectionWidth),
            "section",
            $"On the ground floor plan: section line {f.P(cut.X0, cut.Y0)} to {f.P(cut.X1, cut.Y1)} (through the stair{(input.Stair is null ? ", stair not found: move it onto the stair" : string.Empty)}) with \"A\" marks and arrows at both ends.",
            $"Section {FeetInches.Format(sectionWidth * ipu)} wide on the ground line y = {f.L(groundY)} from x = {f.L(x)}; {mapping}.",
            $"Cut walls and slabs thick and hatched, slab {FeetInches.Format(input.SlabInches)} at every FFL ({levelsText}), the stair as risers and treads with the landing, plinth and footings dashed (\"indicative - by structural engineer\").",
            "Level markers, vertical dimensions, title \"%%uSECTION A-A\" and the scale line.");
        x += sectionWidth + (72 * u) + gap + (72 * u);

        // Site plan after the section, its plot on the ground line (its area reaches 10' around the plot).
        var siteDx = x + (48 * u) - siteBox.MinX;
        var siteDy = groundY - siteBox.MinY;
        var site = siteBox.Offset(siteDx, siteDy);
        Add(
            "site-plan",
            "SITE PLAN",
            DrawingKind.SitePlan,
            (site.MinX, site.MinY),
            site.Inflate(120 * u),
            "site plan",
            input.Plot is null
                ? $"Plot boundary not found: ask the user for the plot size and setbacks; draw the plot with its lower-left corner at {f.P(site.MinX, site.MinY)}."
                : $"COPY the plot boundary {f.R(input.Plot.Value)} with base point {f.P(0, 0)} and second point {f.P(siteDx, siteDy)}: it lands at {f.R(site)}.",
            $"Building outline (the ground floor's outer walls) at {f.R(outline.Offset(siteDx, siteDy))}, compound wall 9\" with the gate, setbacks dimensioned on all sides, road with its width, parking, north arrow.",
            "Text heights for 1/8\" = 1'-0\" (twice the plan ones), title \"%%uSITE PLAN\" and the scale line.");

        // Row 3: furniture layouts (copies of the floor plans), then the schedule and the area statement.
        var furnitureDy = row3Top - planBox.MaxY;
        var furnitureTasks = new List<string>();
        for (var i = 0; i < floorNames.Count; i++)
        {
            var dx = i * pitch;
            furnitureTasks.Add($"COPY the finished {floorNames[i].Title.ToLowerInvariant()} (window {Window(planBox.Offset(dx, 0))}) with base point {f.P(0, 0)} and second point {f.P(0, furnitureDy)}; title \"%%u{floorNames[i].Title.Replace(" PLAN", string.Empty, StringComparison.Ordinal)} FURNITURE LAYOUT\".");
        }

        furnitureTasks.Add("Furniture on A-FURN in every room (beds, wardrobes, sofa, dining, kitchen counter, WC, basin): keep 2'6\"-3' clear in front and never block door swings or windows.");
        Add(
            "furniture",
            "FURNITURE LAYOUT",
            DrawingKind.Layout,
            (outline.MinX, outline.MinY + furnitureDy),
            new SheetRect(planBox.MinX, planBox.MinY + furnitureDy, planBox.MaxX + ((floorNames.Count - 1) * pitch), planBox.MaxY + furnitureDy),
            "furniture",
            [.. furnitureTasks]);

        var tablesX = planBox.MinX + (floorNames.Count * pitch);
        var schedule = Schedule(input.Openings);
        var rowHeight = 18 * u;
        var scheduleHeight = rowHeight * (schedule.Count + 2);
        var scheduleWidth = 33 * 12 * u;
        Add(
            "schedule",
            "DOOR & WINDOW SCHEDULE",
            DrawingKind.Schedule,
            (tablesX, row3Top),
            new SheetRect(tablesX, row3Top - scheduleHeight - (96 * u), tablesX + scheduleWidth, row3Top),
            null,
            $"Table with its top-left corner at {f.P(tablesX, row3Top)} on A-ANNO-TEXT: columns TAG 4', TYPE 6', SIZE (W x H) 8', SILL 5', NOS. 4', REMARKS 6' (LINEs), rows {FeetInches.Format(18)} high, text 6 high (heading 9).",
            "Rows (count per floor from the ground floor, multiply by the floors that repeat it): " + string.Join("; ", schedule.Select(r =>
                $"{r.Tag} {r.Type} {FeetInches.Format(r.WidthInches)} x {FeetInches.Format(r.HeightInches)}" + (r.SillInches > 0 ? $" sill {FeetInches.Format(r.SillInches)}" : string.Empty) + (r.PerFloor > 0 ? $" x{r.PerFloor}" : " (count them on the plans)"))) + ".",
            "Title \"%%uDOOR & WINDOW SCHEDULE\" above the table; note \"sizes are clear openings - check on site\".");

        var areas = Areas(input, ipu, hasCabin);
        var areaX = tablesX + scheduleWidth + gap;
        Add(
            "area-statement",
            "AREA STATEMENT",
            DrawingKind.Schedule,
            (areaX, row3Top),
            new SheetRect(areaX, row3Top - (12 * 12 * u) - (96 * u), areaX + (24 * 12 * u), row3Top),
            null,
            $"Table with its top-left corner at {f.P(areaX, row3Top)}: columns DESCRIPTION 14', SQ.FT 5', SQ.M 5'; rows {FeetInches.Format(18)} high, text 6 high.",
            "Rows: " + string.Join("; ", AreaRows(areas, floorNames.Select(n => n.Short).ToList())) + ".",
            "Note under it: \"Areas are approximate, from the outer wall extents; FAR and coverage to be checked with the local bylaws\".");

        Add(
            "sheets",
            "SHEETS AND PDF",
            DrawingKind.Other,
            (planBox.MinX, planBox.MinY),
            planBox,
            "full set",
            "arrange_drawings (preview first) so every drawing has its title and scale and the rows line up.",
            "create_sheets (A3 unless the user wants A2/A1): A-001 site plan, A-101... floor plans, A-201 elevations, A-301 section, A-501 furniture, A-601 schedules; capture_view one sheet to check.",
            "plot_to_pdf for the whole set when the user wants the PDF (they approve it).");

        var assumptions = new List<string>
        {
            $"Floor to floor {FeetInches.Format(input.FloorHeightInches)}, plinth {FeetInches.Format(input.PlinthInches)}, sill {FeetInches.Format(input.SillInches)}, lintel {FeetInches.Format(input.LintelInches)}, parapet {FeetInches.Format(input.ParapetInches)}, slab {FeetInches.Format(input.SlabInches)}.",
            $"Front (road) side: {front}; G+{upper}" + (input.Terrace ? " with a terrace." : "."),
            "Upper floors keep the ground floor's outer walls, stair and wet areas.",
        };
        if (input.Stair is null)
        {
            assumptions.Add("No stair found on A-STRS: the section line goes through the middle; move it onto the stair.");
        }

        return new FullSetPlan
        {
            Stages = stages,
            Levels = levels,
            Schedule = schedule,
            Areas = areas,
            Cut = cut,
            Assumptions = assumptions,
            Gap = gap,
            Pitch = pitch,
            GroundY = groundY,
        };
    }

    /// <summary>The plan as JSON for the tool result (lengths in drawing units with feet-inch text).</summary>
    public static JsonObject ToJson(FullSetPlan plan, double inchesPerUnit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var f = new Fmt(inchesPerUnit > 0 ? inchesPerUnit : 1);
        return new JsonObject
        {
            ["stages"] = new JsonArray([.. plan.Stages.Select(s => (JsonNode)new JsonObject
            {
                ["step"] = s.Step,
                ["id"] = s.Id,
                ["title"] = s.Title,
                ["kind"] = s.Kind.ToString(),
                ["origin"] = new JsonArray(Round(s.Origin.X), Round(s.Origin.Y)),
                ["originText"] = f.P(s.Origin.X, s.Origin.Y),
                ["area"] = new JsonArray(Round(s.Area.MinX), Round(s.Area.MinY), Round(s.Area.MaxX), Round(s.Area.MaxY)),
                ["guide"] = s.Guide,
                ["tasks"] = new JsonArray([.. s.Tasks.Select(t => (JsonNode)t)]),
            })]),
            ["levels"] = new JsonArray([.. plan.Levels.Select(l => (JsonNode)new JsonObject
            {
                ["name"] = l.Name,
                ["aboveGround"] = FeetInches.Format(l.Inches),
                ["elevationY"] = Round(plan.GroundY + (l.Inches / f.Ipu)),
                ["text"] = l.Text,
            })]),
            ["groundLineY"] = Round(plan.GroundY),
            ["sectionCut"] = new JsonArray(Round(plan.Cut.X0), Round(plan.Cut.Y0), Round(plan.Cut.X1), Round(plan.Cut.Y1)),
            ["schedule"] = new JsonArray([.. plan.Schedule.Select(r => (JsonNode)new JsonObject
            {
                ["tag"] = r.Tag,
                ["type"] = r.Type,
                ["size"] = FeetInches.Format(r.WidthInches) + " x " + FeetInches.Format(r.HeightInches),
                ["sill"] = r.SillInches > 0 ? FeetInches.Format(r.SillInches) : "-",
                ["perFloor"] = r.PerFloor,
            })]),
            ["areas"] = new JsonObject
            {
                ["floorSqFt"] = Math.Round(plan.Areas.FloorSqFt, 1),
                ["floors"] = plan.Areas.Floors,
                ["cabinSqFt"] = Math.Round(plan.Areas.CabinSqFt, 1),
                ["totalSqFt"] = Math.Round(plan.Areas.TotalSqFt, 1),
                ["totalSqM"] = Math.Round(plan.Areas.TotalSqFt * AreaStatement.SqmPerSqFt, 1),
                ["plotSqFt"] = plan.Areas.PlotSqFt is { } p ? Math.Round(p, 1) : null,
                ["coveragePercent"] = plan.Areas.CoveragePercent is { } c ? Math.Round(c, 1) : null,
                ["far"] = plan.Areas.Far is { } r ? Math.Round(r, 2) : null,
            },
            ["gap"] = f.Len(plan.Gap),
            ["planSpacing"] = f.Len(plan.Pitch),
            ["assumptions"] = new JsonArray([.. plan.Assumptions.Select(a => (JsonNode)a)]),
        };
    }

    private static List<ScheduleRow> Schedule(IReadOnlyList<OpeningCount> openings)
    {
        var source = openings.Count > 0
            ? openings
            : [new OpeningCount("MD", 1), new OpeningCount("D", 0), new OpeningCount("D1", 0), new OpeningCount("W", 0), new OpeningCount("V", 0)];
        var rows = new List<ScheduleRow>();
        foreach (var opening in source)
        {
            var key = opening.Tag.ToUpperInvariant();
            var standard = StandardOpenings.TryGetValue(key, out var s)
                ? s
                : key.StartsWith('V') ? ("Ventilator", 24d, 18d, 66d)
                : key.StartsWith('W') || key.StartsWith('K') ? ("Window", 48d, 48d, 36d)
                : ("Door", 36d, 84d, 0d);
            rows.Add(new ScheduleRow(key, standard.Item1, opening.WidthInches ?? standard.Item2, standard.Item3, standard.Item4, opening.Count));
        }

        return rows;
    }

    private static AreaStatement Areas(FullSetInput input, double ipu, bool hasCabin)
    {
        double SqFt(SheetRect r) => r.Width * ipu * r.Height * ipu / 144.0;
        var floor = SqFt(input.Outline);
        var cabin = hasCabin && input.Stair is { } s ? SqFt(s.Inflate(9 / ipu)) : 0;
        return new AreaStatement(floor, 1 + Math.Clamp(input.UpperFloors, 0, 6), cabin, input.Plot is { } p ? SqFt(p) : null);
    }

    private static IEnumerable<string> AreaRows(AreaStatement a, IReadOnlyList<string> floors)
    {
        static string Row(string name, double sqft) =>
            string.Create(CultureInfo.InvariantCulture, $"{name} {sqft:0.0} sq.ft ({sqft * AreaStatement.SqmPerSqFt:0.0} sq.m)");

        if (a.PlotSqFt is { } plot)
        {
            yield return Row("PLOT AREA", plot);
        }

        foreach (var floor in floors)
        {
            yield return Row(floor + " BUILT-UP", a.FloorSqFt);
        }

        if (a.CabinSqFt > 0)
        {
            yield return Row("STAIR CABIN", a.CabinSqFt);
        }

        yield return Row("TOTAL BUILT-UP", a.TotalSqFt);
        if (a.CoveragePercent is { } coverage && a.Far is { } far)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"GROUND COVERAGE {coverage:0.0}%; FAR {far:0.00}");
        }
    }

    private static string ShortName(int level) => level switch
    {
        1 => "FF",
        2 => "SF",
        3 => "TF",
        _ => string.Create(CultureInfo.InvariantCulture, $"L{level}"),
    };

    private static string IdOf(string title) => title.ToLowerInvariant().Replace(" plan", string.Empty, StringComparison.Ordinal).Replace(' ', '-');

    private static int TagOrder(string tag) => tag[0] switch
    {
        'M' => 0,
        'D' => 1,
        'S' => 2,
        'W' => 3,
        'K' => 4,
        _ => 5,
    };

    private static double Round(double value) => DrawingUnits.Round(value);

    [GeneratedRegex(@"^(MD|SD\d*|D\d{0,2}|W\d{0,2}|KW\d*|V\d{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OpeningTag();

    // Coordinates as the AI types them: feet-inches (12'6,30') when 1 unit = 1 inch, else plain numbers.
    private sealed class Fmt(double ipu)
    {
        public double Ipu { get; } = ipu;

        private bool Imperial => Math.Abs(Ipu - 1) < 1e-12;

        public string L(double units) => Imperial
            ? FeetInches.FormatForInput(units * Ipu, 2)
            : DrawingUnits.Round(units).ToString(CultureInfo.InvariantCulture);

        public string P(double x, double y) => L(x) + "," + L(y);

        public string R(SheetRect r) => "(" + P(r.MinX, r.MinY) + ")-(" + P(r.MaxX, r.MaxY) + ")";

        public JsonObject Len(double units) => new()
        {
            ["du"] = DrawingUnits.Round(units),
            ["ftIn"] = FeetInches.Format(units * Ipu),
        };
    }
}
