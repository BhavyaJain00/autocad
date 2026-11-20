using System.Globalization;

namespace AcadCopilot.Core.Sheets;

/// <summary>
/// An axis-aligned rectangle: a drawing's extents in model space (drawing units) or an area on a sheet (paper
/// millimetres). Pure.
/// </summary>
/// <param name="MinX">Smallest X.</param>
/// <param name="MinY">Smallest Y.</param>
/// <param name="MaxX">Largest X.</param>
/// <param name="MaxY">Largest Y.</param>
public readonly record struct SheetRect(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Width (X size).</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height (Y size).</summary>
    public double Height => MaxY - MinY;

    /// <summary>Centre X.</summary>
    public double CenterX => (MinX + MaxX) / 2.0;

    /// <summary>Centre Y.</summary>
    public double CenterY => (MinY + MaxY) / 2.0;

    /// <summary>True when all values are finite and min does not exceed max.</summary>
    public bool IsValid =>
        double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MaxX) && double.IsFinite(MaxY) && MinX <= MaxX && MinY <= MaxY;

    /// <summary>A rectangle from two opposite corners in any order.</summary>
    public static SheetRect FromCorners(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    /// <summary>A rectangle from its lower-left corner and size.</summary>
    public static SheetRect FromSize(double x, double y, double width, double height) => new(x, y, x + width, y + height);

    /// <summary>A rectangle from its centre and size.</summary>
    public static SheetRect FromCenter(double cx, double cy, double width, double height) =>
        new(cx - (width / 2), cy - (height / 2), cx + (width / 2), cy + (height / 2));

    /// <summary>The smallest rectangle containing both.</summary>
    public SheetRect Union(SheetRect other) =>
        new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    /// <summary>The rectangle moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public SheetRect Offset(double dx, double dy) => new(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);

    /// <summary>The rectangle grown by <paramref name="margin"/> on every side (shrunk when negative).</summary>
    public SheetRect Inflate(double margin) => new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

    /// <summary>True when the rectangles overlap (touching edges do not count).</summary>
    public bool Overlaps(SheetRect other) =>
        other.MinX < MaxX && other.MaxX > MinX && other.MinY < MaxY && other.MaxY > MinY;

    /// <summary>True when <paramref name="other"/> lies inside this rectangle (within <paramref name="tolerance"/>).</summary>
    public bool Contains(SheetRect other, double tolerance = 1e-6) =>
        other.MinX >= MinX - tolerance && other.MaxX <= MaxX + tolerance && other.MinY >= MinY - tolerance && other.MaxY <= MaxY + tolerance;

    /// <summary>True when the point lies inside or on the rectangle.</summary>
    public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    /// <summary>
    /// The empty space between the rectangles: the larger of the X gap and the Y gap (0 or negative when they overlap
    /// in both directions). Two drawings are separate when this is larger than the clustering gap.
    /// </summary>
    public double GapTo(SheetRect other)
    {
        var gapX = Math.Max(other.MinX - MaxX, MinX - other.MaxX);
        var gapY = Math.Max(other.MinY - MaxY, MinY - other.MaxY);
        return Math.Max(gapX, gapY);
    }

    /// <summary>Straight-line distance between the rectangles (0 when they touch or overlap).</summary>
    public double DistanceTo(SheetRect other)
    {
        var dx = Math.Max(0, Math.Max(other.MinX - MaxX, MinX - other.MaxX));
        var dy = Math.Max(0, Math.Max(other.MinY - MaxY, MinY - other.MaxY));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>"[x0, y0, x1, y1]" rounded to 2 decimals (for messages).</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Math.Round(MinX, 2)}, {Math.Round(MinY, 2)}, {Math.Round(MaxX, 2)}, {Math.Round(MaxY, 2)}]");
}
