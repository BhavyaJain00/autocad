namespace AcadCopilot.Core.Accuracy;

/// <summary>What lies at a point of a floor.</summary>
/// <param name="Kind">room, wall, outside or gap.</param>
/// <param name="Name">Room name or wall id (empty for outside / gap).</param>
public readonly record struct SpaceHit(string Kind, string Name)
{
    /// <summary>Outside the building.</summary>
    public static SpaceHit Outside { get; } = new("outside", string.Empty);

    /// <summary>Inside the outline but in no room and no wall.</summary>
    public static SpaceHit Gap { get; } = new("gap", string.Empty);

    /// <summary>True for a room.</summary>
    public bool IsRoom => Kind == "room";

    /// <summary>True outside.</summary>
    public bool IsOutside => Kind == "outside";

    /// <summary>"Bedroom 1", "wall W3", "outside", "a gap".</summary>
    public string Describe() => Kind switch
    {
        "room" => Name,
        "wall" => "wall " + Name,
        "outside" => "outside",
        _ => "a gap between rooms",
    };
}

/// <summary>A door leaf's swing: a quarter circle around the hinge.</summary>
/// <param name="Hinge">Hinge point on the wall face.</param>
/// <param name="Closed">Unit vector from the hinge along the closed leaf (towards the other jamb).</param>
/// <param name="Open">Unit vector from the hinge into the room the door swings into.</param>
/// <param name="Radius">Leaf width.</param>
public sealed record DoorSwing(Pt Hinge, Pt Closed, Pt Open, double Radius)
{
    /// <summary>True when <paramref name="p"/> lies inside the swept quarter circle (with a small inward margin).</summary>
    public bool Contains(Pt p, double margin = 0.25)
    {
        var v = p - Hinge;
        return v.Length < Radius - margin && v.Dot(Closed) > margin && v.Dot(Open) > margin;
    }

    /// <summary>Sample points inside the swept area.</summary>
    public IEnumerable<Pt> Samples()
    {
        foreach (var r in new[] { 0.3, 0.6, 0.85, 0.97 })
        {
            foreach (var degrees in new[] { 6.0, 25, 45, 65, 84 })
            {
                var a = degrees * Math.PI / 180;
                yield return Hinge + (((Closed * Math.Cos(a)) + (Open * Math.Sin(a))) * (Radius * r));
            }
        }
    }

    /// <summary>Point on the arc halfway through the swing (for matching drawn arcs).</summary>
    public Pt ArcMiddle => Hinge + (((Closed * Math.Cos(Math.PI / 4)) + (Open * Math.Sin(Math.PI / 4))) * Radius);
}

/// <summary>A door or window with the spaces on both sides worked out.</summary>
public sealed class OpeningInfo
{
    /// <summary>The opening.</summary>
    public required PlanOpening Opening { get; init; }

    /// <summary>Space on the wall's left (normal) side.</summary>
    public SpaceHit Left { get; init; }

    /// <summary>Space on the right side.</summary>
    public SpaceHit Right { get; init; }

    /// <summary>The room it swings into (doors), or null.</summary>
    public string? SwingRoom { get; set; }

    /// <summary>+1 = swings to the wall's left (normal) side, -1 = right, 0 = no swing.</summary>
    public int SwingSide { get; set; }

    /// <summary>The leaves' swings (one; two for a double door; none for sliding doors and openings).</summary>
    public List<DoorSwing> Swings { get; } = [];

    /// <summary>The hinge jamb ("start" / "end") actually used.</summary>
    public string? HingeUsed { get; set; }

    /// <summary>"Living / Kitchen".</summary>
    public string Between => Left.Describe() + " / " + Right.Describe();
}

