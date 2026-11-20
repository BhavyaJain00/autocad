using System.Globalization;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Agents;

/// <summary>A built-in drafting guide: rarely needed know-how kept out of the per-turn instructions.</summary>
/// <param name="Name">Guide name ("Front elevation").</param>
/// <param name="Keywords">Lower-case words that select it (any of them in the recall query).</param>
/// <param name="Text">The guidance.</param>
public sealed record DraftingGuide(string Name, IReadOnlyList<string> Keywords, string Text);

/// <summary>
/// Built-in guides for drawings and elements that are not needed in every turn (elevations, sections, site plan,
/// furniture, hatching, the dog-legged stair). The instructions point at them; the MCP bridge adds the matching guides
/// to every recall_knowledge answer (also when nothing was learned yet), next to the user's own recipes.
/// </summary>
public static class DraftingGuides
{
    /// <summary>Most guides returned for one query.</summary>
    public const int MaxPerQuery = 2;

    /// <summary>All guides.</summary>
    public static IReadOnlyList<DraftingGuide> All { get; } =
    [
        new(
            "Dog-legged stair",
            ["stair", "stairs", "staircase", "steps", "dog-legged", "doglegged", "flight", "landing", "tread", "treads", "riser", "risers"],
            "Layer A-STRS. Riser 6-7\", tread 10\" or more, flight 3'0-3'6 wide, mid-landing at least the flight width; e.g. 18 risers of 7\" for 10'6 floor to floor (9 per flight, 8 treads of 10\" = 6'8 per flight). " +
            "Draw the two flight outlines and the landing with RECTANG or PLINE, the first tread LINE across a flight, then -ARRAY [\"L\",\"\",\"R\",<rows>,<columns>,<spacing>] " +
            "(N rows and 1 column for treads stepping in Y, 1 row and N columns for X, negative spacing to go down/left). " +
            "Walking line: PLINE along the middle of the flights whose last segment is \"W\",\"3\",\"0\",<arrow tip>, then \"\", and TEXT \"UP\" at its start. " +
            "Upper floors: the same outline, a diagonal break line (two short zig-zag LINEs) across the cut flight, \"DN\" on the lower flight. Check that the risers fit the floor height and the landing is clear of doors."),
        new(
            "Elevation",
            ["elevation", "elevations", "facade", "façade"],
            "Layer A-ELEV, projected from the plan (draw it below or beside the plan, at least 20' away; vertical lines from the plan's wall corners and openings). " +
            "Thick ground line PLINE width 2; plinth 1'6 unless told; floor to floor 10'6; slab bands 5\" (two LINEs); sills 3'0 above floor; lintels and door heads 7'0; parapet 3'0 with a 3\" coping band. " +
            "Windows and doors as RECTANG with an OFFSET 2\" frame inside; ventilators 2'x1'6 at 7'0 head height. -HATCH for materials (brick ANSI31 24 or AR-BRSTD 1, plaster as AR-SAND 1, glass ANSI32 12) and SOLID for dark openings if wanted. " +
            "Level markers: a small triangle (PLINE) + TEXT like \"+10'-6\\\" FFL\" at each floor; vertical dimensions (plinth, floors, parapet) on one side; title \"FRONT ELEVATION\" 12 high with the scale."),
        new(
            "Section",
            ["section", "sections", "sectional", "cut"],
            "Layer A-ELEV (cut parts thicker, PLINE width 1 or lineweight 0.5). Pick the cut through the stair and a toilet or kitchen. " +
            "Cut walls and slabs thick and hatched: AR-CONC 1 for RCC slabs, beams and lintels, ANSI31 24 for brick, EARTH 24 below ground; slab 5\", plinth 1'6, floor to floor 10'6, parapet 3'. " +
            "Stair as one PLINE of risers and treads with the landing; doors and windows beyond as thin outlines. Footings dashed (HIDDEN linetype) and marked \"indicative - by structural engineer\". " +
            "Level markers at each floor, vertical dimensions on one side, title \"SECTION A-A\" and section marks (A ... A with arrows) on the plan."),
        new(
            "Site plan",
            ["site", "plot", "compound", "setback", "setbacks", "gate", "road", "location", "key"],
            "Scale 1/8\"=1'-0\": text heights doubled (names 18, notes 12, title 24), DIMSCALE 96 and a dimension style ARCH-96 (same variables as ARCH-48). " +
            "Layer A-SITE: plot boundary PLINE, compound wall 9\" (OFFSET inwards) with a gate opening (12' for cars, 3'-4' wicket) and its swing; " +
            "the building outline from the ground floor; setbacks dimensioned on all four sides; road along the facing side with its width and \"ROAD\" text; parking with a car outline 6'x14'; " +
            "north arrow; title \"SITE PLAN\" with the scale. Setbacks come from local bylaws: use the user's numbers and mark assumed ones."),
        new(
            "Furniture",
            ["furniture", "furnish", "bed", "beds", "sofa", "dining", "wardrobe", "kitchen", "counter", "toilet", "wc", "basin", "layout"],
            "Layer A-FURN, simple RECTANG/CIRCLE/ARC outlines (or existing blocks with -INSERT; list_entities type INSERT shows the block names in use). " +
            "Double bed 6'x6'6 head against a wall, single bed 3'x6'6, side tables 1'6x1'6, wardrobe 2' deep (2'-6' wide), sofa 3' deep, centre table 2'x4', dining 3'x5' with 1'6x1'6 chairs, " +
            "kitchen counter 2' deep along two walls (L) with a sink 2'x1'6 under the window and the hob 2'x1'8 away from the door, fridge 2'6x2'6; " +
            "toilet: WC 1'6x2'3 (EWC) or Indian WC 1'6x2'0, basin 1'6x1'4, shower area 3'x3'. Keep 2'6-3' clear in front of beds and counters and never block door swings or windows."),
        new(
            "Hatching",
            ["hatch", "hatching", "pattern", "patterns", "fill", "solid", "materials"],
            "-HATCH [\"P\",<pattern>,<scale>,<angle>,<point inside>,\"\"]; SOLID: [\"P\",\"S\",<point>,\"\"]. The area must be closed and on screen (ZOOM W first); one area per -HATCH; layer A-HATCH. " +
            "Patterns at 1/4\"=1'-0\": ANSI31 24 brick, AR-CONC 1 RCC, EARTH 24 ground, AR-SAND 1 plaster, ANSI37 24 tiles, AR-PARQ1 1 wood. If a hatch fails (\"no valid boundary\"), close the gap or ZOOM in and pick a point further inside."),
        new(
            "Electrical layout",
            ["electrical", "electric", "wiring", "switchboard", "socket", "sockets", "light", "lights", "fan", "fans", "db", "earthing"],
            "First draft for the electrician, on a COPY of the floor plan. insert_symbol_library, then -INSERT the E-* blocks on E-SWCH / E-LITE / E-POWR " +
            "(switchboard 4'-0\" high on the door latch side, sockets 1'-0\", a fan and a light per room, 15A points for kitchen, geyser and washing machine, " +
            "AC 7'-0\", DB near the main door), wiring as ARCs from the switchboards, then place_services_legend (legend + the DRAFT note). " +
            "Never give loads, wire sizes or MCB ratings as designed. Full norms: recipe \"Electrical layout (first draft)\"."),
        new(
            "Plumbing layout",
            ["plumbing", "drainage", "drain", "water", "supply", "pipe", "pipes", "stack", "shaft", "trap", "chamber", "tank", "sewer"],
            "First draft for the plumber, on a COPY of the floor plan. insert_symbol_library, then the P-* blocks (P-FIXT fixtures and traps, " +
            "P-DRAN stacks / drains / inspection chambers, P-WATR supply / tank); stacks on outer walls or shafts, slopes 1:40-1:60 written on the " +
            "outlet arrows, inspection chambers in the setback, then place_services_legend (legend + the DRAFT note). Never give pipe sizes, slopes " +
            "or venting as designed. Full norms: recipe \"Plumbing layout (first draft)\"."),
        new(
            "Presentation elevation",
            ["presentation", "colour", "color", "colored", "coloured", "render", "rendering", "png", "export"],
            "COPY the finished line elevation aside (keep the original), then fill_presentation_regions (walls, cladding, bands, glass, wood, metal, " +
            "roof; fills go on A-ELEV-PRES-* layers behind the lines), add_presentation_backdrop (sky, ground, 45-degree shadows), capture_view to " +
            "check, and export_view_png for a shareable PNG (Documents\\AcadCopilot Exports). 2D only, no RENDER. Recipe \"Colour presentation elevation\"."),
        new(
            "Vastu",
            ["vastu", "vaastu"],
            "Only when the user asks for it. Usual placements: entrance E or N, kitchen SE (cooking facing east), master bedroom SW, pooja NE, " +
            "toilets NW or W, stair S, SW or W, overhead tank SW, underground sump NE. Treat them as preferences after the plot, road side and " +
            "light; say which ones could not be met."),
        new(
            "Full drawing set",
            ["full", "set", "sheet", "sheets", "pdf", "layouts", "submission", "arrange", "titleblock"],
            "From the approved ground floor plan: plan_full_set first (stages with coordinates, levels, section cut, schedule, areas), update_plan with its stages, " +
            "then draft each stage with run_commands at its place (recall_knowledge with the stage's guide: stair, elevation, section, site plan, furniture), asking before each new drawing. " +
            "Upper floors: COPY the ground floor's outer walls, stair and wet areas by the plan spacing it gives, so walls and stairs stay stacked. " +
            "Levels unless the user says otherwise: plinth 1'6, floors 10'6, sill 3', lintel 7', parapet 3'. Every drawing gets its title and a \"SCALE ...\" line. " +
            "Then arrange_drawings (preview_only first; labels name untitled drawings), create_sheets (A3 unless asked; A-001 site, A-101 plans, A-201 elevations, " +
            "A-301 sections; project and client in the title block; show_sheet + capture_view to check), and plot_to_pdf only when the user wants the PDF " +
            "(they approve it; give them the path). Sheets carry \"NOT FOR CONSTRUCTION - CHECK BY ARCHITECT/ENGINEER\"."),
        new(
            "Drawing setup",
            ["setup", "set-up", "template", "layers", "layer", "units", "linetype", "linetypes", "dimstyle"],
            "apply_drawing_template does all of this in one undo step; type it by hand only if that fails. SETVAR LUNITS 4, INSUNITS 1, MEASUREMENT 0, LTSCALE 24. " +
            "Layers (colour, lineweight mm): A-WALL 7 0.5, A-DOOR 4 0.25, A-GLAZ 5 0.25, A-STRS 4 0.25, A-FURN 6 0.18, A-ANNO-TEXT 7 0.25, A-ANNO-DIMS 3 0.18, " +
            "A-SITE 2 0.35, A-GRID 1 0.18 CENTER, A-ELEV 7 0.35, A-HATCH 8 0.13, A-TTLB 7 0.35. First -LINETYPE [\"L\",\"CENTER\",\"\",\"\"], then one -LAYER step: " +
            "\"N\", \"A-WALL,A-DOOR,...\", then \"C\",<colour>,<layers> per colour, \"L\",\"CENTER\",\"A-GRID\", \"LW\",<mm>,<layers> per weight, \"\". " +
            "Text style: -STYLE [\"ARCH\",\"arial.ttf\",\"0\",\"\",\"\",\"\",\"\"]. Dimension style 1/4\"=1'-0\", one SETVAR each: DIMSCALE 48, DIMLUNIT 4, DIMDEC 1, " +
            "DIMFRAC 2, DIMZIN 3, DIMTSZ 0.09375 (ticks), DIMTXT 0.09375, DIMTAD 1, DIMTIH 0, DIMTOH 0, DIMEXO 0.0625, DIMEXE 0.125, DIMDLE 0.0625, DIMGAP 0.0625, " +
            "DIMTXSTY ARCH; then -DIMSTYLE [\"S\",\"ARCH-48\"] (ARCH-96 the same with DIMSCALE 96)."),
        new(
            "Room sizes (India)",
            ["room", "rooms", "size", "sizes", "minimum", "minimums", "bylaw", "bylaws", "proportion", "accuracy", "validate", "clear"],
            "validate_plan checks these typical minimum clear sizes (Indian residential practice, never a bylaw; the user's rules change them, e.g. " +
            "remember_rule \"Bedrooms at least 9'x10'\"): bedroom 10'x10', master bedroom 11'x12', living 11'x12', kitchen 7'x10' (counter 2' deep), " +
            "toilet (WC) 4'6x7', bath + WC 5'x7', dining 8'x10', pooja 4'x4', parking 9'x17' per car, passage 3'6 (3' at least), stair flight 3'3 " +
            "(3' at least) with risers 6-7\" (6 1/2\" typical) and treads 10\" or more, doors main 3'6 / rooms 3' / toilets 2'6, windows 1/10 to 1/8 of " +
            "the floor area, walls 9\" outer / 4 1/2\" inner, floor to floor 10' to 10'6. Habitable rooms need a window on an outside wall, toilets a " +
            "ventilator (or a shaft), kitchens an outside window; keep living rooms and bedrooms within 2:1. Sizes are whole inches."),
    ];

