using System.Globalization;
using System.Text;
using AcadCopilot.Core.Presentation;

namespace AcadCopilot.Core.Sheets;

/// <summary>Sheet numbers, layout tab names and the PDF file name. Pure.</summary>
public static class SheetNames
{
    /// <summary>Longest layout name AutoCAD accepts.</summary>
    public const int MaxLayoutNameLength = 255;

    /// <summary>Characters AutoCAD does not allow in layout names (plus quotes, kept out for safety).</summary>
    private const string Invalid = "<>/\\\":;?*|,=`'";

    /// <summary>
    /// The sheet series of a drawing kind: site plan 0 (A-001), floor plans 100 (A-101 ...), elevations 200,
    /// sections 300, layouts (furniture, electrical, plumbing) 500, schedules 600, others 900.
    /// </summary>
    public static int Series(DrawingKind kind) => kind switch
    {
        DrawingKind.SitePlan => 0,
        DrawingKind.FloorPlan => 100,
        DrawingKind.Elevation => 200,
        DrawingKind.Section => 300,
        DrawingKind.Layout => 500,
        DrawingKind.Schedule => 600,
        _ => 900,
    };

    /// <summary>The sheet number: "A-101" for the first sheet (index 0) of series 100 with prefix "A".</summary>
    public static string Number(string? prefix, int series, int index)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? "A" : prefix.Trim().TrimEnd('-');
        return string.Create(CultureInfo.InvariantCulture, $"{p}-{series + index + 1:000}");
    }

    /// <summary>The layout tab name: "A-101 Ground Floor Plan" (characters AutoCAD refuses removed).</summary>
    public static string LayoutName(string number, string title)
    {
        var raw = (number + " " + DrawingTitles.TitleCase(title)).Trim();
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (!Invalid.Contains(ch, StringComparison.Ordinal) && !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length > MaxLayoutNameLength ? name[..MaxLayoutNameLength].TrimEnd() : name;
    }

    /// <summary>
    /// <paramref name="name"/>, or "name (2)", "name (3)" ... when <paramref name="taken"/> already holds it (any case).
    /// </summary>
    public static string Unique(string name, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            if (!set.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>True when a layout name looks like one of create_sheets' sheets ("A-101 ...").</summary>
    public static bool IsSheetName(string? name) =>
        name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z]{1,3}-\d{3}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The PDF path in <paramref name="folder"/>: &lt;drawing or name&gt;_&lt;yyyy-MM-dd&gt;.pdf, with -2, -3 ... when
    /// <paramref name="exists"/> says the name is taken (never overwrites).
    /// </summary>
    public static string PdfPath(string folder, string? name, string? drawingName, DateTimeOffset now, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        exists ??= File.Exists;
        var baseName = name;
        if (baseName is not null && baseName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        var stem = ExportFiles.SafeBaseName(baseName, drawingName) + "_" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".pdf");
        for (var n = 2; exists(path); n++)
        {
            path = Path.Combine(folder, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".pdf");
        }

        return path;
    }
}
