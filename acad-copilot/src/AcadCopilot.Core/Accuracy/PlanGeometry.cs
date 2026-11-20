using System.Globalization;
using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Accuracy;

/// <summary>A 2D point in inches (plan or drawing coordinates).</summary>
/// <param name="X">X in inches (+X = east).</param>
/// <param name="Y">Y in inches (+Y = north).</param>
public readonly record struct Pt(double X, double Y)
{
    /// <summary>Sum.</summary>
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Difference.</summary>
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Scaling.</summary>
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);

    /// <summary>Dot product.</summary>
    public double Dot(Pt other) => (X * other.X) + (Y * other.Y);

    /// <summary>Length of the vector.</summary>
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    /// <summary>Distance to <paramref name="other"/>.</summary>
    public double DistanceTo(Pt other) => (this - other).Length;

    /// <summary>Unit vector (zero stays zero).</summary>
    public Pt Unit() => Length < 1e-12 ? this : this * (1 / Length);

    /// <summary>Feet-inch text "(12'-6", 30'-0")".</summary>
    public string Text() => "(" + Inch.Text(X) + ", " + Inch.Text(Y) + ")";

    /// <summary>AutoCAD input text "12'6,30'".</summary>
    public string Input() => FeetInches.FormatForInput(X) + "," + FeetInches.FormatForInput(Y);
}

/// <summary>An axis-aligned box in inches.</summary>
public readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Width (X).</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height (Y).</summary>
    public double Height => MaxY - MinY;

    /// <summary>Centre.</summary>
    public Pt Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    /// <summary>Area in square inches.</summary>
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>True when <paramref name="p"/> is inside or within <paramref name="eps"/> of the box.</summary>
    public bool Contains(Pt p, double eps = 1e-6) =>
        p.X >= MinX - eps && p.X <= MaxX + eps && p.Y >= MinY - eps && p.Y <= MaxY + eps;

    /// <summary>True when the boxes overlap by more than <paramref name="eps"/> in both directions.</summary>
    public bool Overlaps(Box other, double eps = 1e-6) =>
        Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX) > eps &&
        Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY) > eps;

    /// <summary>The overlap of two boxes (may be empty: width or height &lt;= 0).</summary>
    public Box Intersect(Box other) =>
        new(Math.Max(MinX, other.MinX), Math.Max(MinY, other.MinY), Math.Min(MaxX, other.MaxX), Math.Min(MaxY, other.MaxY));

    /// <summary>The box grown by <paramref name="d"/> on every side.</summary>
    public Box Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    /// <summary>The smallest box holding both.</summary>
    public Box Union(Box other) =>
        new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    /// <summary>The box of some points.</summary>
    public static Box Of(IEnumerable<Pt> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return minX > maxX ? default : new Box(minX, minY, maxX, maxY);
    }

    /// <summary>"x 3'-0" to 27'-0", y 5'-0" to 37'-0"".</summary>
    public string Text() => $"x {Inch.Text(MinX)} to {Inch.Text(MaxX)}, y {Inch.Text(MinY)} to {Inch.Text(MaxY)}";
}

/// <summary>A simple polygon (counter-clockwise after construction) in inches.</summary>
public sealed class Polygon
{
    /// <summary>Creates a polygon; the points are put in counter-clockwise order and a repeated closing point is dropped.</summary>
    public Polygon(IEnumerable<Pt> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var list = points.ToList();
        if (list.Count > 1 && list[0].DistanceTo(list[^1]) < 1e-9)
        {
            list.RemoveAt(list.Count - 1);
        }

        var signed = SignedArea(list);
        if (signed < 0)
        {
            list.Reverse();
        }

        Points = list;
        Area = Math.Abs(signed);
        Bounds = Box.Of(list);
        IsAxisRectangle = list.Count == 4 && Math.Abs(Area - Bounds.Area) < 1e-6;
    }

    /// <summary>The vertices, counter-clockwise.</summary>
    public IReadOnlyList<Pt> Points { get; }

    /// <summary>Area in square inches.</summary>
    public double Area { get; }

    /// <summary>Bounding box.</summary>
    public Box Bounds { get; }

