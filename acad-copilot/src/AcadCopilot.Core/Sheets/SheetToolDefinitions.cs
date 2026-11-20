using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Sheets;

/// <summary>
/// Definitions (what the AI sees) of the drawing-set tools: arrange_drawings, create_sheets, plot_to_pdf and
/// plan_full_set. The AutoCAD work is in the plug-in (Plugin/Sheets, Plugin/Tools/ToolRegistry.Sheets.cs).
/// </summary>
public static class SheetToolDefinitions
{
    /// <summary>arrange_drawings.</summary>
    public const string ArrangeDrawings = "arrange_drawings";

    /// <summary>create_sheets.</summary>
    public const string CreateSheets = "create_sheets";

    /// <summary>plot_to_pdf.</summary>
    public const string PlotToPdf = "plot_to_pdf";

    /// <summary>plan_full_set.</summary>
    public const string PlanFullSet = "plan_full_set";

    /// <summary>Most labels per call.</summary>
    public const int MaxLabels = 40;

    /// <summary>Most sheets plotted in one PDF.</summary>
    public const int MaxSheets = 60;

    /// <summary>Allowed scale texts.</summary>
    public static IReadOnlyList<string> ScaleNames { get; } = ["1/4", "1/8", "1/16", "1/32", "1:50", "1:100", "1:200", "1:500"];

    /// <summary>The tool that lines up the drawings of model space.</summary>
    public static ToolDefinition ArrangeDrawingsDefinition() => new()
    {
        Name = ArrangeDrawings,
        Title = "Arrange drawings in model space",
        Risk = ToolRisk.Write,
        Description =
            "Find the separate drawings in model space (groups of objects separated by at least gap of empty space, " +
            "default 10'; drawings are named by their title text such as \"GROUND FLOOR PLAN\", \"FRONT ELEVATION\", " +
            "\"SECTION A-A\", \"SITE PLAN\") and line them up the professional way: floor plans in one row (ground, first, " +
            "second, terrace) with the same spacing of their wall extents so walls and stairs align between floors; " +
            "elevations in a row below (front under the ground floor plan), then sections and the site plan; anything " +
            "else in a third row; about one plan width (at least 20') between drawings. The first floor plan stays " +
            "where it is. Adds a title and a scale line (\"SCALE 1/4\\\" = 1'-0\\\"\") under drawings that lack them " +
            "and corrects existing scale lines. Whole drawings are moved, as one undo step. Untitled drawings stay " +
            "\"DRAWING n\": name them with labels (a point inside + the title). Use preview_only first on a busy " +
            "drawing: the result lists every drawing (number, title, row, move) and a text sketch of the arrangement.",
        InputSchema = Schema.Object(
            Schema.Opt("labels", Schema.Array(
                "Titles for drawings without a title text (or to correct one).",
                Schema.Object(
                    "One label.",
                    Schema.Req("point", Schema.Array("A point inside (or within the gap of) the drawing [x, y], drawing units.", Schema.Number("Coordinate."), 2, 2)),
                    Schema.Req("title", Schema.String("Its title, e.g. FIRST FLOOR PLAN, REAR ELEVATION, SECTION B-B.", minLength: 3, maxLength: 60))),
                0,
                MaxLabels)),
            Schema.Opt("gap", Schema.Number("Gap between drawings after arranging, drawing units (default: one plan width, at least 20').", 1)),
            Schema.Opt("cluster_gap", Schema.Number("Empty space that separates two drawings when finding them, drawing units (default 10').", 1)),
            Schema.Opt("add_titles", Schema.Boolean("Add missing titles and scale lines and correct scale lines (default true).")),
            Schema.Opt("scale", Schema.String("Scale for every title instead of the automatic choice (1/4 = 1/4\"=1'-0\", 1:100 ...).", ScaleNames)),
            Schema.Opt("preview_only", Schema.Boolean("Only report the drawings found and the planned arrangement; change nothing (default false)."))),
    };

