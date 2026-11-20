using System.Globalization;
using AcadCopilot.Core.Accuracy;

namespace AcadCopilot.Core.Sheets;

/// <summary>A line of the title block (paper millimetres, block coordinates: paper lower-left = 0,0).</summary>
/// <param name="X0">Start X.</param>
/// <param name="Y0">Start Y.</param>
/// <param name="X1">End X.</param>
/// <param name="Y1">End Y.</param>
/// <param name="Weight">Lineweight in hundredths of a millimetre (50 = 0.50 mm).</param>
public sealed record BlockLine(double X0, double Y0, double X1, double Y1, int Weight);

/// <summary>A fixed text of the title block (left-aligned at its baseline start).</summary>
/// <param name="X">Baseline start X.</param>
/// <param name="Y">Baseline Y.</param>
/// <param name="Height">Text height (mm).</param>
/// <param name="Text">The text.</param>
public sealed record BlockText(double X, double Y, double Height, string Text);

/// <summary>An attribute of the title block (left-aligned at its baseline start).</summary>
/// <param name="Tag">Attribute tag (PROJECT, TITLE ...).</param>
/// <param name="Prompt">Prompt shown when editing.</param>
/// <param name="X">Baseline start X.</param>
/// <param name="Y">Baseline Y.</param>
/// <param name="Height">Preferred text height (mm); long values are made smaller to fit <paramref name="MaxWidth"/>.</param>
/// <param name="MaxWidth">Width available for the value (mm).</param>
/// <param name="Default">Default value.</param>
public sealed record BlockAttribute(string Tag, string Prompt, double X, double Y, double Height, double MaxWidth, string Default);

/// <summary>The values written into one sheet's title block.</summary>
public sealed record TitleBlockValues
{
    /// <summary>Project name.</summary>
    public string Project { get; init; } = "PROJECT";

    /// <summary>Client name.</summary>
    public string Client { get; init; } = "-";

    /// <summary>Drawing title ("GROUND FLOOR PLAN").</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Sheet number ("A-101").</summary>
    public string SheetNumber { get; init; } = string.Empty;

    /// <summary>Scale ("1/4\" = 1'-0\"", "AS SHOWN").</summary>
    public string Scale { get; init; } = string.Empty;

    /// <summary>Date ("20 Nov 2026").</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>Drawn by ("AI Copilot / ravi").</summary>
    public string DrawnBy { get; init; } = TitleBlockDesign.DrawnByPrefix;

