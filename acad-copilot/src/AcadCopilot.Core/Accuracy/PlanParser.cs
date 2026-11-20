using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Accuracy;

/// <summary>The result of reading a plan: the plan (null when it could not be built) and the input problems.</summary>
/// <param name="Plan">The plan, or null.</param>
/// <param name="Errors">Input problems with their JSON paths.</param>
public sealed record PlanParseResult(HousePlan? Plan, IReadOnlyList<string> Errors);

/// <summary>
/// Reads the validate_plan "plan" JSON into a <see cref="HousePlan"/>. Lengths are feet-inch strings ("11'4", "9",
/// "30'"; a bare number is inches, as in AutoCAD); points are "x,y" strings in plot coordinates. Every value is snapped to
/// 1/16 inch. Problems are collected with their JSON path instead of stopping at the first one.
/// </summary>
public static class PlanParser
{
    /// <summary>Most floors.</summary>
    public const int MaxFloors = 5;

    /// <summary>Reads <paramref name="plan"/>.</summary>
    public static PlanParseResult Parse(JsonObject plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var reader = new Reader();
        var result = reader.ReadPlan(plan);
        return new PlanParseResult(reader.Errors.Count == 0 ? result : null, reader.Errors);
    }

    /// <summary>Reads a length ("11'4", "9", "4-1/2\"") in inches snapped to 1/16, or null.</summary>
    public static double? Length(string? text) =>
        FeetInches.TryParse(text?.Trim(), out var inches) ? Inch.Snap(inches) : null;

    /// <summary>Reads a point "x,y" (each a length) or null.</summary>
    public static Pt? Point(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().TrimStart('(').TrimEnd(')').Split(',');
        return parts.Length == 2 && Length(parts[0]) is { } x && Length(parts[1]) is { } y ? new Pt(x, y) : null;
    }

    private sealed class Reader
    {
        public List<string> Errors { get; } = [];

        public HousePlan? ReadPlan(JsonObject plan)
        {
            var plot = plan["plot"] as JsonObject;
            if (plot is null)
            {
                Errors.Add("plan.plot: missing (width and depth of the plot).");
            }

            var plotWidth = plot is null ? 0 : Len(plot, "width", "plan.plot", required: true, min: 120) ?? 0;
            var plotDepth = plot is null ? 0 : Len(plot, "depth", "plan.plot", required: true, min: 120) ?? 0;
            var roadSide = plot?["road_side"] is JsonValue road ? Side(road.ToString(), "plan.plot.road_side") : null;

            var setbacks = new Dictionary<string, double>(StringComparer.Ordinal);
            if (plan["setbacks"] is JsonObject sb)
            {
                foreach (var side in new[] { "north", "south", "east", "west" })
                {
                    if (Len(sb, side, "plan.setbacks", required: false, min: 0) is { } value)
                    {
                        setbacks[side] = value;
                    }
                }
            }

            var outer = Len(plan, "outer_wall", "plan", required: false, min: 3) ?? 9;
            var inner = Len(plan, "inner_wall", "plan", required: false, min: 2) ?? 4.5;
            var floorHeight = Len(plan, "floor_height", "plan", required: false, min: 84);
            var outline = plan["outline"] is JsonArray o ? Points(o, "plan.outline") : null;

            var floors = new List<PlanFloor>();
            if (plan["floors"] is not JsonArray floorArray || floorArray.Count == 0)
            {
                Errors.Add("plan.floors: give at least one floor (ground floor first) with its rooms and walls.");
            }
            else if (floorArray.Count > MaxFloors)
            {
                Errors.Add($"plan.floors: at most {MaxFloors} floors.");
            }
            else
            {
                for (var i = 0; i < floorArray.Count; i++)
                {
                    if (floorArray[i] is JsonObject floor)
                    {
                        floors.Add(ReadFloor(floor, i, outline, outer, inner));
                    }
                    else
                    {
                        Errors.Add($"plan.floors[{i}]: must be an object.");
                    }
                }
            }

            return new HousePlan
            {
                Name = Text(plan, "name") ?? "House plan",
                PlotWidth = plotWidth,
                PlotDepth = plotDepth,
                RoadSide = roadSide,
                Setbacks = setbacks,
                OuterWall = outer,
                InnerWall = inner,
                FloorHeight = floorHeight ?? 126,
                FloorHeightGiven = floorHeight is not null,
                Floors = floors,
            };
        }