    /// <summary>True for an axis-aligned rectangle.</summary>
    public bool IsAxisRectangle { get; }

    /// <summary>The edges (start, end), counter-clockwise, so the outside is on the right.</summary>
    public IEnumerable<(Pt A, Pt B)> Edges()
    {
        for (var i = 0; i < Points.Count; i++)
        {
            yield return (Points[i], Points[(i + 1) % Points.Count]);
        }
    }

    /// <summary>An axis-aligned rectangle.</summary>
    public static Polygon Rect(double x, double y, double width, double depth) =>
        new([new Pt(x, y), new Pt(x + width, y), new Pt(x + width, y + depth), new Pt(x, y + depth)]);

    /// <summary>A box as a polygon.</summary>
    public static Polygon Rect(Box box) => Rect(box.MinX, box.MinY, box.Width, box.Height);

    /// <summary>True when <paramref name="p"/> is inside, or within <paramref name="eps"/> of the boundary.</summary>
    public bool Contains(Pt p, double eps = 1e-6)
    {
        if (!Bounds.Contains(p, eps))
        {
            return false;
        }

        if (IsAxisRectangle)
        {
            return true;
        }

        foreach (var (a, b) in Edges())
        {
            if (SegmentDistance(p, a, b) <= eps)
            {
                return true;
            }
        }

        var inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var pi = Points[i];
            var pj = Points[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) && p.X < ((pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>True when <paramref name="p"/> is strictly inside (more than <paramref name="eps"/> from the boundary).</summary>
    public bool ContainsStrict(Pt p, double eps = 1e-6)
    {
        if (!Contains(p, 0))
        {
            return false;
        }

        foreach (var (a, b) in Edges())
        {
            if (SegmentDistance(p, a, b) <= eps)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The centroid (area-weighted).</summary>
    public Pt Centroid()
    {
        if (Area < 1e-9)
        {
            return Bounds.Center;
        }

        double cx = 0, cy = 0;
        foreach (var (a, b) in Edges())
        {
            var cross = (a.X * b.Y) - (b.X * a.Y);
            cx += (a.X + b.X) * cross;
            cy += (a.Y + b.Y) * cross;
        }

        var k = 1 / (6 * Area);
        return new Pt(cx * k, cy * k);
    }

    /// <summary>Distance from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static double SegmentDistance(Pt p, Pt a, Pt b)
    {
        var ab = b - a;
        var len2 = ab.Dot(ab);
        if (len2 < 1e-18)
        {
            return p.DistanceTo(a);
        }

        var t = Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return p.DistanceTo(a + (ab * t));
    }

    private static double SignedArea(IReadOnlyList<Pt> points)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2;
    }
}

/// <summary>Inch helpers: snapping, feet-inch text and square feet.</summary>
public static class Inch
{
    /// <summary>All plan values are snapped to this grid first (1/16 inch) so float noise never shows as a gap.</summary>
    public const double Grid = 1.0 / 16;

    /// <summary>Snaps to 1/16 inch.</summary>
    public static double Snap(double inches) => Math.Round(inches / Grid) * Grid;

    /// <summary>True when <paramref name="inches"/> is a multiple of <paramref name="step"/> (within 1/64 inch).</summary>
    public static bool IsMultiple(double inches, double step) =>
        Math.Abs((inches / step) - Math.Round(inches / step)) * step < 1.0 / 64;

    /// <summary>Feet-inch display text to 1/16 inch: 12'-6 1/2".</summary>
    public static string Text(double inches) => FeetInches.Format(inches);

    /// <summary>"11'-0" x 12'-0"".</summary>
    public static string Size(double width, double depth) => Text(width) + " x " + Text(depth);

    /// <summary>Square inches to square feet, rounded to 0.1.</summary>
    public static double SquareFeet(double squareInches) => Math.Round(squareInches / 144, 1);

    /// <summary>"132.5 sq ft".</summary>
    public static string Area(double squareInches) =>
        SquareFeet(squareInches).ToString("0.#", CultureInfo.InvariantCulture) + " sq ft";
}