    /// <summary>The tool that makes the layout sheets.</summary>
    public static ToolDefinition CreateSheetsDefinition() => new()
    {
        Name = CreateSheets,
        Title = "Create sheets (layouts)",
        Risk = ToolRisk.Write,
        Description =
            "Make printable sheets: one Layout tab per drawing set sheet, named \"A-101 Ground Floor Plan\" (A-001 site " +
            "plan, A-101... floor plans, A-201 elevations, A-301 sections, A-501 furniture/services layouts, A-601 " +
            "schedules), on landscape paper (A3 default, A2, A1; ISO or ARCH B/C/D), plotter DWG To PDF.pc3. Each sheet " +
            "gets a frame and a title block (block A-TTLB-<paper> with PROJECT, CLIENT, TITLE, SCALE, DATE, SHEET_NO, " +
            "DRAWN_BY \"AI Copilot / <user>\" and the note \"NOT FOR CONSTRUCTION - CHECK BY ARCHITECT/ENGINEER\") and " +
            "(on ISO A3 the A-TTLB-A3 block of apply_drawing_template is used when the drawing has it) and a locked viewport per drawing at a standard scale (1/4\"=1'-0\" or 1:50 when it fits, else 1/8\" or 1:100; " +
            "all floor plans share one scale; site plan 1/8\"). Small drawings of the same series (elevations, sections, " +
            "schedules) share a sheet unless combine is false. Drawings are found like arrange_drawings (run it first so " +
            "they have titles); untitled ones are left out unless named with labels or listed in drawings. Existing " +
            "sheets with the same name are kept (the new one gets \" (2)\") unless replace_existing is true. Model " +
            "space is not changed except scale lines that disagree with the sheet scale. Then capture_view a sheet to check.",
        InputSchema = Schema.Object(
            Schema.Opt("paper", Schema.String("Sheet size (default A3), landscape.", ["A3", "A2", "A1"])),
            Schema.Opt("standard", Schema.String("iso (A3 420x297 mm, default) or arch (ARCH B 18x12\", C 24x18\", D 36x24\").", ["iso", "arch"])),
            Schema.Opt("project", Schema.String("Project name for the title block.", maxLength: 60)),
            Schema.Opt("client", Schema.String("Client name for the title block.", maxLength: 60)),
            Schema.Opt("drawings", Schema.Array("Only these drawing numbers (from arrange_drawings / an earlier result).", Schema.Integer("Drawing number.", 1, 999), 1, MaxSheets)),
            Schema.Opt("labels", Schema.Array(
                "Titles for untitled drawings.",
                Schema.Object(
                    "One label.",
                    Schema.Req("point", Schema.Array("A point inside the drawing [x, y].", Schema.Number("Coordinate."), 2, 2)),
                    Schema.Req("title", Schema.String("Its title.", minLength: 3, maxLength: 60))),
                0,
                MaxLabels)),
            Schema.Opt("combine", Schema.Boolean("Put several small drawings of one series on a sheet (default true).")),
            Schema.Opt("scale", Schema.String("One scale for every viewport instead of the automatic choice.", ScaleNames)),
            Schema.Opt("cluster_gap", Schema.Number("Empty space that separates two drawings, drawing units (default 10').", 1)),
            Schema.Opt("replace_existing", Schema.Boolean("Delete and rebuild sheets (layouts) with the same names (default false).")),
            Schema.Opt("show_sheet", Schema.Boolean("Open the first new sheet afterwards so capture_view shows it (SETVAR CTAB Model goes back; default false)."))),
    };

    /// <summary>The tool that plots sheets to one PDF.</summary>
    public static ToolDefinition PlotToPdfDefinition() => new()
    {
        Name = PlotToPdf,
        Title = "Plot sheets to PDF",
        Risk = ToolRisk.Destructive,
        Description =
            "Plot sheets (layout tabs) into ONE multi-page PDF with DWG To PDF.pc3, in tab order, to Documents\\AcadCopilot " +
            "Exports\\<drawing>_<yyyy-MM-dd>.pdf (never overwrites: -2, -3 ... are added). The user must approve it (it " +
            "writes a file). sheets: layout names to plot (default: every sheet made by create_sheets, \"A-101 ...\", " +
            "else every layout). Each page uses its layout's paper and plot settings at 1:1 (a layout set to another " +
            "printer is switched to DWG To PDF with the closest paper for this PDF only). Background plotting is off " +
            "during the plot and restored after. Returns the file path and the pages; tell the user where the PDF is.",
        InputSchema = Schema.Object(
            Schema.Opt("sheets", Schema.Array("Layout names to plot, in this order.", Schema.String("Layout name.", minLength: 1, maxLength: 255), 1, MaxSheets)),
            Schema.Opt("name", Schema.String("File name without extension (default: the drawing name).", minLength: 1, maxLength: 60))),
    };

