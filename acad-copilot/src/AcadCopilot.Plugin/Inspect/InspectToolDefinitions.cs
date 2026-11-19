using AcadCopilot.Core.Tools;

namespace AcadCopilot.Plugin.Inspect;

/// <summary>
/// Definitions (name, description for the LLM, strict argument schema) of the read-only inspection tools:
/// capture_view, list_layers, list_entities, get_entity and get_sysvars. Pure (Core types only), so the schemas are
/// linted and validated in unit tests; the bodies are in ToolRegistry.Inspect.cs.
/// </summary>
public static class InspectToolDefinitions
{
    /// <summary>Name of the picture tool.</summary>
    public const string CaptureView = "capture_view";

    /// <summary>Name of the layer list tool.</summary>
    public const string ListLayers = "list_layers";

    /// <summary>Name of the entity list tool.</summary>
    public const string ListEntities = "list_entities";

    /// <summary>Name of the entity detail tool.</summary>
    public const string GetEntity = "get_entity";

    /// <summary>Name of the system variable tool.</summary>
    public const string GetSysvars = "get_sysvars";

    /// <summary>Most entities list_entities returns per call.</summary>
    public const int MaxListLimit = 200;

    /// <summary>Default page size of list_entities.</summary>
    public const int DefaultListLimit = 50;

    /// <summary>Most handles get_entity reads per call.</summary>
    public const int MaxHandles = 20;

    /// <summary>Most names get_sysvars reads per call.</summary>
    public const int MaxSysvars = 40;

    /// <summary>Pattern of an entity handle: 1-16 hex digits.</summary>
    public const string HandlePattern = "^[0-9A-Fa-f]{1,16}$";

    /// <summary>All five definitions.</summary>
    public static IReadOnlyList<ToolDefinition> All() =>
        [CaptureViewDefinition(), ListLayersDefinition(), ListEntitiesDefinition(), GetEntityDefinition(), GetSysvarsDefinition()];

    /// <summary>capture_view: PNG picture of the drawing plus view facts.</summary>
    public static ToolDefinition CaptureViewDefinition() => new()
    {
        Name = CaptureView,
        Title = "Look at the drawing",
        Risk = ToolRisk.Read,
        Description =
            "Take a picture (PNG) of the drawing to check your work the way a drafter looks at the screen: overlapping " +
            "text, gaps at wall corners, wrong proportions, missing or cluttered dimensions. area 'view' (default) is " +
            "exactly what the user sees in the current viewport; 'extents' frames the whole drawing; 'window' frames the " +
            "rectangle given in window. 'extents' and 'window' are rendered off-screen, so the user's view does not move. " +
            "background 'white' (default) looks like a plot (white lines print black, light colours darkened); 'dark' " +
            "keeps the colours of the AutoCAD screen. Returns the image plus facts: the area shown (drawing units and " +
            "feet-inches), view centre and height, drawing extents, entity counts and the capture method. The picture is " +
            "approximate: read exact coordinates with list_entities or get_entity.",
        InputSchema = Schema.Object(
            Schema.Opt("area", Schema.String("What to show (default 'view', or 'window' when window is given).", ["view", "extents", "window"])),
            Schema.Opt("window", Schema.Array(
                "Rectangle to show for area 'window': [x0, y0, x1, y1], two opposite corners in drawing units (WCS).",
                Schema.Number("Coordinate in drawing units."),
                minItems: 4,
                maxItems: 4)),
            Schema.Opt("width", Schema.Integer(
                $"Picture width in pixels (default {CaptureMath.DefaultWidth}); the height follows the area's proportions (at most {CaptureMath.MaxHeight}).",
                minimum: 400,
                maximum: CaptureMath.MaxWidth)),
            Schema.Opt("background", Schema.String("'white' (default, like a plot) or 'dark' (like the AutoCAD screen).", ["white", "dark"])),
            Schema.Opt("method", Schema.String(
                "Capture technique; keep 'auto' (default) unless a picture looks wrong. 'offscreen': AutoCAD renders the drawing off-screen (any area). " +
                "'screen': copy of the drawing window as the user sees it, with grid and UCS icon (area 'view' only). " +
                "'preview': AutoCAD's preview image of the current view (area 'view' only).",
                ["auto", "offscreen", "screen", "preview"]))),
    };

    /// <summary>list_layers: layers with state, colour, linetype and entity counts.</summary>
    public static ToolDefinition ListLayersDefinition() => new()
    {
        Name = ListLayers,
        Title = "List layers",
        Risk = ToolRisk.Read,
        Description =
            "List the drawing's layers: name, on, frozen, locked, colour (ACI number or 'r,g,b'), linetype, lineweight " +
            "(mm, or 'default'), plottable, number of entities, and which layer is current. Check it before drawing to " +
            "reuse the drawing's own layers (walls, doors, windows, dimensions, text, furniture) instead of inventing " +
            "new names, and to find why something is invisible (off, frozen) or cannot be edited (locked).",
        InputSchema = Schema.Object(
            Schema.Opt("name", Schema.String("Only layers matching this pattern: * any text, ? one character, commas separate alternatives ('A-WALL*,A-DOOR').")),
            Schema.Opt("counts", Schema.Boolean("Count the entities on each layer (default true; false is faster on very large drawings)."))),
    };

