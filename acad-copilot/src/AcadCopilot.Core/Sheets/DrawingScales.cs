using System.Globalization;

namespace AcadCopilot.Core.Sheets;

/// <summary>Which scale notation a drawing set uses.</summary>
public enum ScaleSystem
{
    /// <summary>Feet and inches: 1/4" = 1'-0", 1/8" = 1'-0" ...</summary>
    Imperial,

    /// <summary>Ratios: 1:50, 1:100 ...</summary>
    Metric,
}

/// <summary>A standard drawing scale.</summary>
/// <param name="Ratio">Real size divided by paper size (48 for 1/4" = 1'-0", 100 for 1:100).</param>
/// <param name="Text">How it is written ("1/4\" = 1'-0\"", "1:100").</param>
/// <param name="System">Imperial or metric notation.</param>
public sealed record DrawingScale(double Ratio, string Text, ScaleSystem System)
{
    /// <summary>The scale line under a drawing title ("SCALE 1/4\" = 1'-0\"").</summary>
    public string ScaleLine => "SCALE " + Text;

    /// <summary>
    /// The viewport scale: paper millimetres per drawing unit, for a drawing whose unit is <paramref name="inchesPerUnit"/>
    /// inches (1 for INSUNITS inches, 1/25.4 for millimetres).
    /// </summary>
    public double PaperMmPerUnit(double inchesPerUnit) => 25.4 * inchesPerUnit / Ratio;

    /// <summary>
    /// How much bigger than at 1/4" = 1'-0" (1:48) text must be in model space to print the same size: 1 at 1/4", 2 at
    /// 1/8" (titles 24 instead of 12).
    /// </summary>
    public double TextFactor => Ratio / 48.0;
}

/// <summary>The standard scales and the choice of the right one for a drawing and a sheet. Pure.</summary>
public static class DrawingScales
{
    /// <summary>1/4" = 1'-0" (1:48): plans, elevations, sections.</summary>
    public static DrawingScale QuarterInch { get; } = new(48, "1/4\" = 1'-0\"", ScaleSystem.Imperial);

    /// <summary>1/8" = 1'-0" (1:96): larger plans, site plans.</summary>
    public static DrawingScale EighthInch { get; } = new(96, "1/8\" = 1'-0\"", ScaleSystem.Imperial);

    /// <summary>The imperial scales, largest first.</summary>
    public static IReadOnlyList<DrawingScale> Imperial { get; } =
    [
        new(24, "1/2\" = 1'-0\"", ScaleSystem.Imperial),
        QuarterInch,
        EighthInch,
        new(192, "1/16\" = 1'-0\"", ScaleSystem.Imperial),
        new(384, "1/32\" = 1'-0\"", ScaleSystem.Imperial),
    ];

    /// <summary>The metric scales, largest first.</summary>
    public static IReadOnlyList<DrawingScale> Metric { get; } =
    [
        new(20, "1:20", ScaleSystem.Metric),
        new(50, "1:50", ScaleSystem.Metric),
        new(100, "1:100", ScaleSystem.Metric),
        new(200, "1:200", ScaleSystem.Metric),
        new(500, "1:500", ScaleSystem.Metric),
    ];

    /// <summary>The scales of <paramref name="system"/>, largest first.</summary>
    public static IReadOnlyList<DrawingScale> Of(ScaleSystem system) => system == ScaleSystem.Metric ? Metric : Imperial;

    /// <summary>
    /// The largest scale a drawing of <paramref name="kind"/> is normally drawn at: 1/4" = 1'-0" / 1:50 for plans,
    /// elevations, sections and layouts; 1/8" = 1'-0" / 1:100 for the site plan.
    /// </summary>
    public static DrawingScale Preferred(DrawingKind kind, ScaleSystem system) => (kind, system) switch
    {
        (DrawingKind.SitePlan, ScaleSystem.Metric) => Metric[2],
        (DrawingKind.SitePlan, _) => EighthInch,
        (_, ScaleSystem.Metric) => Metric[1],
        _ => QuarterInch,
    };

    /// <summary>The scale given by text: "1/4", "1/4\"=1'-0\"", "1:100", "100", "1/8 inch" (null when not a standard scale).</summary>
    public static DrawingScale? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (t.StartsWith("SCALE", StringComparison.Ordinal))
        {
            t = t[5..].TrimStart(':');
        }

        if (t.StartsWith("1:", StringComparison.Ordinal) && double.TryParse(t[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            return Metric.FirstOrDefault(s => Math.Abs(s.Ratio - ratio) < 1e-9) ?? Imperial.FirstOrDefault(s => Math.Abs(s.Ratio - ratio) < 1e-9);
        }

        var slash = t.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var end = slash + 1;
            while (end < t.Length && char.IsDigit(t[end]))
            {
                end++;
            }

            if (double.TryParse(t[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(t[(slash + 1)..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && numerator > 0)
            {
                var r = 12 * denominator / numerator;
                return Imperial.FirstOrDefault(s => Math.Abs(s.Ratio - r) < 1e-9);
            }
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)
            ? Metric.FirstOrDefault(s => Math.Abs(s.Ratio - plain) < 1e-9) ?? Imperial.FirstOrDefault(s => Math.Abs(s.Ratio - plain) < 1e-9)
            : null;
    }

    /// <summary>
    /// The largest standard scale, not larger than <see cref="Preferred"/>, at which a drawing of
    /// <paramref name="modelWidth"/> x <paramref name="modelHeight"/> drawing units fits in
    /// <paramref name="paperWidthMm"/> x <paramref name="paperHeightMm"/>; the smallest scale when nothing fits.
    /// </summary>
    public static DrawingScale Choose(
        DrawingKind kind, double modelWidth, double modelHeight, double inchesPerUnit, double paperWidthMm, double paperHeightMm, ScaleSystem system)
    {
        var scales = Of(system);
        var preferred = Preferred(kind, system);
        var candidates = scales.Where(s => s.Ratio >= preferred.Ratio).ToList();
        foreach (var scale in candidates)
        {
            if (Fits(scale, modelWidth, modelHeight, inchesPerUnit, paperWidthMm, paperHeightMm))
            {
                return scale;
            }
        }

        return candidates[^1];
    }

    /// <summary>True when the drawing fits the paper area at <paramref name="scale"/>.</summary>
    public static bool Fits(DrawingScale scale, double modelWidth, double modelHeight, double inchesPerUnit, double paperWidthMm, double paperHeightMm)
    {
        ArgumentNullException.ThrowIfNull(scale);
        var k = scale.PaperMmPerUnit(inchesPerUnit);
        return modelWidth * k <= paperWidthMm + 1e-9 && modelHeight * k <= paperHeightMm + 1e-9;
    }

    /// <summary>The scale system for a drawing's INSUNITS: imperial for inches and feet (and unitless), metric otherwise.</summary>
    public static ScaleSystem SystemFor(int insUnits) => insUnits is 0 or 1 or 2 or 8 or 9 or 10 ? ScaleSystem.Imperial : ScaleSystem.Metric;
}