/// <summary>
/// Geometry of one floor shared by validate_plan and check_drawing: wall end extensions and outer walls, what lies at a
/// point, junctions along a wall, the sides and swings of doors.
/// </summary>
public sealed class FloorAnalysis
{
    /// <summary>Analyses <paramref name="floor"/> (sets the walls' end extensions and outer flags).</summary>
    public FloorAnalysis(PlanFloor floor, double outerWall)
    {
        ArgumentNullException.ThrowIfNull(floor);
        Floor = floor;
        foreach (var wall in floor.Walls)
        {
            wall.Outer = OnOutline(wall, floor.Outline, Math.Max(outerWall, wall.Thickness));
            wall.ExtendStart = MeetsOtherWall(wall, wall.From) ? wall.Thickness / 2 : 0;
            wall.ExtendEnd = MeetsOtherWall(wall, wall.To) ? wall.Thickness / 2 : 0;
        }

        Openings = [.. floor.Doors.Concat(floor.Windows).Select(Describe)];
    }

    /// <summary>The floor.</summary>
    public PlanFloor Floor { get; }

    /// <summary>Doors and windows with their sides (and swings for doors).</summary>
    public IReadOnlyList<OpeningInfo> Openings { get; }

    /// <summary>What lies at <paramref name="p"/>; <paramref name="except"/> = a room to ignore.</summary>
    public SpaceHit At(Pt p, string? except = null)
    {
        foreach (var room in Floor.Rooms)
        {
            if (!string.Equals(room.Name, except, StringComparison.Ordinal) && room.Shape.Contains(p, 1e-6))
            {
                return new SpaceHit("room", room.Name);
            }
        }

        foreach (var wall in Floor.Walls)
        {
            if (wall.Contains(p, 1e-6))
            {
                return new SpaceHit("wall", wall.Id);
            }
        }

        return Floor.Outline.Contains(p, 1e-6) ? SpaceHit.Gap : SpaceHit.Outside;
    }

    /// <summary>Intervals along <paramref name="wall"/>'s centreline occupied by other walls meeting or crossing it.</summary>
    public List<(double From, double To, string Id)> Junctions(PlanWall wall)
    {
        var list = new List<(double, double, string)>();
        var half = wall.Thickness / 2;
        foreach (var other in Floor.Walls)
        {
            if (ReferenceEquals(other, wall))
            {
                continue;
            }

            var corners = other.Body().Points;
            var sides = corners.Select(wall.Side).ToList();
            if (sides.Max() <= -half + 1e-6 || sides.Min() >= half - 1e-6)
            {
                continue;
            }

            var along = corners.Select(wall.Along).ToList();
            var from = along.Min();
            var to = along.Max();
            if (to <= -wall.ExtendStart + 1e-6 || from >= wall.Length + wall.ExtendEnd - 1e-6)
            {
                continue;
            }

            // A parallel wall lying on this one is a duplicate, not a junction.
            var parallel = Math.Abs((other.Direction.X * wall.Direction.Y) - (other.Direction.Y * wall.Direction.X)) < 1e-6;
            if (parallel)
            {
                continue;
            }

            list.Add((from, to, other.Id));
        }

        return list;
    }

    /// <summary>Distance from <paramref name="along"/> to the nearest junction face or wall end in <paramref name="direction"/> (-1 towards the start, +1 towards the end).</summary>
    public double DistanceToCorner(PlanWall wall, double along, int direction)
    {
        var best = direction < 0 ? along : wall.Length - along;
        foreach (var (from, to, _) in Junctions(wall))
        {
            if (direction < 0 && to <= along + 1e-6)
            {
                best = Math.Min(best, along - to);
            }
            else if (direction > 0 && from >= along - 1e-6)
            {
                best = Math.Min(best, from - along);
            }
        }

        return Math.Max(0, best);
    }

