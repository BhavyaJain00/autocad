using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using AcadCopilot.Core.Diagnostics;

namespace AcadCopilot.Plugin.Views;

/// <summary>
/// Read-only, selectable rich text showing basic Markdown (headings, paragraphs, bold/italic/strike, inline code,
/// fenced code, bullet/numbered lists, block quotes, tables, links, rules). Re-renders are coalesced so streamed
/// text stays cheap.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    /// <summary>The Markdown text.</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).ScheduleRender()));

    private bool _renderPending;

    /// <summary>Creates the view.</summary>
    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FocusVisualStyle = null;
        IsTabStop = false;
        var document = CreateDocument();
        Document = document;
        document.PagePadding = new Thickness(0);
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));
    }

    /// <summary>The Markdown text.</summary>
    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Renders immediately (tests and first display).</summary>
    public void RenderNow()
    {
        _renderPending = false;
        var document = CreateDocument();
        try
        {
            MarkdownRenderer.Render(Markdown ?? string.Empty, document);
        }
        catch (Exception ex)
        {
            CopilotLog.Warn("Markdown rendering failed; showing plain text", ex);
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run(Markdown ?? string.Empty)));
        }

        Document = document;

        // RichTextBox gives an assigned document its own page padding; text must line up with the rest of the panel.
        document.PagePadding = new Thickness(0);
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        WheelForwarding.Forward(this, e);
        base.OnPreviewMouseWheel(e);
    }

    private FlowDocument CreateDocument()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        document.SetBinding(FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        document.SetBinding(FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        document.SetBinding(FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        return document;
    }

    private void ScheduleRender()
    {
        if (_renderPending)
        {
            return;
        }

        _renderPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_renderPending)
            {
                RenderNow();
            }
        }));
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri is { IsAbsoluteUri: true } uri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                CopilotLog.Warn("Could not open link " + uri, ex);
            }
        }
    }
}

/// <summary>Converts Markdown into <see cref="FlowDocument"/> blocks styled with the panel theme resources.</summary>
public static partial class MarkdownRenderer
{
    private const string MonoFont = "Cascadia Mono, Consolas, Courier New";

    /// <summary>Parses <paramref name="markdown"/> and appends blocks to <paramref name="document"/>.</summary>
    public static void Render(string markdown, FlowDocument document)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        ParseBlocks(lines, 0, lines.Length, blocks, document.FontSize);
        if (blocks.Count > 0)
        {
            var last = blocks[^1];
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        document.Blocks.AddRange(blocks);
    }

    private static void ParseBlocks(string[] lines, int start, int end, List<Block> blocks, double baseSize)
    {
        var i = start;
        while (i < end)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var fence = trimmed[..3];
                var code = new StringBuilder();
                i++;
                while (i < end && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal))
                {
                    if (code.Length > 0)
                    {
                        code.Append('\n');
                    }

                    code.Append(lines[i]);
                    i++;
                }

