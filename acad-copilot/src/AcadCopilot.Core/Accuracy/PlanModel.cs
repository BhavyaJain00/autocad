namespace AcadCopilot.Core.Accuracy;

/// <summary>
/// A structured house plan in plot coordinates (inches; the plot's south-west corner is 0,0, +X east, +Y north), the
/// input of validate_plan and the reference of check_drawing. Built by <see cref="PlanParser"/>.
/// </summary>
public sealed class HousePlan
{
    /// <summary>Plan name ("30x40 east G+1").</summary>
    public string Name { get; init; } = "House plan";

    /// <summary>Plot width (X, inches).</summary>
    public double PlotWidth { get; init; }

    /// <summary>Plot depth (Y, inches).</summary>
    public double PlotDepth { get; init; }

    /// <summary>north / south / east / west, or null.</summary>
    public string? RoadSide { get; init; }

    /// <summary>Required setbacks by side ("north", "south", "east", "west"), inches.</summary>
    public IReadOnlyDictionary<string, double> Setbacks { get; init; } = new Dictionary<string, double>();

    /// <summary>Default thickness of outer walls (9").</summary>
    public double OuterWall { get; init; } = 9;

    /// <summary>Default thickness of inner walls (4 1/2").</summary>
    public double InnerWall { get; init; } = 4.5;

    /// <summary>Floor to floor height (10'-6" default).</summary>
    public double FloorHeight { get; init; } = 126;

    /// <summary>True when floor_height was given.</summary>
    public bool FloorHeightGiven { get; init; }

    /// <summary>The floors, ground first.</summary>
    public IReadOnlyList<PlanFloor> Floors { get; init; } = [];

    /// <summary>The plot as a polygon.</summary>
    public Polygon Plot => Polygon.Rect(0, 0, PlotWidth, PlotDepth);
}

/// <summary>One floor of a <see cref="HousePlan"/>.</summary>
public sealed class PlanFloor
{
    /// <summary>"Ground floor".</summary>
    public string Name { get; init; } = "Ground floor";

    /// <summary>0 = ground.</summary>
    public int Index { get; init; }

    /// <summary>Outer face of the outer walls (building outline).</summary>
    public required Polygon Outline { get; init; }

    /// <summary>True when the outline came from the plan (not derived from the walls).</summary>
    public bool OutlineGiven { get; init; }

    /// <summary>Where the plot's 0,0 sits in the drawing for this floor (null = not given).</summary>
    public Pt? Origin { get; init; }

    /// <summary>Rooms (clear spaces between wall faces).</summary>
    public IReadOnlyList<PlanRoom> Rooms { get; init; } = [];

    /// <summary>Walls (centreline + thickness).</summary>
    public IReadOnlyList<PlanWall> Walls { get; init; } = [];

    /// <summary>Doors and openings.</summary>
    public IReadOnlyList<PlanOpening> Doors { get; init; } = [];

    /// <summary>Windows and ventilators.</summary>
    public IReadOnlyList<PlanOpening> Windows { get; init; } = [];

    /// <summary>The stair, if any.</summary>
    public PlanStair? Stair { get; init; }

    /// <summary>The wall with this id (case-insensitive) or null.</summary>
    public PlanWall? Wall(string id) => Walls.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The room with this name (case-insensitive) or null.</summary>
    public PlanRoom? Room(string name) => Rooms.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A room: its clear (inside face to inside face) shape.</summary>
public sealed class PlanRoom
{
    /// <summary>"Bedroom 1".</summary>
    public required string Name { get; init; }

    /// <summary>Canonical type (<see cref="RoomTypes"/>).</summary>
    public required string Type { get; init; }

    /// <summary>The clear shape.</summary>
    public required Polygon Shape { get; init; }

    /// <summary>Cars (parking).</summary>
    public int Cars { get; init; } = 1;

    /// <summary>window / shaft / exhaust / null.</summary>
    public string? Ventilation { get; init; }

    /// <summary>JSON path for messages.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Clear width (X extent) for rectangles; bounding width otherwise.</summary>
    public double Width => Shape.Bounds.Width;

    /// <summary>Clear depth (Y extent).</summary>
    public double Depth => Shape.Bounds.Height;

    /// <summary>Shorter side.</summary>
    public double Short => Math.Min(Width, Depth);

    /// <summary>Longer side.</summary>
    public double Long => Math.Max(Width, Depth);
}

/// <summary>A wall given by its centreline and thickness.</summary>
public sealed class PlanWall
{
    /// <summary>Id ("W1", "OS").</summary>
    public required string Id { get; init; }

