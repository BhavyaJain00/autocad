using System.Globalization;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Accuracy;

/// <summary>A minimum clear room size (inches): the shorter side and the longer side.</summary>
/// <param name="Short">Minimum shorter side.</param>
/// <param name="Long">Minimum longer side.</param>
public sealed record RoomMinimum(double Short, double Long)
{
    /// <summary>"10'-0" x 10'-0"".</summary>
    public string Text() => Inch.Size(Short, Long);
}

/// <summary>
/// The sizes validate_plan checks against: typical Indian residential defaults (never a bylaw), changed by the user's
/// learned rules (rules.md lines such as "Bedrooms at least 9'x10'", "toilet door 2'6", "riser 7\" max", "follow Vastu").
/// </summary>
public sealed partial class PlanRules
{
    /// <summary>The default minimum clear sizes per room type.</summary>
    public static IReadOnlyDictionary<string, RoomMinimum> DefaultRoomMinimums { get; } = new Dictionary<string, RoomMinimum>(StringComparer.Ordinal)
    {
        ["bedroom"] = new(120, 120),
        ["master_bedroom"] = new(132, 144),
        ["living"] = new(132, 144),
        ["kitchen"] = new(84, 120),
        ["toilet"] = new(54, 84),
        ["bath_wc"] = new(60, 84),
        ["dining"] = new(96, 120),
        ["parking"] = new(108, 204),
        ["pooja"] = new(48, 48),
        ["study"] = new(96, 96),
        ["store"] = new(36, 48),
        ["utility"] = new(42, 60),
        ["balcony"] = new(36, 36),
    };

    /// <summary>Minimum clear sizes per room type (inches).</summary>
    public Dictionary<string, RoomMinimum> RoomMinimums { get; } = new(DefaultRoomMinimums, StringComparer.Ordinal);

    /// <summary>Passage width below which a warning is given (3'-6").</summary>
    public double PassageWidth { get; set; } = 42;

    /// <summary>Passage width below which it is an error (3'-0").</summary>
    public double PassageAbsolute { get; set; } = 36;

    /// <summary>Recommended stair flight width (3'-3").</summary>
    public double StairWidth { get; set; } = 39;

    /// <summary>Stair flight width below which it is an error (3'-0").</summary>
    public double StairWidthAbsolute { get; set; } = 36;

    /// <summary>Lowest riser (6").</summary>
    public double RiserMin { get; set; } = 6;

    /// <summary>Highest riser (7").</summary>
    public double RiserMax { get; set; } = 7;

    /// <summary>Typical riser (6 1/2").</summary>
    public double RiserTypical { get; set; } = 6.5;

    /// <summary>Smallest tread (10").</summary>
    public double TreadMin { get; set; } = 10;

    /// <summary>Main door width (3'-6").</summary>
    public double MainDoor { get; set; } = 42;

    /// <summary>Room door width (3'-0").</summary>
    public double RoomDoor { get; set; } = 36;

    /// <summary>Toilet door width (2'-6").</summary>
    public double ToiletDoor { get; set; } = 30;

    /// <summary>Window area / floor area below which a warning is given (1/10).</summary>
    public double WindowRatioMin { get; set; } = 0.10;

    /// <summary>Good window area / floor area (1/8).</summary>
    public double WindowRatioGood { get; set; } = 0.125;

    /// <summary>Outer wall thickness (9").</summary>
    public double OuterWall { get; set; } = 9;

    /// <summary>Inner wall thickness (4 1/2").</summary>
    public double InnerWall { get; set; } = 4.5;

    /// <summary>Lowest floor to floor height (10'-0").</summary>
    public double FloorHeightMin { get; set; } = 120;

    /// <summary>Highest usual floor to floor height (10'-6").</summary>
    public double FloorHeightMax { get; set; } = 126;

    /// <summary>Longest side / shortest side above which a habitable room is "long and thin".</summary>
    public double MaxAspect { get; set; } = 2.0;

    /// <summary>Vastu hints on.</summary>
    public bool Vastu { get; set; }

    /// <summary>The user's rules that changed something, verbatim.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>The defaults.</summary>
    public static PlanRules Default => new();