                i++;
                blocks.Add(CodeBlock(code.ToString(), baseSize));
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                blocks.Add(Heading(heading.Groups[2].Value.Trim().TrimEnd('#').TrimEnd(), heading.Groups[1].Value.Length, baseSize));
                i++;
                continue;
            }

            if (RuleRegex().IsMatch(trimmed))
            {
                blocks.Add(Rule());
                i++;
                continue;
            }

            if (trimmed.Contains('|', StringComparison.Ordinal) && i + 1 < end && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
            {
                var rows = new List<string> { line };
                i += 2;
                while (i < end && lines[i].Trim().Contains('|', StringComparison.Ordinal) && lines[i].Trim().Length > 0)
                {
                    rows.Add(lines[i]);
                    i++;
                }

                blocks.Add(Table(rows));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quoted = new List<string>();
                while (i < end && lines[i].TrimStart().StartsWith('>'))
                {
                    var content = lines[i].TrimStart()[1..];
                    quoted.Add(content.StartsWith(' ') ? content[1..] : content);
                    i++;
                }

                var inner = new List<Block>();
                ParseBlocks([.. quoted], 0, quoted.Count, inner, baseSize);
                blocks.Add(Quote(inner));
                continue;
            }

            if (ListItemRegex().IsMatch(line))
            {
                var listEnd = i;
                while (listEnd < end)
                {
                    var current = lines[listEnd];
                    if (current.Trim().Length == 0)
                    {
                        // A blank line continues the list only when another item follows.
                        var next = listEnd + 1;
                        if (next < end && ListItemRegex().IsMatch(lines[next]))
                        {
                            listEnd = next;
                            continue;
                        }

                        break;
                    }

                    if (!ListItemRegex().IsMatch(current) && !current.StartsWith("  ", StringComparison.Ordinal) && !current.StartsWith('\t'))
                    {
                        break;
                    }

                    listEnd++;
                }

                blocks.AddRange(BuildLists(lines, i, listEnd, baseSize));
                i = listEnd;
                continue;
            }

            var paragraph = new List<string>();
            while (i < end)
            {
                var current = lines[i];
                var currentTrimmed = current.Trim();
                if (currentTrimmed.Length == 0 || currentTrimmed.StartsWith("```", StringComparison.Ordinal) ||
                    HeadingRegex().IsMatch(current) || ListItemRegex().IsMatch(current) || currentTrimmed.StartsWith('>') ||
                    (paragraph.Count > 0 && RuleRegex().IsMatch(currentTrimmed)) ||
                    (currentTrimmed.Contains('|', StringComparison.Ordinal) && i + 1 < end && TableSeparatorRegex().IsMatch(lines[i + 1].Trim())))
                {
                    break;
                }

                paragraph.Add(currentTrimmed);
                i++;
            }

            if (paragraph.Count == 0)
            {
                paragraph.Add(trimmed);
                i++;
            }

            blocks.Add(Paragraph(paragraph));
        }
    }

    private static Paragraph Paragraph(List<string> lines)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        for (var k = 0; k < lines.Count; k++)
        {
            if (k > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            AddInlines(paragraph.Inlines, lines[k]);
        }

        return paragraph;
    }

    private static Paragraph Heading(string text, int level, double baseSize)
    {
        var paragraph = new Paragraph
        {
            FontSize = baseSize * level switch { 1 => 1.32, 2 => 1.18, 3 => 1.07, _ => 1.0 },
            FontWeight = level == 1 ? FontWeights.Bold : FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 6 : 4, 0, 6),
        };
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static BlockUIContainer Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return new BlockUIContainer(line) { Margin = new Thickness(0, 2, 0, 8) };
    }

    private static Paragraph CodeBlock(string code, double baseSize)
    {
        var paragraph = new Paragraph(new Run(code))
        {
            FontFamily = new FontFamily(MonoFont),
            FontSize = Math.Max(9, baseSize - 0.5),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
        };
        paragraph.SetResourceReference(Block.BackgroundProperty, "CodeBackground");
        paragraph.SetResourceReference(Block.BorderBrushProperty, "LineBrush");
        return paragraph;
    }

    private static Section Quote(List<Block> inner)
    {
        var section = new Section { Padding = new Thickness(10, 2, 0, 2), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(3, 0, 0, 0) };
        section.SetResourceReference(Block.BorderBrushProperty, "StrongLineBrush");
        section.SetResourceReference(TextElement.ForegroundProperty, "TextSecondary");
        if (inner.Count > 0)
        {
            var last = inner[^1];
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        section.Blocks.AddRange(inner);
        return section;
    }

    private static List<Block> BuildLists(string[] lines, int start, int end, double baseSize)
    {
        // Items: (indent, ordered, number, text); continuation lines are appended to the previous item.
        var items = new List<(int Indent, bool Ordered, int Number, string Text)>();
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var match = ListItemRegex().Match(line);
            if (match.Success)
            {
                var indent = match.Groups[1].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
                var marker = match.Groups[2].Value;
                var ordered = char.IsDigit(marker[0]);
                var number = ordered && int.TryParse(marker.TrimEnd('.', ')'), out var n) ? n : 1;
                items.Add((indent, ordered, number, match.Groups[3].Value));
            }
            else if (items.Count > 0)
            {
                var previous = items[^1];
                items[^1] = previous with { Text = previous.Text + "\n" + line.Trim() };
            }
        }

        // Consecutive runs of bullet and numbered items become separate lists.
        var lists = new List<Block>();
        var index = 0;
        while (index < items.Count)
        {
            lists.Add(BuildListLevel(items, ref index, items[index].Indent, baseSize));
        }

        return lists;
    }

    private static List BuildListLevel(List<(int Indent, bool Ordered, int Number, string Text)> items, ref int index, int indent, double baseSize)
    {
        var first = items[index];
        var list = new List
        {
            MarkerStyle = first.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            StartIndex = first.Ordered ? Math.Max(1, first.Number) : 1,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(first.Ordered ? 22 : 18, 0, 0, 0),
            MarkerOffset = 6,
        };

        ListItem? lastItem = null;
        while (index < items.Count)
        {
            var item = items[index];
            if (item.Indent < indent || (item.Indent == indent && lastItem is not null && item.Ordered != first.Ordered))
            {
                break;
            }

            if (item.Indent > indent && lastItem is not null)
            {
                var nested = BuildListLevel(items, ref index, item.Indent, baseSize);
                nested.Margin = new Thickness(0, 2, 0, 2);
                lastItem.Blocks.Add(nested);
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            var textLines = item.Text.Split('\n');
            for (var k = 0; k < textLines.Length; k++)
            {
                if (k > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                AddInlines(paragraph.Inlines, TaskBox(textLines[k]));
            }

            lastItem = new ListItem(paragraph);
            list.ListItems.Add(lastItem);
            index++;
        }

        return list;
    }

    private static string TaskBox(string text) =>
        text.StartsWith("[ ] ", StringComparison.Ordinal) ? "☐ " + text[4..]
        : text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) ? "☑ " + text[4..]
        : text;

    private static Table Table(List<string> rows)
    {
        var cells = rows.Select(SplitRow).ToList();
        var columns = cells.Max(r => r.Count);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1, 1, 0, 0) };
        table.SetResourceReference(Block.BorderBrushProperty, "LineBrush");
        for (var c = 0; c < columns; c++)
        {
            table.Columns.Add(new TableColumn { Width = GridLength.Auto });
        }

        var group = new TableRowGroup();
        for (var r = 0; r < cells.Count; r++)
        {
            var row = new TableRow();
            if (r == 0)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.SetResourceReference(TableRow.BackgroundProperty, "SurfaceAltBackground");
            }

            for (var c = 0; c < columns; c++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                AddInlines(paragraph.Inlines, c < cells[r].Count ? cells[r][c] : string.Empty);
                var cell = new TableCell(paragraph) { Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "LineBrush");
                row.Cells.Add(cell);
            }

            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }

    private static List<string> SplitRow(string row)
    {
        var trimmed = row.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return [.. trimmed.Split('|').Select(c => c.Trim())];
    }

    /// <summary>Parses inline Markdown (code, bold, italic, strike, links) into <paramref name="target"/>.</summary>
    public static void AddInlines(InlineCollection target, string text)
    {
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length > 0)
            {
                target.Add(new Run(plain.ToString()));
                plain.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!|~>".Contains(text[i + 1], StringComparison.Ordinal))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (ch == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    FlushPlain();
                    var code = new Run(text[(i + 1)..close]) { FontFamily = new FontFamily(MonoFont) };
                    code.SetResourceReference(TextElement.BackgroundProperty, "CodeBackground");
                    target.Add(code);
                    i = close + 1;
                    continue;
                }
            }

            if ((ch == '*' || ch == '_') && i + 1 < text.Length && text[i + 1] == ch)
            {
                var marker = new string(ch, 2);
                var close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    var bold = new Bold();
                    AddInlines(bold.Inlines, text[(i + 2)..close]);
                    target.Add(bold);
                    i = close + 2;
                    continue;
                }
            }

            if (ch == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    var strike = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(strike.Inlines, text[(i + 2)..close]);
                    target.Add(strike);
                    i = close + 2;
                    continue;
                }
            }

            if ((ch == '*' || ch == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) &&
                (ch == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var close = FindItalicClose(text, i + 1, ch);
                if (close > i + 1)
                {
                    FlushPlain();
                    var italic = new Italic();
                    AddInlines(italic.Inlines, text[(i + 1)..close]);
                    target.Add(italic);
                    i = close + 1;
                    continue;
                }
            }

            if (ch == '[')
            {
                var link = LinkRegex().Match(text, i);
                if (link.Success && link.Index == i)
                {
                    FlushPlain();
                    target.Add(CreateLink(link.Groups[1].Value, link.Groups[2].Value));
                    i += link.Length;
                    continue;
                }
            }

            if (ch == 'h' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var url = UrlRegex().Match(text, i);
                if (url.Success && url.Index == i)
                {
                    FlushPlain();
                    var value = url.Value.TrimEnd('.', ',', ')', ';', ':');
                    target.Add(CreateLink(value, value));
                    i += value.Length;
                    continue;
                }
            }

            plain.Append(ch);
            i++;
        }

        FlushPlain();
    }

    private static int FindItalicClose(string text, int from, char marker)
    {
        for (var k = from; k < text.Length; k++)
        {
            if (text[k] == marker && !char.IsWhiteSpace(text[k - 1]) &&
                (k + 1 >= text.Length || text[k + 1] != marker) &&
                (marker == '*' || k + 1 >= text.Length || !char.IsLetterOrDigit(text[k + 1])))
            {
                return k;
            }
        }

        return -1;
    }

    private static Inline CreateLink(string label, string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri, Cursor = Cursors.Hand };
            AddInlines(link.Inlines, label);
            link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            return link;
        }

        var span = new Span();
        AddInlines(span.Inlines, label);
        return span;
    }

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\*\s*\*\s*\*[\s*]*|-\s*-\s*-[\s-]*|_\s*_\s*_[\s_]*)$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"https?://[^\s<>""'`]+")]
    private static partial Regex UrlRegex();
}
