namespace AcadCopilot.Core.Services;

/// <summary>A legend line (drawing units).</summary>
public sealed record LegendLine(double X1, double Y1, double X2, double Y2);

/// <summary>A legend text (drawing units). <paramref name="Centered"/>: middle-centre at X,Y; otherwise left, middle.</summary>
public sealed record LegendText(double X, double Y, double Height, string Text, bool Centered);

/// <summary>A symbol reference in the legend: block, insertion point and scale (drawing units).</summary>
public sealed record LegendSymbol(SymbolDefinition Symbol, double X, double Y, double Scale);

/// <summary>Everything a services legend draws, plus its extents.</summary>
public sealed record LegendLayout(
    ServiceDiscipline Discipline,
    string Layer,
    IReadOnlyList<LegendLine> Lines,
    IReadOnlyList<LegendText> Texts,
    IReadOnlyList<LegendSymbol> Symbols,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY);

/// <summary>
/// Lays out a services legend table (title, "SYMBOL | DESCRIPTION" header, one row per symbol) with the draft note
/// below it. Pure; sizes at <c>scale</c> 1 are inches for 1/4" = 1'-0" (text 4 1/2" = 3/32" on paper).
/// </summary>
public static class ServicesLegend
{
    /// <summary>Row height at scale 1.</summary>
    public const double RowHeight = 24;

    /// <summary>Symbol column width at scale 1.</summary>
    public const double SymbolColumn = 36;

    /// <summary>Description column width at scale 1.</summary>
    public const double DescriptionColumn = 168;

    /// <summary>Text height of rows at scale 1.</summary>
    public const double TextHeight = 4.5;

    /// <summary>Longest note line in characters before it wraps.</summary>
    public const int NoteLineLength = 64;

    /// <summary>
    /// Builds the legend with its top-left corner at (<paramref name="left"/>, <paramref name="top"/>).
    /// </summary>
    /// <param name="discipline">Electrical or plumbing (title, layer, note).</param>
    /// <param name="symbols">Rows, in order (usually the symbols used in the drawing).</param>
    /// <param name="left">Left edge, drawing units.</param>
    /// <param name="top">Top edge, drawing units.</param>
    /// <param name="scale">Drawing units per inch at 1/4" = 1'-0" (1 for an inch drawing; 25.4 for millimetres), times a size factor.</param>
    /// <param name="title">Title (default "ELECTRICAL LEGEND" / "PLUMBING LEGEND").</param>
    public static LegendLayout Build(ServiceDiscipline discipline, IReadOnlyList<SymbolDefinition> symbols, double left, double top, double scale = 1, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count == 0)
        {
            throw new ArgumentException("The legend needs at least one symbol.", nameof(symbols));
        }

        if (!(scale > 0) || !double.IsFinite(scale) || !double.IsFinite(left) || !double.IsFinite(top))
        {
            throw new ArgumentException("The legend needs a finite position and a positive scale.");
        }

        var lines = new List<LegendLine>();
        var texts = new List<LegendText>();
        var placed = new List<LegendSymbol>();
        var row = RowHeight * scale;
        var symbolWidth = SymbolColumn * scale;
        var width = (SymbolColumn + DescriptionColumn) * scale;
        var right = left + width;
        var titleBand = 18 * scale;
        var headerBand = 14 * scale;

        title ??= discipline == ServiceDiscipline.Electrical ? "ELECTRICAL LEGEND" : "PLUMBING LEGEND";
        texts.Add(new LegendText(left, top - (titleBand / 2), 9 * scale, title, false));

        var tableTop = top - titleBand;
        var headerBottom = tableTop - headerBand;
        texts.Add(new LegendText(left + (symbolWidth / 2), tableTop - (headerBand / 2), 4 * scale, "SYMBOL", true));
        texts.Add(new LegendText(left + symbolWidth + (6 * scale), tableTop - (headerBand / 2), 4 * scale, "DESCRIPTION", false));

        var y = headerBottom;
        foreach (var symbol in symbols)
        {
            var bounds = SymbolLibrary.Bounds(symbol);
            var fit = Math.Min(1.0, Math.Min((RowHeight - 6) / Math.Max(bounds.Height, 1e-6), (SymbolColumn - 8) / Math.Max(bounds.Width, 1e-6)));
            var symbolScale = fit * scale;
            var centerX = left + (symbolWidth / 2);
            var centerY = y - (row / 2);
            placed.Add(new LegendSymbol(symbol, centerX - (bounds.CenterX * symbolScale), centerY - (bounds.CenterY * symbolScale), symbolScale));
            texts.Add(new LegendText(left + symbolWidth + (6 * scale), centerY, TextHeight * scale, symbol.Title, false));
            y -= row;
        }

        var bottom = y;

        // Frame, header line, row lines and the column line.
        lines.Add(new LegendLine(left, tableTop, right, tableTop));
        lines.Add(new LegendLine(left, bottom, right, bottom));
        lines.Add(new LegendLine(left, tableTop, left, bottom));
        lines.Add(new LegendLine(right, tableTop, right, bottom));
        lines.Add(new LegendLine(left + symbolWidth, tableTop, left + symbolWidth, bottom));
        lines.Add(new LegendLine(left, headerBottom, right, headerBottom));
        for (var i = 1; i < symbols.Count; i++)
        {
            var lineY = headerBottom - (i * row);
            lines.Add(new LegendLine(left, lineY, right, lineY));
        }

        // Draft note under the table.
        var noteY = bottom - (14 * scale);
        texts.Add(new LegendText(left, noteY, 6 * scale, SymbolLibrary.DraftNote(discipline), false));
        noteY -= 10 * scale;
        foreach (var part in Wrap(SymbolLibrary.DraftDetail(discipline), NoteLineLength))
        {
            texts.Add(new LegendText(left, noteY, 4 * scale, part, false));
            noteY -= 7 * scale;
        }

        var noteWidth = SymbolLibrary.DraftNote(discipline).Length * 6 * scale * 0.85;
        return new LegendLayout(
            discipline,
            ServiceLayers.NotesLayer(discipline),
            lines,
            texts,
            placed,
            left,
            noteY,
            Math.Max(right, left + noteWidth),
            top);
    }

    /// <summary>Splits <paramref name="text"/> into lines of at most <paramref name="length"/> characters at spaces.</summary>
    public static IReadOnlyList<string> Wrap(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > length)
            {
                result.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : current + " " + word;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current);
        }

        return result;
    }
}
