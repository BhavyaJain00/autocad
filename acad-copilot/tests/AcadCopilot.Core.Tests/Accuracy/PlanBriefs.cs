using System.Text.Json.Nodes;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Tests.Accuracy;

/// <summary>Builds validate_plan JSON from inch numbers (lengths become feet-inch strings, as the AI writes them).</summary>
internal sealed class PlanJson
{
    public PlanJson(string name, double plotWidth, double plotDepth, string roadSide)
    {
        Root = new JsonObject
        {
            ["name"] = name,
            ["plot"] = new JsonObject { ["width"] = L(plotWidth), ["depth"] = L(plotDepth), ["road_side"] = roadSide },
            ["floors"] = new JsonArray(),
        };
    }

    public JsonObject Root { get; }

    public static string L(double inches) => FeetInches.FormatForInput(inches);

    public static string P(double x, double y) => L(x) + "," + L(y);

    public PlanJson Setbacks(double north, double south, double east, double west)
    {
        Root["setbacks"] = new JsonObject { ["north"] = L(north), ["south"] = L(south), ["east"] = L(east), ["west"] = L(west) };
        return this;
    }

    public PlanJson Outline(double x0, double y0, double x1, double y1)
    {
        Root["outline"] = new JsonArray(P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1));
        return this;
    }

    public PlanJson FloorHeight(double inches)
    {
        Root["floor_height"] = L(inches);
        return this;
    }

    public FloorJson Floor(string name)
    {
        var floor = new FloorJson(name);
        Root["floors"]!.AsArray().Add(floor.Root);
        return floor;
    }

    public JsonObject Floor(int index) => Root["floors"]![index]!.AsObject();

    public JsonObject Args(bool? vastu = null)
    {
        var args = new JsonObject { ["plan"] = Root.DeepClone() };
        if (vastu is { } v)
        {
            args["vastu"] = v;
        }

        return args;
    }
}

internal sealed class FloorJson
{
    private readonly Dictionary<string, (double X1, double Y1, double X2, double Y2)> _walls = new(StringComparer.Ordinal);

    public FloorJson(string name)
    {
        Root = new JsonObject
        {
            ["name"] = name,
            ["rooms"] = new JsonArray(),
            ["walls"] = new JsonArray(),
            ["doors"] = new JsonArray(),
            ["windows"] = new JsonArray(),
        };
    }

    public JsonObject Root { get; }

    public FloorJson Origin(double x, double y)
    {
        Root["origin"] = PlanJson.P(x, y);
        return this;
    }

    public FloorJson Room(string name, string type, double x, double y, double width, double depth, string? ventilation = null)
    {
        var room = new JsonObject { ["name"] = name, ["type"] = type, ["x"] = PlanJson.L(x), ["y"] = PlanJson.L(y), ["width"] = PlanJson.L(width), ["depth"] = PlanJson.L(depth) };
        if (ventilation is not null)
        {
            room["ventilation"] = ventilation;
        }

        Root["rooms"]!.AsArray().Add(room);
        return this;
    }

    public FloorJson Wall(string id, double x1, double y1, double x2, double y2, double? thickness = null)
    {
        _walls[id] = (x1, y1, x2, y2);
        var wall = new JsonObject { ["id"] = id, ["from"] = PlanJson.P(x1, y1), ["to"] = PlanJson.P(x2, y2) };
        if (thickness is { } t)
        {
            wall["thickness"] = PlanJson.L(t);
        }

        Root["walls"]!.AsArray().Add(wall);
        return this;
    }

    /// <summary>A door whose near jamb is at <paramref name="at"/> (x for horizontal walls, y for vertical ones).</summary>
    public FloorJson Door(string tag, string wall, double at, double width, string? swingInto = null, string? hinge = null, string? kind = null)
    {
        var door = new JsonObject { ["tag"] = tag, ["wall"] = wall, ["offset"] = PlanJson.L(Offset(wall, at)), ["width"] = PlanJson.L(width) };
        if (swingInto is not null)
        {
            door["swing_into"] = swingInto;
        }

        if (hinge is not null)
        {
            door["hinge"] = hinge;
        }

        if (kind is not null)
        {
            door["kind"] = kind;
        }

        Root["doors"]!.AsArray().Add(door);
        return this;
    }

    public FloorJson Window(string tag, string wall, double at, double width, string? kind = null)
    {
        var window = new JsonObject { ["tag"] = tag, ["wall"] = wall, ["offset"] = PlanJson.L(Offset(wall, at)), ["width"] = PlanJson.L(width) };
        if (kind is not null)
        {
            window["kind"] = kind;
        }

        Root["windows"]!.AsArray().Add(window);
        return this;
    }