    /// <summary>The guides whose keywords appear in <paramref name="query"/> (best first, at most <see cref="MaxPerQuery"/>).</summary>
    public static IReadOnlyList<DraftingGuide> Match(string? query)
    {
        var words = Words(query);
        if (words.Count == 0)
        {
            return [];
        }

        return [.. All
            .Select(g => (Guide: g, Score: g.Keywords.Count(words.Contains) + (words.Contains(g.Name.ToLowerInvariant()) ? 2 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(MaxPerQuery)
            .Select(x => x.Guide)];
    }

    /// <summary>
    /// Adds the guides matching <paramref name="query"/> to a successful recall_knowledge result ("guides" in its data and
    /// a note in its message); with an empty query it lists the guide names. Failed results are returned unchanged.
    /// </summary>
    public static ToolResult AddTo(ToolResult result, string? query)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
        {
            return result;
        }

        var data = result.Data is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject();
        if (Words(query).Count == 0)
        {
            data["builtInGuides"] = new JsonArray([.. All.Select(g => (JsonNode)g.Name)]);
            return result with { Data = data };
        }

        var guides = Match(query);
        if (guides.Count == 0)
        {
            return result;
        }

        data["guides"] = new JsonArray([.. guides.Select(g => (JsonNode)new JsonObject { ["name"] = g.Name, ["text"] = g.Text })]);
        var names = string.Join(", ", guides.Select(g => g.Name));
        var message = string.Create(CultureInfo.InvariantCulture, $"{result.Message} Built-in guide: {names} (the user's rules, style and recipes take precedence).").Trim();
        return result with { Data = data, Message = message };
    }

    private static HashSet<string> Words(string? query)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query))
        {
            return words;
        }

        foreach (var raw in query.ToLowerInvariant().Split([' ', ',', ';', '.', '/', '(', ')', '"', '\'', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(raw);
            if (raw.Contains('-', StringComparison.Ordinal))
            {
                foreach (var part in raw.Split('-', StringSplitOptions.RemoveEmptyEntries))
                {
                    words.Add(part);
                }
            }
        }

        if (words.Contains("site") && words.Contains("plan"))
        {
            words.Add("site plan");
        }

        return words;
    }
}
