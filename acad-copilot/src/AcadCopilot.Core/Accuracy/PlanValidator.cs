using System.Globalization;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Accuracy;

/// <summary>The result of validate_plan.</summary>
public sealed class PlanValidation
{
    /// <summary>The plan (null when it could not be read).</summary>
    public HousePlan? Plan { get; init; }

    /// <summary>Errors, warnings and hints, errors first.</summary>
    public required IReadOnlyList<PlanFinding> Findings { get; init; }

    /// <summary>The clean dimension schedule (sizes, areas, openings, stair, dimension chains).</summary>
    public required JsonObject Schedule { get; init; }

    /// <summary>A short Markdown summary for the user (room table, areas, checks).</summary>
    public required string Summary { get; init; }

    /// <summary>Number of errors.</summary>
    public int Errors => Findings.Count(f => f.Severity == Severity.Error);

    /// <summary>Number of warnings.</summary>
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);

    /// <summary>True when there are no errors.</summary>
    public bool IsReady => Errors == 0 && Plan is not null;
}

/// <summary>
/// validate_plan: pure checks of a structured house plan before anything is drawn (tiling, sizes, access, doors,
/// light and air, stair, stacking, whole inches, setbacks, areas). The sizes come from <see cref="PlanRules"/> (typical
/// Indian residential defaults plus the user's rules), never from a bylaw.
/// </summary>
public static class PlanValidator
{
    /// <summary>Most gap / overlap regions reported per floor.</summary>
    public const int MaxRegions = 8;

    /// <summary>Reads and validates the "plan" JSON.</summary>
    public static PlanValidation Validate(JsonObject planJson, PlanRules rules)
    {
        ArgumentNullException.ThrowIfNull(planJson);
        var parsed = PlanParser.Parse(planJson);
        if (parsed.Plan is null)
        {
            var findings = parsed.Errors.Select(e => new PlanFinding(Severity.Error, "input", string.Empty, e)).ToList();
            return new PlanValidation
            {
                Findings = findings,
                Schedule = new JsonObject(),
                Summary = "The plan could not be read: " + string.Join(" ", parsed.Errors.Take(5)),
            };
        }

        return Validate(parsed.Plan, rules);
    }

    /// <summary>Validates a plan.</summary>
    public static PlanValidation Validate(HousePlan plan, PlanRules rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        var findings = new FindingList();
        var analyses = plan.Floors.Select(f => new FloorAnalysis(f, plan.OuterWall)).ToList();

        CheckPlot(plan, rules, findings);
        foreach (var analysis in analyses)
        {
            CheckFloor(analysis, rules, findings);
        }

        CheckStair(plan, rules, findings);
        for (var i = 1; i < analyses.Count; i++)
        {
            CheckStacking(analyses[i - 1], analyses[i], findings);
        }

        if (rules.Vastu && analyses.Count > 0)
        {
            VastuHints(analyses[0], findings);
        }

        var schedule = PlanSchedule.Build(plan, analyses);
        var items = findings.Items;
        var summary = PlanSchedule.Summary(plan, schedule, items, rules);
        return new PlanValidation { Plan = plan, Findings = items, Schedule = schedule, Summary = summary };
    }

    // -----------------------------------------------------------------------------------------------------------
    // Plot, setbacks, floor height
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckPlot(HousePlan plan, PlanRules rules, FindingList findings)
    {
        if (!plan.FloorHeightGiven)
        {
            findings.Hint("assumed", string.Empty, $"Floor to floor height not given: assumed {Inch.Text(plan.FloorHeight)}.");
        }
        else if (plan.FloorHeight < rules.FloorHeightMin - 0.01 || plan.FloorHeight > rules.FloorHeightMax + 0.01)
        {
            findings.Warning("floor_height", string.Empty, $"Floor to floor height {Inch.Text(plan.FloorHeight)} is outside the usual {Inch.Text(rules.FloorHeightMin)} to {Inch.Text(rules.FloorHeightMax)}; confirm it with the user.");
        }

        if (plan.OuterWall + 0.01 < rules.OuterWall)
        {
            findings.Warning("wall_thickness", string.Empty, $"Outer walls {Inch.Text(plan.OuterWall)} are thinner than the usual {Inch.Text(rules.OuterWall)}.");
        }

        if (plan.InnerWall + 0.01 < rules.InnerWall)
        {
            findings.Warning("wall_thickness", string.Empty, $"Inner walls {Inch.Text(plan.InnerWall)} are thinner than the usual {Inch.Text(rules.InnerWall)}.");
        }

        if (plan.Floors.Count > 1 && plan.Floors.All(f => f.Rooms.All(r => r.Type != "stair")) && plan.Floors.All(f => f.Stair is null))
        {
            findings.Error("stair_missing", string.Empty, $"The plan has {plan.Floors.Count} floors but no stair: add a stair room (type stair) on every floor and the stair numbers on the ground floor.");
        }

        var plot = plan.Plot;
        foreach (var floor in plan.Floors)
        {
            var box = floor.Outline.Bounds;
            if (box.MinX < -0.01 || box.MinY < -0.01 || box.MaxX > plan.PlotWidth + 0.01 || box.MaxY > plan.PlotDepth + 0.01)
            {
                findings.Error("outside_plot", floor.Name, $"The building outline ({box.Text()}) goes outside the {Inch.Size(plan.PlotWidth, plan.PlotDepth)} plot (plot corner 0,0 = south-west).");
                continue;
            }

            var actual = Setbacks(plan, box);
            foreach (var (side, required) in plan.Setbacks)
            {
                if (actual[side] + 0.01 < required)
                {
                    findings.Error("setback", floor.Name, $"The building is {Inch.Text(actual[side])} from the {side} boundary; the setback given is {Inch.Text(required)} (assumed values: check local bylaws). Move or shrink the outline.");
                }
            }

            foreach (var room in floor.Rooms.Where(r => RoomTypes.IsOutdoor(r.Type) || r.Type == "stair"))
            {
                if (room.Shape.Points.Any(p => !plot.Contains(p, 0.01)))
                {
                    findings.Error("outside_plot", floor.Name, $"{room.Name} ({room.Shape.Bounds.Text()}) goes outside the plot.");
                }
            }
        }
    }

