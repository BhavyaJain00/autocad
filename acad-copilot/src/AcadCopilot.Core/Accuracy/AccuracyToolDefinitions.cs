using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Accuracy;

/// <summary>Definitions (what the AI sees) of the accuracy tools: validate_plan, check_drawing, apply_drawing_template.</summary>
public static class AccuracyToolDefinitions
{
    /// <summary>validate_plan.</summary>
    public const string ValidatePlan = "validate_plan";

    /// <summary>check_drawing.</summary>
    public const string CheckDrawing = "check_drawing";

    /// <summary>apply_drawing_template.</summary>
    public const string ApplyDrawingTemplate = "apply_drawing_template";

    private static JsonObject Len(string description) => Schema.String(description, minLength: 1, maxLength: 24);

    private static JsonObject Point(string description) => Schema.String(description, minLength: 3, maxLength: 40);

    private static JsonObject Points(string description, int min) => Schema.Array(description, Point("Point \"x,y\" in feet-inches."), min, 64);

    /// <summary>The schema of one floor.</summary>
    private static JsonObject FloorSchema() => Schema.Object(
        "One floor, ground floor first. All coordinates in the plot system (south-west plot corner 0,0, +X east, +Y north), the same on every floor.",
        Schema.Opt("name", Schema.String("\"Ground floor\", \"First floor\" ...", maxLength: 40)),
        Schema.Opt("origin", Point("Where this floor's plot corner 0,0 is drawn in the drawing (\"0,0\" for the ground floor; e.g. \"60',0\" for a first floor drawn 60' to the right). check_drawing uses it.")),
        Schema.Opt("outline", Points("Building outline of this floor if it differs from plan.outline.", 3)),
        Schema.Req("rooms", Schema.Array("Every space of the floor (rooms, passages, stair, toilets, outdoor parking/sitout/balcony). Clear sizes: inside face to inside face.", Schema.Object(
            Schema.Req("name", Schema.String("Unique name, e.g. \"Bedroom 1\".", minLength: 1, maxLength: 40)),
            Schema.Opt("type", Schema.String("Room type (decides the checks).", RoomTypes.All)),
            Schema.Opt("x", Len("Clear rectangle: west face x.")),
            Schema.Opt("y", Len("Clear rectangle: south face y.")),
            Schema.Opt("width", Len("Clear rectangle: size along X.")),
            Schema.Opt("depth", Len("Clear rectangle: size along Y.")),
            Schema.Opt("polygon", Points("Clear outline of a non-rectangular room instead of x/y/width/depth.", 3)),
            Schema.Opt("cars", Schema.Integer("Parking: number of cars (default 1).", 1, 6)),
            Schema.Opt("ventilation", Schema.String("How a toilet/kitchen without an outside window is ventilated.", ["window", "shaft", "exhaust"]))), 1, 40)),
        Schema.Req("walls", Schema.Array("Every wall as its CENTRELINE (from, to) and thickness: outer walls on the outline (centreline half the thickness inside it), inner walls between rooms. Ends meet on the other wall's centreline.", Schema.Object(
            Schema.Opt("id", Schema.String("Id used by doors and windows (default W1, W2 ...).", minLength: 1, maxLength: 16)),
            Schema.Req("from", Point("Centreline start \"x,y\".")),
            Schema.Req("to", Point("Centreline end \"x,y\".")),
            Schema.Opt("thickness", Len("Thickness (default: outer_wall on the outline, inner_wall elsewhere)."))), 1, 80)),
        Schema.Opt("doors", Schema.Array("Doors and door-less openings.", Schema.Object(
            Schema.Opt("tag", Schema.String("MD, D1, D2 ...", maxLength: 12)),
            Schema.Req("wall", Schema.String("Id of the wall it is in.", minLength: 1, maxLength: 16)),
            Schema.Req("offset", Len("Distance along the wall centreline from its 'from' point to the near jamb.")),
            Schema.Req("width", Len("Clear width: main 3'6, rooms 3', toilets 2'6.")),
            Schema.Opt("kind", Schema.String("hinged (default), double, sliding or opening (no shutter).", ["hinged", "double", "sliding", "opening"])),
            Schema.Opt("swing_into", Schema.String("Room the leaf swings into (default: into the room, toilet, bedroom or kitchen, never outside or into a passage).", maxLength: 40)),
            Schema.Opt("hinge", Schema.String("Hinge at the start (near 'from') or end jamb (default: the jamb nearer a corner).", ["start", "end"])),
            Schema.Opt("head", Len("Door height (default 7')."))), 0, 60)),
        Schema.Opt("windows", Schema.Array("Windows and ventilators.", Schema.Object(
            Schema.Opt("tag", Schema.String("W1, W2, V1 ...", maxLength: 12)),
            Schema.Req("wall", Schema.String("Id of the wall it is in.", minLength: 1, maxLength: 16)),
            Schema.Req("offset", Len("Distance along the wall centreline from its 'from' point to the near jamb.")),
            Schema.Req("width", Len("Width.")),
            Schema.Opt("kind", Schema.String("window (default), ventilator or french (full height).", ["window", "ventilator", "french"])),
            Schema.Opt("sill", Len("Sill above floor (default 3', ventilator 5').")),
            Schema.Opt("head", Len("Head above floor (default 7')."))), 0, 60)),
        Schema.Opt("stair", Schema.Object(
            "The stair numbers (give them on the floor where the stair starts) and its clear box.",
            Schema.Opt("type", Schema.String("Stair type.", ["dog_legged", "straight", "l_shaped", "u_shaped", "spiral"])),
            Schema.Opt("x", Len("Box west x.")),
            Schema.Opt("y", Len("Box south y.")),
            Schema.Opt("width", Len("Box size along X.")),
            Schema.Opt("depth", Len("Box size along Y.")),
            Schema.Req("flight_width", Len("Clear flight width (3'3 typical, 3' minimum).")),
            Schema.Req("riser", Len("Riser height (6\" to 7\").")),
            Schema.Req("tread", Len("Tread (10\" or more).")),
            Schema.Req("risers", Schema.Integer("Risers floor to floor (risers x riser = floor height).", 2, 40)),
            Schema.Opt("landing", Len("Mid-landing depth (default the flight width).")))));

