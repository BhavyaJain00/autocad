namespace AcadCopilot.Core.Learning;

/// <summary>The categories of rules.md (tool enum value -> "## " heading).</summary>
public static class RuleCategories
{
    /// <summary>The default category.</summary>
    public const string General = "general";

    /// <summary>Category keys (the remember_rule enum) and their headings, in file order.</summary>
    public static IReadOnlyList<(string Key, string Heading)> All { get; } =
    [
        (General, "General"),
        ("planning", "Planning and room sizes"),
        ("walls", "Walls"),
        ("doors_windows", "Doors and windows"),
        ("stairs", "Stairs"),
        ("text", "Text and labels"),
        ("dimensions", "Dimensions"),
        ("layers", "Layers and colours"),
        ("furniture", "Furniture and fixtures"),
        ("elevations_sections", "Elevations and sections"),
        ("site", "Site plan"),
        ("workflow", "How to work with me"),
    ];

    /// <summary>The enum values for the tool schema.</summary>
    public static IReadOnlyList<string> Keys { get; } = [.. All.Select(c => c.Key)];

    /// <summary>The heading of <paramref name="key"/>; unknown or empty keys give "General".</summary>
    public static string Heading(string? key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        foreach (var (k, heading) in All)
        {
            if (string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase) || string.Equals(heading, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return heading;
            }
        }

        return "General";
    }
}
