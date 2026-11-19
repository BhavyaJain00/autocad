using System.Globalization;
using System.Text;

namespace AcadCopilot.Core.Services;

/// <summary>
/// Writes the services symbol library as an AutoCAD R12 DXF (AC1009): every symbol as a BLOCK (geometry on layer 0,
/// colour BYBLOCK, ATTDEF for its label) plus a catalogue sheet in ENTITIES (one INSERT with its ATTRIB per symbol, the
/// symbol's name and title, and the draft notes) on the services layers. R12 is chosen on purpose: it needs no handles,
/// owners or OBJECTS section, so a file written without AutoCAD is still read reliably by AutoCAD's DXFIN /
/// Database.DxfIn. Plain ASCII, CRLF line ends, invariant numbers. Validate with <see cref="DxfReader"/>.
/// </summary>
public static class SymbolLibraryDxf
{
    /// <summary>Catalogue columns.</summary>
    public const int CatalogueColumns = 4;

    /// <summary>Catalogue cell width (inches).</summary>
    public const double CellWidth = 72;

    /// <summary>Catalogue cell height (inches).</summary>
    public const double CellHeight = 60;

    /// <summary>The library DXF text for <paramref name="symbols"/> (default: <see cref="SymbolLibrary.All"/>).</summary>
    public static string Write(IReadOnlyList<SymbolDefinition>? symbols = null)
    {
        symbols ??= SymbolLibrary.All;
        var dxf = new DxfBuilder();

        dxf.Pair(0, "SECTION").Pair(2, "HEADER");
        dxf.Pair(9, "$ACADVER").Pair(1, "AC1009");
        dxf.Pair(9, "$INSBASE").Point(10, 0, 0);
        dxf.Pair(9, "$TEXTSTYLE").Pair(7, "STANDARD");
        dxf.Pair(9, "$CLAYER").Pair(8, "0");
        dxf.Pair(0, "ENDSEC");

        dxf.Pair(0, "SECTION").Pair(2, "TABLES");
        dxf.Pair(0, "TABLE").Pair(2, "LTYPE").Pair(70, 2);
        dxf.Pair(0, "LTYPE").Pair(2, "CONTINUOUS").Pair(70, 0).Pair(3, "Solid line").Pair(72, 65).Pair(73, 0).Number(40, 0);
        dxf.Pair(0, "LTYPE").Pair(2, "DASHED").Pair(70, 0).Pair(3, "Dashed __ __ __ __").Pair(72, 65).Pair(73, 2).Number(40, 0.75).Number(49, 0.5).Number(49, -0.25);
        dxf.Pair(0, "ENDTAB");
        var layers = ServiceLayers.All;
        dxf.Pair(0, "TABLE").Pair(2, "LAYER").Pair(70, layers.Count + 1);
        dxf.Pair(0, "LAYER").Pair(2, "0").Pair(70, 0).Pair(62, 7).Pair(6, "CONTINUOUS");
        foreach (var layer in layers)
        {
            dxf.Pair(0, "LAYER").Pair(2, layer.Name).Pair(70, 0).Pair(62, layer.ColorIndex).Pair(6, layer.Linetype);
        }

        dxf.Pair(0, "ENDTAB");
        dxf.Pair(0, "TABLE").Pair(2, "STYLE").Pair(70, 1);
        dxf.Pair(0, "STYLE").Pair(2, "STANDARD").Pair(70, 0).Number(40, 0).Number(41, 1).Number(50, 0).Pair(71, 0).Number(42, 3).Pair(3, "txt").Pair(4, string.Empty);
        dxf.Pair(0, "ENDTAB");
        dxf.Pair(0, "ENDSEC");

        dxf.Pair(0, "SECTION").Pair(2, "BLOCKS");
        foreach (var symbol in symbols)
        {
            dxf.Pair(0, "BLOCK").Pair(8, "0").Pair(2, symbol.BlockName).Pair(70, symbol.Attribute is null ? 0 : 2).Point(10, 0, 0).Pair(3, symbol.BlockName);
            foreach (var shape in symbol.Shapes)
            {
                WriteShape(dxf, shape, "0", 0, 0);
            }

            if (symbol.Attribute is { } attribute)
            {
                dxf.Pair(0, "ATTDEF").Pair(8, "0").Pair(62, 0);
                dxf.Point(10, attribute.X, attribute.Y).Number(40, attribute.Height).Pair(1, attribute.Default).Pair(3, attribute.Prompt).Pair(2, attribute.Tag).Pair(70, 0).Pair(7, "STANDARD");
                Alignment(dxf, attribute.Centered, attribute.X, attribute.Y, vertical: 74);
            }

            dxf.Pair(0, "ENDBLK").Pair(8, "0");
        }

        dxf.Pair(0, "ENDSEC");

        dxf.Pair(0, "SECTION").Pair(2, "ENTITIES");
        WriteCatalogue(dxf, symbols);
        dxf.Pair(0, "ENDSEC");
        dxf.Pair(0, "EOF");
        return dxf.ToString();
    }