    /// <summary>validate_plan: checks a structured house plan before drawing.</summary>
    public static ToolDefinition ValidatePlanDefinition() => new()
    {
        Name = ValidatePlan,
        Title = "Validate plan",
        Risk = ToolRisk.Read,
        Description =
            "Check a house plan IN NUMBERS before drawing anything, like an architect's dimensioned sketch. Give the plot, " +
            "setbacks, building outline and per floor every room (clear size), every wall (centreline + thickness), the doors " +
            "and windows (wall id, offset along the centreline, width) and the stair. Lengths are feet-inch strings (\"11'4\", " +
            "\"30'\", \"9\" = 9 inches; bare numbers are inches); points are \"x,y\" strings with the plot's south-west corner " +
            "at 0,0, +Y north. Checks: rooms fill the floor with no gap or overlap (rooms + walls = outline), minimum clear " +
            "sizes and proportions (typical Indian residential sizes changed by the user's rules; never a bylaw check), every " +
            "room reachable through doors, doors not clashing with walls, each other or the stair and swinging into rooms with " +
            "the hinge near a corner, outside windows for habitable rooms (1/10-1/8 of the floor area) and ventilation for " +
            "toilets and kitchens, stair riser/tread/width/fit and risers x riser = floor height, upper floors stacked (stair, " +
            "wet areas, outer walls), whole-inch sizes, setbacks and areas; Vastu hints only when asked. Returns errors, " +
            "warnings, hints, a dimension schedule (sizes, areas, opening jambs, hinge points, dimension chains along the south " +
            "and west walls) and a Markdown summary for the user. Fix every error and call it again until there are none, " +
            "then show the summary and ask \"Shall I draw this?\". The last plan without errors is saved for this drawing; " +
            "check_drawing compares the drawing with it.",
        InputSchema = Schema.Object(
            Schema.Req("plan", Schema.Object(
                Schema.Opt("name", Schema.String("Plan name, e.g. \"30x40 east G+1\".", maxLength: 80)),
                Schema.Req("plot", Schema.Object(
                    Schema.Req("width", Len("Plot size along X (east-west), e.g. \"30'\".")),
                    Schema.Req("depth", Len("Plot size along Y (north-south), e.g. \"40'\".")),
                    Schema.Opt("road_side", Schema.String("Side the road is on.", ["north", "south", "east", "west"])))),
                Schema.Opt("setbacks", Schema.Object(
                    "Required setbacks (the user's or assumed values).",
                    Schema.Opt("north", Len("North setback.")),
                    Schema.Opt("south", Len("South setback.")),
                    Schema.Opt("east", Len("East setback.")),
                    Schema.Opt("west", Len("West setback.")))),
                Schema.Opt("outer_wall", Len("Outer wall thickness (default 9).")),
                Schema.Opt("inner_wall", Len("Inner wall thickness (default 4.5).")),
                Schema.Opt("floor_height", Len("Floor to floor height (default 10'6).")),
                Schema.Opt("outline", Points("Building outline: the OUTER face of the outer walls, corners in order.", 3)),
                Schema.Req("floors", Schema.Array("Floors, ground first (G+1 = 2 floors).", FloorSchema(), 1, PlanParser.MaxFloors)))),
            Schema.Opt("vastu", Schema.Boolean("Add Vastu hints (preferences only). Default: on when the user's rules mention Vastu.")),
            Schema.Opt("save", Schema.Boolean("Save the plan for check_drawing when it has no errors (default true)."))),
    };

