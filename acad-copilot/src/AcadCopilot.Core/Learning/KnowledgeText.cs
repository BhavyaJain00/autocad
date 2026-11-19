using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Learning;

/// <summary>Text helpers of the knowledge base: one-line cleanup, keywords, similarity, file-name slugs.</summary>
public static class KnowledgeText
{
    // Words that say nothing about the subject of a rule or lesson.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "for", "with", "by", "from", "as", "is", "are", "be",
        "it", "its", "this", "that", "these", "those", "all", "any", "always", "never", "use", "used", "using", "please",
        "should", "must", "need", "needs", "make", "made", "do", "does", "not", "no", "min", "max", "minimum", "maximum",
        "least", "most", "than", "then", "when", "if", "we", "you", "i", "my", "our", "your", "me", "them", "they",
        "into", "onto", "per", "each", "every", "also", "only", "just", "like", "so", "but", "can", "will", "would",
    };

    // A few drafting synonyms, so "bathroom" finds a rule about toilets.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["bathroom"] = "toilet", ["bath"] = "toilet", ["wc"] = "toilet", ["washroom"] = "toilet", ["restroom"] = "toilet",
        ["staircase"] = "stair", ["steps"] = "stair", ["dim"] = "dimension", ["dims"] = "dimension",
        ["dimensioning"] = "dimension", ["bed"] = "bedroom", ["hall"] = "living", ["lounge"] = "living",
        ["shutter"] = "door", ["ventilator"] = "window", ["glazing"] = "window",
        ["colour"] = "color", ["colours"] = "color", ["lettering"] = "text", ["annotation"] = "text",
        ["thk"] = "thickness", ["thick"] = "thickness",
    };

    /// <summary>Collapses whitespace (newlines included) to single spaces and trims; null becomes "".</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="max"/> characters, ending with "..." when cut.</summary>
    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        return max <= 3 ? text[..max] : text[..(max - 3)].TrimEnd() + "...";
    }

    /// <summary>
    /// Lower-case word tokens (letters and digits; "5'x7'" gives "5", "x7"), singular, synonyms mapped. Stop words are
    /// kept unless <paramref name="dropStopWords"/>.
    /// </summary>
    public static List<string> Tokens(string? text, bool dropStopWords = true)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var token = Normalize(current.ToString());
            current.Clear();
            if (token.Length == 0 || (dropStopWords && StopWords.Contains(token)))
            {
                return;
            }

            tokens.Add(token);
        }

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>Word tokens without numbers: what a rule is about ("toilet size"), not its value.</summary>
    public static HashSet<string> SubjectWords(string? text) =>
        [.. Tokens(text).Where(t => t.Any(char.IsLetter) && !t.Any(char.IsDigit) && t.Length > 1)];

    /// <summary>Jaccard similarity of two sets (0 = nothing shared, 1 = equal); 0 when both are empty.</summary>
    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 && b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>Text compared for exact duplicates: tokens (stop words kept) joined by spaces.</summary>
    public static string Fingerprint(string? text) => string.Join(' ', Tokens(text, dropStopWords: false));

    /// <summary>A safe file name: "Dog-legged stair (G+1)" gives "dog-legged-stair-g-1"; never empty, at most 60 characters.</summary>
    public static string Slug(string? name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Normalize(NormalizationForm.FormKD))
        {
            if (c < 128 && char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }

        return slug.Length == 0 ? "recipe" : slug;
    }

    /// <summary>Today's date as yyyy-MM-dd (local time).</summary>
    public static string Date(DateTimeOffset now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Normalize(string token)
    {
        if (Synonyms.TryGetValue(token, out var mapped))
        {
            return mapped;
        }

        // Crude singular: "toilets" -> "toilet", "boxes" -> "box"; keep "glass", "status", short words and numbers.
        if (token.Length > 3 && !token.Any(char.IsDigit))
        {
            if (token.EndsWith("ies", StringComparison.Ordinal))
            {
                token = token[..^3] + "y";
            }
            else if (token.EndsWith("xes", StringComparison.Ordinal) || token.EndsWith("ches", StringComparison.Ordinal) || token.EndsWith("shes", StringComparison.Ordinal))
            {
                token = token[..^2];
            }
            else if (token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal) && !token.EndsWith("is", StringComparison.Ordinal))
            {
                token = token[..^1];
            }
        }

        return Synonyms.TryGetValue(token, out var again) ? again : token;
    }
}
