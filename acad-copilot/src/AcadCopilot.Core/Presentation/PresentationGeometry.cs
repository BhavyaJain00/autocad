namespace AcadCopilot.Core.Presentation;

/// <summary>A point of a presentation polygon (drawing units).</summary>
public readonly record struct PlanPoint(double X, double Y);

/// <summary>An axis-aligned rectangle (drawing units).</summary>
public readonly record struct PlanRect(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Width.</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height.</summary>
    public double Height => MaxY - MinY;

    /// <summary>A rectangle from two opposite corners in any order.</summary>
    public static PlanRect FromCorners(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    /// <summary>The corners counter-clockwise from the lower left.</summary>
    public IReadOnlyList<PlanPoint> Corners() => [new(MinX, MinY), new(MaxX, MinY), new(MaxX, MaxY), new(MinX, MaxY)];
}

/// <summary>Which way a projection casts its shadow on the elevation.</summary>
public enum ShadowKind
{
    /// <summary>A horizontal projection (chajja / sunshade, slab edge, balcony, canopy): the shadow falls below it.</summary>
    Below,

    /// <summary>A vertical projection (projecting wall, column, fin): the shadow falls beside it, away from the sun.</summary>
    Side,
}

/// <summary>
/// Geometry of the colour presentation elevation (pure): the sky and ground bands around an elevation and the
/// conventional 45-degree shadows of projections (the sun at 45 degrees from the upper left or right, so a projection of
/// depth d casts a shadow d below and d to the side).
/// </summary>
public static class PresentationGeometry
{
    /// <summary>
    /// The sky rectangle (from the ground line to above the elevation, wider than it by <paramref name="marginFraction"/>
    /// of its width on each side) and the ground band below the ground line.
    /// </summary>
    /// <param name="elevation">The elevation's extents.</param>
    /// <param name="groundY">Y of the ground line.</param>
    /// <param name="marginFraction">Side margin as a fraction of the elevation width (default 0.12).</param>
    /// <param name="groundDepth">Depth of the ground band (default: 6% of the elevation height, at least 1 unit).</param>
    public static (PlanRect Sky, PlanRect Ground) Backdrop(PlanRect elevation, double groundY, double marginFraction = 0.12, double? groundDepth = null)
    {
        if (!(elevation.Width > 0) || !(elevation.Height > 0))
        {
            throw new ArgumentException("The elevation window needs a width and a height.", nameof(elevation));
        }

        var margin = elevation.Width * Math.Clamp(marginFraction, 0, 1);
        var top = Math.Max(elevation.MaxY, groundY) + (elevation.Height * 0.12);
        var depth = groundDepth is { } d && d > 0 ? d : Math.Max(elevation.Height * 0.06, 1);
        var left = elevation.MinX - margin;
        var right = elevation.MaxX + margin;
        return (new PlanRect(left, groundY, right, top), new PlanRect(left, groundY - depth, right, groundY));
    }

    /// <summary>
    /// The shadow polygon of a projection seen in elevation as <paramref name="projection"/>, projecting
    /// <paramref name="depth"/> from the wall, with the sun on <paramref name="sunFromLeft"/> side. Clipped to above
    /// <paramref name="groundY"/> when given. Below: the parallelogram under the projection's bottom edge. Side: the
    /// parallelogram beside the edge away from the sun.
    /// </summary>
    public static IReadOnlyList<PlanPoint> Shadow(PlanRect projection, double depth, ShadowKind kind, bool sunFromLeft = true, double? groundY = null)
    {
        if (!(depth > 0) || !double.IsFinite(depth))
        {
            throw new ArgumentException("The projection depth must be positive.", nameof(depth));
        }

        var shift = sunFromLeft ? depth : -depth;
        List<PlanPoint> polygon;
        if (kind == ShadowKind.Below)
        {
            var y = projection.MinY;
            polygon = [new(projection.MinX, y), new(projection.MaxX, y), new(projection.MaxX + shift, y - depth), new(projection.MinX + shift, y - depth)];
        }
        else
        {
            var x = sunFromLeft ? projection.MaxX : projection.MinX;
            polygon = [new(x, projection.MaxY), new(x + shift, projection.MaxY - depth), new(x + shift, projection.MinY), new(x, projection.MinY)];
        }

        if (groundY is { } ground)
        {
            polygon = ClipAbove(polygon, ground);
        }

        return polygon;
    }

    /// <summary>The part of <paramref name="polygon"/> on or above y = <paramref name="y"/> (Sutherland-Hodgman, one edge).</summary>
    public static List<PlanPoint> ClipAbove(IReadOnlyList<PlanPoint> polygon, double y)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var result = new List<PlanPoint>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            var currentIn = current.Y >= y;
            var nextIn = next.Y >= y;
            if (currentIn)
            {
                result.Add(current);
            }

            if (currentIn != nextIn)
            {
                var t = (y - current.Y) / (next.Y - current.Y);
                result.Add(new PlanPoint(current.X + ((next.X - current.X) * t), y));
            }
        }

        return result;
    }

    /// <summary>Area of a polygon (absolute, shoelace).</summary>
    public static double Area(IReadOnlyList<PlanPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        double sum = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(sum) / 2;
    }
}