    /// <summary>Value per attribute tag.</summary>
    public IReadOnlyDictionary<string, string> ByTag => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TitleBlockDesign.TagProject] = Project,
        [TitleBlockDesign.TagClient] = Client,
        [TitleBlockDesign.TagTitle] = Title,
        [TitleBlockDesign.TagSheet] = SheetNumber,
        [TitleBlockDesign.TagScale] = Scale,
        [TitleBlockDesign.TagDate] = Date,
        [TitleBlockDesign.TagDrawnBy] = DrawnBy,

        // Tags of the A3 title block of apply_drawing_template (accuracy pack).
        ["DRAWN"] = DrawnBy,
        ["SHEET"] = SheetNumber,
        ["NOTE"] = TitleBlockDesign.Note,
    };

    /// <summary>"AI Copilot / &lt;user&gt;" for the drawn-by field.</summary>
    public static string DrawnByFor(string? userName) =>
        string.IsNullOrWhiteSpace(userName) ? TitleBlockDesign.DrawnByPrefix : TitleBlockDesign.DrawnByPrefix + " / " + userName.Trim();

    /// <summary>The date as written in the title block ("20 Nov 2026").</summary>
    public static string DateText(DateTimeOffset date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>
/// The sheet frame and title block of one paper size, in paper millimetres: an ISO 5457-style border (20 mm binding
/// margin on the left, 10 mm elsewhere) and a title strip down the right side with the attributes PROJECT, CLIENT,
/// TITLE, SCALE, DATE, SHEET_NO and DRAWN_BY and the fixed "NOT FOR CONSTRUCTION - CHECK BY ARCHITECT/ENGINEER" note.
/// The plug-in turns it into the block "A-TTLB-&lt;paper&gt;" on layer A-TTLB. Pure.
/// </summary>
public sealed record TitleBlockDesign
{
    /// <summary>Layer of the title block.</summary>
    public const string Layer = "A-TTLB";

    /// <summary>Layer of the viewports (not plotted, so their frames do not print).</summary>
    public const string ViewportLayer = "A-VPRT";

    /// <summary>Block name prefix.</summary>
    public const string BlockPrefix = "A-TTLB-";

    /// <summary>The fixed safety note.</summary>
    public const string Note = "NOT FOR CONSTRUCTION - CHECK BY ARCHITECT/ENGINEER";

    /// <summary>Start of the drawn-by value.</summary>
    public const string DrawnByPrefix = "AI Copilot";

    /// <summary>Attribute tags.</summary>
    public const string TagProject = "PROJECT", TagClient = "CLIENT", TagTitle = "TITLE", TagScale = "SCALE", TagDate = "DATE", TagSheet = "SHEET_NO", TagDrawnBy = "DRAWN_BY";

    /// <summary>Binding margin (left).</summary>
    public const double LeftMargin = 20;

    /// <summary>Margin on the other sides.</summary>
    public const double Margin = 10;

    /// <summary>Clear space kept between the frame and the drawings.</summary>
    public const double Inset = 5;

    /// <summary>Average character width as a fraction of the text height (Arial-like capitals).</summary>
    public const double CharWidthFactor = 0.8;

    /// <summary>Smallest text height used when a value is made smaller to fit (mm).</summary>
    public const double MinTextHeight = 1.8;

    /// <summary>The block name.</summary>
    public required string BlockName { get; init; }

    /// <summary>The paper.</summary>
    public required PaperSize Paper { get; init; }

    /// <summary>The frame (inside the margins).</summary>
    public SheetRect Border { get; init; }

    /// <summary>The title strip on the right.</summary>
    public SheetRect Strip { get; init; }

    /// <summary>Where drawings (viewports) may go: inside the frame, left of the strip, inset by <see cref="Inset"/>.</summary>
    public SheetRect Drawable { get; init; }

    /// <summary>Lines.</summary>
    public required IReadOnlyList<BlockLine> Lines { get; init; }

    /// <summary>Fixed texts (labels and the note).</summary>
    public required IReadOnlyList<BlockText> Texts { get; init; }

    /// <summary>Attributes.</summary>
    public required IReadOnlyList<BlockAttribute> Attributes { get; init; }

    /// <summary>
    /// True when the block is not made by create_sheets but already defined in the drawing (the A3 title block of
    /// apply_drawing_template, drawn in paper inches): it is inserted at <see cref="InsertScale"/> and not redefined.
    /// </summary>
    public bool External { get; init; }

    /// <summary>Insertion scale of the block on a millimetre sheet (1 for create_sheets' own blocks, 25.4 for an inch block).</summary>
    public double InsertScale { get; init; } = 1;

    /// <summary>The design for <paramref name="paper"/>.</summary>
    public static TitleBlockDesign For(PaperSize paper)
    {
        ArgumentNullException.ThrowIfNull(paper);
        var border = new SheetRect(LeftMargin, Margin, paper.WidthMm - Margin, paper.HeightMm - Margin);

        // Strip 70 mm wide on A3, growing with the sheet (80 on A2, about 100 on A1); everything in it scales with it.
        var stripWidth = Math.Round(Math.Clamp(paper.WidthMm / 6.0, 70, 110), 1);
        var f = stripWidth / 70.0;
        var strip = new SheetRect(border.MaxX - stripWidth, border.MinY, border.MaxX, border.MaxY);
        var lines = new List<BlockLine>
        {
            new(border.MinX, border.MinY, border.MaxX, border.MinY, 50),
            new(border.MaxX, border.MinY, border.MaxX, border.MaxY, 50),
            new(border.MaxX, border.MaxY, border.MinX, border.MaxY, 50),
            new(border.MinX, border.MaxY, border.MinX, border.MinY, 50),
            new(strip.MinX, strip.MinY, strip.MinX, strip.MaxY, 35),
        };
        var texts = new List<BlockText>();
        var attributes = new List<BlockAttribute>();
        var pad = 2 * f;
        var label = 1.8 * f;

        // Rows from the bottom of the strip: note, drawn by, date | sheet no., scale, title, client, project.
        var y = strip.MinY;
        double Row(double height)
        {
            var bottom = y;
            y += height * f;
            lines.Add(new BlockLine(strip.MinX, y, strip.MaxX, y, 25));
            return bottom;
        }

        void Field(string tag, string caption, double x0, double x1, double bottom, double top, double height, string fallback)
        {
            texts.Add(new BlockText(x0 + pad, top - pad - label, label, caption));
            attributes.Add(new BlockAttribute(tag, caption, x0 + pad, bottom + pad + (0.5 * f), height * f, x1 - x0 - (2 * pad), fallback));
        }

        var noteBottom = Row(15);
        texts.Add(new BlockText(strip.MinX + pad, noteBottom + (8.5 * f), 2.2 * f, "NOT FOR CONSTRUCTION -"));
        texts.Add(new BlockText(strip.MinX + pad, noteBottom + (3.5 * f), 2.2 * f, "CHECK BY ARCHITECT/ENGINEER"));

        var drawnBottom = Row(11);
        Field(TagDrawnBy, "DRAWN BY", strip.MinX, strip.MaxX, drawnBottom, y, 2.5, DrawnByPrefix);

        var sheetBottom = Row(15);
        var middle = strip.MinX + (strip.Width * 0.45);
        lines.Add(new BlockLine(middle, sheetBottom, middle, y, 25));
        Field(TagDate, "DATE", strip.MinX, middle, sheetBottom, y, 2.5, string.Empty);
        Field(TagSheet, "SHEET NO.", middle, strip.MaxX, sheetBottom, y, 6, string.Empty);

        var scaleBottom = Row(11);
        Field(TagScale, "SCALE", strip.MinX, strip.MaxX, scaleBottom, y, 2.5, string.Empty);

        var titleBottom = Row(17);
        Field(TagTitle, "DRAWING TITLE", strip.MinX, strip.MaxX, titleBottom, y, 3.5, string.Empty);

        var clientBottom = Row(12);
        Field(TagClient, "CLIENT", strip.MinX, strip.MaxX, clientBottom, y, 2.5, "-");

        var projectBottom = Row(14);
        Field(TagProject, "PROJECT", strip.MinX, strip.MaxX, projectBottom, y, 3, "PROJECT");

        texts.Add(new BlockText(strip.MinX + pad, strip.MaxY - pad - label, label, "NOTES"));

        var drawable = new SheetRect(border.MinX + Inset, border.MinY + Inset, strip.MinX - Inset, border.MaxY - Inset);
        return new TitleBlockDesign
        {
            BlockName = BlockPrefix + paper.Code,
            Paper = paper,
            Border = border,
            Strip = strip,
            Drawable = drawable,
            Lines = lines,
            Texts = texts,
            Attributes = attributes,
        };
    }

    /// <summary>
    /// The design of the A3 title block that apply_drawing_template defines (block in paper inches, title box at the
    /// bottom right, attributes PROJECT, TITLE, SCALE, DATE, DRAWN, SHEET, NOTE) on an ISO A3 millimetre sheet: drawings go
    /// above its title box. Null when <paramref name="block"/> is not an A3 landscape sheet.
    /// </summary>
    public static TitleBlockDesign? FromTemplate(TemplateTitleBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        const double mm = 25.4;
        var paper = PaperSizes.For("A3", PaperStandard.Iso);
        if (Math.Abs((block.Width * mm) - paper.WidthMm) > 2 || Math.Abs((block.Height * mm) - paper.HeightMm) > 2)
        {
            return null;
        }

        var border = new SheetRect(LeftMargin, Margin, paper.WidthMm - Margin, paper.HeightMm - Margin);
        var lines = block.Lines.Select(l => new BlockLine(l.X1 * mm, l.Y1 * mm, l.X2 * mm, l.Y2 * mm, 25)).ToList();

        // The title box: the lines that are neither the sheet edge nor the frame.
        bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
        bool OnFrame(BlockLine l) =>
            (Near(l.Y0, l.Y1) && new[] { 0, paper.HeightMm, border.MinY, border.MaxY }.Any(y => Near(l.Y0, y))) ||
            (Near(l.X0, l.X1) && new[] { 0, paper.WidthMm, border.MinX, border.MaxX }.Any(x => Near(l.X0, x)));
        var inner = lines.Where(l => !OnFrame(l)).ToList();
        if (inner.Count == 0)
        {
            return null;
        }

        var box = inner.Select(l => SheetRect.FromCorners(l.X0, l.Y0, l.X1, l.Y1)).Aggregate((a, b) => a.Union(b));
        var verticals = inner.Where(l => Near(l.X0, l.X1)).ToList();
        var attributes = new List<BlockAttribute>();
        foreach (var a in block.Attributes)
        {
            var x = a.X * mm;
            var y = a.Y * mm;
            var right = box.Contains(x, y)
                ? verticals.Where(v => v.X0 > x + 0.5 && Math.Min(v.Y0, v.Y1) <= y && Math.Max(v.Y0, v.Y1) >= y).Select(v => v.X0).DefaultIfEmpty(box.MaxX).Min()
                : box.MinX;
            attributes.Add(new BlockAttribute(a.Tag, a.Prompt, x, y, a.Height * mm, Math.Max(10, right - x - 2), a.Default));
        }

        return new TitleBlockDesign
        {
            BlockName = block.Name,
            Paper = paper,
            Border = border,
            Strip = box,
            Drawable = new SheetRect(border.MinX + Inset, box.MaxY + Inset, border.MaxX - Inset, border.MaxY - Inset),
            Lines = lines,
            Texts = [.. block.Texts.Select(t => new BlockText(t.X * mm, t.Y * mm, t.Height * mm, t.Text))],
            Attributes = attributes,
            External = true,
            InsertScale = mm,
        };
    }

    /// <summary>
    /// The text height for <paramref name="value"/> in an attribute: its preferred height, made smaller (down to
    /// <see cref="MinTextHeight"/>) so the value fits the field's width.
    /// </summary>
    public static double FitHeight(BlockAttribute attribute, string? value)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var length = (value ?? string.Empty).Length;
        if (length == 0)
        {
            return attribute.Height;
        }

        var fitting = attribute.MaxWidth / (length * CharWidthFactor);
        return Math.Round(Math.Max(Math.Min(attribute.Height, fitting), Math.Min(MinTextHeight, attribute.Height)), 2);
    }
}
