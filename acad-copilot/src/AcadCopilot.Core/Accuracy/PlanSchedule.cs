using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Accuracy;

/// <summary>
/// The clean dimension schedule of a validated plan (every size in feet-inches, areas in square feet, openings, the
/// stair, the dimension chains along the south and west outer walls) and the short Markdown summary shown to the user.
/// </summary>
public static class PlanSchedule
{
    /// <summary>Builds the schedule.</summary>
    public static JsonObject Build(HousePlan plan, IReadOnlyList<FloorAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(analyses);
        var plotArea = plan.PlotWidth * plan.PlotDepth;
        var floors = new JsonArray();
        double builtUp = 0, carpet = 0, groundCoverage = 0;
        foreach (var analysis in analyses)
        {
            var floor = analysis.Floor;
            var box = floor.Outline.Bounds;
            var outlineArea = floor.Outline.Area;
            var indoor = floor.Rooms.Where(r => !RoomTypes.IsOutdoor(r.Type) && floor.Outline.Contains(r.Shape.Centroid())).ToList();
            var floorCarpet = indoor.Sum(r => r.Shape.Area);
            builtUp += outlineArea;
            carpet += floorCarpet;
            if (floor.Index == 0)
            {
                groundCoverage = outlineArea;
            }

            var rooms = new JsonArray();
            foreach (var room in floor.Rooms)
            {
                var b = room.Shape.Bounds;
                rooms.Add(new JsonObject
                {
                    ["name"] = room.Name,
                    ["type"] = room.Type,
                    ["size"] = room.Shape.IsAxisRectangle ? Inch.Size(room.Width, room.Depth) : "polygon " + Inch.Size(room.Width, room.Depth),
                    ["areaSqFt"] = Inch.SquareFeet(room.Shape.Area),
                    ["from"] = new Pt(b.MinX, b.MinY).Text(),
                    ["to"] = new Pt(b.MaxX, b.MaxY).Text(),
                    ["zone"] = PlanValidator.Zone(room.Shape.Centroid(), box),
                    ["labelAt"] = room.Shape.Centroid().Input(),
                });
            }

            var doors = new JsonArray();
            var windows = new JsonArray();
            foreach (var info in analysis.Openings)
            {
                var o = info.Opening;
                var item = new JsonObject
                {
                    ["tag"] = o.Tag,
                    ["kind"] = o.Kind,
                    ["width"] = Inch.Text(o.Width),
                    ["wall"] = o.Wall.Id,
                    ["between"] = info.Between,
                    ["jambs"] = o.Start.Input() + " to " + o.End.Input(),
                };
                if (o.IsDoor)
                {
                    item["height"] = Inch.Text(o.Head);
                    if (info.SwingRoom is not null)
                    {
                        item["swingsInto"] = info.SwingRoom;
                        item["hinge"] = info.HingeUsed;
                        if (info.Swings.Count == 1)
                        {
                            item["hingePoint"] = info.Swings[0].Hinge.Input();
                        }
                    }

                    doors.Add(item);
                }
                else
                {
                    item["sill"] = Inch.Text(o.Sill);
                    item["head"] = Inch.Text(o.Head);
                    item["size"] = Inch.Size(o.Width, o.Head - o.Sill);
                    windows.Add(item);
                }
            }

            var entry = new JsonObject
            {
                ["name"] = floor.Name,
                ["outline"] = Inch.Size(box.Width, box.Height),
                ["outlineFrom"] = new Pt(box.MinX, box.MinY).Text(),
                ["builtUpSqFt"] = Inch.SquareFeet(outlineArea),
                ["carpetSqFt"] = Inch.SquareFeet(floorCarpet),
                ["rooms"] = rooms,
                ["doors"] = doors,
                ["windows"] = windows,
                ["walls"] = new JsonArray([.. floor.Walls.Select(w => (JsonNode)new JsonObject
                {
                    ["id"] = w.Id,
                    ["thickness"] = Inch.Text(w.Thickness),
                    ["outer"] = w.Outer,
                    ["from"] = w.From.Input(),
                    ["to"] = w.To.Input(),
                })]),
                ["chains"] = Chains(analysis),
            };
            if (floor.Origin is { } origin)
            {
                entry["origin"] = origin.Input();
            }

            if (floor.Stair is { } stair)
            {
                entry["stair"] = new JsonObject
                {
                    ["type"] = stair.Type,
                    ["flightWidth"] = Inch.Text(stair.FlightWidth),
                    ["risers"] = stair.Risers,
                    ["riser"] = Inch.Text(stair.Riser),
                    ["tread"] = Inch.Text(stair.Tread),
                    ["landing"] = Inch.Text(stair.Landing ?? stair.FlightWidth),
                    ["box"] = stair.Box is { } sb ? sb.Text() : null,
                };
            }

            floors.Add(entry);
        }

        var setbacks = new JsonObject();
        if (analyses.Count > 0)
        {
            var actual = PlanValidator.Setbacks(plan, analyses[0].Floor.Outline.Bounds);
            foreach (var (side, value) in actual)
            {
                setbacks[side] = new JsonObject
                {
                    ["actual"] = Inch.Text(value),
                    ["required"] = plan.Setbacks.TryGetValue(side, out var required) ? Inch.Text(required) : null,
                };
            }
        }

        return new JsonObject
        {
            ["name"] = plan.Name,
            ["plot"] = new JsonObject
            {
                ["size"] = Inch.Size(plan.PlotWidth, plan.PlotDepth),
                ["areaSqFt"] = Inch.SquareFeet(plotArea),
                ["roadSide"] = plan.RoadSide,
                ["setbacks"] = setbacks,
            },
            ["walls"] = $"outer {Inch.Text(plan.OuterWall)}, inner {Inch.Text(plan.InnerWall)}",
            ["floorHeight"] = Inch.Text(plan.FloorHeight),
            ["floors"] = floors,
            ["totals"] = new JsonObject
            {
                ["builtUpSqFt"] = Inch.SquareFeet(builtUp),
                ["carpetSqFt"] = Inch.SquareFeet(carpet),
                ["groundCoveragePercent"] = plotArea > 0 ? Math.Round(groundCoverage / plotArea * 100, 1) : 0,
                ["far"] = plotArea > 0 ? Math.Round(builtUp / plotArea, 2) : 0,
                ["note"] = "Areas for information; coverage and FAR limits come from local bylaws.",
            },
        };
    }

