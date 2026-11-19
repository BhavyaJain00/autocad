using System.Text;

namespace AcadCopilot.Core.Tools;

/// <summary>
/// Small AutoCAD-free helpers shared by tool bodies: keyword normalisation for names the LLM passes
/// ("Wall Styles" -&gt; "wall_styles"), singular/plural keyword matching, paging of list results and
/// case-insensitive text filters.
/// </summary>
public static class ToolKeywords
{
    /// <summary>Default page size of list tools.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Normalises a user/LLM supplied name for keyword matching: trimmed, lower case, every character that is not a
    /// letter or digit replaced by an underscore, repeated underscores collapsed ("Door Styles" -&gt; "door_styles",
    /// "3D" -&gt; "3d").
    /// </summary>
    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            var mapped = char.IsLetterOrDigit(c) ? c : '_';
            if (mapped == '_' && (builder.Length == 0 || builder[^1] == '_'))
            {
                continue;
            }

            builder.Append(mapped);
        }

        return builder.ToString().TrimEnd('_');
    }

    /// <summary>
    /// Finds the keyword in <paramref name="keywords"/> matching <paramref name="value"/> after normalisation, also
    /// accepting the singular or plural form ("door" -&gt; "doors", "walls" -&gt; "wall"). Null when nothing matches.
    /// </summary>
    public static string? MatchKeyword(string? value, IReadOnlyList<string> keywords)
    {
        var key = NormalizeKey(value);
        if (key.Length == 0)
        {
            return null;
        }

        foreach (var candidate in new[] { key, key + "s", key.EndsWith('s') ? key[..^1] : key })
        {
            if (keywords.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Clamps paging arguments: offset &gt;= 0, 1 &lt;= limit &lt;= <paramref name="maxLimit"/>
    /// (<paramref name="defaultLimit"/> when absent).
    /// </summary>
    public static (int Offset, int Limit) Page(long? offset, long? limit, int defaultLimit, int maxLimit)
    {
        var o = (int)Math.Clamp(offset ?? 0, 0, int.MaxValue);
        var l = (int)Math.Clamp(limit ?? defaultLimit, 1, maxLimit);
        return (o, l);
    }

    /// <summary>
    /// Offset of the next page, or null when <paramref name="offset"/> + <paramref name="returned"/> covers
    /// <paramref name="total"/>.
    /// </summary>
    public static int? NextOffset(int total, int offset, int returned) =>
        offset + returned < total ? offset + returned : null;

    /// <summary>True when <paramref name="text"/> contains <paramref name="filter"/> (case-insensitive); an empty filter matches all.</summary>
    public static bool ContainsText(string? text, string? filter) =>
        string.IsNullOrEmpty(filter) || (text is not null && text.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
