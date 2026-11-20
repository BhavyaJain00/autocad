namespace AcadCopilot.Core.Accuracy;

/// <summary>A straight piece of a LINE or of a polyline (drawing coordinates, inches).</summary>
/// <param name="Handle">Entity handle (hex).</param>
/// <param name="Layer">Layer.</param>
/// <param name="A">Start.</param>
/// <param name="B">End.</param>
public sealed record DrawnSegment(string Handle, string Layer, Pt A, Pt B)
{
    /// <summary>Length.</summary>
    public double Length => A.DistanceTo(B);

    /// <summary>Distance from <paramref name="p"/> to the segment.</summary>
    public double Distance(Pt p) => Polygon.SegmentDistance(p, A, B);
}

/// <summary>An ARC (angles in radians, counter-clockwise from +X, start to end).</summary>
/// <param name="Handle">Handle.</param>
/// <param name="Layer">Layer.</param>
/// <param name="Center">Centre.</param>
/// <param name="Radius">Radius.</param>
/// <param name="StartAngle">Start angle.</param>
/// <param name="EndAngle">End angle.</param>
public sealed record DrawnArc(string Handle, string Layer, Pt Center, double Radius, double StartAngle, double EndAngle)
{
    /// <summary>The point halfway along the arc.</summary>
    public Pt Middle
    {
        get
        {
            var sweep = EndAngle - StartAngle;
            while (sweep < 0)
            {
                sweep += 2 * Math.PI;
            }

            var a = StartAngle + (sweep / 2);
            return Center + (new Pt(Math.Cos(a), Math.Sin(a)) * Radius);
        }
    }
}

/// <summary>A TEXT or MTEXT with its extents.</summary>
/// <param name="Handle">Handle.</param>
/// <param name="Layer">Layer.</param>
/// <param name="Text">Its text (formatting codes removed where possible).</param>
/// <param name="Bounds">Geometric extents.</param>
/// <param name="Height">Text height.</param>
public sealed record DrawnText(string Handle, string Layer, string Text, Box Bounds, double Height);

/// <summary>A linear (rotated or aligned) dimension.</summary>
/// <param name="Handle">Handle.</param>
/// <param name="Layer">Layer.</param>
/// <param name="Point1">First extension line origin.</param>
/// <param name="Point2">Second extension line origin.</param>
/// <param name="LinePoint">A point on the dimension line.</param>
/// <param name="Rotation">Rotation of a rotated dimension (radians), or null for an aligned one.</param>
/// <param name="Measurement">The measured length.</param>
/// <param name="TextOverride">The user text ("" or null = the measurement; "&lt;&gt;" stands for it).</param>
public sealed record DrawnDimension(string Handle, string Layer, Pt Point1, Pt Point2, Pt LinePoint, double? Rotation, double Measurement, string? TextOverride)
{
    /// <summary>Direction of measurement (unit).</summary>
    public Pt Direction => Rotation is { } r ? new Pt(Math.Cos(r), Math.Sin(r)) : (Point2 - Point1).Unit();

    /// <summary>True when it measures along X.</summary>
    public bool IsHorizontal => Math.Abs(Direction.Y) < 1e-3;

    /// <summary>True when it measures along Y.</summary>
    public bool IsVertical => Math.Abs(Direction.X) < 1e-3;
}

/// <summary>Any entity, for the layer checks.</summary>
/// <param name="Handle">Handle.</param>
/// <param name="Layer">Layer.</param>
/// <param name="Kind">line, polyline, arc, circle, text, mtext, dimension, hatch, insert, other.</param>
public sealed record DrawnObject(string Handle, string Layer, string Kind);

/// <summary>The model-space objects of one area of the drawing, in inches, as read by check_drawing.</summary>
public sealed class DrawingSnapshot
{
    /// <summary>Lines and polyline pieces.</summary>
    public List<DrawnSegment> Segments { get; } = [];

    /// <summary>Arcs (and polyline arcs are left out).</summary>
    public List<DrawnArc> Arcs { get; } = [];

    /// <summary>Texts.</summary>
    public List<DrawnText> Texts { get; } = [];

    /// <summary>Linear dimensions.</summary>
    public List<DrawnDimension> Dimensions { get; } = [];

    /// <summary>Every object read.</summary>
    public List<DrawnObject> Objects { get; } = [];

    /// <summary>True when the area held more objects than were read.</summary>
    public bool Truncated { get; set; }
}

/// <summary>The layer names check_drawing expects (the template's by default).</summary>
public sealed record CheckLayers
{
    /// <summary>Walls.</summary>
    public string Wall { get; init; } = "A-WALL";

    /// <summary>Doors.</summary>
    public string Door { get; init; } = "A-DOOR";

    /// <summary>Windows.</summary>
    public string Glazing { get; init; } = "A-GLAZ";

    /// <summary>Text.</summary>
    public string Text { get; init; } = "A-ANNO-TEXT";

    /// <summary>Dimensions.</summary>
    public string Dims { get; init; } = "A-ANNO-DIMS";
}