    /// <summary>Centreline start.</summary>
    public Pt From { get; init; }

    /// <summary>Centreline end.</summary>
    public Pt To { get; init; }

    /// <summary>Thickness.</summary>
    public double Thickness { get; set; }

    /// <summary>True when the thickness was given.</summary>
    public bool ThicknessGiven { get; init; }

    /// <summary>On the building outline (set by the validator).</summary>
    public bool Outer { get; set; }

    /// <summary>Extension beyond <see cref="From"/> (half the thickness when the end meets another wall).</summary>
    public double ExtendStart { get; set; }

    /// <summary>Extension beyond <see cref="To"/>.</summary>
    public double ExtendEnd { get; set; }

    /// <summary>JSON path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Centreline length.</summary>
    public double Length => From.DistanceTo(To);

    /// <summary>Unit direction From -&gt; To.</summary>
    public Pt Direction => (To - From).Unit();

    /// <summary>Unit normal to the left of the direction.</summary>
    public Pt Normal => new(-Direction.Y, Direction.X);

    /// <summary>True when horizontal (along X).</summary>
    public bool IsHorizontal => Math.Abs(Direction.Y) < 1e-9;

    /// <summary>True when vertical (along Y).</summary>
    public bool IsVertical => Math.Abs(Direction.X) < 1e-9;

    /// <summary>Point at <paramref name="along"/> inches from From on the centreline, <paramref name="side"/> inches to the left.</summary>
    public Pt At(double along, double side = 0) => From + (Direction * along) + (Normal * side);

    /// <summary>Position of <paramref name="p"/> along the centreline (from From).</summary>
    public double Along(Pt p) => (p - From).Dot(Direction);

    /// <summary>Signed distance of <paramref name="p"/> from the centreline (left positive).</summary>
    public double Side(Pt p) => (p - From).Dot(Normal);

    /// <summary>True when <paramref name="p"/> is within the wall body (with its end extensions).</summary>
    public bool Contains(Pt p, double eps = 1e-6)
    {
        var t = Along(p);
        return t >= -ExtendStart - eps && t <= Length + ExtendEnd + eps && Math.Abs(Side(p)) <= (Thickness / 2) + eps;
    }

    /// <summary>The wall body (with extensions) as a polygon.</summary>
    public Polygon Body() => new(
    [
        At(-ExtendStart, -Thickness / 2), At(Length + ExtendEnd, -Thickness / 2),
        At(Length + ExtendEnd, Thickness / 2), At(-ExtendStart, Thickness / 2),
    ]);
}

/// <summary>A door, opening, window or ventilator in a wall.</summary>
public sealed class PlanOpening
{
    /// <summary>Tag ("D1", "MD", "W2", "V1").</summary>
    public required string Tag { get; init; }

    /// <summary>True for doors and openings, false for windows.</summary>
    public bool IsDoor { get; init; }

    /// <summary>The host wall.</summary>
    public required PlanWall Wall { get; init; }

    /// <summary>Distance along the wall centreline from its start to the near jamb.</summary>
    public double Offset { get; init; }

    /// <summary>Clear width.</summary>
    public double Width { get; init; }

    /// <summary>Doors: hinged, double, sliding, opening. Windows: window, ventilator, french.</summary>
    public string Kind { get; init; } = "hinged";

    /// <summary>Room the door swings into (null = decided by the validator).</summary>
    public string? SwingInto { get; init; }

    /// <summary>"start" / "end" jamb of the hinge (null = the one nearer a corner).</summary>
    public string? Hinge { get; init; }

    /// <summary>Sill height (windows) or 0.</summary>
    public double Sill { get; init; }

    /// <summary>Head height.</summary>
    public double Head { get; init; } = 84;

    /// <summary>JSON path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The start jamb on the centreline.</summary>
    public Pt Start => Wall.At(Offset);

    /// <summary>The end jamb on the centreline.</summary>
    public Pt End => Wall.At(Offset + Width);

    /// <summary>The middle of the opening on the centreline.</summary>
    public Pt Center => Wall.At(Offset + (Width / 2));

    /// <summary>True for a door leaf that swings (hinged or double).</summary>
    public bool Swings => IsDoor && Kind is "hinged" or "double";
}

/// <summary>The stair of a floor.</summary>
public sealed class PlanStair
{
    /// <summary>dog_legged, straight, l_shaped, u_shaped (open well), spiral.</summary>
    public string Type { get; init; } = "dog_legged";

