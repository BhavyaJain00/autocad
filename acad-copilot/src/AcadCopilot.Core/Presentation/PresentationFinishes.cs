using System.Globalization;

namespace AcadCopilot.Core.Presentation;

/// <summary>A true colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>"R,G,B" (as AutoCAD's Truecolor prompt takes it).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{R},{G},{B}");

    /// <summary>Parses "R,G,B" (0-255 each, spaces allowed) or "#RRGGBB".</summary>
    public static bool TryParse(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('#') && value.Length == 7 &&
            int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            color = new Rgb((byte)(hex >> 16), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));
            return true;
        }

        var parts = value.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }

        var channels = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!byte.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out channels[i]))
            {
                return false;
            }
        }

        color = new Rgb(channels[0], channels[1], channels[2]);
        return true;
    }

    /// <summary>This colour moved <paramref name="amount"/> (0-1) of the way towards <paramref name="other"/>.</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        static byte Channel(byte a, byte b, double t) => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);
        return new Rgb(Channel(R, other.R, t), Channel(G, other.G, t), Channel(B, other.B, t));
    }

    /// <summary>A lighter tint (towards white).</summary>
    public Rgb Lighter(double amount) => Mix(new Rgb(255, 255, 255), amount);

    /// <summary>A darker shade (towards black).</summary>
    public Rgb Darker(double amount) => Mix(new Rgb(0, 0, 0), amount);
}

/// <summary>How a finish is filled.</summary>
public enum FillKind
{
    /// <summary>SOLID hatch in one true colour.</summary>
    Solid,

    /// <summary>Two-colour LINEAR gradient hatch.</summary>
    Gradient,

    /// <summary>Predefined pattern in a darker colour over a solid background colour.</summary>
    Pattern,
}

/// <summary>A presentation finish: what it is, its layer and its default look.</summary>
/// <param name="Name">Wire name (wall_paint ...).</param>
/// <param name="Layer">Layer (A-ELEV-PRES-...).</param>
/// <param name="Description">What to use it for.</param>
/// <param name="Kind">Solid, gradient or pattern.</param>
/// <param name="Color">Main colour (solid colour, gradient start, or pattern background).</param>
/// <param name="Color2">Gradient end colour, or the pattern's line colour.</param>
/// <param name="GradientAngle">Gradient direction in degrees (0 = left to right, 90 = bottom to top).</param>
/// <param name="Pattern">Predefined hatch pattern (Pattern kind).</param>
/// <param name="TransparencyPercent">Hatch transparency (0 = opaque).</param>
/// <param name="Rank">Draw order: 0 backdrop (sky, ground), 1 surfaces, 2 in front (planting), 3 shadows; all behind the linework.</param>
public sealed record PresentationFinish(
    string Name,
    string Layer,
    string Description,
    FillKind Kind,
    Rgb Color,
    Rgb? Color2 = null,
    double GradientAngle = 0,
    string? Pattern = null,
    int TransparencyPercent = 0,
    int Rank = 1);

/// <summary>
/// The default palette of the colour presentation elevation: pleasant, soft true colours for Indian residential
/// elevations, one layer per kind of finish (A-ELEV-PRES-*), so the line elevation stays untouched underneath and the
/// colours can be switched off or changed per layer.
/// </summary>
public static class PresentationFinishes
{
    /// <summary>Prefix of every presentation layer.</summary>
    public const string LayerPrefix = "A-ELEV-PRES-";

    /// <summary>The shadow finish.</summary>
    public const string Shadow = "shadow";

    /// <summary>The sky finish.</summary>
    public const string Sky = "sky";

    /// <summary>The ground finish.</summary>
    public const string Ground = "ground";

