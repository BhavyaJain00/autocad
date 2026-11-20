using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Sheets;

/// <summary>What a drawing in model space is; decides its row in arrange_drawings and its sheet series.</summary>
public enum DrawingKind
{
    /// <summary>A floor plan (ground, first, terrace ...): top row, one sheet each (A-101 ...).</summary>
    FloorPlan,

    /// <summary>An elevation (front, rear, side): second row, A-201 ...</summary>
    Elevation,

    /// <summary>A section: after the elevations, A-301 ...</summary>
    Section,

    /// <summary>The site plan: after the sections, sheet A-100.</summary>
    SitePlan,

    /// <summary>A plan-based layout (furniture, electrical, plumbing): third row, A-501 ...</summary>
    Layout,

    /// <summary>A table (door/window schedule, area statement): third row, A-601 ...</summary>
    Schedule,

    /// <summary>Anything else (untitled drawings): third row, A-901 ...</summary>
    Other,
}

/// <summary>A recognised drawing title.</summary>
/// <param name="Kind">The drawing kind.</param>
/// <param name="Order">Sort order within the kind: the floor level for plans (-1 basement, 0 ground, 1 first ... 100 terrace), front 0 / rear 1 / left 2 / right 3 for elevations, A=0 / B=1 for sections.</param>
/// <param name="Title">The cleaned title in capitals ("GROUND FLOOR PLAN").</param>
public sealed record DrawingTitleInfo(DrawingKind Kind, double Order, string Title);

/// <summary>
/// Recognises drawing titles ("%%uGROUND FLOOR PLAN", "FRONT ELEVATION", "SECTION A-A", "SITE PLAN") and scale lines
/// ("SCALE 1/4\" = 1'-0\"") in TEXT / MTEXT contents, and makes the standard titles. Pure.
/// </summary>
public static partial class DrawingTitles
{
    /// <summary>Longest text that can be a title.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>Level of a terrace / roof plan.</summary>
    public const double TerraceLevel = 100;

    /// <summary>
    /// The text without AutoCAD control codes (%%u, %%o, %%c ...) and MTEXT formatting (\P, \fArial|b0;, \H2x;, \L, braces),
    /// with runs of white space collapsed, in capitals.
    /// </summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Replace("\\P", " ", StringComparison.Ordinal).Replace("\\~", " ", StringComparison.Ordinal);
        text = MtextCode().Replace(text, string.Empty);
        text = MtextToggle().Replace(text, string.Empty);
        text = text.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        text = PercentCode().Replace(text, m => m.Groups[1].Value.ToUpperInvariant() switch
        {
            "C" => "Ø",
            "D" => "°",
            "P" => "±",
            _ => string.Empty,
        });
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    /// <summary>The title information of <paramref name="raw"/>, or null when the text is not a drawing title.</summary>
    public static DrawingTitleInfo? Classify(string? raw)
    {
        var text = Clean(raw);
        if (text.Length < 4 || text.Length > MaxTitleLength || NotATitle().IsMatch(text))
        {
            return null;
        }

        if (text.Contains("SECTIONAL", StringComparison.Ordinal) || SectionWord().IsMatch(text))
        {
            return new DrawingTitleInfo(DrawingKind.Section, SectionOrder(text), text);
        }

        if (text.Contains("ELEVATION", StringComparison.Ordinal))
        {
            return new DrawingTitleInfo(DrawingKind.Elevation, ElevationOrder(text), text);
        }

        if (SitePlan().IsMatch(text))
        {
            return new DrawingTitleInfo(DrawingKind.SitePlan, 0, text);
        }

        if (text.Contains("SCHEDULE", StringComparison.Ordinal) || text.Contains("AREA STATEMENT", StringComparison.Ordinal) ||
            text.Contains("AREA CALCULATION", StringComparison.Ordinal))
        {
            return new DrawingTitleInfo(DrawingKind.Schedule, text.Contains("AREA", StringComparison.Ordinal) ? 1 : 0, text);
        }

        var level = Level(text);
        if (ServicesWord().IsMatch(text) && (text.Contains("LAYOUT", StringComparison.Ordinal) || PlanWord().IsMatch(text)))
        {
            return new DrawingTitleInfo(DrawingKind.Layout, ((level ?? 0) * 10) + LayoutType(text), text);
        }

        if (PlanWord().IsMatch(text) || (level is not null && text.Contains("FLOOR", StringComparison.Ordinal)))
        {
            return new DrawingTitleInfo(DrawingKind.FloorPlan, level ?? 0, text);
        }

        return null;
    }

    /// <summary>True when the text is a scale line ("SCALE 1/4\" = 1'-0\"", "SCALE: 1:100", "1/8\"=1'-0\"").</summary>
    public static bool IsScaleText(string? raw)
    {
        var text = Clean(raw);
        return text.Length is > 0 and <= MaxTitleLength && (text.StartsWith("SCALE", StringComparison.Ordinal) || BareScale().IsMatch(text));
    }