    /// <summary>Actual distances from the outline box to the plot boundaries.</summary>
    public static Dictionary<string, double> Setbacks(HousePlan plan, Box box) => new(StringComparer.Ordinal)
    {
        ["north"] = plan.PlotDepth - box.MaxY,
        ["south"] = box.MinY,
        ["east"] = plan.PlotWidth - box.MaxX,
        ["west"] = box.MinX,
    };

    // -----------------------------------------------------------------------------------------------------------
    // One floor
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckFloor(FloorAnalysis analysis, PlanRules rules, FindingList findings)
    {
        var floor = analysis.Floor;
        CheckSnapping(floor, findings);
        CheckTiling(analysis, findings);
        CheckRoomSizes(floor, rules, findings);
        var neighbours = Neighbours(analysis, findings);
        CheckOpenings(analysis, rules, findings);
        CheckReachability(analysis, neighbours, findings);
        CheckLightAndAir(analysis, rules, findings);
    }

    private static void CheckSnapping(PlanFloor floor, FindingList findings)
    {
        var name = floor.Name;
        foreach (var room in floor.Rooms)
        {
            if (room.Shape.Points.Any(p => !Inch.IsMultiple(p.X, 0.25) || !Inch.IsMultiple(p.Y, 0.25)))
            {
                findings.Error("snap", name, $"{room.Name}: corners must be on whole or half inches (1/4\" at most), e.g. {Inch.Text(Math.Round(room.Shape.Bounds.MinX * 2) / 2)}.");
            }

            if (!room.Shape.IsAxisRectangle)
            {
                continue;
            }

            foreach (var (label, value) in new[] { ("width", room.Width), ("depth", room.Depth) })
            {
                if (!Inch.IsMultiple(value, 0.5))
                {
                    findings.Error("snap", name, $"{room.Name} {label} {Inch.Text(value)} is not on whole inches: use {Inch.Text(Math.Round(value))}.");
                }
                else if (!Inch.IsMultiple(value, 1))
                {
                    findings.Hint("half_inch", name, $"{room.Name} {label} is {Inch.Text(value)} (a half inch from the 4 1/2\" walls); fine, or adjust a neighbour to make it whole.");
                }
            }
        }

        foreach (var wall in floor.Walls)
        {
            if (!Inch.IsMultiple(wall.From.X, 0.25) || !Inch.IsMultiple(wall.From.Y, 0.25) || !Inch.IsMultiple(wall.To.X, 0.25) || !Inch.IsMultiple(wall.To.Y, 0.25))
            {
                findings.Error("snap", name, $"Wall {wall.Id}: the centreline {wall.From.Text()} to {wall.To.Text()} is not on 1/4\" steps; snap it.");
            }

            if (!wall.IsHorizontal && !wall.IsVertical)
            {
                findings.Hint("diagonal", name, $"Wall {wall.Id} is not horizontal or vertical: the gap and overlap check is approximate around it.");
            }
        }

        foreach (var o in floor.Doors.Concat(floor.Windows))
        {
            if (!Inch.IsMultiple(o.Width, 1))
            {
                findings.Error("snap", name, $"{o.Tag}: width {Inch.Text(o.Width)} is not a whole inch: use {Inch.Text(Math.Round(o.Width))}.");
            }

            if (!Inch.IsMultiple(o.Offset, 0.25))
            {
                findings.Error("snap", name, $"{o.Tag}: offset {Inch.Text(o.Offset)} is not on 1/4\" steps.");
            }

            if (!o.IsDoor && (!Inch.IsMultiple(o.Sill, 1) || !Inch.IsMultiple(o.Head, 1)))
            {
                findings.Error("snap", name, $"{o.Tag}: sill {Inch.Text(o.Sill)} and head {Inch.Text(o.Head)} must be whole inches.");
            }
        }
    }

    private sealed class Region
    {
        public Box Box { get; set; }

        public double Area { get; set; }
    }

