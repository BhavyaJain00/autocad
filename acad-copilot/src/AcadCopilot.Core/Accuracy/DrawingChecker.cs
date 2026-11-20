using System.Globalization;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Accuracy;

/// <summary>What check_drawing found.</summary>
public sealed class DrawingCheckReport
{
    /// <summary>Errors, warnings and hints, errors first.</summary>
    public required IReadOnlyList<PlanFinding> Findings { get; init; }

    /// <summary>Per room: planned and measured clear size.</summary>
    public required JsonArray Rooms { get; init; }

    /// <summary>Counts of what was read.</summary>
    public required JsonObject Read { get; init; }

    /// <summary>Number of errors.</summary>
    public int Errors => Findings.Count(f => f.Severity == Severity.Error);

    /// <summary>Number of warnings.</summary>
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);
}

/// <summary>
/// check_drawing: compares what is drawn (wall lines, door arcs, window lines, text, dimensions, layers) with the last
/// validated plan of the drawing. Pure: the plug-in reads the <see cref="DrawingSnapshot"/>; this class judges it.
/// Tolerance for sizes and positions: 1/2 inch.
/// </summary>
public static class DrawingChecker
{
    /// <summary>Size and position tolerance (1/2").</summary>
    public const double Tolerance = 0.5;

    /// <summary>Endpoints closer than this are joined (1/16").</summary>
    public const double JoinTolerance = 1.0 / 16;

    /// <summary>Most findings of one kind.</summary>
    public const int MaxPerKind = 10;

    /// <summary>The stages check_drawing knows.</summary>
    public static IReadOnlyList<string> Stages { get; } = ["all", "walls", "openings", "annotation"];

    /// <summary>The area of the drawing to read for <paramref name="floor"/> placed at <paramref name="origin"/> (outline and outdoor rooms plus 12').</summary>
    public static Box Region(PlanFloor floor, Pt origin)
    {
        ArgumentNullException.ThrowIfNull(floor);
        var box = floor.Outline.Bounds;
        foreach (var room in floor.Rooms)
        {
            box = box.Union(room.Shape.Bounds);
        }

        return new Box(box.MinX + origin.X, box.MinY + origin.Y, box.MaxX + origin.X, box.MaxY + origin.Y).Inflate(144);
    }

    /// <summary>Checks <paramref name="snapshot"/> against <paramref name="floor"/> drawn with its plot corner at <paramref name="origin"/>.</summary>
    public static DrawingCheckReport Check(HousePlan plan, PlanFloor floor, Pt origin, DrawingSnapshot snapshot, CheckLayers layers, string stage = "all")
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(layers);
        var analysis = new FloorAnalysis(floor, plan.OuterWall);
        var context = new Context(plan, analysis, origin, snapshot, layers);
        var findings = new FindingList();
        var rooms = new JsonArray();
        var all = stage == "all";
        if (all || stage == "walls")
        {
            CheckWallTopology(context, findings);
            CheckWallFaces(context, findings);
            MeasureRooms(context, findings, rooms);
        }

        if (all || stage == "openings")
        {
            CheckDoors(context, findings);
            CheckWindows(context, findings);
        }

        if (all || stage == "annotation")
        {
            CheckText(context, findings);
            CheckDimensions(context, findings);
        }

        CheckLayers(context, findings, stage);
        if (snapshot.Truncated)
        {
            findings.Warning("truncated", floor.Name, "The area holds more objects than were read; some checks are incomplete.");
        }

