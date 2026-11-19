using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AcadCopilot.Core.Learning;

/// <summary>A built-in recipe shipped inside AcadCopilot.Core (Learning\BuiltIn\&lt;slug&gt;.md).</summary>
/// <param name="Slug">File name without .md.</param>
/// <param name="Markdown">The recipe file's text.</param>
public sealed record BuiltInRecipe(string Slug, string Markdown);

/// <summary>What <see cref="BuiltInKnowledge.Seed"/> did.</summary>
/// <param name="Written">Recipes written (new, or an unedited older built-in version updated).</param>
/// <param name="Kept">Recipes left alone (the user edited or deleted them, or they are current).</param>
public sealed record BuiltInSeedResult(IReadOnlyList<string> Written, IReadOnlyList<string> Kept);

/// <summary>
/// Built-in recipes (electrical and plumbing first-draft layouts, the colour presentation elevation) copied into the
/// knowledge folder's recipes\ on first start, so recall_knowledge finds them like the user's own recipes. They are
/// written once: a recipe the user edited or deleted is never written again; an unedited one is updated when a newer
/// version ships. What was seeded is remembered in recipes\.builtin.json (slug -> SHA-256 of the text written).
/// </summary>
public static class BuiltInKnowledge
{
    /// <summary>Marker file in recipes\ remembering what was seeded.</summary>
    public const string MarkerFileName = ".builtin.json";

    private const string ResourcePrefix = "AcadCopilot.Core.Learning.BuiltIn.";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Lazy<IReadOnlyList<BuiltInRecipe>> Loaded = new(Load);

    /// <summary>The built-in recipes (embedded resources), by slug.</summary>
    public static IReadOnlyList<BuiltInRecipe> Recipes => Loaded.Value;

    /// <summary>
    /// Copies the built-in recipes into <paramref name="knowledgeDirectory"/>\recipes\ (see the class remarks). Never
    /// throws for one unwritable file: it is reported as kept. Safe to call at every start.
    /// </summary>
    public static BuiltInSeedResult Seed(string knowledgeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeDirectory);
        var recipes = Path.Combine(knowledgeDirectory, "recipes");
        Directory.CreateDirectory(recipes);
        var markerPath = Path.Combine(recipes, MarkerFileName);
        var marker = ReadMarker(markerPath);
        var written = new List<string>();
        var kept = new List<string>();
        var changed = false;
        foreach (var recipe in Recipes)
        {
            var path = Path.Combine(recipes, recipe.Slug + ".md");
            var text = Normalize(recipe.Markdown);
            var hash = Hash(text);
            try
            {
                var exists = File.Exists(path);
                bool write;
                if (!marker.TryGetValue(recipe.Slug, out var seededHash))
                {
                    // Never seeded: write it unless a recipe of the same name already exists (the user's own).
                    write = !exists;
                }
                else if (!exists || seededHash == hash)
                {
                    // Deleted by the user (respect that), or already current.
                    write = false;
                }
                else
                {
                    // Seeded before: update only an unedited copy.
                    write = Hash(Normalize(File.ReadAllText(path, Encoding.UTF8))) == seededHash;
                }

                if (write)
                {
                    WriteAtomically(path, text);
                    written.Add(recipe.Slug);
                }
                else
                {
                    kept.Add(recipe.Slug);
                }

                if (write || !marker.ContainsKey(recipe.Slug))
                {
                    marker[recipe.Slug] = hash;
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept.Add(recipe.Slug);
            }
        }

        if (changed)
        {
            try
            {
                WriteAtomically(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next start tries again; nothing user-visible depends on the marker.
            }
        }

        return new BuiltInSeedResult(written, kept);
    }

    private static List<BuiltInRecipe> Load()
    {
        var assembly = typeof(BuiltInKnowledge).Assembly;
        var recipes = new List<BuiltInRecipe>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var slug = name[ResourcePrefix.Length..^3];
            recipes.Add(new BuiltInRecipe(slug, reader.ReadToEnd()));
        }

        return recipes;
    }

    private static Dictionary<string, string> ReadMarker(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8)) is { } read)
            {
                return new Dictionary<string, string>(read, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken marker: treat everything as never seeded (existing files are still never overwritten).
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text, Utf8NoBom);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
