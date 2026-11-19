namespace AcadCopilot.Core.Services;

/// <summary>A drawing primitive of a symbol block, in block units (inches), base point at 0,0.</summary>
public abstract record SymbolShape;

/// <summary>A straight line.</summary>
public sealed record SymbolLine(double X1, double Y1, double X2, double Y2) : SymbolShape;

/// <summary>A full circle.</summary>
public sealed record SymbolCircle(double X, double Y, double Radius) : SymbolShape;

/// <summary>A circular arc, counter-clockwise from <paramref name="StartDegrees"/> to <paramref name="EndDegrees"/>.</summary>
public sealed record SymbolArc(double X, double Y, double Radius, double StartDegrees, double EndDegrees) : SymbolShape;

/// <summary>A polyline vertex; <paramref name="Bulge"/> = tan(arc angle / 4) of the segment to the next vertex (+ = counter-clockwise).</summary>
public readonly record struct SymbolVertex(double X, double Y, double Bulge = 0);

/// <summary>A 2D polyline, optionally closed, with a constant width (a wide arc segment draws a filled shape).</summary>
public sealed record SymbolPolyline(IReadOnlyList<SymbolVertex> Vertices, bool Closed, double Width = 0) : SymbolShape;

/// <summary>A filled triangle (a 2D SOLID; the fourth corner repeats the third).</summary>
public sealed record SymbolSolid(double X1, double Y1, double X2, double Y2, double X3, double Y3) : SymbolShape;

/// <summary>A fixed text (style STANDARD). <paramref name="Centered"/>: middle-centre at X,Y; otherwise left baseline.</summary>
public sealed record SymbolText(double X, double Y, double Height, string Text, bool Centered) : SymbolShape;

/// <summary>
/// An attribute definition of a symbol block (the one value -INSERT asks for, such as "SB1" or "110 SWR").
/// <paramref name="Centered"/>: middle-centre at X,Y; otherwise left baseline.
/// </summary>
public sealed record SymbolAttribute(string Tag, string Prompt, string Default, double X, double Y, double Height, bool Centered);

/// <summary>One symbol of the services library: a block definition plus what it means and where it goes.</summary>
/// <param name="BlockName">Block name in the drawing (E-SWITCHBOARD ...).</param>
/// <param name="Title">Legend text ("Switchboard").</param>
/// <param name="Description">Where and how it is used (Indian residential first-draft practice).</param>
/// <param name="Discipline">Electrical or plumbing.</param>
/// <param name="Layer">The layer to insert it on.</param>
/// <param name="BasePoint">Where the insertion point is, in words (for the AI).</param>
/// <param name="Shapes">Block geometry on layer 0, colour BYBLOCK (inches).</param>
/// <param name="Attribute">The attribute -INSERT asks for, or null.</param>
public sealed record SymbolDefinition(
    string BlockName,
    string Title,
    string Description,
    ServiceDiscipline Discipline,
    string Layer,
    string BasePoint,
    IReadOnlyList<SymbolShape> Shapes,
    SymbolAttribute? Attribute);

/// <summary>Axis-aligned bounds of a symbol (block units).</summary>
public readonly record struct SymbolBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Width.</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height.</summary>
    public double Height => MaxY - MinY;

    /// <summary>Centre X.</summary>
    public double CenterX => (MinX + MaxX) / 2;

    /// <summary>Centre Y.</summary>
    public double CenterY => (MinY + MaxY) / 2;
}

/// <summary>
/// The standard electrical and plumbing symbol library (Indian residential first-draft conventions), as data. The
/// library DXF (<see cref="SymbolLibraryDxf"/>), the insert_symbol_library tool and the legend are generated from it.
/// Units are inches (1 drawing unit = 1 inch); symbols are sized to read at 1/4" = 1'-0" (1:48); fixtures (WC, wash
/// basin, inspection chamber, tank) are drawn at about real size.
/// </summary>
public static class SymbolLibrary
{
    /// <summary>File name of the library (bundle Contents\Blocks\ and %LOCALAPPDATA%\AcadCopilot\blocks\).</summary>
    public const string FileName = "AcadCopilot-Services.dxf";

