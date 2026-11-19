using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Services;

/// <summary>Definitions (what the AI sees) of the services tools: insert_symbol_library and place_services_legend.</summary>
public static class ServiceToolDefinitions
{
    /// <summary>insert_symbol_library.</summary>
    public const string InsertSymbolLibrary = "insert_symbol_library";

    /// <summary>place_services_legend.</summary>
    public const string PlaceServicesLegend = "place_services_legend";

    /// <summary>Wire names of the disciplines.</summary>
    public static readonly string[] Disciplines = ["electrical", "plumbing"];

    /// <summary>The tool that imports the electrical and plumbing symbol blocks and layers into the active drawing.</summary>
    public static ToolDefinition InsertSymbolLibraryDefinition() => new()
    {
        Name = InsertSymbolLibrary,
        Title = "Insert symbol library",
        Risk = ToolRisk.Write,
        Description =
            "Import the standard electrical and plumbing symbol blocks into the active drawing (block definitions only; " +
            "nothing is placed) and create the services layers E-LITE, E-POWR, E-SWCH, E-ANNO, P-WATR, P-DRAN, P-FIXT, " +
            "P-ANNO. Call it once before drawing an electrical or plumbing layout (first draft for an engineer), then place " +
            "each symbol with run_commands: -INSERT <block> <x,y> then Enter for the X scale, Enter for Y, the rotation, " +
            "and answer the one attribute prompt (a label such as SB1, or \"\" to keep the default). Insert each symbol on " +
            "its layer (set it with -LAYER S first). Blocks: E-SWITCHBOARD, E-SOCKET-5A, E-SOCKET-15A, E-LIGHT, E-FAN, " +
            "E-EXHAUST-FAN, E-AC-POINT, E-GEYSER, E-DB, E-EARTH, P-BASIN-TAP, P-WASH-BASIN, P-WC, P-FLOOR-TRAP, " +
            "P-NAHANI-TRAP, P-INLET-PIPE, P-OUTLET-PIPE, P-STACK, P-INSPECTION-CHAMBER, P-WATER-TANK. They are drawn in " +
            "inches (scale 1 in a 1 unit = 1 inch drawing; AutoCAD scales them in a millimetre drawing). Returns each block " +
            "with its layer, base point, attribute prompt and meaning. recall_knowledge \"electrical layout\" / \"plumbing " +
            "layout\" gives the Indian residential first-draft norms. Optional file: the user's own block library (.dwg or " +
            ".dxf) to import its blocks instead.",
        InputSchema = Schema.Object(
            Schema.Opt("discipline", Schema.String("Which symbols to import (default both).", ["electrical", "plumbing", "both"])),
            Schema.Opt("replace_existing", Schema.Boolean("Redefine blocks that already exist in the drawing with the library's version (default false: existing blocks are kept).")),
            Schema.Opt("file", Schema.String("Full path of the user's own block library drawing (.dwg or .dxf) to import all its blocks from, instead of the built-in library. It is only read.", minLength: 5, maxLength: 260))),
    };

    /// <summary>The tool that draws a legend table of services symbols with the draft note.</summary>
    public static ToolDefinition PlaceServicesLegendDefinition() => new()
    {
        Name = PlaceServicesLegend,
        Title = "Place services legend",
        Risk = ToolRisk.Write,
        Description =
            "Draw the legend table of an electrical or plumbing layout (title, SYMBOL / DESCRIPTION rows with the real " +
            "blocks) and below it the mandatory draft note \"DRAFT - TO BE CHECKED BY A QUALIFIED ELECTRICAL / PLUMBING " +
            "ENGINEER\" on E-ANNO / P-ANNO, as one undo step. Imports the symbol library first if needed. Place it beside " +
            "the plan, outside the building (at = its top-left corner). Default rows: the symbols of that discipline " +
            "already used in the drawing (all of them when none is used yet). Size 1 suits 1/4\" = 1'-0\" in an inch " +
            "drawing (about 18' wide); use 2 for 1/8\" = 1'-0\". Returns the legend's extents and the handles created.",
        InputSchema = Schema.Object(
            Schema.Req("discipline", Schema.String("electrical or plumbing.", Disciplines)),
            Schema.Req("at", Schema.Array("Top-left corner [x, y] in drawing units.", Schema.Number("Coordinate."), 2, 2)),
            Schema.Opt("symbols", Schema.Array("Block names for the rows, in order (default: the ones used in the drawing).", Schema.String("Block name, e.g. E-LIGHT.", minLength: 3, maxLength: 64), 1, 30)),
            Schema.Opt("size", Schema.Number("Size factor (default 1 = for 1/4\" = 1'-0\"; 2 for 1/8\" = 1'-0\").", 0.1, 20)),
            Schema.Opt("title", Schema.String("Title (default ELECTRICAL LEGEND / PLUMBING LEGEND).", minLength: 2, maxLength: 80))),
    };
}
