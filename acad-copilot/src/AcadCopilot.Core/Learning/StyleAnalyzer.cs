using System.Globalization;
using AcadCopilot.Core.Drafting;

namespace AcadCopilot.Core.Learning;

/// <summary>A layer of the drawing.</summary>
/// <param name="Name">Layer name.</param>
/// <param name="Color">ACI number ("7") or "r,g,b".</param>
/// <param name="Linetype">Linetype name.</param>
/// <param name="LineweightMm">Lineweight in mm; null = Default / ByLayer.</param>
/// <param name="Objects">Model-space objects on it.</param>
public sealed record LayerSample(string Name, string Color, string Linetype, double? LineweightMm, int Objects);

/// <summary>A TEXT or MTEXT object.</summary>
/// <param name="Style">Text style name.</param>
/// <param name="Height">Height in drawing units.</param>
/// <param name="Layer">Layer.</param>
public sealed record TextSample(string Style, double Height, string Layer);

/// <summary>A text style.</summary>
/// <param name="Name">Style name.</param>
/// <param name="Font">Font file ("arial.ttf", "romans.shx").</param>
/// <param name="FixedHeight">Fixed height (0 = asked by TEXT).</param>
public sealed record TextStyleSample(string Name, string Font, double FixedHeight);

/// <summary>A dimension style with the number of dimensions that use it.</summary>
/// <param name="Name">Style name.</param>
/// <param name="Uses">Dimensions using it in model space.</param>
/// <param name="Variables">Key variables ("DIMSCALE" -> "48").</param>
public sealed record DimStyleSample(string Name, int Uses, IReadOnlyDictionary<string, string> Variables);

/// <summary>A straight segment (LINE or a straight polyline segment).</summary>
public sealed record SegmentSample(double X1, double Y1, double X2, double Y2, string Layer)
{
    /// <summary>Length in drawing units.</summary>
    public double Length => Math.Sqrt(((X2 - X1) * (X2 - X1)) + ((Y2 - Y1) * (Y2 - Y1)));
}

/// <summary>An ARC (door swings).</summary>
/// <param name="Radius">Radius in drawing units.</param>
/// <param name="SweepDegrees">Included angle.</param>
/// <param name="Layer">Layer.</param>
public sealed record ArcSample(double Radius, double SweepDegrees, string Layer);

/// <summary>Block references of one block on one layer.</summary>
/// <param name="Name">Block name (dynamic blocks: their definition's name).</param>
/// <param name="Layer">Layer.</param>
/// <param name="Count">References.</param>
public sealed record BlockSample(string Name, string Layer, int Count);

/// <summary>Hatches of one pattern and scale on one layer.</summary>
/// <param name="Pattern">Pattern name.</param>
/// <param name="Scale">Pattern scale.</param>
/// <param name="Layer">Layer.</param>
/// <param name="Count">Hatches.</param>
public sealed record HatchSample(string Pattern, double Scale, string Layer, int Count);

/// <summary>What the plug-in reads from a drawing for <c>learn_style_from_drawing</c> (no AutoCAD types).</summary>
public sealed record StyleSample
{
    /// <summary>Drawing file name.</summary>
    public string? DrawingName { get; init; }

    /// <summary>What 1 drawing unit is in inches (1 for architectural inches, 1/25.4 for mm); 0 = unknown.</summary>
    public double InchesPerUnit { get; init; }

    /// <summary>Units in words ("architectural, 1 unit = 1 inch").</summary>
    public string? UnitsSummary { get; init; }

    /// <summary>Layers.</summary>
    public IReadOnlyList<LayerSample> Layers { get; init; } = [];

    /// <summary>Text objects.</summary>
    public IReadOnlyList<TextSample> Texts { get; init; } = [];

    /// <summary>Text styles.</summary>
    public IReadOnlyList<TextStyleSample> TextStyles { get; init; } = [];

    /// <summary>Dimension styles (with use counts).</summary>
    public IReadOnlyList<DimStyleSample> DimStyles { get; init; } = [];

    /// <summary>Current dimension style name.</summary>
    public string? CurrentDimStyle { get; init; }