    /// <summary>The tool that plans the full drawing set from the ground floor plan.</summary>
    public static ToolDefinition PlanFullSetDefinition() => new()
    {
        Name = PlanFullSet,
        Title = "Plan the full drawing set",
        Risk = ToolRisk.Read,
        Description =
            "From the approved ground floor plan, plan the whole drawing set as a to-do list with coordinates: upper " +
            "floors (outer walls, stair and toilets/kitchen stacked; same plan spacing arrange_drawings uses), terrace, " +
            "front / rear / side elevations with every level (plinth 1'6, floors 10'6, sill 3', lintel 7', parapet 3' " +
            "unless given), section A-A through the stair (cut line and mapping), site plan, furniture layouts, door and " +
            "window schedule (tags counted on the plan), area statement (built-up areas, coverage, FAR), then sheets " +
            "and PDF. Reads the drawing only. The plan is found by its title (GROUND FLOOR PLAN) or plan_point; " +
            "outline, stair, plot and wet_areas override what is read (pass a validated plan's numbers here). Then " +
            "update_plan with the stages and draft each with run_commands, stage by stage, asking the user before " +
            "each new drawing as usual.",
        InputSchema = Schema.Object(
            Schema.Opt("plan_point", Schema.Array("A point inside the ground floor plan drawing [x, y] (default: the drawing titled GROUND FLOOR PLAN).", Schema.Number("Coordinate."), 2, 2)),
            Schema.Opt("upper_floors", Schema.Integer("Floors above the ground floor (1 = G+1, default 1).", 0, 6)),
            Schema.Opt("terrace", Schema.Boolean("Add a terrace plan with the stair cabin (default true).")),
            Schema.Opt("front", Schema.String("Road / front side of the plan (default south = bottom).", ["south", "north", "east", "west"])),
            Schema.Opt("floor_height", Schema.Number("Floor to floor height in inches (default 126 = 10'-6\").", 84, 240)),
            Schema.Opt("plinth", Schema.Number("Plinth height in inches (default 18).", 0, 60)),
            Schema.Opt("sill", Schema.Number("Window sill above floor in inches (default 36).", 0, 84)),
            Schema.Opt("lintel", Schema.Number("Lintel / door head above floor in inches (default 84).", 72, 120)),
            Schema.Opt("parapet", Schema.Number("Parapet height in inches (default 36).", 0, 72)),
            Schema.Opt("outline", Rect("Outer wall extents of the ground floor [x0, y0, x1, y1] (default: read from A-WALL).")),
            Schema.Opt("stair", Rect("Stair extents [x0, y0, x1, y1] (default: read from A-STRS).")),
            Schema.Opt("plot", Rect("Plot boundary [x0, y0, x1, y1] (default: read from A-SITE or a closed boundary around the plan).")),
            Schema.Opt("wet_areas", Schema.Array(
                "Toilets, kitchen, utility (default: rooms named TOILET, BATH, WC, KITCHEN, UTILITY on the plan).",
                Schema.Object(
                    "One wet area.",
                    Schema.Req("name", Schema.String("Room name.", minLength: 1, maxLength: 40)),
                    Schema.Req("box", Rect("Its extents [x0, y0, x1, y1]."))),
                0,
                20))),
    };

    /// <summary>All four definitions.</summary>
    public static IReadOnlyList<ToolDefinition> All() =>
        [ArrangeDrawingsDefinition(), CreateSheetsDefinition(), PlotToPdfDefinition(), PlanFullSetDefinition()];

    private static JsonObject Rect(string description) => Schema.Array(description + " Drawing units.", Schema.Number("Coordinate."), 4, 4);
}
