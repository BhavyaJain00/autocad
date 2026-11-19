using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Presentation;

/// <summary>
/// Where export_view_png writes its pictures: Documents\AcadCopilot Exports\&lt;name&gt;_&lt;yyyyMMdd-HHmmss&gt;.png. Names are
/// cleaned to safe file-name characters, never contain a folder and never overwrite an existing file.
/// </summary>
public static class ExportFiles
{
    /// <summary>Folder name under Documents.</summary>
    public const string FolderName = "AcadCopilot Exports";

    /// <summary>Environment variable that overrides the export folder (tests).</summary>
    public const string FolderEnvironmentVariable = "ACADCOPILOT_EXPORTS";

    /// <summary>Longest base name kept.</summary>
    public const int MaxBaseNameLength = 60;

    /// <summary>The export folder: %ACADCOPILOT_EXPORTS% or Documents\AcadCopilot Exports.</summary>
    public static string Folder
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(FolderEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden;
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
            {
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            }

            return Path.Combine(documents, FolderName);
        }
    }

    /// <summary>
    /// A safe base name from the AI's <paramref name="name"/> or else the drawing name (without extension): letters,
    /// digits, space, '-', '_', '.', '(', ')' kept, everything else becomes '-', at most <see cref="MaxBaseNameLength"/>.
    /// </summary>
    public static string SafeBaseName(string? name, string? drawingName)
    {
        var source = !string.IsNullOrWhiteSpace(name)
            ? name
            : Path.GetFileNameWithoutExtension(drawingName ?? string.Empty);
        if (source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            source = source[..^4];
        }

        var builder = new StringBuilder();
        foreach (var ch in source.Trim())
        {
            var keep = char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '(' or ')' or '.';
            builder.Append(keep && ch < 0x2000 ? ch : '-');
        }

        var cleaned = builder.ToString().Trim(' ', '.', '-');
        while (cleaned.Contains("--", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        }

        if (cleaned.Length > MaxBaseNameLength)
        {
            cleaned = cleaned[..MaxBaseNameLength].TrimEnd(' ', '.', '-');
        }

        return cleaned.Length == 0 ? "drawing" : cleaned;
    }

    /// <summary>
    /// The full path of a new picture in <paramref name="folder"/>: &lt;base&gt;_&lt;yyyyMMdd-HHmmss&gt;.png, with -2, -3 ...
    /// when <paramref name="exists"/> says the name is taken.
    /// </summary>
    public static string NewPath(string folder, string baseName, DateTimeOffset now, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        exists ??= File.Exists;
        var stem = SafeBaseName(baseName, null) + "_" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".png");
        for (var n = 2; exists(path); n++)
        {
            path = Path.Combine(folder, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".png");
        }

        return path;
    }
}