    private static void CheckTiling(FloorAnalysis analysis, FindingList findings)
    {
        var floor = analysis.Floor;
        var name = floor.Name;
        var xs = new List<double>();
        var ys = new List<double>();
        void Add(Pt p)
        {
            xs.Add(p.X);
            ys.Add(p.Y);
        }

        foreach (var p in floor.Outline.Points)
        {
            Add(p);
        }

        foreach (var room in floor.Rooms)
        {
            foreach (var p in room.Shape.Points)
            {
                Add(p);
            }
        }

        foreach (var wall in floor.Walls)
        {
            foreach (var p in wall.Body().Points)
            {
                Add(p);
            }
        }

        var gx = Unique(xs);
        var gy = Unique(ys);
        if (gx.Count < 2 || gy.Count < 2 || gx.Count * gy.Count > 250_000)
        {
            return;
        }

        var nx = gx.Count - 1;
        var ny = gy.Count - 1;
        var gap = new bool[nx, ny];
        var pairs = new Dictionary<(string, string), Region>();
        var roomWall = new Dictionary<(string, string), Region>();
        var outsideRooms = new Dictionary<string, Region>(StringComparer.Ordinal);
        var outsideWalls = new Dictionary<string, Region>(StringComparer.Ordinal);
        var covering = new List<PlanRoom>();
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++)
            {
                var cell = new Box(gx[i], gy[j], gx[i + 1], gy[j + 1]);
                if (cell.Width < 1e-4 || cell.Height < 1e-4)
                {
                    continue;
                }

                var c = cell.Center;
                var inOutline = floor.Outline.Contains(c, 0);
                covering.Clear();
                covering.AddRange(floor.Rooms.Where(r => r.Shape.Contains(c, 0)));
                var wall = floor.Walls.FirstOrDefault(w => w.Contains(c, 0));
                if (!inOutline)
                {
                    foreach (var room in covering.Where(r => !RoomTypes.IsOutdoor(r.Type) && r.Type != "stair"))
                    {
                        Grow(outsideRooms, room.Name, cell);
                    }

                    if (wall is not null && covering.Count == 0)
                    {
                        Grow(outsideWalls, wall.Id, cell);
                    }

                    continue;
                }

                if (covering.Count == 0 && wall is null)
                {
                    gap[i, j] = true;
                    continue;
                }

                for (var a = 0; a < covering.Count; a++)
                {
                    for (var b = a + 1; b < covering.Count; b++)
                    {
                        Grow(pairs, (covering[a].Name, covering[b].Name), cell);
                    }
                }

                if (wall is not null)
                {
                    foreach (var room in covering)
                    {
                        var hit = floor.Walls.First(w => w.Contains(c, 0));
                        Grow(roomWall, (room.Name, hit.Id), cell);
                    }
                }
            }
        }

        foreach (var region in GapRegions(gap, gx, gy).OrderByDescending(r => r.Area).Take(MaxRegions))
        {
            var near = floor.Rooms.Where(r => r.Shape.Bounds.Inflate(1).Overlaps(region.Box, 0)).Select(r => r.Name).ToList();
            var between = near.Count > 0 ? " next to " + string.Join(", ", near) : string.Empty;
            findings.Error("gap", name, $"Gap of {Inch.Size(region.Box.Width, region.Box.Height)} ({Inch.Area(region.Area)}) at {region.Box.Text()}{between}: no room and no wall covers it. Rooms must fill the floor exactly: widen a room, move a wall or add the missing room.");
        }

        foreach (var ((a, b), region) in pairs.OrderByDescending(p => p.Value.Area).Take(MaxRegions))
        {
            findings.Error("overlap", name, $"{a} and {b} overlap by {Inch.Size(region.Box.Width, region.Box.Height)} at {region.Box.Text()}: make one end where the other begins (with the wall between them).");
        }

        foreach (var ((room, wallId), region) in roomWall.OrderByDescending(p => p.Value.Area).Take(MaxRegions))
        {
            var depth = Math.Min(region.Box.Width, region.Box.Height);
            findings.Error("room_in_wall", name, $"{room} runs {Inch.Text(depth)} into wall {wallId} ({region.Box.Text()}): a room's size is its clear size, from wall face to wall face.");
        }

        foreach (var (room, region) in outsideRooms)
        {
            findings.Error("outside_outline", name, $"{room} lies partly outside the building outline ({region.Box.Text()}).");
        }

        foreach (var (wallId, region) in outsideWalls)
        {
            if (Math.Min(region.Box.Width, region.Box.Height) > 0.3)
            {
                findings.Error("wall_outside", name, $"Wall {wallId} sticks out of the building outline ({region.Box.Text()}): outer walls lie inside the outline, their outer face on it.");
            }
        }
    }

    private static void Grow<TKey>(Dictionary<TKey, Region> map, TKey key, Box cell)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var region))
        {
            region.Box = region.Box.Union(cell);
            region.Area += cell.Area;
        }
        else
        {
            map[key] = new Region { Box = cell, Area = cell.Area };
        }
    }

    private static List<Region> GapRegions(bool[,] gap, List<double> gx, List<double> gy)
    {
        var nx = gap.GetLength(0);
        var ny = gap.GetLength(1);
        var seen = new bool[nx, ny];
        var regions = new List<Region>();
        var stack = new Stack<(int, int)>();
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++)
            {
                if (!gap[i, j] || seen[i, j])
                {
                    continue;
                }

                var region = new Region { Box = new Box(gx[i], gy[j], gx[i + 1], gy[j + 1]) };
                stack.Push((i, j));
                seen[i, j] = true;
                while (stack.Count > 0)
                {
                    var (a, b) = stack.Pop();
                    var cell = new Box(gx[a], gy[b], gx[a + 1], gy[b + 1]);
                    region.Box = region.Box.Union(cell);
                    region.Area += cell.Area;
                    foreach (var (da, db) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var x = a + da;
                        var y = b + db;
                        if (x >= 0 && y >= 0 && x < nx && y < ny && gap[x, y] && !seen[x, y])
                        {
                            seen[x, y] = true;
                            stack.Push((x, y));
                        }
                    }
                }

                if (region.Area >= 0.5 && Math.Min(region.Box.Width, region.Box.Height) >= Inch.Grid - 1e-6)
                {
                    regions.Add(region);
                }
            }
        }

        return regions;
    }

    private static List<double> Unique(List<double> values)
    {
        values.Sort();
        var result = new List<double>();
        foreach (var v in values)
        {
            if (result.Count == 0 || v - result[^1] > 1e-4)
            {
                result.Add(v);
            }
        }

        return result;
    }

    private static void CheckRoomSizes(PlanFloor floor, PlanRules rules, FindingList findings)
    {
        var name = floor.Name;
        foreach (var room in floor.Rooms)
        {
            var size = room.Shape.IsAxisRectangle ? Inch.Size(room.Width, room.Depth) : $"about {Inch.Size(room.Width, room.Depth)} (polygon, {Inch.Area(room.Shape.Area)})";
            if (room.Type is "passage" or "lobby")
            {
                if (room.Short + 0.01 < rules.PassageAbsolute)
                {
                    findings.Error("passage_narrow", name, $"{room.Name} is only {Inch.Text(room.Short)} wide; a passage needs at least {Inch.Text(rules.PassageAbsolute)} ({Inch.Text(rules.PassageWidth)} recommended).");
                }
                else if (room.Short + 0.01 < rules.PassageWidth)
                {
                    findings.Warning("passage_narrow", name, $"{room.Name} is {Inch.Text(room.Short)} wide; {Inch.Text(rules.PassageWidth)} or more is recommended.");
                }

                continue;
            }

            if (room.Type == "parking")
            {
                var need = new RoomMinimum(rules.RoomMinimums["parking"].Short, rules.RoomMinimums["parking"].Long);
                var cars = room.Cars;
                if (room.Short + 0.01 < need.Short || room.Long + 0.01 < need.Long || room.Shape.Area + 1 < cars * need.Short * need.Long)
                {
                    findings.Error("room_small", name, $"{room.Name} is {size}; {cars} car{(cars > 1 ? "s need" : " needs")} {Inch.Size(need.Short, need.Long)} each ({Inch.Area(cars * need.Short * need.Long)}).");
                }

                continue;
            }

            if (rules.RoomMinimums.TryGetValue(room.Type, out var min))
            {
                if (room.Short + 0.01 < min.Short || room.Long + 0.01 < min.Long)
                {
                    findings.Error("room_small", name, $"{room.Name} ({room.Type.Replace('_', ' ')}) is {size}; the minimum clear size is {min.Text()} (typical value, the user's rules can change it). Enlarge it, or confirm with the user and remember_rule a smaller minimum.");
                }
            }

            if ((RoomTypes.IsHabitable(room.Type) || RoomTypes.IsSemiHabitable(room.Type)) && room.Short > 0 && room.Long / room.Short > rules.MaxAspect + 0.01)
            {
                findings.Warning("proportion", name, $"{room.Name} is long and thin ({size}, {(room.Long / room.Short).ToString("0.0", CultureInfo.InvariantCulture)}:1); keep living rooms and bedrooms within {rules.MaxAspect.ToString("0.#", CultureInfo.InvariantCulture)}:1.");
            }
        }
    }

    /// <summary>Open connections (rooms touching with no wall between) and rooms open to the outside, by sampling every room edge.</summary>
    private static Dictionary<string, Dictionary<string, double>> Neighbours(FloorAnalysis analysis, FindingList findings)
    {
        var floor = analysis.Floor;
        var map = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var room in floor.Rooms)
        {
            var lengths = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (a, b) in room.Shape.Edges())
            {
                var length = a.DistanceTo(b);
                var dir = (b - a).Unit();
                var outward = new Pt(dir.Y, -dir.X);
                const double step = 3;
                for (var t = step / 2; t < length; t += step)
                {
                    var hit = analysis.At(a + (dir * t) + (outward * 0.75), room.Name);
                    var key = hit.IsRoom ? hit.Name : hit.IsOutside ? "#outside" : null;
                    if (key is not null)
                    {
                        lengths[key] = lengths.GetValueOrDefault(key) + Math.Min(step, length - t + (step / 2));
                    }
                }
            }

            map[room.Name] = lengths;
            if (!RoomTypes.IsOutdoor(room.Type) && room.Type != "stair" && lengths.GetValueOrDefault("#outside") >= 12)
            {
                findings.Error("open_side", floor.Name, $"{room.Name} has no wall over {Inch.Text(lengths["#outside"])} of its boundary: it is open to the outside. Add the outer wall, or make it an outdoor space (parking, sitout, balcony).");
            }
        }

        return map;
    }

    private static void CheckOpenings(FloorAnalysis analysis, PlanRules rules, FindingList findings)
    {
        var floor = analysis.Floor;
        var name = floor.Name;
        foreach (var group in analysis.Openings.GroupBy(o => o.Opening.Wall))
        {
            var wall = group.Key;
            var list = group.OrderBy(o => o.Opening.Offset).ToList();
            var junctions = analysis.Junctions(wall);
            foreach (var info in list)
            {
                var o = info.Opening;
                if (o.Offset < -0.01 || o.Offset + o.Width > wall.Length + 0.01)
                {
                    findings.Error("opening_off_wall", name, $"{o.Tag} ({Inch.Text(o.Offset)} + {Inch.Text(o.Width)}) runs past the end of wall {wall.Id} ({Inch.Text(wall.Length)} long, measured on its centreline from {wall.From.Text()}).");
                    continue;
                }

                foreach (var (from, to, id) in junctions)
                {
                    if (Math.Min(to, o.Offset + o.Width) - Math.Max(from, o.Offset) > Inch.Grid)
                    {
                        findings.Error("opening_in_junction", name, $"{o.Tag} on wall {wall.Id} cuts into wall {id} (it meets {wall.Id} from {Inch.Text(Math.Max(0, from))} to {Inch.Text(to)} along the centreline): move it along the wall.");
                    }
                }
            }

            for (var i = 1; i < list.Count; i++)
            {
                var a = list[i - 1].Opening;
                var b = list[i].Opening;
                if (b.Offset < a.Offset + a.Width - Inch.Grid)
                {
                    findings.Error("opening_overlap", name, $"{a.Tag} and {b.Tag} overlap on wall {wall.Id} ({a.Tag} ends at {Inch.Text(a.Offset + a.Width)}, {b.Tag} starts at {Inch.Text(b.Offset)}).");
                }
            }
        }

        foreach (var info in analysis.Openings)
        {
            var o = info.Opening;
            var left = info.Left;
            var right = info.Right;
            if (!left.IsRoom && !left.IsOutside || !right.IsRoom && !right.IsOutside)
            {
                findings.Error("opening_nowhere", name, $"{o.Tag} on wall {o.Wall.Id} opens onto {left.Describe()} on one side and {right.Describe()} on the other: an opening must join two rooms, or a room and the outside.");
                continue;
            }

            if (left.IsOutside && right.IsOutside)
            {
                findings.Error("opening_nowhere", name, $"{o.Tag} on wall {o.Wall.Id} has the outside on both sides.");
                continue;
            }

            if (o.IsDoor)
            {
                CheckDoor(analysis, info, rules, findings);
            }
        }

        var swings = analysis.Openings.Where(i => i.Swings.Count > 0).ToList();
        for (var i = 0; i < swings.Count; i++)
        {
            for (var j = i + 1; j < swings.Count; j++)
            {
                var a = swings[i];
                var b = swings[j];
                var clash = a.Swings.Any(sa => b.Swings.Any(sb => sa.Samples().Any(p => sb.Contains(p)) || sb.Samples().Any(p => sa.Contains(p))));
                if (clash)
                {
                    findings.Error("door_clash", name, $"The swings of {a.Opening.Tag} and {b.Opening.Tag} clash: move one door, change its hinge side or make it a sliding door.");
                }
            }
        }
    }

    private static void CheckDoor(FloorAnalysis analysis, OpeningInfo info, PlanRules rules, FindingList findings)
    {
        var floor = analysis.Floor;
        var name = floor.Name;
        var o = info.Opening;
        var types = new[] { info.Left, info.Right }.Where(s => s.IsRoom).Select(s => floor.Room(s.Name)!.Type).ToList();
        var toOutside = info.Left.IsOutside || info.Right.IsOutside;
        if (o.Width + 0.01 < 24)
        {
            findings.Error("door_narrow", name, $"{o.Tag} is only {Inch.Text(o.Width)} wide: nobody can pass.");
        }
        else if (toOutside && !types.Any(RoomTypes.IsOutdoor) && o.Width + 0.01 < rules.MainDoor)
        {
            findings.Warning("door_narrow", name, $"{o.Tag} is an entrance door of {Inch.Text(o.Width)}; the main door is usually {Inch.Text(rules.MainDoor)}.");
        }
        else if (types.Any(RoomTypes.IsToilet))
        {
            if (o.Width + 0.01 < rules.ToiletDoor)
            {
                findings.Warning("door_narrow", name, $"{o.Tag} (toilet) is {Inch.Text(o.Width)}; toilet doors are usually {Inch.Text(rules.ToiletDoor)}.");
            }
        }
        else if (types.Any(t => t is "bedroom" or "master_bedroom" or "kitchen" or "study") && o.Width + 0.01 < rules.RoomDoor && o.Kind != "opening")
        {
            findings.Warning("door_narrow", name, $"{o.Tag} is {Inch.Text(o.Width)}; room doors are usually {Inch.Text(rules.RoomDoor)}.");
        }

        if (!o.Swings)
        {
            return;
        }

        if (o.SwingInto is { } into && info.SwingSide == 0)
        {
            findings.Error("swing_room", name, $"{o.Tag} swings into '{into}', but the door is between {info.Between}.");
            return;
        }

        if (info.SwingRoom is null)
        {
            return;
        }

        var swingRoom = floor.Room(info.SwingRoom)!;
        var other = info.SwingSide > 0 ? info.Right : info.Left;
        if (other.IsRoom && RoomTypes.IsCirculation(swingRoom.Type) && !RoomTypes.IsCirculation(floor.Room(other.Name)!.Type))
        {
            findings.Warning("swing_into_passage", name, $"{o.Tag} swings into {swingRoom.Name} and blocks it; doors normally open into the room ({other.Name}).");
        }

        foreach (var swing in info.Swings)
        {
            var hitWalls = new SortedSet<string>(StringComparer.Ordinal);
            var leaves = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in swing.Samples())
            {
                var hit = analysis.At(p);
                if (hit.Kind == "wall")
                {
                    hitWalls.Add(hit.Name);
                }
                else if (!(hit.IsRoom && hit.Name == swingRoom.Name))
                {
                    leaves.Add(hit.Describe());
                }
            }

            if (hitWalls.Count > 0)
            {
                findings.Error("swing_hits_wall", name, $"The swing of {o.Tag} ({Inch.Text(swing.Radius)} leaf, hinge at {swing.Hinge.Text()}) hits wall {string.Join(", ", hitWalls)} in {swingRoom.Name}: move the door, swap the hinge side or use a narrower leaf.");
            }
            else if (leaves.Count > 0)
            {
                findings.Error("swing_leaves_room", name, $"The swing of {o.Tag} does not fit in {swingRoom.Name}: it reaches {string.Join(", ", leaves)}.");
            }

            if (floor.Stair?.Box is { } stairBox && swingRoom.Type != "stair" && swing.Samples().Any(p => stairBox.Contains(p, -0.25)))
            {
                findings.Error("swing_stair", name, $"The swing of {o.Tag} sweeps over the stair.");
            }
        }

        if (info.HingeUsed is "start" or "end")
        {
            var along = info.HingeUsed == "start" ? o.Offset : o.Offset + o.Width;
            var distance = analysis.DistanceToCorner(o.Wall, along, info.HingeUsed == "start" ? -1 : 1);
            if (distance > 12.01)
            {
                findings.Warning("hinge_far", name, $"The hinge of {o.Tag} is {Inch.Text(distance)} from the nearest corner; put the hinge within 1'-0\" of a corner so the open leaf rests against a wall.");
            }
        }
    }

    private static void CheckReachability(FloorAnalysis analysis, Dictionary<string, Dictionary<string, double>> neighbours, FindingList findings)
    {
        var floor = analysis.Floor;
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["#outside"] = [] };
        foreach (var room in floor.Rooms)
        {
            graph[room.Name] = [];
        }

        void Link(string a, string b)
        {
            graph[a].Add(b);
            graph[b].Add(a);
        }

        foreach (var info in analysis.Openings.Where(i => i.Opening.IsDoor))
        {
            var a = info.Left.IsRoom ? info.Left.Name : info.Left.IsOutside ? "#outside" : null;
            var b = info.Right.IsRoom ? info.Right.Name : info.Right.IsOutside ? "#outside" : null;
            if (a is not null && b is not null && a != b)
            {
                Link(a, b);
            }
        }

        foreach (var (room, lengths) in neighbours)
        {
            foreach (var (other, length) in lengths)
            {
                if (length >= 29.9 && (other != "#outside" || RoomTypes.IsOutdoor(floor.Room(room)!.Type) || floor.Room(room)!.Type == "stair"))
                {
                    Link(room, other);
                }
            }
        }

        var roots = new List<string>();
        if (floor.Index == 0)
        {
            roots.Add("#outside");
        }
        else
        {
            roots.AddRange(floor.Rooms.Where(r => r.Type == "stair").Select(r => r.Name));
            if (roots.Count == 0)
            {
                findings.Error("stair_missing", floor.Name, $"{floor.Name} has no stair room (type stair), so nothing on it can be reached.");
                return;
            }
        }

        var seen = new HashSet<string>(roots, StringComparer.Ordinal);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in graph[current])
            {
                // Upper floors: the open air is not a way in.
                if (next == "#outside" && floor.Index > 0)
                {
                    continue;
                }

                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        foreach (var room in floor.Rooms.Where(r => !seen.Contains(r.Name) && !RoomTypes.NeedsNoAccess(r.Type)))
        {
            var from = floor.Index == 0 ? "the entrance" : "the stair";
            findings.Error("unreachable", floor.Name, $"{room.Name} cannot be reached from {from} through doors or open sides: add a door (e.g. from a passage or the living room).");
        }

        if (floor.Index == 0 && !analysis.Openings.Any(i => i.Opening.IsDoor && (i.Left.IsOutside || i.Right.IsOutside)) &&
            !neighbours.Any(n => n.Value.ContainsKey("#outside") && RoomTypes.IsOutdoor(floor.Room(n.Key)!.Type)))
        {
            findings.Error("no_entrance", floor.Name, "The ground floor has no entrance: add the main door in an outer wall.");
        }

        // Passing through a bedroom or toilet to reach another non-attached room is poor planning.
        foreach (var room in floor.Rooms.Where(r => !RoomTypes.NeedsNoAccess(r.Type) && seen.Contains(r.Name)))
        {
            if (RoomTypes.IsToilet(room.Type) || room.Type is "dress" or "store" or "balcony" or "pooja")
            {
                continue;
            }

            if (!Reachable(graph, roots, room.Name, avoid: n => n != room.Name && floor.Room(n) is { } r && r.Type is "bedroom" or "master_bedroom" or "toilet" or "bath_wc", floor.Index))
            {
                findings.Warning("through_bedroom", floor.Name, $"{room.Name} can only be reached through a bedroom or toilet; give it its own access from a passage or the living room.");
            }
        }
    }

    private static bool Reachable(Dictionary<string, HashSet<string>> graph, List<string> roots, string target, Func<string, bool> avoid, int floorIndex)
    {
        var seen = new HashSet<string>(roots, StringComparer.Ordinal);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target)
            {
                return true;
            }

            foreach (var next in graph[current])
            {
                if ((next == "#outside" && floorIndex > 0) || (avoid(next) && next != target))
                {
                    continue;
                }

                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    private static void CheckLightAndAir(FloorAnalysis analysis, PlanRules rules, FindingList findings)
    {
        var floor = analysis.Floor;
        var name = floor.Name;
        var outsideWindows = new Dictionary<string, List<PlanOpening>>(StringComparer.Ordinal);
        foreach (var info in analysis.Openings.Where(i => !i.Opening.IsDoor))
        {
            var sides = new[] { info.Left, info.Right };
            var outdoorSide = sides.Any(s => s.IsOutside || (s.IsRoom && RoomTypes.IsOutdoor(floor.Room(s.Name)!.Type)));
            foreach (var side in sides.Where(s => s.IsRoom && !RoomTypes.IsOutdoor(floor.Room(s.Name)!.Type)))
            {
                if (outdoorSide)
                {
                    if (!outsideWindows.TryGetValue(side.Name, out var list))
                    {
                        outsideWindows[side.Name] = list = [];
                    }

                    list.Add(info.Opening);
                }
                else
                {
                    findings.Hint("borrowed_light", name, $"{info.Opening.Tag} is between two rooms ({info.Between}): borrowed light, it does not count as an outside window.");
                }
            }
        }

        // Doors to a balcony / sitout also bring light and air.
        foreach (var info in analysis.Openings.Where(i => i.Opening.IsDoor))
        {
            var sides = new[] { info.Left, info.Right };
            if (sides.Any(s => s.IsRoom && RoomTypes.IsOutdoor(floor.Room(s.Name)!.Type)))
            {
                foreach (var side in sides.Where(s => s.IsRoom && !RoomTypes.IsOutdoor(floor.Room(s.Name)!.Type)))
                {
                    if (!outsideWindows.TryGetValue(side.Name, out var list))
                    {
                        outsideWindows[side.Name] = list = [];
                    }

                    list.Add(info.Opening);
                }
            }
        }

        foreach (var room in floor.Rooms)
        {
            var windows = outsideWindows.GetValueOrDefault(room.Name) ?? [];
            var mechanical = room.Ventilation is "shaft" or "exhaust" or "duct";
            if (RoomTypes.IsToilet(room.Type))
            {
                if (windows.Count == 0 && !mechanical)
                {
                    findings.Error("no_ventilation", name, $"{room.Name} needs a ventilator (about 2'-0\" x 1'-6\", sill 5'-0\") on an outside wall, or set ventilation \"shaft\" / \"exhaust\" if it opens to a duct.");
                }
                else if (windows.Count == 0)
                {
                    findings.Hint("mechanical_ventilation", name, $"{room.Name} is ventilated by a {room.Ventilation}: show it on the plan.");
                }

                continue;
            }

            if (room.Type == "kitchen" && windows.Count == 0)
            {
                if (mechanical)
                {
                    findings.Warning("kitchen_window", name, $"{room.Name} has no outside window ({room.Ventilation} only); a kitchen should have a window on an outside wall.");
                }
                else
                {
                    findings.Error("kitchen_window", name, $"{room.Name} needs a window on an outside wall (light, air and smoke).");
                }

                continue;
            }

            if (!RoomTypes.IsHabitable(room.Type) && !RoomTypes.IsSemiHabitable(room.Type))
            {
                continue;
            }

            if (windows.Count == 0)
            {
                if (RoomTypes.IsHabitable(room.Type))
                {
                    findings.Error("no_window", name, $"{room.Name} has no window on an outside wall; habitable rooms need one.");
                }
                else
                {
                    findings.Warning("no_window", name, $"{room.Name} has no window on an outside wall.");
                }

                continue;
            }

            var glazed = windows.Sum(w => w.Width * Math.Max(0, w.Head - w.Sill));
            var area = room.Shape.Area;
            if (area > 0 && glazed / area + 1e-9 < rules.WindowRatioMin)
            {
                var need = area * rules.WindowRatioMin;
                var good = area * rules.WindowRatioGood;
                findings.Warning("window_area", name, $"{room.Name}: windows {Inch.Area(glazed)} for {Inch.Area(area)} of floor ({Ratio(glazed / area)}); aim for 1/10 to 1/8 of the floor area ({Inch.Area(need)} to {Inch.Area(good)}).");
            }
        }
    }

    private static string Ratio(double ratio) => ratio <= 0 ? "none" : "1/" + Math.Round(1 / ratio).ToString(CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------------------------------------------
    // Stair
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckStair(HousePlan plan, PlanRules rules, FindingList findings)
    {
        if (plan.Floors.Count > 1)
        {
            foreach (var floor in plan.Floors.Where(f => f.Rooms.All(r => r.Type != "stair")))
            {
                findings.Error("stair_missing", floor.Name, $"{floor.Name} has no stair room (type stair) although the house has {plan.Floors.Count} floors; put the stair in the same place on every floor.");
            }
        }

        var holder = plan.Floors.FirstOrDefault(f => f.Stair is not null);
        if (holder?.Stair is not { } stair)
        {
            if (plan.Floors.Any(f => f.Rooms.Any(r => r.Type == "stair")))
            {
                findings.Warning("stair_numbers", string.Empty, "Give the stair numbers (type, flight_width, riser, tread, risers, and its box) so the stair can be checked.");
            }

            return;
        }

        var name = holder.Name;
        if (stair.Riser > rules.RiserMax + 0.01 || stair.Riser < rules.RiserMin - 0.01)
        {
            findings.Error("riser", name, $"Riser {Inch.Text(stair.Riser)} is outside {Inch.Text(rules.RiserMin)} to {Inch.Text(rules.RiserMax)} ({Inch.Text(rules.RiserTypical)} is typical).");
        }

        if (stair.Tread + 0.01 < rules.TreadMin)
        {
            findings.Error("tread", name, $"Tread {Inch.Text(stair.Tread)} is less than {Inch.Text(rules.TreadMin)}.");
        }

        if (!Inch.IsMultiple(stair.Tread, 0.5) || !Inch.IsMultiple(stair.FlightWidth, 1))
        {
            findings.Error("snap", name, $"Stair: tread {Inch.Text(stair.Tread)} and flight width {Inch.Text(stair.FlightWidth)} must be whole inches.");
        }

        if (stair.FlightWidth + 0.01 < rules.StairWidthAbsolute)
        {
            findings.Error("stair_width", name, $"Stair flight {Inch.Text(stair.FlightWidth)} wide is too narrow (at least {Inch.Text(rules.StairWidthAbsolute)}, {Inch.Text(rules.StairWidth)} recommended).");
        }
        else if (stair.FlightWidth + 0.01 < rules.StairWidth)
        {
            findings.Warning("stair_width", name, $"Stair flight {Inch.Text(stair.FlightWidth)} wide; {Inch.Text(rules.StairWidth)} is recommended.");
        }

        if (stair.Risers >= 2)
        {
            var rise = stair.Risers * stair.Riser;
            if (Math.Abs(rise - plan.FloorHeight) > 0.26)
            {
                findings.Error("stair_rise", name, $"{stair.Risers} risers of {Inch.Text(stair.Riser)} rise {Inch.Text(rise)}, but the floor height is {Inch.Text(plan.FloorHeight)}. {SuggestRisers(plan.FloorHeight, rules)}");
            }
        }

        var flights = stair.Type is "straight" ? 1 : stair.Type is "spiral" ? 0 : 2;
        if (flights > 0 && stair.Risers >= 2)
        {
            var perFlight = (int)Math.Ceiling(stair.Risers / (double)flights);
            if (perFlight > 15)
            {
                findings.Warning("flight_long", name, $"{perFlight} risers in one flight; more than 15 needs a landing in between.");
            }

            var landing = stair.Landing ?? stair.FlightWidth;
            if (stair.Landing is { } given && given + 0.01 < stair.FlightWidth)
            {
                findings.Error("landing", name, $"Landing {Inch.Text(given)} is shallower than the flight width {Inch.Text(stair.FlightWidth)}.");
            }

            if (stair.Box is { } box)
            {
                var treads = perFlight - 1;
                var going = treads * stair.Tread;
                var needLong = flights == 1 ? going : going + landing;
                var needShort = flights == 1 ? stair.FlightWidth : stair.Type == "l_shaped" ? stair.FlightWidth + landing : 2 * stair.FlightWidth;
                var longSide = Math.Max(box.Width, box.Height);
                var shortSide = Math.Min(box.Width, box.Height);
                if (longSide + 0.01 < needLong || shortSide + 0.01 < needShort)
                {
                    findings.Error("stair_fit", name, $"The {stair.Type.Replace('_', '-')} stair needs about {Inch.Size(needShort, needLong)} ({treads} treads of {Inch.Text(stair.Tread)} = {Inch.Text(going)} per flight{(flights == 2 ? " + landing " + Inch.Text(landing) : string.Empty)}), but its box is {Inch.Size(shortSide, longSide)}.");
                }

                var center = box.Center;
                if (!holder.Rooms.Any(r => r.Type == "stair" && r.Shape.Contains(center)))
                {
                    findings.Warning("stair_room", name, $"The stair box ({box.Text()}) is not inside a stair room.");
                }
            }
        }
        else if (stair.Type == "spiral")
        {
            findings.Hint("spiral", name, "Spiral stair: check the tread width at the walking line and the headroom.");
        }

        findings.Hint("headroom", name, "Headroom: keep at least 7'-0\" clear under beams, the landing and the flight above; check it in the section.");
    }

    private static string SuggestRisers(double floorHeight, PlanRules rules)
    {
        var best = (Risers: 0, Riser: 0.0);
        for (var n = 10; n <= 30; n++)
        {
            var riser = floorHeight / n;
            if (riser >= rules.RiserMin - 1e-9 && riser <= rules.RiserMax + 1e-9 &&
                (best.Risers == 0 || Math.Abs(riser - rules.RiserTypical) < Math.Abs(best.Riser - rules.RiserTypical)))
            {
                best = (n, riser);
            }
        }

        return best.Risers == 0
            ? "No whole number of risers fits; change the floor height."
            : $"Use {best.Risers} risers of {Inch.Text(Inch.Snap(best.Riser))} ({Inch.Text(floorHeight)} / {best.Risers}).";
    }

    // -----------------------------------------------------------------------------------------------------------
    // Stacking
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckStacking(FloorAnalysis below, FloorAnalysis above, FindingList findings)
    {
        var lower = below.Floor;
        var upper = above.Floor;
        var name = upper.Name;

        var stairBelow = lower.Rooms.Where(r => r.Type == "stair").Select(r => r.Shape.Bounds).ToList();
        var stairAbove = upper.Rooms.Where(r => r.Type == "stair").Select(r => r.Shape.Bounds).ToList();
        foreach (var box in stairAbove)
        {
            var match = stairBelow.Any(b => Math.Abs(b.MinX - box.MinX) <= 0.5 && Math.Abs(b.MinY - box.MinY) <= 0.5 && Math.Abs(b.MaxX - box.MaxX) <= 0.5 && Math.Abs(b.MaxY - box.MaxY) <= 0.5);
            if (!match && stairBelow.Count > 0)
            {
                findings.Error("stair_stack", name, $"The stair room of {name} ({box.Text()}) is not exactly above the stair room of {lower.Name} ({stairBelow[0].Text()}).");
            }
        }

        foreach (var room in upper.Rooms.Where(r => RoomTypes.IsWet(r.Type)))
        {
            var overlap = lower.Rooms.Where(r => RoomTypes.IsWet(r.Type)).Sum(r => OverlapArea(room.Shape.Bounds, r.Shape.Bounds));
            if (overlap < room.Shape.Area * 0.5)
            {
                var under = lower.Rooms.Where(r => OverlapArea(room.Shape.Bounds, r.Shape.Bounds) > 144).Select(r => r.Name).ToList();
                findings.Warning("wet_stack", name, $"{room.Name} is not above a wet area of {lower.Name}{(under.Count > 0 ? " (it sits over " + string.Join(", ", under) + ")" : string.Empty)}: stack toilets and kitchens for the drains, or plan a sunken slab.");
            }
        }

        foreach (var wall in upper.Walls.Where(w => w.Outer))
        {
            double unsupported = 0;
            const double step = 6;
            for (var t = step / 2; t < wall.Length; t += step)
            {
                var p = wall.At(t);
                if (!lower.Walls.Any(w => w.Contains(p, 0.5)))
                {
                    unsupported += Math.Min(step, wall.Length - t + (step / 2));
                }
            }

            if (unsupported > 12)
            {
                findings.Warning("wall_stack", name, $"Outer wall {wall.Id} of {name} has no wall below it for {Inch.Text(unsupported)}: align it with the {lower.Name} walls, or it needs a beam (structural engineer).");
            }
        }

        if (upper.Outline.Points.Any(p => !lower.Outline.Contains(p, 0.5)))
        {
            findings.Warning("cantilever", name, $"{name} projects beyond {lower.Name} (outline {upper.Outline.Bounds.Text()}); a projection needs structural design.");
        }
    }

    private static double OverlapArea(Box a, Box b)
    {
        var i = a.Intersect(b);
        return i.Width > 0 && i.Height > 0 ? i.Area : 0;
    }

    // -----------------------------------------------------------------------------------------------------------
    // Vastu (optional preferences)
    // -----------------------------------------------------------------------------------------------------------

    /// <summary>The compass zone (NE, N, NW, E, C, W, SE, S, SW) of <paramref name="p"/> in <paramref name="box"/> (+Y = north).</summary>
    public static string Zone(Pt p, Box box)
    {
        var fx = box.Width <= 0 ? 0.5 : (p.X - box.MinX) / box.Width;
        var fy = box.Height <= 0 ? 0.5 : (p.Y - box.MinY) / box.Height;
        var ns = fy > 2.0 / 3 ? "N" : fy < 1.0 / 3 ? "S" : string.Empty;
        var ew = fx > 2.0 / 3 ? "E" : fx < 1.0 / 3 ? "W" : string.Empty;
        var zone = ns + ew;
        return zone.Length == 0 ? "C" : zone;
    }

    private static void VastuHints(FloorAnalysis ground, FindingList findings)
    {
        var floor = ground.Floor;
        var box = floor.Outline.Bounds;
        void Prefer(PlanRoom room, string[] good, string what)
        {
            var zone = Zone(room.Shape.Centroid(), box);
            if (!good.Contains(zone))
            {
                findings.Hint("vastu", floor.Name, $"Vastu (preference only): {room.Name} is in the {zone} zone; Vastu prefers {what}.");
            }
        }

        foreach (var room in floor.Rooms)
        {
            switch (room.Type)
            {
                case "kitchen":
                    Prefer(room, ["SE", "E", "NW"], "the SE (or NW)");
                    break;
                case "master_bedroom":
                    Prefer(room, ["SW", "S", "W"], "the SW");
                    break;
                case "pooja":
                    Prefer(room, ["NE", "N", "E"], "the NE");
                    break;
                case "toilet" or "bath_wc":
                    if (Zone(room.Shape.Centroid(), box) is "NE" or "C")
                    {
                        findings.Hint("vastu", floor.Name, $"Vastu (preference only): {room.Name} is in the {Zone(room.Shape.Centroid(), box)} zone; Vastu avoids toilets in the NE and the centre (NW or W preferred).");
                    }

                    break;
                case "stair":
                    if (Zone(room.Shape.Centroid(), box) is "NE" or "C")
                    {
                        findings.Hint("vastu", floor.Name, $"Vastu (preference only): the stair is in the {Zone(room.Shape.Centroid(), box)} zone; Vastu prefers S, SW or W.");
                    }

                    break;
            }
        }

        foreach (var info in ground.Openings.Where(i => i.Opening.IsDoor && (i.Left.IsOutside || i.Right.IsOutside)))
        {
            var outward = info.Left.IsOutside ? info.Opening.Wall.Normal : info.Opening.Wall.Normal * -1;
            var facing = Math.Abs(outward.X) > Math.Abs(outward.Y) ? (outward.X > 0 ? "east" : "west") : (outward.Y > 0 ? "north" : "south");
            if (facing is "south" or "west")
            {
                findings.Hint("vastu", floor.Name, $"Vastu (preference only): the entrance {info.Opening.Tag} faces {facing}; Vastu prefers an east or north entrance (or the auspicious part of a {facing} wall).");
            }
        }
    }
}