    /// <summary>The stair's box (clear), or null when only the numbers were given.</summary>
    public Box? Box { get; init; }

    /// <summary>Clear flight width.</summary>
    public double FlightWidth { get; init; }

    /// <summary>Riser height.</summary>
    public double Riser { get; init; }

    /// <summary>Tread (going).</summary>
    public double Tread { get; init; }

    /// <summary>Number of risers floor to floor.</summary>
    public int Risers { get; init; }

    /// <summary>Mid-landing depth (null = not given).</summary>
    public double? Landing { get; init; }

    /// <summary>JSON path.</summary>
    public string Path { get; init; } = string.Empty;
}

/// <summary>Room types and their groups.</summary>
public static class RoomTypes
{
    /// <summary>All canonical types (the tool's enum).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "living", "dining", "kitchen", "bedroom", "master_bedroom", "toilet", "bath_wc", "passage", "lobby", "foyer",
        "stair", "parking", "pooja", "store", "utility", "study", "family", "dress", "wash", "balcony", "sitout",
        "porch", "terrace", "shaft", "other",
    ];

    private static readonly HashSet<string> Habitable = new(StringComparer.Ordinal) { "living", "bedroom", "master_bedroom", "kitchen" };
    private static readonly HashSet<string> SemiHabitable = new(StringComparer.Ordinal) { "dining", "study", "family" };
    private static readonly HashSet<string> Wet = new(StringComparer.Ordinal) { "kitchen", "toilet", "bath_wc", "utility", "wash" };
    private static readonly HashSet<string> Outdoor = new(StringComparer.Ordinal) { "parking", "balcony", "sitout", "porch", "terrace" };
    private static readonly HashSet<string> Circulation = new(StringComparer.Ordinal) { "passage", "lobby", "foyer", "stair" };
    private static readonly HashSet<string> Private = new(StringComparer.Ordinal) { "bedroom", "master_bedroom", "toilet", "bath_wc", "kitchen", "store", "pooja", "dress", "utility", "wash", "study" };

    /// <summary>Living, bedrooms, kitchen: a window on an outside wall is required.</summary>
    public static bool IsHabitable(string type) => Habitable.Contains(type);

    /// <summary>Dining, study, family: a window on an outside wall is recommended.</summary>
    public static bool IsSemiHabitable(string type) => SemiHabitable.Contains(type);

    /// <summary>Kitchen, toilets, utility, wash: stack them over floors.</summary>
    public static bool IsWet(string type) => Wet.Contains(type);

    /// <summary>Toilet or bath + WC.</summary>
    public static bool IsToilet(string type) => type is "toilet" or "bath_wc";

    /// <summary>Open-air spaces (may lie outside the outline, may be open to the outside).</summary>
    public static bool IsOutdoor(string type) => Outdoor.Contains(type);

    /// <summary>Passages, lobbies, foyers and the stair.</summary>
    public static bool IsCirculation(string type) => Circulation.Contains(type);

    /// <summary>Rooms a door should swing into rather than out of.</summary>
    public static bool IsPrivate(string type) => Private.Contains(type);

    /// <summary>Spaces nobody walks into (shafts).</summary>
    public static bool NeedsNoAccess(string type) => type == "shaft";

    /// <summary>The canonical type of a name or synonym ("Bathroom" -&gt; bath_wc), or null.</summary>
    public static string? Normalize(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (All.Contains(t))
        {
            return t;
        }

        return t switch
        {
            "hall" or "drawing" or "drawing_room" or "living_room" or "lounge" => "living",
            "bed" or "bed_room" or "guest_bedroom" or "kids_bedroom" or "children_bedroom" => "bedroom",
            "master" or "master_bed" or "mbr" => "master_bedroom",
            "wc" or "water_closet" or "powder" or "powder_room" => "toilet",
            "bath" or "bathroom" or "attached_toilet" or "common_toilet" or "bath_and_wc" or "bath_+_wc" => "bath_wc",
            "corridor" or "circulation" => "passage",
            "entrance" or "entry" => "foyer",
            "staircase" or "stairs" or "steps" => "stair",
            "car_parking" or "garage" or "car_porch" => "parking",
            "puja" or "prayer" or "mandir" => "pooja",
            "storage" => "store",
            "duct" or "ots" or "void" => "shaft",
            "verandah" or "veranda" => "sitout",
            "open_terrace" => "terrace",
            "wash_area" => "wash",
            "dressing" => "dress",
            _ => null,
        };
    }
}