    private OpeningInfo Describe(PlanOpening opening)
    {
        var wall = opening.Wall;
        var reach = (wall.Thickness / 2) + 6;
        var center = opening.Center;
        var left = At(center + (wall.Normal * reach));
        var right = At(center - (wall.Normal * reach));
        var info = new OpeningInfo { Opening = opening, Left = left, Right = right };
        if (!opening.Swings || !left.IsRoom && !right.IsRoom)
        {
            return info;
        }

        int side;
        if (opening.SwingInto is { } into)
        {
            side = string.Equals(into, left.Name, StringComparison.OrdinalIgnoreCase) && left.IsRoom ? 1
                : string.Equals(into, right.Name, StringComparison.OrdinalIgnoreCase) && right.IsRoom ? -1
                : 0;
        }
        else
        {
            side = DefaultSwingSide(left, right);
        }

        if (side == 0)
        {
            return info;
        }

        info.SwingSide = side;
        info.SwingRoom = side > 0 ? left.Name : right.Name;
        var open = wall.Normal * side;
        var face = wall.Thickness / 2;
        var startJamb = wall.At(opening.Offset, face * side);
        var endJamb = wall.At(opening.Offset + opening.Width, face * side);
        if (opening.Kind == "double")
        {
            var leaf = opening.Width / 2;
            info.Swings.Add(new DoorSwing(startJamb, wall.Direction, open, leaf));
            info.Swings.Add(new DoorSwing(endJamb, wall.Direction * -1, open, leaf));
            info.HingeUsed = "both";
            return info;
        }

        var hinge = opening.Hinge;
        if (hinge is null)
        {
            var toStart = DistanceToCorner(wall, opening.Offset, -1);
            var toEnd = DistanceToCorner(wall, opening.Offset + opening.Width, 1);
            hinge = toEnd < toStart ? "end" : "start";
        }

        info.HingeUsed = hinge;
        info.Swings.Add(hinge == "start"
            ? new DoorSwing(startJamb, wall.Direction, open, opening.Width)
            : new DoorSwing(endJamb, wall.Direction * -1, open, opening.Width));
        return info;
    }

    private int DefaultSwingSide(SpaceHit left, SpaceHit right)
    {
        if (!left.IsRoom)
        {
            return -1;
        }

        if (!right.IsRoom)
        {
            return 1;
        }

        var l = Floor.Room(left.Name)!.Type;
        var r = Floor.Room(right.Name)!.Type;
        if (RoomTypes.IsToilet(l) != RoomTypes.IsToilet(r))
        {
            return RoomTypes.IsToilet(l) ? 1 : -1;
        }

        if (RoomTypes.IsOutdoor(l) != RoomTypes.IsOutdoor(r))
        {
            return RoomTypes.IsOutdoor(l) ? -1 : 1;
        }

        if (RoomTypes.IsPrivate(l) != RoomTypes.IsPrivate(r))
        {
            return RoomTypes.IsPrivate(l) ? 1 : -1;
        }

        if (RoomTypes.IsCirculation(l) != RoomTypes.IsCirculation(r))
        {
            return RoomTypes.IsCirculation(l) ? -1 : 1;
        }

        return 1;
    }

    private bool MeetsOtherWall(PlanWall wall, Pt end)
    {
        foreach (var other in Floor.Walls)
        {
            if (ReferenceEquals(other, wall))
            {
                continue;
            }

            var t = other.Along(end);
            if (t >= -0.01 && t <= other.Length + 0.01 && Math.Abs(other.Side(end)) <= (other.Thickness / 2) + 0.01)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="wall"/> runs along an edge of <paramref name="outline"/> (its centreline within half the outer wall thickness).</summary>
    public static bool OnOutline(PlanWall wall, Polygon outline, double outerWall)
    {
        var mid = wall.At(wall.Length / 2);
        foreach (var (a, b) in outline.Edges())
        {
            var edge = (b - a).Unit();
            var parallel = Math.Abs((edge.X * wall.Direction.Y) - (edge.Y * wall.Direction.X)) < 1e-6;
            if (parallel && Polygon.SegmentDistance(mid, a, b) <= (outerWall / 2) + 0.6)
            {
                return true;
            }
        }

        return false;
    }
}