    /// <summary>Insertion point of symbol number <paramref name="index"/> on the catalogue sheet (bounds centred in its cell).</summary>
    public static (double X, double Y) CataloguePoint(int index, SymbolDefinition symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var column = index % CatalogueColumns;
        var row = index / CatalogueColumns;
        var bounds = SymbolLibrary.Bounds(symbol);
        var centerX = (column * CellWidth) + (CellWidth / 2);
        var centerY = -(row * CellHeight) - (CellHeight * 0.4);
        return (Math.Round(centerX - bounds.CenterX, 4), Math.Round(centerY - bounds.CenterY, 4));
    }

    private static void WriteCatalogue(DxfBuilder dxf, IReadOnlyList<SymbolDefinition> symbols)
    {
        Text(dxf, ServiceLayers.ElectricalNotes, 0, 24, 8, "AI COPILOT - ELECTRICAL AND PLUMBING SYMBOL LIBRARY (1 unit = 1 inch)", centered: false);
        Text(dxf, ServiceLayers.ElectricalNotes, 0, 12, 4, "Blocks for -INSERT after insert_symbol_library; insert each on its layer. Base points: see the knowledge recipes.", centered: false);
        for (var i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            var (x, y) = CataloguePoint(i, symbol);
            dxf.Pair(0, "INSERT").Pair(8, symbol.Layer);
            if (symbol.Attribute is not null)
            {
                dxf.Pair(66, 1);
            }

            dxf.Pair(2, symbol.BlockName).Point(10, x, y).Number(41, 1).Number(42, 1).Number(43, 1).Number(50, 0);
            if (symbol.Attribute is { } attribute)
            {
                var ax = x + attribute.X;
                var ay = y + attribute.Y;
                dxf.Pair(0, "ATTRIB").Pair(8, symbol.Layer).Point(10, ax, ay).Number(40, attribute.Height).Pair(1, attribute.Default).Pair(2, attribute.Tag).Pair(70, 0).Pair(7, "STANDARD");
                Alignment(dxf, attribute.Centered, ax, ay, vertical: 74);
                dxf.Pair(0, "SEQEND").Pair(8, symbol.Layer);
            }

            var column = i % CatalogueColumns;
            var row = i / CatalogueColumns;
            var left = (column * CellWidth) + 4;
            var bottom = -((row + 1) * CellHeight);
            var notes = ServiceLayers.NotesLayer(symbol.Discipline);
            Text(dxf, notes, left, bottom + 9, 3.5, symbol.BlockName, centered: false);
            Text(dxf, notes, left, bottom + 4, 3, symbol.Title, centered: false);
        }

        var rows = (symbols.Count + CatalogueColumns - 1) / CatalogueColumns;
        var noteY = -(rows * CellHeight) - 14;
        foreach (var discipline in new[] { ServiceDiscipline.Electrical, ServiceDiscipline.Plumbing })
        {
            var layer = ServiceLayers.NotesLayer(discipline);
            Text(dxf, layer, 0, noteY, 6, SymbolLibrary.DraftNote(discipline), centered: false);
            Text(dxf, layer, 0, noteY - 8, 3.5, SymbolLibrary.DraftDetail(discipline), centered: false);
            noteY -= 22;
        }
    }