    /// <summary>
    /// The dimension chains of a floor: wall faces along the south and west outer walls (outer face, wall thickness, room,
    /// inner wall ... outer face), each segment in feet-inches, and the total, which equals the outline size.
    /// </summary>
    public static JsonObject Chains(FloorAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var floor = analysis.Floor;
        var box = floor.Outline.Bounds;
        var reach = floor.Walls.Where(w => w.Outer).Select(w => w.Thickness).DefaultIfEmpty(9).Max() + 0.5;
        var xs = new List<double> { box.MinX, box.MaxX };
        var ys = new List<double> { box.MinY, box.MaxY };
        foreach (var wall in floor.Walls)
        {
            var body = wall.Body().Bounds;
            if (wall.IsVertical && body.MinY <= box.MinY + reach)
            {
                xs.Add(body.MinX);
                xs.Add(body.MaxX);
            }

            if (wall.IsHorizontal && body.MinX <= box.MinX + reach)
            {
                ys.Add(body.MinY);
                ys.Add(body.MaxY);
            }
        }

        return new JsonObject
        {
            ["south"] = Chain(xs, box.MinX, box.MaxX),
            ["west"] = Chain(ys, box.MinY, box.MaxY),
            ["overallWidth"] = Inch.Text(box.Width),
            ["overallDepth"] = Inch.Text(box.Height),
        };
    }

    private static JsonObject Chain(List<double> values, double min, double max)
    {
        var sorted = values.Where(v => v >= min - 1e-6 && v <= max + 1e-6).Select(Inch.Snap).Distinct().OrderBy(v => v).ToList();
        var segments = new JsonArray();
        for (var i = 1; i < sorted.Count; i++)
        {
            segments.Add(Inch.Text(sorted[i] - sorted[i - 1]));
        }

        return new JsonObject
        {
            ["at"] = new JsonArray([.. sorted.Select(v => (JsonNode)Inch.Text(v))]),
            ["segments"] = segments,
            ["total"] = Inch.Text(max - min),
        };
    }

    /// <summary>The Markdown summary the AI shows before asking "Shall I draw this?".</summary>
    public static string Summary(HousePlan plan, JsonObject schedule, IReadOnlyList<PlanFinding> findings, PlanRules rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(rules);
        var text = new StringBuilder();
        var plot = schedule["plot"]!.AsObject();
        text.Append(CultureInfo.InvariantCulture, $"**{plan.Name}**: plot {plot["size"]} ({plot["areaSqFt"]} sq ft)");
        if (plan.RoadSide is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $", road on the {plan.RoadSide}");
        }

        text.Append(CultureInfo.InvariantCulture, $"; walls {schedule["walls"]}; floor height {schedule["floorHeight"]}.");
        if (plot["setbacks"] is JsonObject sb && sb.Count > 0)
        {
            text.Append(" Setbacks: ").Append(string.Join(", ", sb.Select(s => $"{s.Key} {s.Value!["actual"]}"))).Append('.');
        }

        text.Append('\n');
        foreach (var floor in schedule["floors"]!.AsArray().Select(f => f!.AsObject()))
        {
            text.Append('\n').Append(CultureInfo.InvariantCulture, $"**{floor["name"]}** (outline {floor["outline"]}, built-up {floor["builtUpSqFt"]} sq ft, carpet {floor["carpetSqFt"]} sq ft)\n\n");
            text.Append("| Room | Clear size | Area | Where |\n|---|---|---|---|\n");
            foreach (var room in floor["rooms"]!.AsArray().Select(r => r!.AsObject()))
            {
                text.Append(CultureInfo.InvariantCulture, $"| {room["name"]} | {room["size"]} | {room["areaSqFt"]} sq ft | {room["zone"]} |\n");
            }

            var doors = floor["doors"]!.AsArray();
            var windows = floor["windows"]!.AsArray();
            if (doors.Count + windows.Count > 0)
            {
                text.Append('\n').Append(string.Join(", ", doors.Select(d => $"{d!["tag"]} {d["width"]}").Concat(windows.Select(w => $"{w!["tag"]} {w["size"]}"))));
                text.Append('\n');
            }
        }

        var totals = schedule["totals"]!.AsObject();
        text.Append('\n').Append(CultureInfo.InvariantCulture, $"Total built-up {totals["builtUpSqFt"]} sq ft, carpet {totals["carpetSqFt"]} sq ft, ground coverage {totals["groundCoveragePercent"]}%, FAR {totals["far"]} (information only).\n");
        var errors = findings.Count(f => f.Severity == Severity.Error);
        var warnings = findings.Count(f => f.Severity == Severity.Warning);
        text.Append(CultureInfo.InvariantCulture, $"Checks: {errors} error{(errors == 1 ? string.Empty : "s")}, {warnings} warning{(warnings == 1 ? string.Empty : "s")}");
        text.Append(rules.Applied.Count > 0 ? $"; using {rules.Applied.Count} of your rules" : "; typical sizes (not bylaw checks)");
        text.Append(rules.Vastu ? "; Vastu hints on." : ".");
        return text.ToString();
    }
}