        return new DrawingCheckReport
        {
            Findings = findings.Items,
            Rooms = rooms,
            Read = new JsonObject
            {
                ["wallLines"] = context.Walls.Count,
                ["doorArcs"] = context.DoorArcs.Count,
                ["windowLines"] = context.Glazing.Count,
                ["texts"] = snapshot.Texts.Count,
                ["dimensions"] = snapshot.Dimensions.Count,
                ["objects"] = snapshot.Objects.Count,
            },
        };
    }

    private sealed class Context
    {
        public Context(HousePlan plan, FloorAnalysis analysis, Pt origin, DrawingSnapshot snapshot, CheckLayers layers)
        {
            Plan = plan;
            Analysis = analysis;
            Origin = origin;
            Snapshot = snapshot;
            Layers = layers;
            Walls = [.. snapshot.Segments.Where(s => Same(s.Layer, layers.Wall) && s.Length > 1e-6)];
            Glazing = [.. snapshot.Segments.Where(s => Same(s.Layer, layers.Glazing) && s.Length > 1e-6)];
            DoorArcs = [.. snapshot.Arcs.Where(a => Same(a.Layer, layers.Door))];
            PlanFaces = [.. PlannedFaces(analysis).Select(e => (D(e.A), D(e.B)))];
        }

        public HousePlan Plan { get; }

        public FloorAnalysis Analysis { get; }

        public PlanFloor Floor => Analysis.Floor;

        public Pt Origin { get; }

        public DrawingSnapshot Snapshot { get; }

        public CheckLayers Layers { get; }

        public List<DrawnSegment> Walls { get; }

        public List<DrawnSegment> Glazing { get; }

        public List<DrawnArc> DoorArcs { get; }

        /// <summary>Edges of the planned wall bodies, with the openings cut out, in drawing coordinates.</summary>
        public List<(Pt A, Pt B)> PlanFaces { get; }

        /// <summary>Plan point -&gt; drawing point.</summary>
        public Pt D(Pt p) => p + Origin;

        /// <summary>Drawing point -&gt; plan point.</summary>
        public Pt P(Pt p) => p - Origin;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The faces of the planned walls (plan coordinates): both long faces without the openings, the ends and the jambs.</summary>
    private static List<(Pt A, Pt B)> PlannedFaces(FloorAnalysis analysis)
    {
        var faces = new List<(Pt A, Pt B)>();
        foreach (var wall in analysis.Floor.Walls)
        {
            var half = wall.Thickness / 2;
            var from = -wall.ExtendStart;
            var to = wall.Length + wall.ExtendEnd;
            var cuts = analysis.Openings.Where(i => ReferenceEquals(i.Opening.Wall, wall)).Select(i => (i.Opening.Offset, End: i.Opening.Offset + i.Opening.Width)).OrderBy(c => c.Offset).ToList();
            foreach (var side in new[] { -half, half })
            {
                var t = from;
                foreach (var (start, end) in cuts)
                {
                    if (start > t)
                    {
                        faces.Add((wall.At(t, side), wall.At(start, side)));
                    }

                    t = Math.Max(t, end);
                }

                if (to > t)
                {
                    faces.Add((wall.At(t, side), wall.At(to, side)));
                }
            }

            faces.Add((wall.At(from, -half), wall.At(from, half)));
            faces.Add((wall.At(to, -half), wall.At(to, half)));
            foreach (var (start, end) in cuts)
            {
                faces.Add((wall.At(start, -half), wall.At(start, half)));
                faces.Add((wall.At(end, -half), wall.At(end, half)));
            }
        }

        return faces;
    }

    // -----------------------------------------------------------------------------------------------------------
    // Walls
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckWallTopology(Context c, FindingList findings)
    {
        var name = c.Floor.Name;
        var walls = c.Walls;
        if (walls.Count == 0)
        {
            findings.Error("no_walls", name, $"No wall lines on layer {c.Layers.Wall} in this floor's area ({Region(c.Floor, c.Origin).Text()}); draw the walls there, or pass the right origin / wall_layer.");
            return;
        }

        var overlaps = 0;
        for (var i = 0; i < walls.Count && overlaps < MaxPerKind; i++)
        {
            for (var j = i + 1; j < walls.Count && overlaps < MaxPerKind; j++)
            {
                if (CollinearOverlap(walls[i], walls[j]) is { } length && length > 0.1)
                {
                    overlaps++;
                    findings.Error("wall_overlap", name, $"Wall lines {walls[i].Handle} and {walls[j].Handle} lie on top of each other for {Inch.Text(length)} near {Mid(walls[i], walls[j]).Text()}: erase the duplicate.");
                }
            }
        }

        var reported = new List<Pt>();
        var count = 0;
        foreach (var segment in walls)
        {
            foreach (var end in new[] { segment.A, segment.B })
            {
                if (Connected(end, segment, walls) || reported.Any(r => r.DistanceTo(end) < 13))
                {
                    continue;
                }

                if (count++ >= MaxPerKind)
                {
                    break;
                }

                reported.Add(end);
                var (nearest, distance) = Nearest(end, segment, walls);
                if (nearest is not null && distance <= 12)
                {
                    findings.Error("wall_gap", name, $"Gap of {Inch.Text(distance)} in the wall lines at {end.Text()} (between {segment.Handle} and {nearest.Handle}): extend or FILLET R 0 so they meet.");
                }
                else
                {
                    findings.Error("wall_dangling", name, $"Wall line {segment.Handle} ends in the open at {end.Text()}: join it to the next wall face or jamb, or trim it.");
                }
            }
        }
    }

    private static bool Connected(Pt end, DrawnSegment self, List<DrawnSegment> walls)
    {
        foreach (var other in walls)
        {
            if (ReferenceEquals(other, self))
            {
                continue;
            }

            if (other.Distance(end) <= JoinTolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static (DrawnSegment? Segment, double Distance) Nearest(Pt end, DrawnSegment self, List<DrawnSegment> walls)
    {
        DrawnSegment? best = null;
        var distance = double.MaxValue;
        foreach (var other in walls)
        {
            if (ReferenceEquals(other, self))
            {
                continue;
            }

            var d = other.Distance(end);
            if (d < distance)
            {
                distance = d;
                best = other;
            }
        }

        return (best, distance);
    }

    private static double? CollinearOverlap(DrawnSegment a, DrawnSegment b)
    {
        var da = (a.B - a.A).Unit();
        var db = (b.B - b.A).Unit();
        if (Math.Abs((da.X * db.Y) - (da.Y * db.X)) > 1e-4)
        {
            return null;
        }

        var normal = new Pt(-da.Y, da.X);
        if (Math.Abs((b.A - a.A).Dot(normal)) > 0.01)
        {
            return null;
        }

        var a0 = 0.0;
        var a1 = a.Length;
        var b0 = (b.A - a.A).Dot(da);
        var b1 = (b.B - a.A).Dot(da);
        return Math.Min(a1, Math.Max(b0, b1)) - Math.Max(a0, Math.Min(b0, b1));
    }

    private static Pt Mid(DrawnSegment a, DrawnSegment b) => (a.A + a.B + b.A + b.B) * 0.25;

    private static void CheckWallFaces(Context c, FindingList findings)
    {
        if (c.Walls.Count == 0)
        {
            return;
        }

        var name = c.Floor.Name;
        var openings = c.Analysis.Openings;
        var missingCount = 0;

        // Every room edge that faces a planned wall must be drawn (except across openings).
        foreach (var room in c.Floor.Rooms)
        {
            foreach (var (a, b) in room.Shape.Edges())
            {
                var length = a.DistanceTo(b);
                var dir = (b - a).Unit();
                var outward = new Pt(dir.Y, -dir.X);
                var missing = new List<(double From, double To)>();
                const double step = 2;
                for (var t = 1.0; t < length - 0.5; t += step)
                {
                    var p = a + (dir * t);
                    var beyond = c.Analysis.At(p + (outward * 0.75), room.Name);
                    if (beyond.Kind != "wall" || InOpening(openings, p, 1))
                    {
                        continue;
                    }

                    var dp = c.D(p);
                    if (!c.Walls.Any(w => w.Distance(dp) <= Tolerance))
                    {
                        if (missing.Count > 0 && t - missing[^1].To <= step + 0.01)
                        {
                            missing[^1] = (missing[^1].From, t);
                        }
                        else
                        {
                            missing.Add((t, t));
                        }
                    }
                }

                foreach (var (from, to) in missing.Where(m => m.To - m.From >= 3))
                {
                    if (missingCount++ >= MaxPerKind)
                    {
                        break;
                    }

                    findings.Error("wall_face_missing", name, $"{room.Name}: no wall line within 1/2\" of its {SideName(outward)} face from {c.D(a + (dir * from)).Text()} to {c.D(a + (dir * to)).Text()} ({Inch.Text(to - from)}); the wall is missing or out of place.");
                }
            }
        }

        // Outer face of the building (outline edges), except across openings.
        foreach (var (a, b) in c.Floor.Outline.Edges())
        {
            var length = a.DistanceTo(b);
            var dir = (b - a).Unit();
            var missing = 0.0;
            Pt? first = null;
            for (var t = 1.0; t < length - 0.5; t += 2)
            {
                var p = a + (dir * t);
                if (InOpening(openings, p, 1) || c.Floor.Rooms.Any(r => RoomTypes.IsOutdoor(r.Type) && r.Shape.Contains(p + (new Pt(-dir.Y, dir.X) * 1), 0)))
                {
                    continue;
                }

                if (!c.Analysis.Floor.Walls.Any(w => w.Contains(p + (new Pt(-dir.Y, dir.X) * 1))))
                {
                    continue;
                }

                if (!c.Walls.Any(w => w.Distance(c.D(p)) <= Tolerance))
                {
                    missing += 2;
                    first ??= p;
                }
            }

            if (missing >= 4 && first is { } f && missingCount++ < MaxPerKind)
            {
                findings.Error("outer_face_missing", name, $"The outer face of the building is not drawn for about {Inch.Text(missing)} along the edge from {c.D(a).Text()} to {c.D(b).Text()} (first gap near {c.D(f).Text()}).");
            }
        }

        // Wall lines across openings (not broken).
        foreach (var info in openings)
        {
            var o = info.Opening;
            if (o.Width < 6)
            {
                continue;
            }

            var half = o.Wall.Thickness / 2;
            foreach (var side in new[] { half, -half })
            {
                var p = c.D(o.Wall.At(o.Offset + (o.Width / 2), side));
                var crossing = c.Walls.FirstOrDefault(w => w.Distance(p) <= 0.25 && Math.Abs((w.B - w.A).Unit().Dot(o.Wall.Direction)) > 0.99);
                if (crossing is not null)
                {
                    findings.Error("wall_across_opening", name, $"Wall line {crossing.Handle} runs across opening {o.Tag} at {p.Text()}: BREAK it between the jambs.");
                    break;
                }
            }
        }
    }

    private static bool InOpening(IReadOnlyList<OpeningInfo> openings, Pt p, double margin)
    {
        foreach (var info in openings)
        {
            var o = info.Opening;
            var t = o.Wall.Along(p);
            if (t >= o.Offset - margin && t <= o.Offset + o.Width + margin && Math.Abs(o.Wall.Side(p)) <= (o.Wall.Thickness / 2) + 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string SideName(Pt outward) =>
        Math.Abs(outward.X) > Math.Abs(outward.Y) ? (outward.X > 0 ? "east" : "west") : (outward.Y > 0 ? "north" : "south");

    private static void MeasureRooms(Context c, FindingList findings, JsonArray rooms)
    {
        if (c.Walls.Count == 0)
        {
            return;
        }

        var name = c.Floor.Name;
        var planSegments = c.PlanFaces.Select(f => new DrawnSegment("plan", "plan", f.A, f.B)).ToList();
        foreach (var room in c.Floor.Rooms.Where(r => r.Shape.IsAxisRectangle && !RoomTypes.IsOutdoor(r.Type)))
        {
            var box = room.Shape.Bounds;
            var widths = new List<double>();
            var depths = new List<double>();
            var notDrawn = 0;
            foreach (var f in new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 })
            {
                var row = c.D(new Pt(box.Center.X, box.MinY + (box.Height * f)));
                var col = c.D(new Pt(box.MinX + (box.Width * f), box.Center.Y));
                if (Span(row, new Pt(1, 0), planSegments) is { } wp)
                {
                    if (Span(row, new Pt(1, 0), c.Walls) is { } wd)
                    {
                        widths.Add(wd - wp);
                    }
                    else
                    {
                        notDrawn++;
                    }
                }

                if (Span(col, new Pt(0, 1), planSegments) is { } dp)
                {
                    if (Span(col, new Pt(0, 1), c.Walls) is { } dd)
                    {
                        depths.Add(dd - dp);
                    }
                    else
                    {
                        notDrawn++;
                    }
                }
            }

            if (notDrawn > 0 && (widths.Count == 0 || depths.Count == 0))
            {
                rooms.Add(new JsonObject { ["room"] = room.Name, ["planned"] = Inch.Size(room.Width, room.Depth), ["measured"] = "not enclosed by wall lines" });
                findings.Error("room_open", name, $"{room.Name} is not enclosed by wall lines (no wall found on one side of its centre {c.D(box.Center).Text()}).");
                continue;
            }

            if (widths.Count == 0 && depths.Count == 0)
            {
                rooms.Add(new JsonObject { ["room"] = room.Name, ["planned"] = Inch.Size(room.Width, room.Depth), ["measured"] = "not measured (open sides)" });
                continue;
            }

            var dw = widths.Count > 0 ? Median(widths) : 0;
            var dd2 = depths.Count > 0 ? Median(depths) : 0;
            var measuredWidth = Inch.Snap(room.Width + dw);
            var measuredDepth = Inch.Snap(room.Depth + dd2);
            rooms.Add(new JsonObject
            {
                ["room"] = room.Name,
                ["planned"] = Inch.Size(room.Width, room.Depth),
                ["measured"] = Inch.Size(measuredWidth, measuredDepth),
            });
            if (Math.Abs(dw) > Tolerance || Math.Abs(dd2) > Tolerance)
            {
                findings.Error("room_size", name, $"{room.Name} measures {Inch.Size(measuredWidth, measuredDepth)} clear in the drawing; the plan says {Inch.Size(room.Width, room.Depth)} (tolerance 1/2\"). Move the wall lines to the planned faces.");
            }
        }
    }

    /// <summary>Distance between the nearest segments hit by rays from <paramref name="p"/> in +dir and -dir, or null when one side hits nothing.</summary>
    private static double? Span(Pt p, Pt dir, List<DrawnSegment> segments)
    {
        var forward = Ray(p, dir, segments);
        var backward = Ray(p, dir * -1, segments);
        return forward is { } f && backward is { } b ? f + b : null;
    }

    private static double? Ray(Pt p, Pt d, List<DrawnSegment> segments)
    {
        double? best = null;
        foreach (var s in segments)
        {
            var e = s.B - s.A;
            var denominator = (d.X * e.Y) - (d.Y * e.X);
            if (Math.Abs(denominator) < 1e-12)
            {
                continue;
            }

            var w = s.A - p;
            var t = ((w.X * e.Y) - (w.Y * e.X)) / denominator;
            var u = ((w.X * d.Y) - (w.Y * d.X)) / denominator;
            if (t > 0.01 && u >= -1e-9 && u <= 1 + 1e-9 && (best is null || t < best))
            {
                best = t;
            }
        }

        return best;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    // -----------------------------------------------------------------------------------------------------------
    // Doors and windows
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckDoors(Context c, FindingList findings)
    {
        var name = c.Floor.Name;
        var used = new HashSet<DrawnArc>();
        foreach (var info in c.Analysis.Openings.Where(i => i.Opening.IsDoor && i.Swings.Count > 0))
        {
            var o = info.Opening;
            foreach (var swing in info.Swings)
            {
                var hinge = c.D(swing.Hinge);
                var match = c.DoorArcs.FirstOrDefault(a => !used.Contains(a) && a.Center.DistanceTo(hinge) <= 1 && Math.Abs(a.Radius - swing.Radius) <= 1);
                if (match is not null)
                {
                    used.Add(match);
                    var middle = c.P(match.Middle) - swing.Hinge;
                    if (middle.Dot(swing.Open) < 0)
                    {
                        findings.Error("door_swing_side", name, $"{o.Tag}: the swing arc {match.Handle} opens to the wrong side; it must swing into {info.SwingRoom}.");
                    }
                    else if (middle.Dot(swing.Closed) < 0)
                    {
                        findings.Error("door_swing_side", name, $"{o.Tag}: the swing arc {match.Handle} sweeps away from the opening; it must end at the other jamb.");
                    }

                    continue;
                }

                // Hinged at the other jamb?
                var other = c.D(swing.Hinge + (swing.Closed * swing.Radius));
                var flipped = c.DoorArcs.FirstOrDefault(a => !used.Contains(a) && a.Center.DistanceTo(other) <= 1 && Math.Abs(a.Radius - swing.Radius) <= 1);
                if (flipped is not null)
                {
                    used.Add(flipped);
                    findings.Warning("door_hinge", name, $"{o.Tag} is hinged at {other.Text()}, the plan has the hinge at {hinge.Text()} (nearer the corner).");
                    continue;
                }

                findings.Error("door_missing", name, $"{o.Tag} ({o.Wall.Id}, {Inch.Text(o.Width)}): no swing arc of radius {Inch.Text(swing.Radius)} centred on the hinge {hinge.Text()} on layer {c.Layers.Door}.");
            }
        }

        var extras = c.DoorArcs.Where(a => !used.Contains(a) && a.Radius >= 12 && a.Radius <= 60).Take(MaxPerKind);
        foreach (var arc in extras)
        {
            findings.Warning("door_extra", name, $"Door arc {arc.Handle} (radius {Inch.Text(arc.Radius)}, centre {arc.Center.Text()}) belongs to no door of the plan.");
        }
    }

    private static void CheckWindows(Context c, FindingList findings)
    {
        var name = c.Floor.Name;
        var used = new HashSet<DrawnSegment>();
        foreach (var info in c.Analysis.Openings.Where(i => !i.Opening.IsDoor))
        {
            var o = info.Opening;
            var wall = o.Wall;
            var found = false;
            foreach (var s in c.Glazing)
            {
                var a = c.P(s.A);
                var b = c.P(s.B);
                if (Math.Abs((b - a).Unit().Dot(wall.Direction)) < 0.99 || Math.Abs(wall.Side(a)) > (wall.Thickness / 2) + Tolerance || Math.Abs(wall.Side(b)) > (wall.Thickness / 2) + Tolerance)
                {
                    continue;
                }

                var t0 = Math.Min(wall.Along(a), wall.Along(b));
                var t1 = Math.Max(wall.Along(a), wall.Along(b));
                var overlap = Math.Min(t1, o.Offset + o.Width) - Math.Max(t0, o.Offset);
                if (overlap > 0)
                {
                    used.Add(s);
                    found |= overlap >= o.Width * 0.8;
                }
            }

            if (!found)
            {
                findings.Error("window_missing", name, $"{o.Tag} ({o.Wall.Id}, {Inch.Text(o.Width)} from {c.D(o.Start).Text()}): no window lines on layer {c.Layers.Glazing} across the opening.");
            }
        }

        var extras = c.Glazing.Where(s => !used.Contains(s)).Take(MaxPerKind);
        foreach (var s in extras)
        {
            findings.Warning("window_extra", name, $"Window line {s.Handle} ({s.A.Text()} to {s.B.Text()}) is in no planned window.");
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // Text and dimensions
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckText(Context c, FindingList findings)
    {
        var name = c.Floor.Name;
        var texts = c.Snapshot.Texts;
        foreach (var room in c.Floor.Rooms.Where(r => r.Type != "shaft"))
        {
            var shape = room.Shape;
            var inside = texts.Where(t => shape.Contains(c.P(t.Bounds.Center))).ToList();
            if (!inside.Any(t => LabelMatches(t.Text, room)))
            {
                findings.Warning("label_missing", name, $"{room.Name} has no name label inside it (TEXT at about {c.D(shape.Centroid()).Text()}).");
            }
        }

        var overlaps = 0;
        for (var i = 0; i < texts.Count && overlaps < MaxPerKind; i++)
        {
            for (var j = i + 1; j < texts.Count && overlaps < MaxPerKind; j++)
            {
                var shrink = Math.Min(texts[i].Height, texts[j].Height) * 0.1;
                if (texts[i].Bounds.Inflate(-shrink).Overlaps(texts[j].Bounds.Inflate(-shrink)))
                {
                    overlaps++;
                    findings.Warning("text_overlap", name, $"Texts {texts[i].Handle} \"{Short(texts[i].Text)}\" and {texts[j].Handle} \"{Short(texts[j].Text)}\" overlap near {texts[i].Bounds.Center.Text()}: move one.");
                }
            }
        }

        var crossings = 0;
        foreach (var text in texts)
        {
            if (crossings >= MaxPerKind)
            {
                break;
            }

            var box = text.Bounds.Inflate(-text.Height * 0.1);
            var wall = c.Walls.FirstOrDefault(w => SegmentCrossesBox(w, box));
            if (wall is not null)
            {
                crossings++;
                findings.Warning("text_on_line", name, $"Text {text.Handle} \"{Short(text.Text)}\" crosses wall line {wall.Handle}: move it inside the room.");
                continue;
            }

            foreach (var info in c.Analysis.Openings.Where(i => i.Swings.Count > 0))
            {
                var corners = new[] { new Pt(box.MinX, box.MinY), new Pt(box.MaxX, box.MinY), new Pt(box.MaxX, box.MaxY), new Pt(box.MinX, box.MaxY), box.Center };
                if (info.Swings.Any(s => corners.Any(p => s.Contains(c.P(p), 0))))
                {
                    crossings++;
                    findings.Warning("text_on_swing", name, $"Text {text.Handle} \"{Short(text.Text)}\" sits in the swing of {info.Opening.Tag}: move it clear of the door.");
                    break;
                }
            }
        }
    }

    private static bool LabelMatches(string text, PlanRoom room)
    {
        var t = Normalize(text);
        if (t.Length == 0)
        {
            return false;
        }

        if (t.Contains(Normalize(room.Name), StringComparison.Ordinal))
        {
            return true;
        }

        string[] keys = room.Type switch
        {
            "living" => ["LIVING", "HALL", "DRAWING", "LOUNGE"],
            "dining" => ["DINING"],
            "kitchen" => ["KITCHEN", "KIT"],
            "bedroom" or "master_bedroom" => ["BED", "MBR", "BR"],
            "toilet" or "bath_wc" => ["TOILET", "WC", "BATH", "TOI"],
            "passage" or "lobby" or "foyer" => ["PASSAGE", "LOBBY", "FOYER", "CORRIDOR", "PASS"],
            "stair" => ["STAIR", "UP", "DN"],
            "parking" => ["PARKING", "CAR", "PORCH"],
            "pooja" => ["POOJA", "PUJA", "PRAYER"],
            "balcony" => ["BALCONY", "BAL"],
            "sitout" => ["SITOUT", "SIT", "VERANDAH"],
            "utility" or "wash" => ["UTILITY", "WASH"],
            "store" => ["STORE"],
            "study" => ["STUDY"],
            _ => [],
        };
        return keys.Any(k => t.Contains(k, StringComparison.Ordinal));
    }

    private static string Normalize(string text) => new([.. text.ToUpperInvariant().Where(char.IsLetterOrDigit)]);

    private static string Short(string text) => text.Length <= 30 ? text : text[..27] + "...";

    private static bool SegmentCrossesBox(DrawnSegment s, Box box)
    {
        if (box.Width <= 0 || box.Height <= 0)
        {
            return false;
        }

        // Sample the segment: cheap and enough for text boxes a few inches big.
        var length = s.Length;
        var steps = Math.Max(2, (int)Math.Ceiling(length / Math.Max(0.5, Math.Min(box.Width, box.Height) / 2)));
        for (var i = 0; i <= steps; i++)
        {
            var p = s.A + ((s.B - s.A) * (i / (double)steps));
            if (box.Contains(p, 0))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckDimensions(Context c, FindingList findings)
    {
        var name = c.Floor.Name;
        var dims = c.Snapshot.Dimensions;
        if (dims.Count == 0)
        {
            findings.Hint("no_dimensions", name, "No dimensions in this floor's area yet.");
            return;
        }

        var lines = c.Snapshot.Segments;
        var count = 0;
        foreach (var dim in dims)
        {
            if (count >= MaxPerKind)
            {
                break;
            }

            if (dim.TextOverride is { Length: > 0 } text && !text.Contains("<>", StringComparison.Ordinal))
            {
                var shown = Drafting.FeetInches.TryParse(text.Replace("\\P", " ", StringComparison.Ordinal).Trim(), out var value) ? value : (double?)null;
                if (shown is null || Math.Abs(shown.Value - dim.Measurement) > 1.0 / 32)
                {
                    count++;
                    findings.Error("dim_text", name, $"Dimension {dim.Handle} shows \"{text}\" but measures {Inch.Text(dim.Measurement)}: remove the text override and fix the geometry instead.");
                    continue;
                }
            }

            if (!Inch.IsMultiple(dim.Measurement, 0.5))
            {
                count++;
                findings.Warning("dim_fraction", name, $"Dimension {dim.Handle} measures {Inch.Text(dim.Measurement)}, not a whole or half inch: one of its points is off; snap it to the wall corner.");
            }

            foreach (var p in new[] { dim.Point1, dim.Point2 })
            {
                if (!lines.Any(l => l.Distance(p) <= Tolerance))
                {
                    count++;
                    findings.Warning("dim_point", name, $"Dimension {dim.Handle}: its point {p.Text()} is not on any line; dimension from the wall faces and corners.");
                    break;
                }
            }
        }

        var box = c.Floor.Outline.Bounds;
        var origin = c.Origin;
        CheckChains(dims.Where(d => d.IsHorizontal).ToList(), horizontal: true, box.MinX + origin.X, box.MaxX + origin.X, name, findings);
        CheckChains(dims.Where(d => d.IsVertical).ToList(), horizontal: false, box.MinY + origin.Y, box.MaxY + origin.Y, name, findings);
    }

    private static void CheckChains(List<DrawnDimension> dims, bool horizontal, double min, double max, string floor, FindingList findings)
    {
        if (dims.Count == 0)
        {
            return;
        }

        var size = max - min;
        var axis = horizontal ? "width" : "depth";
        double Lo(DrawnDimension d) => horizontal ? Math.Min(d.Point1.X, d.Point2.X) : Math.Min(d.Point1.Y, d.Point2.Y);
        double Hi(DrawnDimension d) => horizontal ? Math.Max(d.Point1.X, d.Point2.X) : Math.Max(d.Point1.Y, d.Point2.Y);
        double Line(DrawnDimension d) => horizontal ? d.LinePoint.Y : d.LinePoint.X;

        if (!dims.Any(d => Math.Abs(d.Measurement - size) <= Tolerance && Math.Abs(Lo(d) - min) <= Tolerance))
        {
            findings.Error("dim_overall", floor, $"No overall {axis} dimension of {Inch.Text(size)} (outer face to outer face) was found; add it outside the chain.");
        }

        var groups = new List<List<DrawnDimension>>();
        foreach (var dim in dims.OrderBy(Line))
        {
            var group = groups.FirstOrDefault(g => Math.Abs(Line(g[0]) - Line(dim)) <= Tolerance);
            if (group is null)
            {
                groups.Add([dim]);
            }
            else
            {
                group.Add(dim);
            }
        }

        foreach (var group in groups.Where(g => g.Count >= 2))
        {
            var ordered = group.OrderBy(Lo).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var gap = Lo(ordered[i]) - Hi(ordered[i - 1]);
                if (Math.Abs(gap) > Tolerance)
                {
                    findings.Error("dim_chain", floor, $"The {axis} dimension chain at {Inch.Text(Line(ordered[0]))} has {(gap > 0 ? "a gap" : "an overlap")} of {Inch.Text(Math.Abs(gap))} between {ordered[i - 1].Handle} and {ordered[i].Handle}.");
                }
            }

            var from = Lo(ordered[0]);
            var to = Hi(ordered[^1]);
            var total = ordered.Sum(d => d.Measurement);
            if (from >= min - 1 && to <= max + 1 && (to - from) > size * 0.5 && Math.Abs(total - size) > Tolerance && (Math.Abs(from - min) > Tolerance || Math.Abs(to - max) > Tolerance))
            {
                findings.Error("dim_sum", floor, string.Create(CultureInfo.InvariantCulture, $"The {axis} chain at {Inch.Text(Line(ordered[0]))} adds up to {Inch.Text(total)}, but the building is {Inch.Text(size)}: a segment is missing at the {(Math.Abs(from - min) > Tolerance ? (horizontal ? "west" : "south") : (horizontal ? "east" : "north"))} end."));
            }
        }
    }

    // -----------------------------------------------------------------------------------------------------------
    // Layers
    // -----------------------------------------------------------------------------------------------------------

    private static void CheckLayers(Context c, FindingList findings, string stage)
    {
        var name = c.Floor.Name;
        var onZero = c.Snapshot.Objects.Where(o => Same(o.Layer, "0") || (Same(o.Layer, "Defpoints") && o.Kind != "dimension")).ToList();
        if (onZero.Count > 0)
        {
            findings.Warning("layer_zero", name, $"{onZero.Count} object{(onZero.Count == 1 ? " is" : "s are")} on layer {onZero[0].Layer} ({Handles(onZero)}): move them to their A-* layer (CHPROP / -LAYER).");
        }

        if (stage is "all" or "annotation")
        {
            var dims = c.Snapshot.Objects.Where(o => o.Kind == "dimension" && !Same(o.Layer, c.Layers.Dims)).ToList();
            if (dims.Count > 0)
            {
                findings.Warning("layer_dims", name, $"{dims.Count} dimension{(dims.Count == 1 ? " is" : "s are")} not on {c.Layers.Dims} ({Handles(dims)}).");
            }

            var texts = c.Snapshot.Objects.Where(o => o.Kind is "text" or "mtext" && !Same(o.Layer, c.Layers.Text) && !IsAnnotationLayer(o.Layer)).ToList();
            if (texts.Count > 0)
            {
                findings.Warning("layer_text", name, $"{texts.Count} text{(texts.Count == 1 ? " is" : "s are")} not on {c.Layers.Text} ({Handles(texts)}).");
            }
        }

        var hatches = c.Snapshot.Objects.Where(o => o.Kind == "hatch" && !o.Layer.Contains("HATCH", StringComparison.OrdinalIgnoreCase) && !o.Layer.Contains("PRES", StringComparison.OrdinalIgnoreCase)).ToList();
        if (hatches.Count > 0)
        {
            findings.Warning("layer_hatch", name, $"{hatches.Count} hatch{(hatches.Count == 1 ? " is" : "es are")} not on A-HATCH ({Handles(hatches)}).");
        }

        var arcsOnWall = c.Snapshot.Arcs.Where(a => Same(a.Layer, c.Layers.Wall) && a.Radius >= 12 && a.Radius <= 60).ToList();
        if (arcsOnWall.Count > 0)
        {
            findings.Error("layer_door", name, $"{arcsOnWall.Count} door swing arc{(arcsOnWall.Count == 1 ? " is" : "s are")} on {c.Layers.Wall} ({string.Join(", ", arcsOnWall.Take(8).Select(a => a.Handle))}): put door leaves and swings on {c.Layers.Door}.");
        }
    }

    private static bool IsAnnotationLayer(string layer) =>
        layer.Contains("ANNO", StringComparison.OrdinalIgnoreCase) || layer.Contains("TEXT", StringComparison.OrdinalIgnoreCase) ||
        layer.Contains("TTLB", StringComparison.OrdinalIgnoreCase) || layer.Contains("TITLE", StringComparison.OrdinalIgnoreCase) ||
        layer.StartsWith("E-", StringComparison.OrdinalIgnoreCase) || layer.StartsWith("P-", StringComparison.OrdinalIgnoreCase);

    private static string Handles(IEnumerable<DrawnObject> objects)
    {
        var list = objects.Select(o => o.Handle).ToList();
        return string.Join(", ", list.Take(8)) + (list.Count > 8 ? ", ..." : string.Empty);
    }
}