    private static void Text(DxfBuilder dxf, string layer, double x, double y, double height, string text, bool centered)
    {
        dxf.Pair(0, "TEXT").Pair(8, layer).Point(10, x, y).Number(40, height).Pair(1, text).Pair(7, "STANDARD");
        Alignment(dxf, centered, x, y, vertical: 73);
    }

    // Middle-centre text: horizontal 1 (centre) + vertical 2 (middle) with the alignment point. Left baseline needs nothing.
    private static void Alignment(DxfBuilder dxf, bool centered, double x, double y, int vertical)
    {
        if (centered)
        {
            dxf.Pair(72, 1).Point(11, x, y).Pair(vertical, 2);
        }
    }

    private static void WriteShape(DxfBuilder dxf, SymbolShape shape, string layer, double dx, double dy)
    {
        switch (shape)
        {
            case SymbolLine l:
                dxf.Pair(0, "LINE").Pair(8, layer).Pair(62, 0).Point(10, l.X1 + dx, l.Y1 + dy).Point(11, l.X2 + dx, l.Y2 + dy);
                break;
            case SymbolCircle c:
                dxf.Pair(0, "CIRCLE").Pair(8, layer).Pair(62, 0).Point(10, c.X + dx, c.Y + dy).Number(40, c.Radius);
                break;
            case SymbolArc a:
                dxf.Pair(0, "ARC").Pair(8, layer).Pair(62, 0).Point(10, a.X + dx, a.Y + dy).Number(40, a.Radius).Number(50, a.StartDegrees).Number(51, a.EndDegrees);
                break;
            case SymbolPolyline p:
                dxf.Pair(0, "POLYLINE").Pair(8, layer).Pair(62, 0).Pair(66, 1).Point(10, 0, 0).Pair(70, p.Closed ? 1 : 0).Number(40, p.Width).Number(41, p.Width);
                foreach (var v in p.Vertices)
                {
                    dxf.Pair(0, "VERTEX").Pair(8, layer).Pair(62, 0).Point(10, v.X + dx, v.Y + dy).Number(40, p.Width).Number(41, p.Width);
                    if (Math.Abs(v.Bulge) > 1e-12)
                    {
                        dxf.Number(42, v.Bulge);
                    }
                }

                dxf.Pair(0, "SEQEND").Pair(8, layer);
                break;
            case SymbolSolid s:
                dxf.Pair(0, "SOLID").Pair(8, layer).Pair(62, 0).Point(10, s.X1 + dx, s.Y1 + dy).Point(11, s.X2 + dx, s.Y2 + dy).Point(12, s.X3 + dx, s.Y3 + dy).Point(13, s.X3 + dx, s.Y3 + dy);
                break;
            case SymbolText t:
                dxf.Pair(0, "TEXT").Pair(8, layer).Pair(62, 0).Point(10, t.X + dx, t.Y + dy).Number(40, t.Height).Pair(1, t.Text).Pair(7, "STANDARD");
                Alignment(dxf, t.Centered, t.X + dx, t.Y + dy, vertical: 73);
                break;
            default:
                throw new ArgumentException($"Unknown symbol shape {shape.GetType().Name}.", nameof(shape));
        }
    }

    /// <summary>Writes group code / value pairs in DXF's text form.</summary>
    private sealed class DxfBuilder
    {
        private readonly StringBuilder _text = new();

        public DxfBuilder Pair(int code, string value)
        {
            foreach (var ch in value)
            {
                if (ch > 126 || ch < 32)
                {
                    throw new ArgumentException($"DXF text must be plain ASCII: '{value}'.", nameof(value));
                }
            }

            _text.Append(code.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("\r\n").Append(value).Append("\r\n");
            return this;
        }

        public DxfBuilder Pair(int code, int value) => Pair(code, value.ToString(CultureInfo.InvariantCulture));

        public DxfBuilder Number(int code, double value) => Pair(code, Format(value));

        public DxfBuilder Point(int code, double x, double y) => Number(code, x).Number(code + 10, y).Number(code + 20, 0);

        public override string ToString() => _text.ToString();

        private static string Format(double value)
        {
            var rounded = Math.Round(value, 6);
            if (rounded == 0)
            {
                rounded = 0; // no "-0.0"
            }

            return rounded.ToString("0.0#####", CultureInfo.InvariantCulture);
        }
    }
}
