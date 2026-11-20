namespace AcadCopilot.Plugin.Inspect;

/// <summary>Why <see cref="ViewFraming.Decide"/> moves the view.</summary>
public enum FramingReason
{
    /// <summary>The new objects are visible and big enough: the view stays.</summary>
    None,

    /// <summary>Some of the new objects are outside the view.</summary>
    Outside,

    /// <summary>The new objects are inside the view but too small to see.</summary>
    TooSmall,
}

/// <summary>Where the view should go: the visible area (world XY of a plan view) and why.</summary>
/// <param name="Window">The new visible area; its proportions match the viewport's.</param>
/// <param name="Reason">Why the view moves.</param>
public readonly record struct FramingDecision(PlanBox Window, FramingReason Reason);

/// <summary>
/// "Follow the AI's drawing": pure maths (no AutoCAD types, unit-tested) that decides whether the view should zoom to the
/// objects a run_commands call created, and to which window. It zooms when part of them is outside the visible area, or
/// when they are so small that they cover under <see cref="SmallFraction"/> of the view; the window is their extents
/// plus <see cref="Margin"/> on every side, fitted to the viewport's proportions. A zoom-in never goes deeper than
/// <see cref="MaxZoomIn"/> times per call, so a tiny object does not fill the whole screen.
/// </summary>
public static class ViewFraming
{
    /// <summary>Margin around the new objects, as a fraction of their size on each side (10%).</summary>
    public const double Margin = 0.10;

    /// <summary>New objects smaller than this fraction of the view (in both directions) count as too small to see.</summary>
    public const double SmallFraction = 0.03;

    /// <summary>The most the view may zoom in for one call.</summary>
    public const double MaxZoomIn = 8.0;

    /// <summary>
    /// Decides whether <paramref name="view"/> (the visible area) should move to show <paramref name="created"/> (the
    /// extents of the new objects), or null to keep the view. Both boxes are world XY of a plan view.
    /// </summary>
    public static FramingDecision? Decide(PlanBox view, PlanBox created)
    {
        if (!view.IsValid || view.Width <= 0 || view.Height <= 0 || !created.IsValid)
        {
            return null;
        }

        var tolerance = Math.Max(view.Width, view.Height) * 1e-6;
        var outside = created.MinX < view.MinX - tolerance || created.MinY < view.MinY - tolerance ||
                      created.MaxX > view.MaxX + tolerance || created.MaxY > view.MaxY + tolerance;
        var small = !outside && created.Width < view.Width * SmallFraction && created.Height < view.Height * SmallFraction;
        if (!outside && !small)
        {
            return null;
        }

        var aspect = view.Height / view.Width;
        var width = created.Width * (1 + (2 * Margin));
        var height = created.Height * (1 + (2 * Margin));

        // Never closer than MaxZoomIn per call (also covers a single point or a straight line along an axis).
        width = Math.Max(width, view.Width / MaxZoomIn);
        height = Math.Max(height, view.Height / MaxZoomIn);
        if (height / width < aspect)
        {
            height = width * aspect;
        }
        else
        {
            width = height / aspect;
        }

        var cx = created.CenterX;
        var cy = created.CenterY;
        var window = new PlanBox(cx - (width / 2), cy - (height / 2), cx + (width / 2), cy + (height / 2));
        return new FramingDecision(window, outside ? FramingReason.Outside : FramingReason.TooSmall);
    }

    /// <summary>The smallest box around <paramref name="boxes"/>, or null when there is none.</summary>
    public static PlanBox? Union(IEnumerable<PlanBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        PlanBox? result = null;
        foreach (var box in boxes.Where(b => b.IsValid))
        {
            result = result is { } r
                ? new PlanBox(Math.Min(r.MinX, box.MinX), Math.Min(r.MinY, box.MinY), Math.Max(r.MaxX, box.MaxX), Math.Max(r.MaxY, box.MaxY))
                : box;
        }

        return result;
    }
}
