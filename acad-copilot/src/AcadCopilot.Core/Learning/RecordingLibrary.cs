using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Learning;

/// <summary>One saved recording.</summary>
/// <param name="Name">File name ("2026-11-19_1412_House.md").</param>
/// <param name="Path">Full path.</param>
/// <param name="Modified">Last write time (local).</param>
/// <param name="Bytes">File size.</param>
/// <param name="Title">The first line without "# " ("Recording 2026-11-19 14:12 - House.dwg").</param>
/// <param name="Summary">The actions line of the summary, when found.</param>
public sealed record RecordingInfo(string Name, string Path, DateTime Modified, long Bytes, string Title, string? Summary);

/// <summary>A part of a recording file.</summary>
/// <param name="Name">File name.</param>
/// <param name="Text">The characters read.</param>
/// <param name="Offset">Where they start.</param>
/// <param name="TotalCharacters">Length of the whole file.</param>
public sealed record RecordingPart(string Name, string Text, int Offset, int TotalCharacters)
{
    /// <summary>True when more follows (read again with Offset + Text.Length).</summary>
    public bool HasMore => Offset + Text.Length < TotalCharacters;
}

/// <summary>
/// The "Learn from me" recordings in knowledge\recordings (plain Markdown, user-editable, never sent anywhere by
/// themselves): saving with a dated file name, listing newest first, reading in capped parts, deleting. Names given by the
/// AI are resolved only inside the folder (no paths, no "..").
/// </summary>
public sealed class RecordingLibrary
{
    /// <summary>Folder name under the knowledge folder.</summary>
    public const string FolderName = "recordings";

    /// <summary>Characters returned by one read when the caller does not say.</summary>
    public const int DefaultReadCharacters = 30_000;

    /// <summary>Most characters returned by one read.</summary>
    public const int MaxReadCharacters = 60_000;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Creates the library of the knowledge folder <paramref name="knowledgeDirectory"/>.</summary>
    public RecordingLibrary(string knowledgeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeDirectory);
        Directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(knowledgeDirectory), FolderName);
    }

    /// <summary>knowledge\recordings.</summary>
    public string Directory { get; }

    /// <summary>"2026-11-19_1412_House-GF.md" for a recording started at <paramref name="started"/> in <paramref name="drawing"/>.</summary>
    public static string FileNameFor(DateTimeOffset started, string? drawing)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(drawing ?? string.Empty);
        var safe = new StringBuilder();
        foreach (var c in stem)
        {
            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        var name = safe.ToString().Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        if (name.Length == 0)
        {
            name = "drawing";
        }

        if (name.Length > 60)
        {
            name = name[..60];
        }

        return started.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + "_" + name + ".md";
    }

    /// <summary>Saves <paramref name="markdown"/> under a new file name (never overwrites) and returns its full path.</summary>
    public string Save(DateTimeOffset started, string? drawing, string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        System.IO.Directory.CreateDirectory(Directory);
        var name = FileNameFor(started, drawing);
        var path = System.IO.Path.Combine(Directory, name);
        for (var i = 2; File.Exists(path); i++)
        {
            path = System.IO.Path.Combine(Directory, System.IO.Path.GetFileNameWithoutExtension(name) + "_" + i.ToString(CultureInfo.InvariantCulture) + ".md");
        }

        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, markdown, Utf8NoBom);
        File.Move(temp, path);
        return path;
    }

    /// <summary>The recordings, newest first.</summary>
    public IReadOnlyList<RecordingInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var list = new List<RecordingInfo>();
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*.md"))
        {
            string title = file.Name;
            string? summary = null;
            try
            {
                foreach (var line in File.ReadLines(file.FullName).Take(20))
                {
                    if (line.StartsWith("# ", StringComparison.Ordinal) && title == file.Name)
                    {
                        title = line[2..].Trim();
                    }
                    else if (summary is null && line.StartsWith("- ", StringComparison.Ordinal) && line.Contains(" actions (", StringComparison.Ordinal))
                    {
                        summary = line[2..].Trim();
                    }
                }
            }
            catch (IOException)
            {
                // Listed with its name only.
            }

            list.Add(new RecordingInfo(file.Name, file.FullName, file.LastWriteTime, file.Length, title, summary));
        }

        return [.. list.OrderByDescending(r => r.Name, StringComparer.Ordinal).ThenByDescending(r => r.Modified)];
    }

    /// <summary>
    /// The full path of the recording named <paramref name="name"/>: its file name (with or without .md), a unique part of
    /// it, or "latest" / empty for the newest. Null when none or several match. Never leaves the recordings folder.
    /// </summary>
    public string? Resolve(string? name)
    {
        var all = List();
        if (all.Count == 0)
        {
            return null;
        }

        var wanted = (name ?? string.Empty).Trim();
        if (wanted.Length == 0 || string.Equals(wanted, "latest", StringComparison.OrdinalIgnoreCase) || string.Equals(wanted, "newest", StringComparison.OrdinalIgnoreCase))
        {
            return all[0].Path;
        }

        // Only a file name counts: a path given by the AI is cut to its last part.
        wanted = System.IO.Path.GetFileName(wanted.Replace('/', '\\'));
        var exact = all.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileNameWithoutExtension(r.Name), wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Path;
        }

        var partial = all.Where(r => r.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0].Path : null;
    }

    /// <summary>Reads at most <paramref name="maxCharacters"/> characters of a recording from <paramref name="offset"/>.</summary>
    /// <exception cref="FileNotFoundException">No recording matches <paramref name="name"/>.</exception>
    public RecordingPart Read(string? name, int offset = 0, int maxCharacters = DefaultReadCharacters)
    {
        var path = Resolve(name) ?? throw new FileNotFoundException($"No recording matches \"{name}\".");
        var text = File.ReadAllText(path, Encoding.UTF8);
        var start = Math.Clamp(offset, 0, text.Length);
        var count = Math.Min(Math.Clamp(maxCharacters, 1, MaxReadCharacters), text.Length - start);
        if (start + count < text.Length)
        {
            // End at a line break so a command is not cut in half.
            var lineEnd = text.LastIndexOf('\n', start + count - 1, Math.Min(count, 4_000));
            if (lineEnd > start)
            {
                count = lineEnd + 1 - start;
            }
        }

        return new RecordingPart(System.IO.Path.GetFileName(path), text.Substring(start, count), start, text.Length);
    }

    /// <summary>Deletes the recording file <paramref name="path"/> (only inside the recordings folder). True when it was deleted.</summary>
    public bool Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), Directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return false;
        }

        File.Delete(full);
        return true;
    }
}