        private PlanFloor ReadFloor(JsonObject floor, int index, Polygon? planOutline, double outer, double inner)
        {
            var path = $"plan.floors[{index}]";
            var name = Text(floor, "name") ?? (index == 0 ? "Ground floor" : index == 1 ? "First floor" : index == 2 ? "Second floor" : $"Floor {index}");

            var walls = new List<PlanWall>();
            if (floor["walls"] is JsonArray wallArray)
            {
                for (var i = 0; i < wallArray.Count; i++)
                {
                    var wp = $"{path}.walls[{i}]";
                    if (wallArray[i] is not JsonObject w)
                    {
                        Errors.Add(wp + ": must be an object.");
                        continue;
                    }

                    var from = Pnt(w, "from", wp);
                    var to = Pnt(w, "to", wp);
                    var thickness = Len(w, "thickness", wp, required: false, min: 2);
                    var id = Text(w, "id") ?? $"W{i + 1}";
                    if (walls.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
                    {
                        Errors.Add($"{wp}.id: '{id}' is used twice on this floor.");
                    }

                    if (from is { } f && to is { } t)
                    {
                        if (f.DistanceTo(t) < 6)
                        {
                            Errors.Add($"{wp}: the wall from {f.Text()} to {t.Text()} is shorter than 6\".");
                            continue;
                        }

                        walls.Add(new PlanWall { Id = id, From = f, To = t, Thickness = thickness ?? 0, ThicknessGiven = thickness is not null, Path = wp });
                    }
                }
            }

            if (walls.Count == 0)
            {
                Errors.Add($"{path}.walls: give the walls (centreline from/to and thickness) of {name}.");
            }

            var floorOutline = floor["outline"] is JsonArray fo ? Points(fo, path + ".outline") : null;
            var outlineGiven = floorOutline is not null || planOutline is not null;
            var outline = floorOutline ?? planOutline ?? DeriveOutline(walls, outer);

            // Wall thickness defaults: on the outline -> outer, otherwise inner.
            foreach (var wall in walls.Where(w => !w.ThicknessGiven))
            {
                wall.Thickness = outline is not null && IsOnOutline(wall, outline, outer) ? outer : inner;
            }

            var rooms = new List<PlanRoom>();
            if (floor["rooms"] is JsonArray roomArray)
            {
                for (var i = 0; i < roomArray.Count; i++)
                {
                    var rp = $"{path}.rooms[{i}]";
                    if (roomArray[i] is JsonObject r && ReadRoom(r, rp) is { } room)
                    {
                        if (rooms.Any(x => string.Equals(x.Name, room.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            Errors.Add($"{rp}.name: '{room.Name}' is used twice on this floor; give every room its own name (Bedroom 1, Bedroom 2).");
                        }

                        rooms.Add(room);
                    }
                }
            }

            if (rooms.Count == 0)
            {
                Errors.Add($"{path}.rooms: give the rooms of {name}.");
            }

            var doors = ReadOpenings(floor["doors"] as JsonArray, path + ".doors", walls, isDoor: true);
            var windows = ReadOpenings(floor["windows"] as JsonArray, path + ".windows", walls, isDoor: false);
            var stair = floor["stair"] is JsonObject s ? ReadStair(s, path + ".stair") : null;
            var origin = floor["origin"] is JsonValue ov ? PointOrError(ov.ToString(), path + ".origin") : null;

            return new PlanFloor
            {
                Name = name,
                Index = index,
                Outline = outline ?? Polygon.Rect(0, 0, 1, 1),
                OutlineGiven = outlineGiven,
                Origin = origin,
                Rooms = rooms,
                Walls = walls,
                Doors = doors,
                Windows = windows,
                Stair = stair,
            };
        }

        private PlanRoom? ReadRoom(JsonObject r, string rp)
        {
            var name = Text(r, "name");
            if (name is null)
            {
                Errors.Add(rp + ".name: missing.");
                return null;
            }

            var typeText = Text(r, "type");
            var type = RoomTypes.Normalize(typeText) ?? RoomTypes.Normalize(name) ?? RoomTypes.Normalize(FirstWord(name));
            if (type is null)
            {
                if (typeText is not null)
                {
                    Errors.Add($"{rp}.type: '{typeText}' is not a room type. Use one of: {string.Join(", ", RoomTypes.All)}.");
                    return null;
                }

                type = "other";
            }

            Polygon? shape = null;
            if (r["polygon"] is JsonArray polygon)
            {
                shape = Points(polygon, rp + ".polygon");
            }
            else
            {
                var x = Len(r, "x", rp, required: true, min: null);
                var y = Len(r, "y", rp, required: true, min: null);
                var w = Len(r, "width", rp, required: true, min: 12);
                var d = Len(r, "depth", rp, required: true, min: 12);
                if (x is { } xv && y is { } yv && w is { } wv && d is { } dv)
                {
                    shape = Polygon.Rect(xv, yv, wv, dv);
                }
            }

            if (shape is null)
            {
                return null;
            }

            var cars = r["cars"] is JsonValue c && c.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 6) : 1;
            var ventilation = Text(r, "ventilation")?.ToLowerInvariant();
            return new PlanRoom { Name = name, Type = type, Shape = shape, Cars = cars, Ventilation = ventilation, Path = rp };
        }

        private List<PlanOpening> ReadOpenings(JsonArray? array, string path, List<PlanWall> walls, bool isDoor)
        {
            var list = new List<PlanOpening>();
            if (array is null)
            {
                return list;
            }

            for (var i = 0; i < array.Count; i++)
            {
                var op = $"{path}[{i}]";
                if (array[i] is not JsonObject o)
                {
                    Errors.Add(op + ": must be an object.");
                    continue;
                }

                var wallId = Text(o, "wall");
                var wall = wallId is null ? null : walls.FirstOrDefault(w => string.Equals(w.Id, wallId, StringComparison.OrdinalIgnoreCase));
                if (wall is null)
                {
                    Errors.Add(wallId is null
                        ? op + ".wall: missing (the id of the wall it sits in)."
                        : $"{op}.wall: there is no wall '{wallId}' on this floor (ids: {string.Join(", ", walls.Select(w => w.Id))}).");
                }

                var offset = Len(o, "offset", op, required: true, min: null);
                var width = Len(o, "width", op, required: true, min: 12);
                var kind = (Text(o, "kind") ?? (isDoor ? "hinged" : "window")).ToLowerInvariant();
                var defaultSill = isDoor ? 0 : kind == "ventilator" ? 60 : kind == "french" ? 0 : 36;
                var sill = Len(o, "sill", op, required: false, min: 0) ?? defaultSill;
                var head = Len(o, "head", op, required: false, min: 24) ?? 84;
                var tag = Text(o, "tag") ?? (isDoor ? $"D{i + 1}" : kind == "ventilator" ? $"V{i + 1}" : $"W{i + 1}");
                if (head <= sill)
                {
                    Errors.Add($"{op}: head {Inch.Text(head)} must be above the sill {Inch.Text(sill)}.");
                }

                var hinge = Text(o, "hinge")?.ToLowerInvariant();
                if (hinge is not null and not ("start" or "end"))
                {
                    Errors.Add($"{op}.hinge: use \"start\" or \"end\".");
                    hinge = null;
                }

                if (wall is not null && offset is { } off && width is { } wid)
                {
                    list.Add(new PlanOpening
                    {
                        Tag = tag,
                        IsDoor = isDoor,
                        Wall = wall,
                        Offset = off,
                        Width = wid,
                        Kind = kind,
                        SwingInto = Text(o, "swing_into"),
                        Hinge = hinge,
                        Sill = sill,
                        Head = head,
                        Path = op,
                    });
                }
            }

            return list;
        }

        private PlanStair? ReadStair(JsonObject s, string sp)
        {
            var type = (Text(s, "type") ?? "dog_legged").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            Box? box = null;
            if (s["x"] is not null || s["y"] is not null || s["width"] is not null || s["depth"] is not null)
            {
                var x = Len(s, "x", sp, required: true, min: null);
                var y = Len(s, "y", sp, required: true, min: null);
                var w = Len(s, "width", sp, required: true, min: 24);
                var d = Len(s, "depth", sp, required: true, min: 24);
                if (x is { } xv && y is { } yv && w is { } wv && d is { } dv)
                {
                    box = new Box(xv, yv, xv + wv, yv + dv);
                }
            }

            var flight = Len(s, "flight_width", sp, required: true, min: 12);
            var riser = Len(s, "riser", sp, required: true, min: 3);
            var tread = Len(s, "tread", sp, required: true, min: 6);
            var landing = Len(s, "landing", sp, required: false, min: 12);
            var risers = s["risers"] is JsonValue rv && rv.TryGetValue<int>(out var n) ? n : 0;
            if (risers < 2)
            {
                Errors.Add(sp + ".risers: the number of risers floor to floor (e.g. 20) is missing.");
            }

            return flight is null || riser is null || tread is null
                ? null
                : new PlanStair { Type = type, Box = box, FlightWidth = flight.Value, Riser = riser.Value, Tread = tread.Value, Risers = risers, Landing = landing, Path = sp };
        }

        private static Polygon? DeriveOutline(List<PlanWall> walls, double outer)
        {
            if (walls.Count == 0)
            {
                return null;
            }

            var box = Box.Of(walls.SelectMany(w => new[] { w.From, w.To }));
            var half = outer / 2;
            return Polygon.Rect(box.Inflate(half));
        }

        private static bool IsOnOutline(PlanWall wall, Polygon outline, double outer)
        {
            var mid = wall.At(wall.Length / 2);
            foreach (var (a, b) in outline.Edges())
            {
                var edge = (b - a).Unit();
                var parallel = Math.Abs((edge.X * wall.Direction.Y) - (edge.Y * wall.Direction.X)) < 1e-6;
                if (parallel && Polygon.SegmentDistance(mid, a, b) <= outer / 2 + 0.6)
                {
                    return true;
                }
            }

            return false;
        }

        private Polygon? Points(JsonArray array, string path)
        {
            var points = new List<Pt>();
            for (var i = 0; i < array.Count; i++)
            {
                if (PointOrError(array[i]?.ToString(), $"{path}[{i}]") is { } p)
                {
                    points.Add(p);
                }
            }

            if (points.Count != array.Count)
            {
                return null;
            }

            if (points.Count < 3)
            {
                Errors.Add(path + ": a polygon needs at least 3 points.");
                return null;
            }

            var polygon = new Polygon(points);
            if (polygon.Area < 144)
            {
                Errors.Add(path + ": the polygon has (almost) no area; list its corners in order.");
                return null;
            }

            return polygon;
        }

        private Pt? Pnt(JsonObject o, string key, string path)
        {
            if (o[key] is not JsonValue v)
            {
                Errors.Add($"{path}.{key}: missing (a point \"x,y\" in feet-inches, e.g. \"3'4.5,5'4.5\").");
                return null;
            }

            return PointOrError(v.ToString(), $"{path}.{key}");
        }

        private Pt? PointOrError(string? text, string path)
        {
            var p = Point(text);
            if (p is null)
            {
                Errors.Add($"{path}: '{text}' is not a point; write \"x,y\" in feet-inches, e.g. \"12'6,30'\".");
            }

            return p;
        }

        private double? Len(JsonObject o, string key, string path, bool required, double? min)
        {
            var node = o[key];
            if (node is null)
            {
                if (required)
                {
                    Errors.Add($"{path}.{key}: missing (a length such as \"11'4\" or \"9\").");
                }

                return null;
            }

            var text = node.GetValueKind() == JsonValueKind.Number ? node.ToJsonString() : node.ToString();
            var value = Length(text);
            if (value is null)
            {
                Errors.Add($"{path}.{key}: '{text}' is not a length; write feet-inches such as \"11'4\" (a bare number is inches).");
                return null;
            }

            if (min is { } m && value < m)
            {
                Errors.Add(string.Create(CultureInfo.InvariantCulture, $"{path}.{key}: {Inch.Text(value.Value)} is too small (at least {Inch.Text(m)}); bare numbers are inches, feet need ' (\"10'\")."));
                return null;
            }

            return value;
        }

        private string? Side(string text, string path)
        {
            var side = text.Trim().ToLowerInvariant();
            if (side is "north" or "south" or "east" or "west")
            {
                return side;
            }

            Errors.Add($"{path}: '{text}' must be north, south, east or west.");
            return null;
        }

        private static string? Text(JsonObject o, string key) =>
            o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetValue<string>())
                ? v.GetValue<string>().Trim()
                : null;

        private static string FirstWord(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
    }
}