    /// <summary>Straight segments (lines and polyline segments).</summary>
    public IReadOnlyList<SegmentSample> Segments { get; init; } = [];

    /// <summary>Polyline constant widths with their layer.</summary>
    public IReadOnlyList<(double Width, string Layer)> PolylineWidths { get; init; } = [];

    /// <summary>Arcs.</summary>
    public IReadOnlyList<ArcSample> Arcs { get; init; } = [];

    /// <summary>Block references.</summary>
    public IReadOnlyList<BlockSample> Blocks { get; init; } = [];

    /// <summary>Hatches.</summary>
    public IReadOnlyList<HatchSample> Hatches { get; init; } = [];

    /// <summary>LTSCALE.</summary>
    public double? Ltscale { get; init; }
}

/// <summary>A learned style: style.md sections ("Layers" -> lines) plus where it came from.</summary>
public sealed record StyleProfile
{
    /// <summary>Most layers kept in style.md.</summary>
    public const int MaxLayers = 16;

    /// <summary>Section names.</summary>
    public const string UnitsSection = "Units";

    /// <summary>Layers section.</summary>
    public const string LayersSection = "Layers";

    /// <summary>Text section.</summary>
    public const string TextSection = "Text";

    /// <summary>Dimensions section.</summary>
    public const string DimensionsSection = "Dimensions";

    /// <summary>Walls section.</summary>
    public const string WallsSection = "Walls";

    /// <summary>Doors, windows and stairs section.</summary>
    public const string OpeningsSection = "Doors, windows and stairs";

    /// <summary>Blocks section.</summary>
    public const string BlocksSection = "Blocks and furniture";

    /// <summary>Hatches section.</summary>
    public const string HatchesSection = "Hatches";

    /// <summary>Sections in order, each a list of bullet texts.</summary>
    public IReadOnlyList<(string Title, IReadOnlyList<string> Lines)> Sections { get; init; } = [];

    /// <summary>"House-GF.dwg" (added to "Learned from").</summary>
    public string? Source { get; init; }
}

/// <summary>
/// Turns a <see cref="StyleSample"/> into style.md sections: layers with colour / linetype / lineweight, the text styles
/// and most used heights, the dimension style variables, wall thicknesses (spacing of parallel lines, polyline widths),
/// door swing radii, block names used for doors, windows and furniture, hatch patterns. Pure, unit-tested.
/// </summary>
public static class StyleAnalyzer
{
    /// <summary>Most segments compared for wall thicknesses (the longest are kept).</summary>
    public const int MaxSegments = 3_000;

    private static readonly string[] DoorWords = ["DOOR", "DR", "SHUTTER", "GATE"];
    private static readonly string[] WindowWords = ["WIN", "WINDOW", "GLAZ", "VENT", "WDW"];
    private static readonly string[] StairWords = ["STAIR", "STR", "STRS"];
    private static readonly string[] FurnitureWords =
        ["FURN", "BED", "SOFA", "CHAIR", "TABLE", "DINING", "WARDROBE", "WC", "BASIN", "SINK", "TOILET", "KITCHEN", "COUNTER", "CAR", "PLANT", "TREE", "FIXT", "SANITARY", "SHOWER"];

    /// <summary>Analyses <paramref name="sample"/>.</summary>
    public static StyleProfile Analyze(StyleSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var ipu = sample.InchesPerUnit > 0 ? sample.InchesPerUnit : 0;
        var sections = new List<(string, IReadOnlyList<string>)>();
        void Add(string title, List<string> lines)
        {
            if (lines.Count > 0)
            {
                sections.Add((title, lines));
            }
        }

        if (!string.IsNullOrWhiteSpace(sample.UnitsSummary))
        {
            Add(StyleProfile.UnitsSection, [KnowledgeText.OneLine(sample.UnitsSummary) + (sample.Ltscale is { } lt ? ", LTSCALE " + Num(lt) : string.Empty)]);
        }

        Add(StyleProfile.LayersSection, Layers(sample));
        Add(StyleProfile.TextSection, Text(sample, ipu));
        Add(StyleProfile.DimensionsSection, Dimensions(sample));
        Add(StyleProfile.WallsSection, Walls(sample, ipu));
        Add(StyleProfile.OpeningsSection, Openings(sample, ipu));
        Add(StyleProfile.BlocksSection, Blocks(sample));
        Add(StyleProfile.HatchesSection, Hatches(sample));
        return new StyleProfile { Sections = sections, Source = sample.DrawingName };
    }

