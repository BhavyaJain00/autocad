using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Presentation;

/// <summary>
/// Definitions (what the AI sees) of the colour presentation tools: fill_presentation_regions,
/// add_presentation_backdrop and export_view_png.
/// </summary>
public static class PresentationToolDefinitions
{
    /// <summary>fill_presentation_regions.</summary>
    public const string FillRegions = "fill_presentation_regions";

    /// <summary>add_presentation_backdrop.</summary>
    public const string AddBackdrop = "add_presentation_backdrop";

    /// <summary>export_view_png.</summary>
    public const string ExportViewPng = "export_view_png";

    /// <summary>Most fills per call.</summary>
    public const int MaxFills = 40;

    /// <summary>Most shadows per call.</summary>
    public const int MaxShadows = 40;

    /// <summary>Default export width in pixels.</summary>
    public const int DefaultExportWidth = 2400;

    /// <summary>Smallest export width.</summary>
    public const int MinExportWidth = 800;

    /// <summary>Largest export width.</summary>
    public const int MaxExportWidth = 4000;

    /// <summary>"R,G,B" or "#RRGGBB".</summary>
    public const string ColorPattern = @"^\s*(\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}|#[0-9A-Fa-f]{6})\s*$";

    /// <summary>The tool that fills closed areas of an elevation with presentation colours.</summary>
    public static ToolDefinition FillRegionsDefinition() => new()
    {
        Name = FillRegions,
        Title = "Fill presentation colours",
        Risk = ToolRisk.Write,
        Description =
            "Colour presentation elevation, step 1: fill closed areas of a line elevation with true-colour solid, gradient " +
            "or textured hatches, each finish on its own layer A-ELEV-PRES-* (WALL, STON, BRCK, WOOD, GLAZ, METL, BAND, ROOF, " +
            "PLNT, SKY, GRND, SHAD), so the line drawing stays as it is and the colours can be switched off per layer. Each " +
            "fill is either point (an internal point, like HATCH's pick point; the area must be closed and visible in the " +
            "current view; openings inside it such as windows stay unfilled when islands is true) or handle (a closed " +
            "polyline, circle, ellipse, spline or region). Finishes and default colours: wall_paint cream, wall_accent " +
            "terracotta, wall_white, wall_grey, stone_cladding (sandstone pattern), brick_cladding, wood (teak), glass " +
            "(blue gradient), metal (dark grey), slab_band (off-white bands, chajjas, coping), roof_tiles, roof, planting, " +
            "sky, ground, lawn, paving, shadow. Override with color \"R,G,B\" (and color2 for a gradient's second colour " +
            "or a pattern's lines), gradient true/false, angle in degrees. Fills are sent behind the linework (backdrop, " +
            "surfaces, planting, shadows order). One undo step; a fill that fails is reported and the others are kept. " +
            "Tip: COPY the line elevation to the side first if the user wants to keep a plain version. Then " +
            "add_presentation_backdrop, capture_view to check, export_view_png to share.",
        InputSchema = Schema.Object(
            Schema.Req("fills", Schema.Array(
                "The areas to fill.",
                Schema.Object(
                    "One fill.",
                    Schema.Req("finish", Schema.String("The finish (sets layer, colour and look).", PresentationFinishes.Names)),
                    Schema.Opt("point", Schema.Array("An internal point [x, y] of a closed area (drawing units).", Schema.Number("Coordinate."), 2, 2)),
                    Schema.Opt("handle", Schema.String("Handle of a closed boundary object instead of a point (\"2A3F\").", minLength: 1, maxLength: 16)),
                    Schema.Opt("color", Schema.String("True colour \"R,G,B\" (or \"#RRGGBB\") instead of the finish's.", pattern: ColorPattern)),
                    Schema.Opt("color2", Schema.String("Second colour: a gradient's end colour, or a pattern's line colour.", pattern: ColorPattern)),
                    Schema.Opt("gradient", Schema.Boolean("true = two-colour LINEAR gradient, false = solid.")),
                    Schema.Opt("angle", Schema.Number("Gradient angle in degrees (0 left to right, 90 bottom to top).", -360, 360))),
                1,
                MaxFills)),
            Schema.Opt("islands", Schema.Boolean("Leave inner closed areas (windows, doors in a wall) unfilled for point fills (default true)."))),
    };