    /// <summary>The floor level named in a title (-1 basement, -0.5 stilt, 0 ground, 1 first ... 100 terrace), or null.</summary>
    public static double? Level(string? raw)
    {
        var text = Clean(raw);
        if (Regex.IsMatch(text, @"\b(TERRACE|ROOF)\b", RegexOptions.CultureInvariant))
        {
            return TerraceLevel;
        }

        if (Regex.IsMatch(text, @"\bBASEMENT\b", RegexOptions.CultureInvariant))
        {
            return -1;
        }

        if (Regex.IsMatch(text, @"\b(STILT|PARKING FLOOR)\b", RegexOptions.CultureInvariant))
        {
            return -0.5;
        }

        if (Regex.IsMatch(text, @"\b(GROUND|G\.?F\.?)\b", RegexOptions.CultureInvariant))
        {
            return 0;
        }

        if (Regex.IsMatch(text, @"\bMEZZANINE\b", RegexOptions.CultureInvariant))
        {
            return 0.5;
        }

        string[][] numbered =
        [
            ["FIRST", "1ST"], ["SECOND", "2ND"], ["THIRD", "3RD"], ["FOURTH", "4TH"], ["FIFTH", "5TH"], ["SIXTH", "6TH"],
        ];
        for (var i = 0; i < numbered.Length; i++)
        {
            if (numbered[i].Any(w => Regex.IsMatch(text, $@"\b{w}\b", RegexOptions.CultureInvariant)))
            {
                return i + 1;
            }
        }

        if (Regex.IsMatch(text, @"\bTYPICAL\b", RegexOptions.CultureInvariant))
        {
            return 1.5;
        }

        return null;
    }

    /// <summary>The standard title of a floor plan at <paramref name="level"/> ("GROUND FLOOR PLAN", "TERRACE PLAN").</summary>
    public static string FloorPlanTitle(double level) => level switch
    {
        >= TerraceLevel => "TERRACE PLAN",
        -1 => "BASEMENT PLAN",
        -0.5 => "STILT FLOOR PLAN",
        0 => "GROUND FLOOR PLAN",
        1 => "FIRST FLOOR PLAN",
        2 => "SECOND FLOOR PLAN",
        3 => "THIRD FLOOR PLAN",
        4 => "FOURTH FLOOR PLAN",
        5 => "FIFTH FLOOR PLAN",
        _ => string.Create(CultureInfo.InvariantCulture, $"FLOOR {level} PLAN"),
    };

    /// <summary>Title case for layout tab names: "GROUND FLOOR PLAN" -> "Ground Floor Plan", "SECTION A-A" stays "Section A-A".</summary>
    public static string TitleCase(string? text)
    {
        var words = Clean(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length <= 1 || word.Any(char.IsDigit) || (word.Contains('-', StringComparison.Ordinal) && word.Split('-').All(p => p.Length <= 1)))
            {
                continue;
            }

            words[i] = word[0] + word[1..].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    private static double SectionOrder(string text)
    {
        var match = Regex.Match(text, @"\b([A-Z0-9])\s*-\s*\1\b", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(text, @"\bSECTION\s+(?:ON\s+)?([A-Z0-9])\b", RegexOptions.CultureInvariant);
        }

        if (!match.Success)
        {
            return 0;
        }

        var ch = match.Groups[1].Value[0];
        return char.IsDigit(ch) ? ch - '1' : ch - 'A';
    }

    private static double ElevationOrder(string text)
    {
        (string Word, double Order)[] order =
        [
            ("FRONT", 0), ("REAR", 1), ("BACK", 1), ("LEFT", 2), ("RIGHT", 3), ("SIDE", 4), ("NORTH", 5), ("EAST", 6), ("SOUTH", 7), ("WEST", 8),
        ];
        foreach (var (word, value) in order)
        {
            if (Regex.IsMatch(text, $@"\b{word}\b", RegexOptions.CultureInvariant))
            {
                return value;
            }
        }

        return 9;
    }

    private static double LayoutType(string text) =>
        text.Contains("FURNITURE", StringComparison.Ordinal) ? 0
        : text.Contains("ELECTRICAL", StringComparison.Ordinal) ? 1
        : text.Contains("PLUMBING", StringComparison.Ordinal) || text.Contains("DRAINAGE", StringComparison.Ordinal) || text.Contains("WATER", StringComparison.Ordinal) ? 2
        : 3;

    [GeneratedRegex(@"\\[ACFHQTWfhcqtwap][^;\\{}]*;", RegexOptions.CultureInvariant)]
    private static partial Regex MtextCode();

    [GeneratedRegex(@"\\[LlOoKkSN]", RegexOptions.CultureInvariant)]
    private static partial Regex MtextToggle();

    [GeneratedRegex("%%([A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex PercentCode();

    [GeneratedRegex(@"^(SEE|REFER|REF\.?|NOTE|NOTES|AS PER)\b|[=]", RegexOptions.CultureInvariant)]
    private static partial Regex NotATitle();

    [GeneratedRegex(@"\bSECTION\b", RegexOptions.CultureInvariant)]
    private static partial Regex SectionWord();

    [GeneratedRegex(@"\b(SITE|LOCATION|KEY)\s+(PLAN|LAYOUT)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SitePlan();

    [GeneratedRegex(@"\bPLANS?\b", RegexOptions.CultureInvariant)]
    private static partial Regex PlanWord();

    [GeneratedRegex(@"\b(FURNITURE|ELECTRICAL|PLUMBING|DRAINAGE|WATER SUPPLY|LIGHTING)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ServicesWord();

    [GeneratedRegex(@"^(\d+\s*/\s*\d+|\d+)\s*""\s*=\s*1\s*'(\s*-?\s*0\s*""?)?$|^1\s*:\s*\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex BareScale();
}
