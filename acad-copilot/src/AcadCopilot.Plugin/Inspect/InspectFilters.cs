using System.Text;
using System.Text.RegularExpressions;

namespace AcadCopilot.Plugin.Inspect;

/// <summary>
/// AutoCAD-style name patterns for layer and type filters, case-insensitive: '*' any text, '?' one character,
/// '#' one digit, '@' one letter, ',' separates alternatives, a leading '~' negates the whole pattern.
/// "A-WALL*,A-DOOR" matches A-WALL, A-WALL-EXT and A-DOOR. Pure (no AutoCAD types).
/// </summary>
public sealed class NamePattern
{
    private readonly Regex _regex;
    private readonly bool _negated;

    private NamePattern(string text, Regex regex, bool negated)
    {
        Text = text;
        _regex = regex;
        _negated = negated;
    }

    /// <summary>The pattern as given.</summary>
    public string Text { get; }

    /// <summary>Parses a pattern; null or blank means "match everything" and returns null.</summary>
    public static NamePattern? Parse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var text = pattern.Trim();
        var negated = text.StartsWith('~');
        var body = negated ? text[1..] : text;
        var alternatives = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var regex = new StringBuilder("^(?:");
        for (var i = 0; i < alternatives.Length; i++)
        {
            if (i > 0)
            {
                regex.Append('|');
            }

            foreach (var c in alternatives[i])
            {
                regex.Append(c switch
                {
                    '*' => ".*",
                    '?' => ".",
                    '#' => "[0-9]",
                    '@' => "\\p{L}",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        regex.Append(")$");
        return new NamePattern(
            text,
            new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)),
            negated);
    }

    /// <summary>True when <paramref name="name"/> matches.</summary>
    public bool IsMatch(string? name) => _regex.IsMatch(name ?? string.Empty) != _negated;
}

/// <summary>An axis-aligned rectangle in drawing units (plan / XY), used for window filters and extents. Pure.</summary>
/// <param name="MinX">Smallest X.</param>
/// <param name="MinY">Smallest Y.</param>
/// <param name="MaxX">Largest X.</param>
/// <param name="MaxY">Largest Y.</param>
public readonly record struct PlanBox(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Width (X size).</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height (Y size).</summary>
    public double Height => MaxY - MinY;

    /// <summary>Centre X.</summary>
    public double CenterX => (MinX + MaxX) / 2.0;

    /// <summary>Centre Y.</summary>
    public double CenterY => (MinY + MaxY) / 2.0;

    /// <summary>A box from two opposite corners in any order.</summary>
    public static PlanBox FromCorners(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

    /// <summary>True when the boxes overlap or touch (a crossing selection).</summary>
    public bool Intersects(PlanBox other) =>
        other.MinX <= MaxX && other.MaxX >= MinX && other.MinY <= MaxY && other.MaxY >= MinY;

    /// <summary>True when <paramref name="other"/> lies completely inside this box (a window selection).</summary>
    public bool Contains(PlanBox other) =>
        other.MinX >= MinX && other.MaxX <= MaxX && other.MinY >= MinY && other.MaxY <= MaxY;

    /// <summary>The smallest box containing both.</summary>
    public PlanBox Union(PlanBox other) =>
        new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    /// <summary>The box grown by <paramref name="fraction"/> of its larger side on every edge (at least <paramref name="minimum"/>).</summary>
    public PlanBox Expand(double fraction, double minimum)
    {
        var margin = Math.Max(Math.Max(Width, Height) * fraction, minimum);
        return new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);
    }

    /// <summary>True when all four values are finite and min does not exceed max.</summary>
    public bool IsValid =>
        double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MaxX) && double.IsFinite(MaxY) && MinX <= MaxX && MinY <= MaxY;
}