    /// <summary>
    /// The defaults changed by the user's rules (one rule per line, as in rules.md). Lines that name a room type, door,
    /// wall, passage, stair, riser, tread, floor height or Vastu together with a size are applied; the rest are ignored.
    /// </summary>
    public static PlanRules FromRules(IEnumerable<string>? lines)
    {
        var rules = new PlanRules();
        foreach (var raw in lines ?? [])
        {
            if (!string.IsNullOrWhiteSpace(raw) && rules.Apply(raw.Trim()))
            {
                rules.Applied.Add(raw.Trim());
            }
        }

        return rules;
    }

    /// <summary>Applies one rule line; true when it changed something.</summary>
    public bool Apply(string line)
    {
        var text = line.ToLowerInvariant().Replace('’', '\'').Replace('′', '\'').Replace('″', '"').Replace('×', 'x');
        if (text.Contains("vastu", StringComparison.Ordinal) || text.Contains("vaastu", StringComparison.Ordinal))
        {
            Vastu = !NegativeVastu().IsMatch(text);
            return true;
        }

        var pair = PairPattern().Match(text);
        foreach (var (keyword, type) in RoomKeywords)
        {
            if (!ContainsWord(text, keyword) || text.Contains("door", StringComparison.Ordinal) || text.Contains("window", StringComparison.Ordinal))
            {
                continue;
            }

            if (type == "passage")
            {
                return Single(text, feetDefault: true) is { } width && Set(() => PassageWidth = width);
            }

            if (type == "stair")
            {
                if (text.Contains("riser", StringComparison.Ordinal) || text.Contains("tread", StringComparison.Ordinal))
                {
                    break;
                }

                return Single(text, feetDefault: true) is { } width && Set(() => StairWidth = width);
            }

            if (!pair.Success || ParseLength(pair.Groups[1].Value, feetDefault: true) is not { } a || ParseLength(pair.Groups[2].Value, feetDefault: true) is not { } b)
            {
                return false;
            }

            var minimum = new RoomMinimum(Math.Min(a, b), Math.Max(a, b));
            RoomMinimums[type] = minimum;
            if (type == "toilet" && keyword.StartsWith("toilet", StringComparison.Ordinal))
            {
                RoomMinimums["bath_wc"] = minimum;
            }

            return true;
        }

        if (text.Contains("door", StringComparison.Ordinal) && Single(text, feetDefault: true) is { } door)
        {
            if (text.Contains("main", StringComparison.Ordinal) || text.Contains("entrance", StringComparison.Ordinal))
            {
                MainDoor = door;
            }
            else if (text.Contains("toilet", StringComparison.Ordinal) || text.Contains("bath", StringComparison.Ordinal) || text.Contains("wc", StringComparison.Ordinal))
            {
                ToiletDoor = door;
            }
            else
            {
                RoomDoor = door;
            }

            return true;
        }

        if (text.Contains("riser", StringComparison.Ordinal) && Single(text, feetDefault: false) is { } riser)
        {
            if (text.Contains("min", StringComparison.Ordinal) || text.Contains("at least", StringComparison.Ordinal))
            {
                RiserMin = riser;
            }
            else
            {
                RiserMax = riser;
                RiserTypical = Math.Min(RiserTypical, riser);
            }

            return true;
        }

        if (text.Contains("tread", StringComparison.Ordinal) && Single(text, feetDefault: false) is { } tread)
        {
            TreadMin = tread;
            return true;
        }

        if ((text.Contains("floor height", StringComparison.Ordinal) || text.Contains("floor to floor", StringComparison.Ordinal)) && Single(text, feetDefault: true) is { } height)
        {
            FloorHeightMin = Math.Min(height, FloorHeightMin);
            FloorHeightMax = Math.Max(height, FloorHeightMax);
            return true;
        }

        if (text.Contains("wall", StringComparison.Ordinal) && Single(text, feetDefault: false) is { } thickness && thickness <= 24)
        {
            if (text.Contains("outer", StringComparison.Ordinal) || text.Contains("outside", StringComparison.Ordinal) || text.Contains("external", StringComparison.Ordinal))
            {
                OuterWall = thickness;
            }
            else if (text.Contains("inner", StringComparison.Ordinal) || text.Contains("inside", StringComparison.Ordinal) || text.Contains("internal", StringComparison.Ordinal) || text.Contains("partition", StringComparison.Ordinal))
            {
                InnerWall = thickness;
            }
            else
            {
                return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>Reads "10'", "10'6", "10'-6\"", "4.5\"", "10 ft", "9 inch" or a bare number (feet or inches by default).</summary>
    public static double? ParseLength(string text, bool feetDefault)
    {
        var t = text.Trim();
        var m = FeetWords().Match(t);
        if (m.Success)
        {
            var feet = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var inches = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return Inch.Snap((feet * 12) + inches);
        }

        var i = InchWords().Match(t);
        if (i.Success)
        {
            return Inch.Snap(double.Parse(i.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (Drafting.FeetInches.TryParse(t, out var parsed) && (t.Contains('\'', StringComparison.Ordinal) || t.Contains('"', StringComparison.Ordinal)))
        {
            return Inch.Snap(parsed);
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare) ? Inch.Snap(feetDefault ? bare * 12 : bare) : null;
    }

    private static double? Single(string text, bool feetDefault)
    {
        var m = LengthToken().Match(text);
        return m.Success ? ParseLength(m.Value, feetDefault) : null;
    }

    private static bool Set(Action action)
    {
        action();
        return true;
    }

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, @"(^|[^a-z])" + Regex.Escape(word) + @"($|[^a-z])", RegexOptions.CultureInvariant);

    private static readonly (string Keyword, string Type)[] RoomKeywords =
    [
        ("master bedroom", "master_bedroom"), ("master bedrooms", "master_bedroom"), ("master", "master_bedroom"),
        ("bedrooms", "bedroom"), ("bedroom", "bedroom"),
        ("living room", "living"), ("living", "living"), ("hall", "living"), ("drawing room", "living"),
        ("kitchens", "kitchen"), ("kitchen", "kitchen"),
        ("bath+wc", "bath_wc"), ("bath + wc", "bath_wc"), ("bathrooms", "bath_wc"), ("bathroom", "bath_wc"), ("baths", "bath_wc"), ("bath", "bath_wc"),
        ("toilets", "toilet"), ("toilet", "toilet"), ("wc", "toilet"),
        ("dining", "dining"),
        ("passages", "passage"), ("passage", "passage"), ("corridor", "passage"),
        ("parking", "parking"), ("car park", "parking"),
        ("pooja", "pooja"), ("puja", "pooja"),
        ("study", "study"), ("store", "store"), ("utility", "utility"), ("balcony", "balcony"),
        ("staircase", "stair"), ("stairs", "stair"), ("stair", "stair"),
    ];

    [GeneratedRegex(@"no vastu|not? (?:follow|use|want)[a-z ]*vastu|ignore vastu|without vastu|vastu (?:off|not needed|is not needed)|don't [a-z ]*vastu", RegexOptions.CultureInvariant)]
    private static partial Regex NegativeVastu();

    [GeneratedRegex(@"(\d+(?:\.\d+)?(?:\s*'\s*-?\s*\d*(?:\.\d+)?\s*""?)?(?:\s*(?:ft|feet|foot))?)\s*(?:x|by|\*)\s*(\d+(?:\.\d+)?(?:\s*'\s*-?\s*\d*(?:\.\d+)?\s*""?)?(?:\s*(?:ft|feet|foot))?)", RegexOptions.CultureInvariant)]
    private static partial Regex PairPattern();

    [GeneratedRegex(@"\d+(?:\.\d+)?(?:\s*'\s*-?\s*\d+(?:\.\d+)?\s*""?|\s*'|\s*""|\s*(?:ft|feet|foot)(?:\s*\d+(?:\.\d+)?\s*(?:in|inch|inches)?)?|\s*(?:in|inch|inches)\b)?", RegexOptions.CultureInvariant)]
    private static partial Regex LengthToken();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(?:ft|feet|foot)(?:\s*(\d+(?:\.\d+)?)\s*(?:in|inch|inches)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FeetWords();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(?:in|inch|inches)$", RegexOptions.CultureInvariant)]
    private static partial Regex InchWords();
}
