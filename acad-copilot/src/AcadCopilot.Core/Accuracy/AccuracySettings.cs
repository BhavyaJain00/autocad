using System.Text.Json;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Recovery;

namespace AcadCopilot.Core.Accuracy;

/// <summary>
/// Settings of the accuracy tools, %LOCALAPPDATA%\AcadCopilot\accuracy.json: the user's own template drawing
/// (DWT/DWG) that apply_drawing_template uses instead of the built-in one.
/// </summary>
public sealed class AccuracySettings
{
    /// <summary>Creates the settings over <paramref name="file"/>.</summary>
    public AccuracySettings(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        File = file;
    }

    /// <summary>The settings file.</summary>
    public string File { get; }

    /// <summary>The default settings file.</summary>
    public static AccuracySettings CreateDefault() => new(Path.Combine(AppPaths.Root, "accuracy.json"));

    /// <summary>The user's template path, or null for the built-in template.</summary>
    public string? TemplatePath
    {
        get
        {
            try
            {
                return System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File)) is JsonObject json &&
                    json["templatePath"] is JsonValue v && v.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path)
                    ? path
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Saves (or clears with null) the user's template path.</summary>
    public void SetTemplatePath(string? path)
    {
        var folder = Path.GetDirectoryName(File);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = new JsonObject { ["templatePath"] = string.IsNullOrWhiteSpace(path) ? null : path };
        AtomicFile.WriteAllText(File, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Checks a template path: a full path of an existing .dwt or .dwg. Returns the problem, or null when it is fine.
    /// </summary>
    public static string? CheckTemplatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "The template path is empty.";
        }

        var extension = Path.GetExtension(path);
        if (!Path.IsPathFullyQualified(path) || !(extension.Equals(".dwt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase)))
        {
            return "The template must be the full path of a .dwt or .dwg file, for example C:\\Templates\\MyOffice.dwt.";
        }

        return System.IO.File.Exists(path) ? null : $"{path} does not exist.";
    }
}