    /// <summary>The tool that adds sky, ground and shadows to an elevation.</summary>
    public static ToolDefinition AddBackdropDefinition() => new()
    {
        Name = AddBackdrop,
        Title = "Add sky, ground and shadows",
        Risk = ToolRisk.Write,
        Description =
            "Colour presentation elevation, step 2: add a sky gradient behind the elevation and a ground band below the " +
            "ground line (layers A-ELEV-PRES-SKY / -GRND), and simple 45-degree shadows of projections (layer " +
            "A-ELEV-PRES-SHAD, dark blue-grey, 55% transparent): for each chajja / sunshade, slab edge, balcony or canopy " +
            "give its elevation rectangle and how far it projects from the wall (kind below: the shadow falls the same " +
            "distance below it, shifted away from the sun); for a projecting wall, column or fin use kind side. Everything " +
            "is sent behind the linework and the colour fills. window = the elevation's extents [x0, y0, x1, y1] (from " +
            "get_drawing_info or list_entities); ground_y = the ground line. One undo step. Then capture_view to check.",
        InputSchema = Schema.Object(
            Schema.Req("window", Schema.Array("The elevation's extents [x0, y0, x1, y1] in drawing units.", Schema.Number("Coordinate."), 4, 4)),
            Schema.Req("ground_y", Schema.Number("Y of the ground line (drawing units).")),
            Schema.Opt("sky", Schema.Boolean("Add the sky gradient (default true).")),
            Schema.Opt("ground", Schema.String("Ground band finish (default ground; none = no band).", ["ground", "lawn", "paving", "none"])),
            Schema.Opt("shadows", Schema.Array(
                "Projections that cast shadows.",
                Schema.Object(
                    "One projection.",
                    Schema.Req("box", Schema.Array("The projection's rectangle in the elevation [x0, y0, x1, y1].", Schema.Number("Coordinate."), 4, 4)),
                    Schema.Req("depth", Schema.Number("How far it projects from the wall (drawing units), e.g. 18 for an 18\" chajja.", 0.001)),
                    Schema.Opt("kind", Schema.String("below (horizontal projection, default) or side (vertical projection).", ["below", "side"]))),
                0,
                MaxShadows)),
            Schema.Opt("sun", Schema.String("Side the sun comes from (default left: shadows fall to the right).", ["left", "right"]))),
    };

    /// <summary>The tool that saves a presentation picture of the drawing as a PNG file.</summary>
    public static ToolDefinition ExportViewPngDefinition() => new()
    {
        Name = ExportViewPng,
        Title = "Export view as PNG",
        Risk = ToolRisk.Write,
        Description =
            "Save a picture of the drawing as a PNG file the user can share (WhatsApp, e-mail), in Documents\\AcadCopilot " +
            "Exports\\<name>_<date-time>.png (never overwrites; nothing else on disk changes). Paper-white background, " +
            "linework dark like a print, colour fills (presentation hatches, solid and gradient fills) in their exact " +
            "colours. area: extents (whole drawing, default), view (the current view) or window [x0, y0, x1, y1] (for " +
            "example just the presentation elevation with some margin). width in pixels (default 2400). Returns the file " +
            "path, the size and a small preview picture; tell the user where the file is. Off and frozen layers are left out.",
        InputSchema = Schema.Object(
            Schema.Opt("area", Schema.String("What to export (default extents; window needs window).", ["extents", "view", "window"])),
            Schema.Opt("window", Schema.Array("[x0, y0, x1, y1] for area window, drawing units.", Schema.Number("Coordinate."), 4, 4)),
            Schema.Opt("width", Schema.Integer("Picture width in pixels (default 2400).", MinExportWidth, MaxExportWidth)),
            Schema.Opt("name", Schema.String("File name without extension (default: the drawing name).", minLength: 1, maxLength: 60)),
            Schema.Opt("background", Schema.String("white (default, print-like) or black (as AutoCAD shows it on a dark screen).", ["white", "black"]))),
    };
}