    public FloorJson Stair(double x, double y, double width, double depth, double flight, double riser, double tread, int risers, double landing)
    {
        Root["stair"] = new JsonObject
        {
            ["type"] = "dog_legged",
            ["x"] = PlanJson.L(x),
            ["y"] = PlanJson.L(y),
            ["width"] = PlanJson.L(width),
            ["depth"] = PlanJson.L(depth),
            ["flight_width"] = PlanJson.L(flight),
            ["riser"] = PlanJson.L(riser),
            ["tread"] = PlanJson.L(tread),
            ["risers"] = risers,
            ["landing"] = PlanJson.L(landing),
        };
        return this;
    }

    private double Offset(string wall, double at)
    {
        var (x1, y1, x2, y2) = _walls[wall];
        return Math.Abs(y1 - y2) < 1e-9 ? (x2 > x1 ? at - x1 : x1 - at) : (y2 > y1 ? at - y1 : y1 - at);
    }
}

/// <summary>Test briefs: Indian houses in feet, drawn the way the AI is told to plan them.</summary>
internal static class PlanBriefs
{
    /// <summary>
    /// 30'x40' plot, road south, setbacks 5' front / 3' rear and sides, outline 24'x32', two columns (13' and 9'-1 1/2"),
    /// 9" outer and 4 1/2" inner walls, floor height 10'-0", dog-legged stair (20 risers of 6", treads 10").
    /// Ground floor: living, master bedroom, kitchen, stair, attached toilet. First floor (G+1): two bedrooms with toilets.
    /// <paramref name="floors"/> = 1 (G), 2 (G+1) or 3 (G+2, the first floor repeated).
    /// </summary>
    public static PlanJson ThirtyForty(int floors = 2)
    {
        var plan = new PlanJson("30x40 south road G+1", 360, 480, "south")
            .Setbacks(north: 36, south: 60, east: 36, west: 36)
            .Outline(36, 60, 324, 444)
            .FloorHeight(120);

        var ground = Walls(plan.Floor("Ground floor").Origin(0, 0))
            .Room("Living", "living", 45, 69, 156, 180)
            .Room("Bedroom 1", "master_bedroom", 45, 253.5, 156, 181.5)
            .Room("Kitchen", "kitchen", 205.5, 69, 109.5, 120)
            .Room("Stair", "stair", 205.5, 193.5, 109.5, 177)
            .Room("Toilet 1", "bath_wc", 205.5, 375, 109.5, 60)
            .Door("MD", "OS", 49, 42, "Living", "start")
            .Door("D1", "V1", 145, 36, "Kitchen", "end")
            .Door("O1", "V1", 200, 39, kind: "opening")
            .Door("D2", "HA1", 160, 36, "Bedroom 1", "end")
            .Door("D3", "V1", 379, 30, "Toilet 1", "start")
            .Window("W1", "OS", 120, 60)
            .Window("W2", "ON", 80, 60)
            .Window("W3", "OW", 300, 48)
            .Window("W4", "OE", 100, 42)
            .Window("VT1", "OE", 395, 24, "ventilator")
            .Window("W5", "OE", 260, 48)
            .Stair(205.5, 193.5, 109.5, 177, flight: 39, riser: 6, tread: 10, risers: 20, landing: 39);

        for (var i = 1; i < floors; i++)
        {
            var name = i == 1 ? "First floor" : "Second floor";
            var suffix = i == 1 ? string.Empty : " B";
            Walls(plan.Floor(name).Origin(i * 720, 0))
                .Room("Bedroom 2" + suffix, "bedroom", 45, 69, 156, 180)
                .Room("Bedroom 3" + suffix, "master_bedroom", 45, 253.5, 156, 181.5)
                .Room("Toilet 2" + suffix, "bath_wc", 205.5, 69, 109.5, 120)
                .Room("Stair", "stair", 205.5, 193.5, 109.5, 177)
                .Room("Toilet 3" + suffix, "bath_wc", 205.5, 375, 109.5, 60)
                .Door("D4", "V1", 205.5, 36, "Bedroom 2" + suffix, "end")
                .Door("D5", "V1", 261.5, 36, "Bedroom 3" + suffix, "start")
                .Door("D6", "V1", 73, 30, "Toilet 2" + suffix, "start")
                .Door("D7", "V1", 379, 30, "Toilet 3" + suffix, "start")
                .Window("W6", "OS", 90, 60)
                .Window("W7", "OW", 120, 48)
                .Window("W8", "ON", 80, 60)
                .Window("VT2", "OE", 100, 24, "ventilator")
                .Window("VT3", "OE", 395, 24, "ventilator")
                .Window("W9", "OE", 260, 48);
        }

        return plan;

        static FloorJson Walls(FloorJson floor) => floor
            .Wall("OS", 40.5, 64.5, 319.5, 64.5)
            .Wall("OE", 319.5, 64.5, 319.5, 439.5)
            .Wall("ON", 40.5, 439.5, 319.5, 439.5)
            .Wall("OW", 40.5, 64.5, 40.5, 439.5)
            .Wall("V1", 203.25, 64.5, 203.25, 439.5)
            .Wall("HA1", 40.5, 251.25, 203.25, 251.25)
            .Wall("HB1", 203.25, 191.25, 319.5, 191.25)
            .Wall("HB2", 203.25, 372.75, 319.5, 372.75);
    }