    /// <summary>
    /// Typical spacings of parallel lines (wall thicknesses): for every segment at least 2' long, the distance to the
    /// nearest parallel segment that overlaps it, counted when it lies between 2" and 24" (in drawing units when the
    /// scale is unknown). Returns (spacing, count) most common first.
    /// </summary>
    public static IReadOnlyList<(double Spacing, int Count)> ParallelSpacings(IReadOnlyList<SegmentSample> segments, double inchesPerUnit)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ipu = inchesPerUnit > 0 ? inchesPerUnit : 1;
        var minLength = 24 / ipu;
        var minGap = 2 / ipu;
        var maxGap = 24 / ipu;
        var lines = segments.Where(s => s.Length >= minLength).OrderByDescending(s => s.Length).Take(MaxSegments)
            .Select(s => new Seg(s))
            .ToList();

        // Group by direction (1 degree buckets, 0-180) and compare within a bucket and its neighbours.
        var buckets = lines.GroupBy(l => l.Bucket).ToDictionary(g => g.Key, g => g.ToList());
        var counts = new Dictionary<double, int>();
        foreach (var line in lines)
        {
            double? nearest = null;
            foreach (var bucket in new[] { line.Bucket, (line.Bucket + 1) % 180, (line.Bucket + 179) % 180 })
            {
                if (!buckets.TryGetValue(bucket, out var others))
                {
                    continue;
                }

                foreach (var other in others)
                {
                    if (ReferenceEquals(other, line) || !line.IsParallel(other))
                    {
                        continue;
                    }

                    var gap = line.Distance(other);
                    if (gap < minGap || gap > maxGap || line.Overlap(other) < Math.Min(minLength / 2, Math.Min(line.Length, other.Length) * 0.5))
                    {
                        continue;
                    }

                    if (nearest is null || gap < nearest)
                    {
                        nearest = gap;
                    }
                }
            }

            if (nearest is { } found)
            {
                // Round to 1/4" (or 2 significant digits of drawing units when the scale is unknown).
                var key = inchesPerUnit > 0 ? Math.Round(found * ipu * 4) / 4 / ipu : RoundSignificant(found, 2);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }

        return [.. counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => (p.Key, p.Value))];
    }

    private static List<string> Layers(StyleSample sample)
    {
        var lines = new List<string>();
        foreach (var layer in sample.Layers
            .Where(l => l.Objects > 0 && !string.Equals(l.Name, "Defpoints", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Objects)
            .Take(StyleProfile.MaxLayers))
        {
            var weight = layer.LineweightMm is { } mm ? Num(mm) + " mm" : "default weight";
            lines.Add($"{layer.Name}: colour {layer.Color}, {layer.Linetype}, {weight} ({layer.Objects} objects)");
        }

        return lines;
    }

    private static List<string> Text(StyleSample sample, double ipu)
    {
        var lines = new List<string>();
        if (sample.Texts.Count == 0)
        {
            return lines;
        }

        var fonts = sample.TextStyles.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        foreach (var style in sample.Texts.GroupBy(t => t.Style, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3))
        {
            var font = fonts.TryGetValue(style.Key, out var s) ? $" ({s.Font}{(s.FixedHeight > 0 ? ", fixed height " + Length(s.FixedHeight, ipu) : ", height 0")})" : string.Empty;
            var heights = style.GroupBy(t => Math.Round(t.Height, 4)).OrderByDescending(g => g.Count()).Take(4)
                .Select(g => $"{Length(g.Key, ipu)} x{g.Count()}");
            lines.Add($"Style {style.Key}{font}: heights {string.Join(", ", heights)}");
        }

        foreach (var layer in sample.Texts.GroupBy(t => t.Layer, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(1))
        {
            lines.Add($"Text mostly on layer {layer.Key} ({layer.Count()} of {sample.Texts.Count})");
        }

        return lines;
    }

    private static List<string> Dimensions(StyleSample sample)
    {
        var lines = new List<string>();
        foreach (var style in sample.DimStyles.Where(d => d.Uses > 0 || string.Equals(d.Name, sample.CurrentDimStyle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Uses).Take(2))
        {
            var vars = string.Join(", ", style.Variables.Select(v => v.Key + " " + v.Value));
            lines.Add($"Style {style.Name} ({style.Uses} dimensions{(string.Equals(style.Name, sample.CurrentDimStyle, StringComparison.OrdinalIgnoreCase) ? ", current" : string.Empty)}): {vars}");
        }

        return lines;
    }

    private static List<string> Walls(StyleSample sample, double ipu)
    {
        var lines = new List<string>();
        var wallSegments = sample.Segments.Where(s => s.Layer.Contains("WALL", StringComparison.OrdinalIgnoreCase)).ToList();
        var useWallLayers = wallSegments.Count >= 8;
        var spacings = ParallelSpacings(useWallLayers ? wallSegments : sample.Segments, ipu);
        var total = spacings.Sum(s => s.Count);
        var common = spacings.Where(s => s.Count >= Math.Max(2, total / 20)).Take(3).ToList();
        if (common.Count > 0)
        {
            var layers = useWallLayers
                ? " on " + string.Join(", ", wallSegments.GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key))
                : string.Empty;
            lines.Add($"Wall thicknesses (parallel line spacing{layers}): {string.Join(", ", common.Select(c => $"{Length(c.Spacing, ipu)} ({c.Count} lines)"))}");
        }

        var widths = sample.PolylineWidths.Where(w => w.Width > 0)
            .GroupBy(w => (Math.Round(w.Width, 4), w.Layer))
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{Length(g.Key.Item1, ipu)} on {g.Key.Layer} x{g.Count()}")
            .ToList();
        if (widths.Count > 0)
        {
            lines.Add("Polylines with width: " + string.Join(", ", widths));
        }

        return lines;
    }

    private static List<string> Openings(StyleSample sample, double ipu)
    {
        var lines = new List<string>();
        var swings = sample.Arcs.Where(a => Math.Abs(a.SweepDegrees - 90) < 3 && (HasWord(a.Layer, DoorWords) || !sample.Arcs.Any(x => HasWord(x.Layer, DoorWords))))
            .GroupBy(a => ipu > 0 ? Math.Round(a.Radius * ipu) / ipu : RoundSignificant(a.Radius, 2))
            .OrderByDescending(g => g.Count())
            .Take(4)
            .ToList();
        if (swings.Count > 0)
        {
            var layer = swings.SelectMany(g => g).GroupBy(a => a.Layer, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
            lines.Add($"Door swings (90 degree arcs on {layer}): widths {string.Join(", ", swings.Select(g => $"{Length(g.Key, ipu)} x{g.Count()}"))}");
        }

        var windowLayers = sample.Layers.Where(l => l.Objects > 0 && HasWord(l.Name, WindowWords)).Select(l => l.Name).Take(2).ToList();
        if (windowLayers.Count > 0)
        {
            lines.Add("Windows on layer " + string.Join(", ", windowLayers));
        }

        var stairLayers = sample.Layers.Where(l => l.Objects > 0 && HasWord(l.Name, StairWords)).Select(l => $"{l.Name} ({l.Objects} objects)").Take(2).ToList();
        if (stairLayers.Count > 0)
        {
            lines.Add("Stairs on layer " + string.Join(", ", stairLayers));
        }

        return lines;
    }

    private static List<string> Blocks(StyleSample sample)
    {
        var lines = new List<string>();
        var blocks = sample.Blocks.Where(b => !b.Name.StartsWith('*')).ToList();
        if (blocks.Count == 0)
        {
            return lines;
        }

        string Kind(BlockSample b) =>
            HasWord(b.Name, DoorWords) || HasWord(b.Layer, DoorWords) ? "Doors"
            : HasWord(b.Name, WindowWords) || HasWord(b.Layer, WindowWords) ? "Windows"
            : HasWord(b.Name, FurnitureWords) || HasWord(b.Layer, FurnitureWords) ? "Furniture and fixtures"
            : "Other blocks";
        foreach (var kind in blocks.GroupBy(Kind).OrderBy(g => g.Key == "Other blocks" ? 1 : 0).ThenByDescending(g => g.Sum(b => b.Count)))
        {
            var names = kind.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(b => b.Count))
                .Take(6)
                .Select(g => $"{g.Key} x{g.Sum(b => b.Count)} ({g.OrderByDescending(b => b.Count).First().Layer})");
            lines.Add($"{kind.Key}: {string.Join(", ", names)}");
        }

        return lines;
    }

    private static List<string> Hatches(StyleSample sample) =>
        [.. sample.Hatches
            .GroupBy(h => (h.Pattern.ToUpperInvariant(), Math.Round(h.Scale, 3), h.Layer))
            .OrderByDescending(g => g.Sum(h => h.Count))
            .Take(5)
            .Select(g => $"{g.Key.Item1} scale {Num(g.Key.Item2)} on {g.Key.Layer} x{g.Sum(h => h.Count)}")];

    private static bool HasWord(string text, string[] words)
    {
        var upper = text.ToUpperInvariant();
        foreach (var word in words)
        {
            var index = upper.IndexOf(word, StringComparison.Ordinal);
            while (index >= 0)
            {
                // A short word ("DR", "WC", "CAR") must stand alone between separators; longer ones may be part of a name.
                var before = index == 0 || !char.IsLetter(upper[index - 1]);
                var after = index + word.Length >= upper.Length || !char.IsLetter(upper[index + word.Length]);
                if (word.Length > 3 || (before && after))
                {
                    return true;
                }

                index = upper.IndexOf(word, index + 1, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string Length(double value, double ipu) =>
        ipu > 0 && Math.Abs(ipu - 1) < 1e-9
            ? FeetInches.FormatForInput(value, 8)
            : ipu > 0 ? $"{Num(value)} ({FeetInches.FormatForInput(value * ipu, 8)})" : Num(value);

    private static string Num(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static double RoundSignificant(double value, int digits)
    {
        if (value == 0)
        {
            return 0;
        }

        var scale = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(value))) + 1 - digits);
        return Math.Round(value / scale) * scale;
    }

    private sealed class Seg
    {
        private readonly double _x1;
        private readonly double _y1;
        private readonly double _ux;
        private readonly double _uy;

        public Seg(SegmentSample s)
        {
            _x1 = s.X1;
            _y1 = s.Y1;
            Length = s.Length;
            _ux = (s.X2 - s.X1) / Length;
            _uy = (s.Y2 - s.Y1) / Length;
            var angle = Math.Atan2(_uy, _ux) * 180 / Math.PI;
            angle = ((angle % 180) + 180) % 180;
            Bucket = (int)Math.Floor(angle) % 180;
        }

        public double Length { get; }

        public int Bucket { get; }

        public bool IsParallel(Seg other) => Math.Abs((_ux * other._uy) - (_uy * other._ux)) < 0.0087; // about 0.5 degree

        // Perpendicular distance from the other segment's start point to this line.
        public double Distance(Seg other) => Math.Abs(((other._x1 - _x1) * _uy) - ((other._y1 - _y1) * _ux));

        // Length of the overlap of the other segment's projection onto this one.
        public double Overlap(Seg other)
        {
            var a = Project(other._x1, other._y1);
            var b = Project(other._x1 + (other._ux * other.Length), other._y1 + (other._uy * other.Length));
            var lo = Math.Max(0, Math.Min(a, b));
            var hi = Math.Min(Length, Math.Max(a, b));
            return hi - lo;
        }

        private double Project(double x, double y) => ((x - _x1) * _ux) + ((y - _y1) * _uy);
    }
}
