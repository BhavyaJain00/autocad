using System.Globalization;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Sheets;

/// <summary>Paper standard of a sheet size.</summary>
public enum PaperStandard
{
    /// <summary>ISO 216 (A3 420 x 297 mm ...).</summary>
    Iso,

    /// <summary>ANSI architectural (ARCH B 18" x 12", ARCH C 24" x 18", ARCH D 36" x 24").</summary>
    Arch,
}

/// <summary>A landscape sheet size.</summary>
/// <param name="Size">The requested size class: A3, A2 or A1 (ARCH B, C and D are the ARCH sheets of about that size).</param>
/// <param name="Standard">ISO or ARCH.</param>
/// <param name="Name">Display name ("ISO A3 (420 x 297 mm)").</param>
/// <param name="WidthMm">Width in millimetres (the long side: landscape).</param>
/// <param name="HeightMm">Height in millimetres.</param>
/// <param name="Code">Short code used in block names ("ISO-A3", "ARCH-B").</param>
public sealed record PaperSize(string Size, PaperStandard Standard, string Name, double WidthMm, double HeightMm, string Code);

/// <summary>The sheet sizes create_sheets offers and the choice of the plotter's media name. Pure.</summary>
public static partial class PaperSizes
{
    /// <summary>The plot device used for sheets and PDFs.</summary>
    public const string PdfDevice = "DWG To PDF.pc3";

    /// <summary>The size classes the tool accepts.</summary>
    public static IReadOnlyList<string> SizeNames { get; } = ["A3", "A2", "A1"];

    /// <summary>All sizes.</summary>
    public static IReadOnlyList<PaperSize> All { get; } =
    [
        new("A3", PaperStandard.Iso, "ISO A3 (420 x 297 mm)", 420, 297, "ISO-A3"),
        new("A2", PaperStandard.Iso, "ISO A2 (594 x 420 mm)", 594, 420, "ISO-A2"),
        new("A1", PaperStandard.Iso, "ISO A1 (841 x 594 mm)", 841, 594, "ISO-A1"),
        new("A3", PaperStandard.Arch, "ARCH B (18 x 12 in)", 457.2, 304.8, "ARCH-B"),
        new("A2", PaperStandard.Arch, "ARCH C (24 x 18 in)", 609.6, 457.2, "ARCH-C"),
        new("A1", PaperStandard.Arch, "ARCH D (36 x 24 in)", 914.4, 609.6, "ARCH-D"),
    ];

    /// <summary>The size for "A3"/"A2"/"A1" (any case; ARCH B/C/D also accepted) in <paramref name="standard"/>; A3 ISO when unknown.</summary>
    public static PaperSize For(string? size, PaperStandard standard)
    {
        var key = (size ?? "A3").Trim().ToUpperInvariant().Replace("ARCH", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        key = key switch
        {
            "B" => "A3",
            "C" => "A2",
            "D" => "A1",
            _ => key,
        };
        return All.FirstOrDefault(p => p.Size == key && p.Standard == standard) ?? All[standard == PaperStandard.Arch ? 3 : 0];
    }

    /// <summary>
    /// The plotter media for <paramref name="paper"/> among the device's canonical media names (DWG To PDF.pc3 names them
    /// like "ISO_full_bleed_A3_(420.00_x_297.00_MM)" or "ARCH_full_bleed_B_(18.00_x_12.00_Inches)"): the one whose size
    /// matches within 1 mm, landscape first, then full bleed (no unprintable margin), plain, expand. Rotated = the media
    /// is portrait, so the layout must be plotted rotated 90 degrees. Null when none matches.
    /// </summary>
    public static (string Name, bool Rotated)? PickMedia(IEnumerable<string> canonicalNames, PaperSize paper)
    {
        ArgumentNullException.ThrowIfNull(canonicalNames);
        ArgumentNullException.ThrowIfNull(paper);
        var best = canonicalNames
            .Select(name => (Name: name, Size: MediaSizeMm(name)))
            .Where(m => m.Size is not null)
            .Select(m => (m.Name, Landscape: Near(m.Size!.Value.Width, paper.WidthMm) && Near(m.Size.Value.Height, paper.HeightMm),
                Portrait: Near(m.Size.Value.Width, paper.HeightMm) && Near(m.Size.Value.Height, paper.WidthMm)))
            .Where(m => m.Landscape || m.Portrait)
            .OrderBy(m => m.Landscape ? 0 : 1)
            .ThenBy(m => MediaRank(m.Name))
            .ThenBy(m => m.Name.Length)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Name is null ? null : (best.Name, !best.Landscape);
    }

    /// <summary>The size in millimetres written in a media name ("..._(420.00_x_297.00_MM)", "(18.00 x 12.00 Inches)"), or null.</summary>
    public static (double Width, double Height)? MediaSizeMm(string? mediaName)
    {
        if (string.IsNullOrEmpty(mediaName))
        {
            return null;
        }

        var match = MediaSize().Match(mediaName);
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return null;
        }

        var factor = match.Groups[3].Value.StartsWith("in", StringComparison.OrdinalIgnoreCase) ? 25.4 : 1;
        return (w * factor, h * factor);
    }

    private static int MediaRank(string name) =>
        name.Contains("full_bleed", StringComparison.OrdinalIgnoreCase) || name.Contains("full bleed", StringComparison.OrdinalIgnoreCase) ? 0
        : name.Contains("expand", StringComparison.OrdinalIgnoreCase) ? 2
        : 1;

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1.0;

    [GeneratedRegex(@"\(\s*(\d+(?:\.\d+)?)\s*[_ ]?x[_ ]?\s*(\d+(?:\.\d+)?)\s*[_ ]?\s*(MM|Inches|in)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaSize();
}