    /// <summary>
    /// 30'x50' plot, road north, front setback 15' with the car parking (9'x17') in it, rear and west 3', east 0. Outline
    /// 27'x29'; living and kitchen at the front, a 3'-6" passage to the master bedroom, bedroom 2 and the common toilet.
    /// Single floor, floor height not given.
    /// </summary>
    public static PlanJson ThirtyFifty()
    {
        var plan = new PlanJson("30x50 north road 2BHK", 360, 600, "north")
            .Setbacks(north: 180, south: 36, east: 0, west: 36)
            .Outline(36, 36, 360, 384);
        plan.Floor("Ground floor")
            .Wall("OS", 40.5, 40.5, 355.5, 40.5)
            .Wall("ON", 40.5, 379.5, 355.5, 379.5)
            .Wall("OW", 40.5, 40.5, 40.5, 379.5)
            .Wall("OE", 355.5, 40.5, 355.5, 379.5)
            .Wall("X1", 40.5, 228.75, 355.5, 228.75, 4.5)
            .Wall("VN", 230.25, 228.75, 230.25, 379.5)
            .Wall("VA", 179.25, 40.5, 179.25, 228.75)
            .Wall("VB", 225.75, 40.5, 225.75, 228.75)
            .Wall("HB", 225.75, 167.25, 355.5, 167.25)
            .Room("Living", "living", 45, 231, 183, 144)
            .Room("Kitchen", "kitchen", 232.5, 231, 118.5, 144)
            .Room("Master Bedroom", "master_bedroom", 45, 45, 132, 181.5)
            .Room("Passage", "passage", 181.5, 45, 42, 181.5)
            .Room("Bedroom 2", "bedroom", 228, 45, 123, 120)
            .Room("Common Toilet", "toilet", 228, 169.5, 123, 57)
            .Room("Parking", "parking", 230, 390, 108, 204)
            .Door("MD", "ON", 182, 42, "Living", "end")
            .Door("D1", "VN", 235, 36, "Kitchen", "start")
            .Door("O1", "X1", 181.5, 42, kind: "opening")
            .Door("D2", "VA", 186, 36, "Master Bedroom", "end")
            .Door("D3", "VB", 125, 36, "Bedroom 2", "end")
            .Door("D4", "VB", 174, 30, "Common Toilet", "start")
            .Window("W1", "ON", 60, 60)
            .Window("W2", "OW", 280, 48)
            .Window("W3", "ON", 270, 48)
            .Window("W4", "OE", 290, 36)
            .Window("W5", "OW", 90, 48)
            .Window("W6", "OS", 70, 60)
            .Window("W7", "OS", 260, 48)
            .Window("W8", "OE", 70, 48)
            .Window("VT1", "OE", 185, 24, "ventilator");
        return plan;
    }

    /// <summary>Validates with the default rules.</summary>
    public static PlanValidation Validate(PlanJson plan, PlanRules? rules = null) =>
        PlanValidator.Validate(plan.Root, rules ?? PlanRules.Default);

    /// <summary>Validates and parses for further use.</summary>
    public static HousePlan Parse(PlanJson plan) => PlanParser.Parse(plan.Root).Plan ?? throw new InvalidOperationException("plan did not parse");

    /// <summary>The findings as text for assertion messages.</summary>
    public static string Describe(PlanValidation validation) => string.Join("\n", validation.Findings.Select(f => f.Text()));

    /// <summary>The JSON object at a path of the plan (for changes in tests).</summary>
    public static JsonObject Item(PlanJson plan, int floor, string list, string key, string value) =>
        plan.Floor(floor)[list]!.AsArray().Select(n => n!.AsObject()).First(o => o[key]?.ToString() == value);
}