    /// <summary>Bundle sub-folder of the library (Contents\Blocks).</summary>
    public const string BundleFolder = "Blocks";

    /// <summary>The draft label of every services drawing.</summary>
    public static string DraftNote(ServiceDiscipline discipline) => discipline == ServiceDiscipline.Electrical
        ? "DRAFT - TO BE CHECKED BY A QUALIFIED ELECTRICAL ENGINEER"
        : "DRAFT - TO BE CHECKED BY A QUALIFIED PLUMBING ENGINEER";

    /// <summary>The second line of the draft note.</summary>
    public static string DraftDetail(ServiceDiscipline discipline) => discipline == ServiceDiscipline.Electrical
        ? "Indicative first-draft layout only. Circuits, wire sizes, MCB/RCCB ratings, earthing and loads to be designed and verified by the engineer."
        : "Indicative first-draft layout only. Pipe sizes, slopes, traps, vents and the connection to the sewer / septic tank to be designed and verified by the engineer.";

    /// <summary>Every symbol, electrical first.</summary>
    public static IReadOnlyList<SymbolDefinition> All { get; } = Build();

    /// <summary>The symbols of one discipline.</summary>
    public static IReadOnlyList<SymbolDefinition> Of(ServiceDiscipline discipline) => [.. All.Where(s => s.Discipline == discipline)];