    /// <summary>Every finish.</summary>
    public static IReadOnlyList<PresentationFinish> All { get; } =
    [
        new("wall_paint", LayerPrefix + "WALL", "Painted plaster walls (main colour)", FillKind.Solid, new Rgb(243, 233, 212)),
        new("wall_accent", LayerPrefix + "WALL", "Accent wall / feature band (terracotta)", FillKind.Solid, new Rgb(201, 122, 92)),
        new("wall_white", LayerPrefix + "WALL", "White or off-white plaster", FillKind.Solid, new Rgb(248, 246, 240)),
        new("wall_grey", LayerPrefix + "WALL", "Grey texture paint / cement finish", FillKind.Solid, new Rgb(176, 178, 180)),
        new("stone_cladding", LayerPrefix + "STON", "Stone cladding (sandstone coursing)", FillKind.Pattern, new Rgb(201, 180, 150), new Rgb(150, 128, 100), Pattern: "AR-B816"),
        new("brick_cladding", LayerPrefix + "BRCK", "Exposed brick / brick tile cladding", FillKind.Pattern, new Rgb(178, 94, 68), new Rgb(232, 220, 204), Pattern: "AR-BRSTD"),
        new("wood", LayerPrefix + "WOOD", "Wooden doors, louvres and cladding (teak)", FillKind.Solid, new Rgb(150, 98, 60)),
        new("glass", LayerPrefix + "GLAZ", "Window and door glass (blue reflective gradient)", FillKind.Gradient, new Rgb(112, 162, 202), new Rgb(208, 230, 244), GradientAngle: 45),
        new("metal", LayerPrefix + "METL", "Railings, grills, window frames, gates (dark grey)", FillKind.Solid, new Rgb(70, 78, 88)),
        new("slab_band", LayerPrefix + "BAND", "Slab bands, chajjas / sunshades, parapet coping, plinth band", FillKind.Solid, new Rgb(236, 232, 224)),
        new("roof_tiles", LayerPrefix + "ROOF", "Sloping roof with clay tiles", FillKind.Pattern, new Rgb(172, 84, 62), new Rgb(120, 52, 40), Pattern: "AR-RROOF"),
        new("roof", LayerPrefix + "ROOF", "Flat roof edge, mumty / headroom slab, solid roof", FillKind.Solid, new Rgb(150, 150, 156)),
        new("planting", LayerPrefix + "PLNT", "Trees, shrubs, planters in front of the building", FillKind.Solid, new Rgb(104, 152, 88), Rank: 2),
        new(Sky, LayerPrefix + "SKY", "Sky behind the building (light at the horizon, blue above)", FillKind.Gradient, new Rgb(236, 244, 251), new Rgb(150, 194, 232), GradientAngle: 90, Rank: 0),
        new(Ground, LayerPrefix + "GRND", "Ground / earth band below the ground line", FillKind.Solid, new Rgb(164, 140, 110), Rank: 0),
        new("lawn", LayerPrefix + "GRND", "Lawn in front of the building", FillKind.Solid, new Rgb(140, 178, 98), Rank: 0),
        new("paving", LayerPrefix + "GRND", "Paved driveway / forecourt", FillKind.Pattern, new Rgb(204, 198, 186), new Rgb(150, 144, 132), Pattern: "AR-CONC", Rank: 0),
        new(Shadow, LayerPrefix + "SHAD", "Shadow under projections (chajjas, slabs, balconies) and beside projecting walls", FillKind.Solid, new Rgb(52, 58, 82), TransparencyPercent: 55, Rank: 3),
    ];

    /// <summary>Wire names, for the tool schema.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(f => f.Name)];

    /// <summary>The finish named <paramref name="name"/> (any case), or null.</summary>
    public static PresentationFinish? Find(string? name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The presentation layers with the colour each is created with (the first finish on it).</summary>
    public static IReadOnlyList<(string Layer, Rgb Color)> Layers { get; } =
        [.. All.GroupBy(f => f.Layer, StringComparer.Ordinal).Select(g => (g.Key, g.First().Color))];

    /// <summary>True when <paramref name="layer"/> is a presentation layer.</summary>
    public static bool IsPresentationLayer(string? layer) =>
        layer is not null && layer.StartsWith(LayerPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Draw-order rank of a presentation layer (0 backdrop .. 3 shadows; 1 for unknown names).</summary>
    public static int RankOfLayer(string? layer) =>
        All.FirstOrDefault(f => string.Equals(f.Layer, layer, StringComparison.OrdinalIgnoreCase))?.Rank ?? 1;

    /// <summary>
    /// The look of one fill: <paramref name="finish"/> with the AI's overrides (colour, second colour, gradient on or
    /// off, angle). A solid finish asked as a gradient runs from its colour to a lighter tint.
    /// </summary>
    public static PresentationFinish Resolve(PresentationFinish finish, Rgb? color, Rgb? color2, bool? gradient, double? angle)
    {
        ArgumentNullException.ThrowIfNull(finish);
        var result = finish;
        if (color is { } c)
        {
            result = result with { Color = c };
            if (finish.Kind == FillKind.Pattern && color2 is null)
            {
                result = result with { Color2 = c.Darker(0.3) };
            }
            else if (finish.Kind == FillKind.Gradient && color2 is null)
            {
                result = result with { Color2 = c.Lighter(0.55) };
            }
        }

        if (color2 is { } c2)
        {
            result = result with { Color2 = c2 };
        }

        if (gradient == true && result.Kind != FillKind.Gradient)
        {
            result = result with { Kind = FillKind.Gradient, Color2 = color2 ?? result.Color.Lighter(0.5), Pattern = null };
        }
        else if (gradient == false && result.Kind == FillKind.Gradient)
        {
            result = result with { Kind = FillKind.Solid, Color2 = null };
        }

        if (angle is { } a)
        {
            result = result with { GradientAngle = a };
        }

        return result;
    }
}
