using System.Globalization;
using System.Text.Json.Nodes;

namespace AcadCopilot.Core.Drafting;

/// <summary>
/// The unit settings of a drawing that decide how lengths are typed and reported:
/// LUNITS (display/input format), INSUNITS (what one drawing unit means) and LUPREC (decimal places).
/// Lengths in drafting reports are given in drawing units AND, when the scale is known, in feet-inches.
/// </summary>
public sealed record DrawingUnits
{
    /// <summary>LUNITS: 1 scientific, 2 decimal, 3 engineering (feet and decimal inches), 4 architectural (feet-inches), 5 fractional.</summary>
    public int LUnits { get; init; } = 2;

    /// <summary>INSUNITS: 0 unitless, 1 inches, 2 feet, 3 miles, 4 millimetres, 5 centimetres, 6 metres, ...</summary>
    public int InsUnits { get; init; }

    /// <summary>LUPREC: number of decimal places (or the fraction denominator exponent for LUNITS 4 and 5).</summary>
    public int LuPrec { get; init; } = 4;

    /// <summary>
    /// True when AutoCAD accepts feet-inch input such as <c>12'6"</c> or <c>4-1/2"</c>: only with LUNITS 3 or 4.
    /// With other LUNITS values the copilot must type plain numbers in drawing units.
    /// </summary>
    public bool AcceptsFeetInches => LUnits is 3 or 4;

    /// <summary>
    /// How many inches one drawing unit is, or null when it cannot be known (unitless decimal drawing).
    /// Engineering and architectural units (LUNITS 3 and 4) always mean 1 unit = 1 inch.
    /// </summary>
    public double? InchesPerUnit => LUnits is 3 or 4
        ? 1.0
        : InsUnits switch
        {
            1 => 1.0,
            2 => 12.0,
            3 => 63360.0,
            4 => 1.0 / 25.4,
            5 => 1.0 / 2.54,
            6 => 1000.0 / 25.4,
            7 => 1000000.0 / 25.4,
            8 => 1e-6,
            9 => 0.001,
            10 => 36.0,
            14 => 100.0 / 25.4,
            _ => null,
        };

    /// <summary>Plain name of one drawing unit ("inch", "foot", "millimetre", "unit").</summary>
    public string UnitName => LUnits is 3 or 4
        ? "inch"
        : InsUnits switch
        {
            1 => "inch",
            2 => "foot",
            3 => "mile",
            4 => "millimetre",
            5 => "centimetre",
            6 => "metre",
            7 => "kilometre",
            8 => "microinch",
            9 => "mil",
            10 => "yard",
            14 => "decimetre",
            _ => "unit",
        };

    /// <summary>Name of the LUNITS format ("architectural", "decimal", ...).</summary>
    public string FormatName => LUnits switch
    {
        1 => "scientific",
        2 => "decimal",
        3 => "engineering",
        4 => "architectural",
        5 => "fractional",
        _ => "LUNITS " + LUnits.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// One-line description for the LLM: "architectural (LUNITS 4): 1 unit = 1 inch; type 12'6" or 150".
    /// </summary>
    public string Describe()
    {
        var scale = InchesPerUnit is null
            ? "the size of 1 unit is not set (INSUNITS 0), so no feet-inch conversion is possible"
            : $"1 unit = 1 {UnitName}";
        var input = AcceptsFeetInches
            ? "feet-inch input such as 12'6\" or 4-1/2\" works"
            : "type plain numbers in drawing units (feet-inch input like 12'6\" is rejected with LUNITS " + LUnits.ToString(CultureInfo.InvariantCulture) + ")";
        return $"{FormatName} (LUNITS {LUnits.ToString(CultureInfo.InvariantCulture)}, INSUNITS {InsUnits.ToString(CultureInfo.InvariantCulture)}): {scale}; {input}.";
    }

    /// <summary>A length in drawing units as feet-inch text (<c>30'-0"</c>), or null when the scale is unknown.</summary>
    public string? ToFeetInches(double length)
    {
        if (InchesPerUnit is not { } perUnit || double.IsNaN(length) || double.IsInfinity(length))
        {
            return null;
        }

        var inches = length * perUnit;
        return Math.Abs(inches) > 1e9 ? null : FeetInches.Format(inches);
    }

    /// <summary>An area in square drawing units as square feet, or null when the scale is unknown.</summary>
    public double? ToSquareFeet(double area)
    {
        if (InchesPerUnit is not { } perUnit)
        {
            return null;
        }

        var squareInches = area * perUnit * perUnit;
        return Math.Round(squareInches / 144.0, 2);
    }

    /// <summary>Rounds a value in drawing units for reports (6 significant decimals at most, no -0).</summary>
    public static double Round(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    /// <summary>
    /// A length for reports: the rounded value in drawing units plus its feet-inch text when known,
    /// e.g. <c>{"du":360,"ftIn":"30'-0\""}</c>.
    /// </summary>
    public JsonObject Length(double length)
    {
        var node = new JsonObject { ["du"] = Round(length) };
        if (ToFeetInches(length) is { } text)
        {
            node["ftIn"] = text;
        }

        return node;
    }

    /// <summary>Text for a length: "360 (30'-0\")" or just "360" when the scale is unknown.</summary>
    public string LengthText(double length)
    {
        var value = Round(length).ToString("0.######", CultureInfo.InvariantCulture);
        return ToFeetInches(length) is { } text ? $"{value} ({text})" : value;
    }
}