    /// <summary>list_entities: filtered, paged compact entity summaries.</summary>
    public static ToolDefinition ListEntitiesDefinition() => new()
    {
        Name = ListEntities,
        Title = "List entities",
        Risk = ToolRisk.Read,
        Description =
            "List entities with compact geometry to check exact positions and sizes: handle, DXF type, layer and key " +
            "points, lengths, sizes and areas in drawing units plus feet-inches text. Filters are optional and combine: " +
            "layer (pattern: * ? and commas, e.g. 'A-WALL*,A-DOOR'), type (DXF names, comma separated: LINE, LWPOLYLINE, " +
            "CIRCLE, ARC, TEXT, MTEXT, DIMENSION, HATCH, INSERT ...), window [x0, y0, x1, y1] in drawing units " +
            "(entities whose bounding box touches it; inside=true keeps only those completely inside). Results come in " +
            "drawing order (oldest first), paged with limit and offset; total and countsByType count every match. " +
            "space 'model' (default) or 'current' (the active layout). Use get_entity for every detail of one entity.",
        InputSchema = Schema.Object(
            Schema.Opt("layer", Schema.String("Layer name or pattern.")),
            Schema.Opt("type", Schema.String("DXF type name(s), comma separated (case-insensitive).")),
            Schema.Opt("window", Schema.Array(
                "[x0, y0, x1, y1]: two opposite corners of a rectangle in drawing units (WCS).",
                Schema.Number("Coordinate in drawing units."),
                minItems: 4,
                maxItems: 4)),
            Schema.Opt("inside", Schema.Boolean("With window: only entities completely inside it (default false: touching or crossing).")),
            Schema.Opt("space", Schema.String("'model' (default) or 'current' (the layout the user is in).", ["model", "current"])),
            Schema.Opt("limit", Schema.Integer($"Entities per page (default {DefaultListLimit}).", minimum: 1, maximum: MaxListLimit)),
            Schema.Opt("offset", Schema.Integer("Matches to skip (default 0); use nextOffset from the previous page.", minimum: 0))),
    };

    /// <summary>get_entity: every useful property of one or more entities by handle.</summary>
    public static ToolDefinition GetEntityDefinition() => new()
    {
        Name = GetEntity,
        Title = "Read entity",
        Risk = ToolRisk.Read,
        Description =
            "Read everything useful about entities by handle (hex text such as '2A3', as returned by run_commands and " +
            "list_entities): layer, colour, linetype, lineweight, the full geometry (every polyline vertex with bulge " +
            "and width, arc angles, text and its style, dimension measurement and points, hatch pattern, loops and area, " +
            "block name, attributes and dynamic block properties) and the bounding box, in drawing units plus feet-inches. " +
            $"Give handle for one entity or handles for up to {MaxHandles}.",
        InputSchema = Schema.Object(
            Schema.Opt("handle", Schema.String("Entity handle (hex).", pattern: HandlePattern)),
            Schema.Opt("handles", Schema.Array(
                "Several entity handles (hex).",
                Schema.String("Entity handle (hex).", pattern: HandlePattern),
                minItems: 1,
                maxItems: MaxHandles))),
    };

    /// <summary>get_sysvars: read system variables.</summary>
    public static ToolDefinition GetSysvarsDefinition() => new()
    {
        Name = GetSysvars,
        Title = "Read system variables",
        Risk = ToolRisk.Read,
        Description =
            "Read AutoCAD system variables (read only): names -> values, for example LUNITS, INSUNITS, LUPREC, DIMSCALE, " +
            "DIMTXT, DIMSTYLE, LTSCALE, TEXTSIZE, TEXTSTYLE, OSMODE, ORTHOMODE, CLAYER, CELTYPE, TILEMODE, CVPORT, " +
            "VIEWCTR, VIEWSIZE, EXTMIN, EXTMAX. Points come back as [x, y] or [x, y, z]. Unknown names are listed under " +
            "'unknown'. To change a variable use run_commands (type its name, or SETVAR).",
        InputSchema = Schema.Object(
            Schema.Req("names", Schema.Array(
                "System variable names.",
                Schema.String("System variable name, e.g. LUNITS.", pattern: "^[A-Za-z_][A-Za-z0-9_$]{0,63}$"),
                minItems: 1,
                maxItems: MaxSysvars))),
    };
}