    /// <summary>check_drawing: compares the drawing with the validated plan.</summary>
    public static ToolDefinition CheckDrawingDefinition() => new()
    {
        Name = CheckDrawing,
        Title = "Check drawing",
        Risk = ToolRisk.Read,
        Description =
            "Read what is drawn for one floor and compare it with the last plan validate_plan saved for this drawing. " +
            "Reports wall-line gaps, duplicates and dangling ends; missing wall faces and wall lines left across openings; " +
            "each room's clear size measured between the wall lines against the plan (tolerance 1/2\"); missing, extra, " +
            "wrongly hinged or wrongly swinging doors (arcs on A-DOOR centred on the hinge) and missing or extra windows " +
            "(lines on A-GLAZ); missing room names, overlapping text and text on walls or door swings; dimensions whose text " +
            "is overridden, whose points are on nothing, whose chains have gaps or do not add up to the overall size, and a " +
            "missing overall dimension; objects on layer 0 or on the wrong layer. Run it after each stage (stage \"walls\" " +
            "after walls, \"openings\" after doors and windows, \"annotation\" after text and dimensions, \"all\" at the end) " +
            "and fix every error before going on. Coordinates in the result are drawing coordinates.",
        InputSchema = Schema.Object(
            Schema.Opt("floor", Schema.String("Floor name, or its number (1 = ground floor; default 1).", minLength: 1, maxLength: 40)),
            Schema.Opt("origin", Point("Where the floor's plot corner 0,0 is in the drawing (default: the floor's origin from the plan, else 0,0).")),
            Schema.Opt("stage", Schema.String("Which checks (default all).", DrawingChecker.Stages)),
            Schema.Opt("wall_layer", Schema.String("Wall layer (default A-WALL).", minLength: 1, maxLength: 64)),
            Schema.Opt("door_layer", Schema.String("Door layer (default A-DOOR).", minLength: 1, maxLength: 64)),
            Schema.Opt("glazing_layer", Schema.String("Window layer (default A-GLAZ).", minLength: 1, maxLength: 64)),
            Schema.Opt("text_layer", Schema.String("Text layer (default A-ANNO-TEXT).", minLength: 1, maxLength: 64)),
            Schema.Opt("dims_layer", Schema.String("Dimension layer (default A-ANNO-DIMS).", minLength: 1, maxLength: 64)),
            Schema.Opt("max_findings", Schema.Integer("Most findings returned (default 40).", 5, 200))),
    };

    /// <summary>apply_drawing_template: sets up units, layers, styles and the title block in one undo step.</summary>
    public static ToolDefinition ApplyDrawingTemplateDefinition() => new()
    {
        Name = ApplyDrawingTemplate,
        Title = "Apply drawing template",
        Risk = ToolRisk.Write,
        Description =
            "Set up the active drawing for feet-inch architectural drafting in ONE undo step, without dialogs: units " +
            "(LUNITS 4 architectural, INSUNITS 1 inches, LUPREC 1/16\", MEASUREMENT 0), linetypes CENTER/HIDDEN/DASHED/PHANTOM, " +
            "layers with colours, linetypes and lineweights (A-WALL, A-DOOR, A-GLAZ, A-FURN, A-STRS, A-ANNO-TEXT, " +
            "A-ANNO-DIMS, A-SITE, A-GRID, A-HATCH, A-TTLB, A-ELEV), text style ARCH (Arial, height 0), dimension styles " +
            "ARCH-48 (1/4\" = 1'-0\") and ARCH-96 (1/8\" = 1'-0\") with ticks, text above the line and architectural units, " +
            "LTSCALE, and the title block block A-TTLB-A3 (A3 sheet in inches with attributes PROJECT, TITLE, SCALE, DATE, " +
            "DRAWN, SHEET, NOTE; insert it with -INSERT at scale 48 or 96 on A-TTLB). Existing layers and styles are kept " +
            "unless overwrite is true. Use it once per new drawing instead of typing the setup (skip it when the user's " +
            "drawing already has its own standards). If the user has their own template (.dwt/.dwg), pass template_path " +
            "(remember_template saves it for next time): its units, layers, linetypes, text and dimension styles and blocks are " +
            "imported instead. Returns what was created, kept or updated and the text heights to use.",
        InputSchema = Schema.Object(
            Schema.Opt("scale", Schema.String("Plan scale: sets the current dimension style and LTSCALE (default 1/4).", ["1/4", "1/8"])),
            Schema.Opt("overwrite", Schema.Boolean("Update existing layers and styles to the template's values (default false: existing ones are kept).")),
            Schema.Opt("title_block", Schema.Boolean("Define the title block block (default true).")),
            Schema.Opt("template_path", Schema.String("Full path of the user's own template drawing (.dwt or .dwg) to import instead of the built-in template; it is only read.", minLength: 5, maxLength: 260)),
            Schema.Opt("remember_template", Schema.Boolean("Save template_path as the default for every later call.")),
            Schema.Opt("use_built_in", Schema.Boolean("Use the built-in template even when a template path was remembered (true + remember_template also forgets it)."))),
    };
}
