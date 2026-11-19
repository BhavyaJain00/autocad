using System.Text;
using System.Text.RegularExpressions;

namespace AcadCopilot.Core.Learning;

/// <summary>One line of a knowledge file: a bullet ("- text (2026-11-19)") or any other line the user wrote.</summary>
/// <param name="IsBullet">True for "- " / "* " items (continuation lines are joined into them).</param>
/// <param name="Text">Bullet text without the marker and date, or the raw line.</param>
/// <param name="Date">The trailing "(yyyy-MM-dd)" of a bullet, if any.</param>
public sealed record KnowledgeLine(bool IsBullet, string Text, string? Date = null)
{
    /// <summary>The line as written to the file.</summary>
    public string Render() => IsBullet ? "- " + Text + (Date is null ? string.Empty : " (" + Date + ")") : Text;
}

/// <summary>A "## Title" section of a knowledge file (Title null = the lines before the first heading).</summary>
public sealed class KnowledgeSection(string? title)
{
    /// <summary>Heading text without "## ", or null.</summary>
    public string? Title { get; set; } = title;

    /// <summary>The section's lines in file order.</summary>
    public List<KnowledgeLine> Lines { get; } = [];

    /// <summary>The bullets.</summary>
    public IEnumerable<KnowledgeLine> Bullets => Lines.Where(l => l.IsBullet);
}

/// <summary>
/// A user-editable Markdown knowledge file (rules.md, style.md, lessons.md): a "# Title" line, intro lines, then "## "
/// sections of "- " bullets. Parsing is tolerant (anything the user writes survives a round trip as a raw line), and
/// rendering is stable.
/// </summary>
public sealed partial class KnowledgeDocument
{
    /// <summary>The "# " title (without the marker), or null.</summary>
    public string? Title { get; set; }

    /// <summary>Lines between the title and the first section or bullet (the explanation for the user).</summary>
    public List<string> Intro { get; } = [];

    /// <summary>Sections in file order; the first may be untitled (bullets before any heading).</summary>
    public List<KnowledgeSection> Sections { get; } = [];

    /// <summary>All bullets of all sections.</summary>
    public IEnumerable<(KnowledgeSection Section, KnowledgeLine Line)> AllBullets =>
        Sections.SelectMany(s => s.Bullets.Select(b => (s, b)));

    /// <summary>Parses <paramref name="markdown"/> (null or empty gives an empty document).</summary>
    public static KnowledgeDocument Parse(string? markdown)
    {
        var document = new KnowledgeDocument();
        if (string.IsNullOrEmpty(markdown))
        {
            return document;
        }

        KnowledgeSection? section = null;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (section is null && document.Title is null && document.Intro.Count == 0 && trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                document.Title = trimmed[2..].Trim();
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                section = new KnowledgeSection(trimmed[3..].Trim());
                document.Sections.Add(section);
                continue;
            }

            var isBullet = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal);
            var indented = line.Length > 0 && char.IsWhiteSpace(line[0]) && trimmed.Length > 0;
            if (!isBullet && indented && section is not null && section.Lines.Count > 0 && section.Lines[^1].IsBullet)
            {
                // Continuation of the bullet above.
                var previous = section.Lines[^1];
                section.Lines[^1] = SplitDate(previous.Text + " " + trimmed, previous.Date);
                continue;
            }

            if (section is null)
            {
                if (!isBullet)
                {
                    document.Intro.Add(line);
                    continue;
                }

                section = new KnowledgeSection(null);
                document.Sections.Add(section);
            }

            section.Lines.Add(isBullet ? SplitDate(trimmed[2..].Trim(), null) : new KnowledgeLine(false, line));
        }

        TrimBlankEnds(document.Intro);
        foreach (var s in document.Sections)
        {
            while (s.Lines.Count > 0 && !s.Lines[^1].IsBullet && string.IsNullOrWhiteSpace(s.Lines[^1].Text))
            {
                s.Lines.RemoveAt(s.Lines.Count - 1);
            }

            while (s.Lines.Count > 0 && !s.Lines[0].IsBullet && string.IsNullOrWhiteSpace(s.Lines[0].Text))
            {
                s.Lines.RemoveAt(0);
            }
        }

        return document;
    }

    /// <summary>The section titled <paramref name="title"/> (any case), or null.</summary>
    public KnowledgeSection? Find(string? title) =>
        Sections.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));

    /// <summary>The section titled <paramref name="title"/>, added at the end when missing.</summary>
    public KnowledgeSection GetOrAdd(string title)
    {
        if (Find(title) is { } found)
        {
            return found;
        }

        var section = new KnowledgeSection(title);
        Sections.Add(section);
        return section;
    }

    /// <summary>Removes empty titled sections.</summary>
    public void RemoveEmptySections() =>
        Sections.RemoveAll(s => s.Lines.All(l => !l.IsBullet && string.IsNullOrWhiteSpace(l.Text)));

    /// <summary>Renders the document as Markdown (CRLF line ends, one blank line between blocks).</summary>
    public string Render()
    {
        var builder = new StringBuilder();
        if (Title is not null)
        {
            builder.Append("# ").Append(Title).Append("\r\n\r\n");
        }

        if (Intro.Count > 0)
        {
            foreach (var line in Intro)
            {
                builder.Append(line).Append("\r\n");
            }

            builder.Append("\r\n");
        }

        foreach (var section in Sections)
        {
            if (section.Title is not null)
            {
                builder.Append("## ").Append(section.Title).Append("\r\n\r\n");
            }

            foreach (var line in section.Lines)
            {
                builder.Append(line.Render()).Append("\r\n");
            }

            if (section.Lines.Count > 0)
            {
                builder.Append("\r\n");
            }
        }

        return builder.ToString().TrimEnd() + "\r\n";
    }

    /// <summary>Splits a trailing "(yyyy-MM-dd)" off a bullet's text.</summary>
    public static KnowledgeLine SplitDate(string text, string? fallbackDate)
    {
        var match = TrailingDate().Match(text);
        return match.Success
            ? new KnowledgeLine(true, text[..match.Index].TrimEnd(), match.Groups[1].Value)
            : new KnowledgeLine(true, text.Trim(), fallbackDate);
    }

    private static void TrimBlankEnds(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }
    }

    [GeneratedRegex(@"\s*\((\d{4}-\d{2}-\d{2})\)\s*$")]
    private static partial Regex TrailingDate();
}
