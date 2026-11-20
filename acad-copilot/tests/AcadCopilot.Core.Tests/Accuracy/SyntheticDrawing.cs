using AcadCopilot.Core.Accuracy;

namespace AcadCopilot.Core.Tests.Accuracy;

/// <summary>
/// Draws a floor of a plan the way a careful draftsman would (as check_drawing reads it): wall faces along every room edge
/// and the outline, broken at the openings with jamb lines, door arcs on A-DOOR, window lines on A-GLAZ, room names on
/// A-ANNO-TEXT, the south and west dimension chains plus overall dimensions on A-ANNO-DIMS.
/// </summary>
internal static class SyntheticDrawing
{
    public static DrawingSnapshot Draw(HousePlan plan, PlanFloor floor, Pt origin)
    {
        var analysis = new FloorAnalysis(floor, plan.OuterWall);
        var snapshot = new DrawingSnapshot();
        var handle = 0x100;
        string H() => (handle++).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        Pt D(Pt p) => p + origin;

        void Segment(string layer, Pt a, Pt b)
        {
            var h = H();
            snapshot.Segments.Add(new DrawnSegment(h, layer, D(a), D(b)));
            snapshot.Objects.Add(new DrawnObject(h, layer, "line"));
        }

        // Wall faces: every room edge that has a wall behind it, and the outline, broken at the openings.
        var edges = new List<(Pt A, Pt B, string? Room)>();
        foreach (var r in floor.Rooms.Where(r => !RoomTypes.IsOutdoor(r.Type)))
        {
            edges.AddRange(r.Shape.Edges().Select(e => (e.A, e.B, (string?)r.Name)));
        }

        edges.AddRange(floor.Outline.Edges().Select(e => (e.A, e.B, (string?)null)));
        foreach (var (a, b, room) in edges)
        {
            var length = a.DistanceTo(b);
            var dir = (b - a).Unit();
            var outward = room is null ? new Pt(-dir.Y, dir.X) : new Pt(dir.Y, -dir.X);
            Pt? start = null;
            Pt last = a;
            for (var t = 0.0; t <= length + 1e-9; t += 0.25)
            {
                var p = a + (dir * Math.Min(t, length));
                var behind = analysis.At(p + (outward * 0.75), room);
                var wall = behind.Kind == "wall" && !InOpening(analysis, p);
                if (wall && start is null)
                {
                    start = p;
                }
                else if (!wall && start is { } s)
                {
                    Segment("A-WALL", s, last);
                    start = null;
                }

                last = p;
            }

            if (start is { } open && open.DistanceTo(last) > 0.1)
            {
                Segment("A-WALL", open, last);
            }
        }

        // Jambs, door arcs and window lines.
        foreach (var info in analysis.Openings)
        {
            var o = info.Opening;
            var half = o.Wall.Thickness / 2;
            Segment("A-WALL", o.Wall.At(o.Offset, -half), o.Wall.At(o.Offset, half));
            Segment("A-WALL", o.Wall.At(o.Offset + o.Width, -half), o.Wall.At(o.Offset + o.Width, half));
            if (!o.IsDoor)
            {
                Segment("A-GLAZ", o.Wall.At(o.Offset), o.Wall.At(o.Offset + o.Width));
                continue;
            }

            foreach (var swing in info.Swings)
            {
                snapshot.Arcs.Add(Arc(swing, origin, H()));
                snapshot.Objects.Add(new DrawnObject(snapshot.Arcs[^1].Handle, "A-DOOR", "arc"));
            }
        }

        // Room names.
        foreach (var room in floor.Rooms)
        {
            var c = D(room.Shape.Centroid());
            var width = room.Name.Length * 4.5;
            var h = H();
            snapshot.Texts.Add(new DrawnText(h, "A-ANNO-TEXT", room.Name.ToUpperInvariant(), new Box(c.X - (width / 2), c.Y - 4.5, c.X + (width / 2), c.Y + 4.5), 9));
            snapshot.Objects.Add(new DrawnObject(h, "A-ANNO-TEXT", "text"));
        }

        // Dimension chains: south (horizontal) and west (vertical), plus overall.
        var box = floor.Outline.Bounds;
        var chains = PlanSchedule.Chains(analysis);
        var xs = chains["south"]!["at"]!.AsArray().Select(n => PlanParser.Length(n!.ToString())!.Value).ToList();
        var ys = chains["west"]!["at"]!.AsArray().Select(n => PlanParser.Length(n!.ToString())!.Value).ToList();
        for (var i = 1; i < xs.Count; i++)
        {
            Dim(snapshot, H(), new Pt(xs[i - 1], box.MinY), new Pt(xs[i], box.MinY), new Pt(xs[i], box.MinY - 42), 0, origin);
        }

        Dim(snapshot, H(), new Pt(box.MinX, box.MinY), new Pt(box.MaxX, box.MinY), new Pt(box.MaxX, box.MinY - 72), 0, origin);
        for (var i = 1; i < ys.Count; i++)
        {
            Dim(snapshot, H(), new Pt(box.MinX, ys[i - 1]), new Pt(box.MinX, ys[i]), new Pt(box.MinX - 42, ys[i]), Math.PI / 2, origin);
        }

        Dim(snapshot, H(), new Pt(box.MinX, box.MinY), new Pt(box.MinX, box.MaxY), new Pt(box.MinX - 72, box.MaxY), Math.PI / 2, origin);
        return snapshot;
    }

    public static DrawnArc Arc(DoorSwing swing, Pt origin, string handle)
    {
        var closed = Math.Atan2(swing.Closed.Y, swing.Closed.X);
        var open = Math.Atan2(swing.Open.Y, swing.Open.X);
        var ccw = (swing.Closed.X * swing.Open.Y) - (swing.Closed.Y * swing.Open.X) > 0;
        return ccw
            ? new DrawnArc(handle, "A-DOOR", swing.Hinge + origin, swing.Radius, closed, open)
            : new DrawnArc(handle, "A-DOOR", swing.Hinge + origin, swing.Radius, open, closed);
    }

    private static void Dim(DrawingSnapshot snapshot, string handle, Pt a, Pt b, Pt line, double rotation, Pt origin)
    {
        var measurement = rotation == 0 ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y);
        snapshot.Dimensions.Add(new DrawnDimension(handle, "A-ANNO-DIMS", a + origin, b + origin, line + origin, rotation, measurement, string.Empty));
        snapshot.Objects.Add(new DrawnObject(handle, "A-ANNO-DIMS", "dimension"));
    }

    private static bool InOpening(FloorAnalysis analysis, Pt p)
    {
        foreach (var info in analysis.Openings)
        {
            var o = info.Opening;
            var t = o.Wall.Along(p);
            if (t > o.Offset + 1e-6 && t < o.Offset + o.Width - 1e-6 && Math.Abs(o.Wall.Side(p)) <= (o.Wall.Thickness / 2) + 0.01)
            {
                return true;
            }
        }

        return false;
    }
}