    /// <summary>The symbol with block name <paramref name="blockName"/> (any case), or null.</summary>
    public static SymbolDefinition? Find(string? blockName) =>
        All.FirstOrDefault(s => string.Equals(s.BlockName, blockName?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Bounds of the symbol's geometry and attribute text (text widths estimated).</summary>
    public static SymbolBounds Bounds(SymbolDefinition symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var xs = new List<double>();
        var ys = new List<double>();
        void Add(double x, double y)
        {
            xs.Add(x);
            ys.Add(y);
        }

        void AddText(double x, double y, double height, string text, bool centered)
        {
            var width = Math.Max(1, text.Length) * height * 0.8;
            if (centered)
            {
                Add(x - (width / 2), y - (height / 2));
                Add(x + (width / 2), y + (height / 2));
            }
            else
            {
                Add(x, y);
                Add(x + width, y + height);
            }
        }

        foreach (var shape in symbol.Shapes)
        {
            switch (shape)
            {
                case SymbolLine l:
                    Add(l.X1, l.Y1);
                    Add(l.X2, l.Y2);
                    break;
                case SymbolCircle c:
                    Add(c.X - c.Radius, c.Y - c.Radius);
                    Add(c.X + c.Radius, c.Y + c.Radius);
                    break;
                case SymbolArc a:
                    var sweep = ((a.EndDegrees - a.StartDegrees) % 360 + 360) % 360;
                    for (var i = 0; i <= 16; i++)
                    {
                        var angle = (a.StartDegrees + (sweep * i / 16)) * Math.PI / 180;
                        Add(a.X + (a.Radius * Math.Cos(angle)), a.Y + (a.Radius * Math.Sin(angle)));
                    }

                    break;
                case SymbolPolyline p:
                    var half = p.Width / 2;
                    for (var i = 0; i < p.Vertices.Count; i++)
                    {
                        var v = p.Vertices[i];
                        Add(v.X - half, v.Y - half);
                        Add(v.X + half, v.Y + half);
                        var hasNext = i + 1 < p.Vertices.Count || p.Closed;
                        if (hasNext && Math.Abs(v.Bulge) > 1e-12)
                        {
                            var next = p.Vertices[(i + 1) % p.Vertices.Count];
                            foreach (var (x, y) in SymbolGeometry.ArcPoints(v.X, v.Y, next.X, next.Y, v.Bulge, 12))
                            {
                                Add(x - half, y - half);
                                Add(x + half, y + half);
                            }
                        }
                    }

                    break;
                case SymbolSolid s:
                    Add(s.X1, s.Y1);
                    Add(s.X2, s.Y2);
                    Add(s.X3, s.Y3);
                    break;
                case SymbolText t:
                    AddText(t.X, t.Y, t.Height, t.Text, t.Centered);
                    break;
            }
        }

        if (symbol.Attribute is { } attribute)
        {
            AddText(attribute.X, attribute.Y, attribute.Height, attribute.Default.Length > 0 ? attribute.Default : "XX", attribute.Centered);
        }

        return xs.Count == 0 ? new SymbolBounds(0, 0, 0, 0) : new SymbolBounds(xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    private static SymbolPolyline Rect(double x0, double y0, double x1, double y1) =>
        new([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], Closed: true);

    private static SymbolAttribute Id(string defaultValue, string prompt, double x, double y, double height = 4, bool centered = false) =>
        new("ID", prompt, defaultValue, x, y, height, centered);

    private static List<SymbolDefinition> Build()
    {
        const string Centre = "centre of the symbol";
        const string Back = "centre of the back edge (the wall side); the fixture extends towards -Y, so rotate it to face into the room";
        var cos45 = 4 * Math.Cos(Math.PI / 4);
        var e = ServiceDiscipline.Electrical;
        var p = ServiceDiscipline.Plumbing;
        return
        [
            new("E-SWITCHBOARD", "Switchboard (4'-0\" above floor)", "Switchboard (modular switches) at 4'-0\" above floor, inside the room next to the door on its latch (opening) side, about 6\" from the door frame.", e, ServiceLayers.Switches, Centre,
                [Rect(-6, -3, 6, 3), new SymbolLine(-3, -1.5, -3, 1.5), new SymbolLine(0, -1.5, 0, 1.5), new SymbolLine(3, -1.5, 3, 1.5)],
                Id("SB", "Switchboard number", 0, 4.5, centered: true)),
            new("E-SOCKET-5A", "5A socket outlet (1'-0\" above floor)", "5A / 6A socket outlet at 1'-0\" above floor (beside beds, study table, TV); 3'-6\" above floor over kitchen and wash counters.", e, ServiceLayers.Power, Centre,
                [new SymbolArc(0, -2, 4, 0, 180), new SymbolLine(-4, -2, 4, -2), new SymbolText(0, 0, 2.5, "5", true)],
                Id(string.Empty, "Circuit / label", 5, 1, 3)),
            new("E-SOCKET-15A", "15A power socket with switch", "15A / 16A power socket with its own switch: kitchen (fridge, microwave, mixer at 3'-6\"), washing machine, geyser and AC points; 1'-0\" above floor elsewhere.", e, ServiceLayers.Power, Centre,
                [new SymbolPolyline([new(2, -2, 1), new(-2, -2)], Closed: false, Width: 4), new SymbolArc(0, -2, 4, 0, 180), new SymbolLine(-4, -2, 4, -2), new SymbolText(0, -5, 2.5, "15A", true)],
                Id(string.Empty, "Circuit / label", 5, 1, 3)),
            new("E-LIGHT", "Light point", "Light point (ceiling or wall bracket at 7'-0\"); at least one per room, centred in the room or over the work area.", e, ServiceLayers.Lighting, Centre,
                [new SymbolCircle(0, 0, 4), new SymbolLine(-cos45, -cos45, cos45, cos45), new SymbolLine(-cos45, cos45, cos45, -cos45)],
                Id(string.Empty, "Circuit / label", 5, 3, 3)),
            new("E-FAN", "Ceiling fan", "Ceiling fan point at the centre of the room (one per habitable room; two in rooms longer than about 16'-0\").", e, ServiceLayers.Lighting, Centre,
                [new SymbolCircle(0, 0, 7), new SymbolCircle(0, 0, 1.5), new SymbolLine(0, 1.5, 0, 7), new SymbolLine(-1.299, -0.75, -6.062, -3.5), new SymbolLine(1.299, -0.75, 6.062, -3.5)],
                Id(string.Empty, "Circuit / label", 8, 5, 3)),
            new("E-EXHAUST-FAN", "Exhaust fan", "Exhaust fan point high on the outer wall of the kitchen and of each toilet (about 7'-0\" above floor).", e, ServiceLayers.Lighting, Centre,
                [Rect(-5, -5, 5, 5), new SymbolCircle(0, 0, 4), new SymbolText(0, 0, 3, "EF", true)],
                Id(string.Empty, "Circuit / label", 6, 4, 3)),
            new("E-AC-POINT", "AC point (7'-0\" above floor)", "Split AC power point (16A/20A) at about 7'-0\" above floor on the wall where the indoor unit goes, near an outer wall for the piping.", e, ServiceLayers.Power, Centre,
                [Rect(-6, -3, 6, 3), new SymbolText(0, 0, 3, "AC", true)],
                Id(string.Empty, "Circuit / label", 7, 2, 3)),
            new("E-GEYSER", "Geyser point (6'-0\" above floor)", "Geyser (water heater) point with a 15A/16A socket and switch in each toilet, at about 6'-0\" above floor, away from the shower spray.", e, ServiceLayers.Power, Centre,
                [new SymbolCircle(0, 0, 5), new SymbolText(0, 0, 3, "GY", true)],
                Id(string.Empty, "Circuit / label", 6, 3, 3)),
            new("E-DB", "Distribution board (MCB DB)", "Distribution board (MCB DB with main switch / RCCB) near the main entrance door, 5'-0\" to 6'-0\" above floor, close to the meter.", e, ServiceLayers.Power, Centre,
                [Rect(-8, -4, 8, 4), new SymbolSolid(-8, -4, 8, -4, 8, 4)],
                Id("DB", "Board name", 0, 5.5, centered: true)),
            new("E-EARTH", "Earth pit", "Earthing point / earth pit outside the building near the DB (usually in the setback).", e, ServiceLayers.Power, "top of the earth lead",
                [new SymbolLine(0, 0, 0, -6), new SymbolLine(-6, -6, 6, -6), new SymbolLine(-4, -8.5, 4, -8.5), new SymbolLine(-2, -11, 2, -11)],
                Id(string.Empty, "Earth pit number", 3, -3, 3)),
            new("P-BASIN-TAP", "Wash basin tap", "Pillar cock / tap over a wash basin or sink; water supply from the wall behind.", p, ServiceLayers.Fixtures, Centre,
                [new SymbolCircle(0, 0, 2), new SymbolLine(2, 0, 7, 0), new SymbolLine(7, 0, 7, -2.5)],
                Id(string.Empty, "Label", 3, 3, 3)),
            new("P-WASH-BASIN", "Wash basin", "Wash basin 20\" x 16\" with tap and waste; top at 2'-8\" above floor.", p, ServiceLayers.Fixtures, Back,
                [Rect(-10, -16, 10, 0), new SymbolCircle(0, -9, 6), new SymbolCircle(0, -9, 0.75), new SymbolCircle(0, -2.5, 1)],
                Id(string.Empty, "Label", 11, -8, 3)),
            new("P-WC", "WC (European)", "European water closet with cistern, about 18\" x 27\"; keep 1'-3\" from the side wall to its centre and 2'-0\" clear in front.", p, ServiceLayers.Fixtures, Back,
                [Rect(-9, -7, 9, 0),
                 new SymbolPolyline([new(-7, -7), new(-7, -20, 1), new(7, -20), new(7, -7)], Closed: true),
                 new SymbolPolyline([new(-4.5, -9), new(-4.5, -20, 1), new(4.5, -20), new(4.5, -9)], Closed: true)],
                Id(string.Empty, "Label", 10, -14, 3)),
            new("P-FLOOR-TRAP", "Floor trap", "Floor trap (FT) with grating in toilet, bath and wash areas, at the lowest point of the floor slope.", p, ServiceLayers.Fixtures, Centre,
                [Rect(-3, -3, 3, 3), new SymbolCircle(0, 0, 2.4), new SymbolLine(-2.4, 0, 2.4, 0), new SymbolLine(0, -2.4, 0, 2.4)],
                Id("FT", "Label", 4, 2, 3)),
            new("P-NAHANI-TRAP", "Nahani trap", "Nahani trap (NT) in bathrooms, kitchen wash and balcony; floor sloped 1:60 to 1:100 towards it.", p, ServiceLayers.Fixtures, Centre,
                [Rect(-3, -3, 3, 3), new SymbolLine(-3, -3, 3, 3), new SymbolLine(-3, 3, 3, -3)],
                Id("NT", "Label", 4, 2, 3)),
            new("P-INLET-PIPE", "Water supply (inlet) pipe", "Flow arrow and size label for a water supply pipe run (CPVC/PPR); insert on the pipe line, rotated to the flow direction.", p, ServiceLayers.Water, "centre of the arrow line",
                [new SymbolLine(-12, 0, 12, 0), new SymbolSolid(12, 0, 7, 2, 7, -2)],
                new SymbolAttribute("SIZE", "Pipe size", "20 DIA", 0, 1.5, 3, false)),
            new("P-OUTLET-PIPE", "Drainage (outlet) pipe", "Flow arrow and size label for a drainage pipe run (soil 110 SWR, waste 75 SWR) with its slope (1:40 to 1:60); insert on the pipe line, rotated to the flow direction.", p, ServiceLayers.Drainage, "centre of the arrow line",
                [new SymbolLine(-12, 0, -7, 0), new SymbolLine(-5, 0, 0, 0), new SymbolLine(2, 0, 7, 0), new SymbolSolid(12, 0, 7, 2, 7, -2)],
                new SymbolAttribute("SIZE", "Pipe size / slope", "110 SWR", 0, 1.5, 3, false)),
            new("P-STACK", "Soil / waste stack", "Vertical soil (SP) or waste (WP) pipe stack in a shaft or along an outer wall, next to the toilets; also rain water pipes (RWP).", p, ServiceLayers.Drainage, Centre,
                [new SymbolCircle(0, 0, 3), new SymbolCircle(0, 0, 1.5)],
                Id("SP", "Stack (SP / WP / RWP)", 4, 2, 3)),
            new("P-INSPECTION-CHAMBER", "Inspection chamber", "Inspection chamber (IC) 1'-6\" x 1'-6\" min. in the setback at every junction and change of direction of the drain, at most 30'-0\" apart.", p, ServiceLayers.Drainage, Centre,
                [Rect(-9, -9, 9, 9), Rect(-6.5, -6.5, 6.5, 6.5)],
                Id("IC", "Chamber number", 0, 0, 4, centered: true)),
            new("P-WATER-TANK", "Water tank", "Overhead (OHT) or underground (UGT) water tank with its capacity.", p, ServiceLayers.Water, Centre,
                [Rect(-18, -12, 18, 12), new SymbolLine(-18, -12, 18, 12), new SymbolLine(-18, 12, 18, -12)],
                Id("OHT 1000 L", "Tank and capacity", 0, 13.5, 4, centered: true)),
        ];
    }
}

/// <summary>Small geometry helpers for symbols (pure).</summary>
public static class SymbolGeometry
{
    /// <summary>
    /// Points along a bulged polyline segment from (x1, y1) to (x2, y2) (excluding the end points), for bounds.
    /// </summary>
    public static IEnumerable<(double X, double Y)> ArcPoints(double x1, double y1, double x2, double y2, double bulge, int steps)
    {
        var chord = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
        if (chord < 1e-12 || Math.Abs(bulge) < 1e-12)
        {
            yield break;
        }

        var theta = 4 * Math.Atan(bulge);
        var radius = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
        var mx = (x1 + x2) / 2;
        var my = (y1 + y2) / 2;
        var distance = radius * Math.Cos(theta / 2);

        // Centre to the left of the chord for a positive (counter-clockwise) bulge.
        var nx = -(y2 - y1) / chord;
        var ny = (x2 - x1) / chord;
        var sign = bulge > 0 ? 1 : -1;
        var cx = mx + (sign * nx * distance);
        var cy = my + (sign * ny * distance);
        var start = Math.Atan2(y1 - cy, x1 - cx);
        for (var i = 1; i < steps; i++)
        {
            var angle = start + (theta * i / steps);
            yield return (cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle)));
        }
    }
}
