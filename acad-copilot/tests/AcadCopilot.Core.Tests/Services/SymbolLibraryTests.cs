using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AcadCopilot.Core.Services;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests.Services;

public sealed class SymbolLibraryTests
{
    private static readonly string[] Required =
    [
        "E-SWITCHBOARD", "E-SOCKET-5A", "E-SOCKET-15A", "E-LIGHT", "E-FAN", "E-AC-POINT", "E-DB", "E-EARTH", "E-GEYSER",
        "P-BASIN-TAP", "P-WC", "P-FLOOR-TRAP", "P-NAHANI-TRAP", "P-INLET-PIPE", "P-OUTLET-PIPE", "P-INSPECTION-CHAMBER", "P-WATER-TANK",
    ];

    [Fact]
    public void The_library_has_every_requested_symbol_on_a_services_layer()
    {
        var names = SymbolLibrary.All.Select(s => s.BlockName).ToList();
        Assert.All(Required, name => Assert.Contains(name, names));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var symbol in SymbolLibrary.All)
        {
            var layer = ServiceLayers.Find(symbol.Layer);
            Assert.NotNull(layer);
            Assert.Equal(symbol.Discipline, layer.Discipline);
            Assert.NotEmpty(symbol.Shapes);
            Assert.False(string.IsNullOrWhiteSpace(symbol.Title));
            Assert.False(string.IsNullOrWhiteSpace(symbol.Description));
            Assert.NotNull(symbol.Attribute);
            Assert.Matches("^[A-Z]+$", symbol.Attribute.Tag);
        }

        Assert.Equal(new[] { "E-LITE", "E-POWR", "E-SWCH", "E-ANNO", "P-WATR", "P-DRAN", "P-FIXT", "P-ANNO" }, ServiceLayers.All.Select(l => l.Name));
        Assert.Equal(ServiceDiscipline.Electrical, SymbolLibrary.Find("e-light")!.Discipline);
        Assert.Null(SymbolLibrary.Find("E-NOPE"));
        Assert.Contains("ELECTRICAL ENGINEER", SymbolLibrary.DraftNote(ServiceDiscipline.Electrical), StringComparison.Ordinal);
        Assert.Contains("PLUMBING ENGINEER", SymbolLibrary.DraftNote(ServiceDiscipline.Plumbing), StringComparison.Ordinal);
        Assert.StartsWith("DRAFT - TO BE CHECKED BY A QUALIFIED", SymbolLibrary.DraftNote(ServiceDiscipline.Plumbing), StringComparison.Ordinal);
    }

    [Fact]
    public void Bounds_cover_the_geometry_and_the_label()
    {
        var wc = SymbolLibrary.Bounds(SymbolLibrary.Find("P-WC")!);
        Assert.Equal(-27, wc.MinY, 1);
        Assert.Equal(0, wc.MaxY, 1);
        Assert.Equal(-9, wc.MinX, 1);

        var light = SymbolLibrary.Bounds(SymbolLibrary.Find("E-LIGHT")!);
        Assert.Equal(-4, light.MinX, 6);
        Assert.Equal(-4, light.MinY, 6);
        Assert.True(light.MaxX > 5); // the label sits to the right

        var socket = SymbolLibrary.Bounds(SymbolLibrary.Find("E-SOCKET-15A")!);
        Assert.Equal(-6.25, socket.MinY, 6); // the "15A" text under the base line
        Assert.Equal(4, socket.MaxY, 6); // the label (y 1, height 3) is above the half disc (top 2)
    }

    [Fact]
    public void Bulge_arc_points_follow_a_semicircle()
    {
        var points = SymbolGeometry.ArcPoints(-7, -20, 7, -20, 1, 4).ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal(0, points[1].X, 6);
        Assert.Equal(-27, points[1].Y, 6); // counter-clockwise from the left end passes below the chord
        Assert.All(points, p => Assert.Equal(7, Math.Sqrt((p.X * p.X) + ((p.Y + 20) * (p.Y + 20))), 6));
    }

    [Fact]
    public void The_dxf_is_valid_r12_with_blocks_attdefs_and_a_catalogue_of_inserts()
    {
        var text = SymbolLibraryDxf.Write();
        Assert.Empty(DxfReader.Validate(text));

        var file = DxfReader.Parse(text);
        Assert.Equal("AC1009", file.Header["$ACADVER"]);
        Assert.Equal(new[] { "HEADER", "TABLES", "BLOCKS", "ENTITIES" }, file.Sections);
        Assert.True(file.HasEof);
        Assert.Contains(file.Table("LTYPE"), l => l.Get(2) == "DASHED");
        Assert.All(ServiceLayers.All, layer => Assert.Contains(file.Table("LAYER"), l => l.Get(2) == layer.Name && l.GetInt(62) == layer.ColorIndex));
        Assert.Equal(SymbolLibrary.All.Count, file.Blocks.Count);

        foreach (var symbol in SymbolLibrary.All)
        {
            var block = file.Block(symbol.BlockName);
            Assert.NotNull(block);
            Assert.Equal(2, block.Flags);
            Assert.Equal(symbol.Shapes.Count + 1, block.Entities.Count);
            Assert.All(block.Entities, e => Assert.Equal("0", e.Layer));
            Assert.All(block.Entities.Where(e => e.Type != "ATTDEF"), e => Assert.Equal(0, e.GetInt(62, -1))); // BYBLOCK
            var attdef = Assert.Single(block.Entities, e => e.Type == "ATTDEF");
            Assert.Equal(symbol.Attribute!.Tag, attdef.Get(2));
            Assert.Equal(symbol.Attribute.Prompt, attdef.Get(3));
            Assert.Equal(symbol.Attribute.Default, attdef.Get(1));

            var insert = Assert.Single(file.Entities, e => e.Type == "INSERT" && e.Get(2) == symbol.BlockName);
            Assert.Equal(symbol.Layer, insert.Layer);
            Assert.Equal(1, insert.GetInt(66));
            Assert.True(insert.HasSeqEnd);
            var attrib = Assert.Single(insert.Children);
            Assert.Equal("ATTRIB", attrib.Type);
            Assert.Equal(symbol.Attribute.Tag, attrib.Get(2));
            Assert.Equal(insert.GetDouble(10) + symbol.Attribute.X, attrib.GetDouble(10), 4);
        }

        var wc = file.Block("P-WC")!;
        var polylines = wc.Entities.Where(e => e.Type == "POLYLINE").ToList();
        Assert.Equal(3, polylines.Count);
        Assert.All(polylines, p => Assert.True(p.HasSeqEnd && p.GetInt(70) == 1));
        Assert.Contains(polylines[1].Children, v => v.GetDouble(42) == 1);
        Assert.Contains(file.Entities, e => e.Type == "TEXT" && e.Get(1) == SymbolLibrary.DraftNote(ServiceDiscipline.Electrical));
        Assert.Contains(file.Entities, e => e.Type == "TEXT" && e.Get(1) == SymbolLibrary.DraftNote(ServiceDiscipline.Plumbing));
    }

    [Fact]
    public void The_dxf_is_ascii_with_crlf_and_the_same_in_any_culture()
    {
        var invariant = SymbolLibraryDxf.Write();
        Assert.True(invariant.All(ch => ch is '\r' or '\n' || (ch >= 32 && ch < 127)));
        Assert.DoesNotContain("\r\r", invariant, StringComparison.Ordinal);
        Assert.Equal(invariant.Split('\n').Length - 1, invariant.Split("\r\n").Length - 1);
        Assert.EndsWith("  0\r\nEOF\r\n", invariant, StringComparison.Ordinal);
        Assert.DoesNotContain("-0.0\r\n", invariant, StringComparison.Ordinal);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, SymbolLibraryDxf.Write());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_validator_finds_broken_structures()
    {
        var good = SymbolLibraryDxf.Write();

        var noEndblk = ReplaceFirst(good, "  0\r\nENDBLK\r\n  8\r\n0\r\n", string.Empty);
        Assert.Contains(DxfReader.Validate(noEndblk), p => p.Contains("ENDBLK", StringComparison.Ordinal) || p.Contains("BLOCK", StringComparison.Ordinal));

        var unknownBlock = ReplaceFirst(good, "  2\r\nE-LIGHT\r\n 10", "  2\r\nE-LIGHTX\r\n 10");
        Assert.Contains(DxfReader.Validate(unknownBlock), p => p.Contains("E-LIGHTX", StringComparison.Ordinal));

        var badLayer = ReplaceFirst(good, "  0\r\nINSERT\r\n  8\r\nE-SWCH\r\n", "  0\r\nINSERT\r\n  8\r\nE-NOWHERE\r\n");
        Assert.Contains(DxfReader.Validate(badLayer), p => p.Contains("E-NOWHERE", StringComparison.Ordinal));

        var badNumber = ReplaceFirst(good, " 40\r\n4.0\r\n", " 40\r\n4,0\r\n");
        Assert.Contains(DxfReader.Validate(badNumber), p => p.Contains("not a number", StringComparison.Ordinal));

        var noSeqend = ReplaceFirst(good, "  0\r\nSEQEND\r\n  8\r\nE-SWCH\r\n", string.Empty);
        Assert.Contains(DxfReader.Validate(noSeqend), p => p.Contains("SEQEND", StringComparison.Ordinal));

        Assert.Contains(DxfReader.Validate(good.Replace("  0\r\nEOF\r\n", string.Empty, StringComparison.Ordinal)), p => p.Contains("EOF", StringComparison.Ordinal));
        Assert.Contains(DxfReader.Validate("  0\r\nSECTION\r\n  2\r\n"), p => p.Length > 0);
    }

    [Fact]
    public void The_bundle_library_file_is_up_to_date_with_the_code()
    {
        var path = Path.Combine(RepositoryRoot(), "deploy", "AcadCopilot.bundle", "Contents", SymbolLibrary.BundleFolder, SymbolLibrary.FileName);
        Assert.True(File.Exists(path), $"{path} is missing: run scripts\\make-symbol-library.ps1.");
        var onDisk = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var expected = SymbolLibraryDxf.Write().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(onDisk == expected, "The bundle's symbol library is out of date: run scripts\\make-symbol-library.ps1.");
        Assert.Empty(DxfReader.Validate(File.ReadAllText(path)));
    }

    [Fact]
    public void The_legend_has_a_row_per_symbol_the_symbols_fit_and_the_draft_note_follows()
    {
        var rows = SymbolLibrary.Of(ServiceDiscipline.Plumbing);
        var legend = ServicesLegend.Build(ServiceDiscipline.Plumbing, rows, 100, 500);
        Assert.Equal(ServiceLayers.PlumbingNotes, legend.Layer);
        Assert.Equal(rows.Count, legend.Symbols.Count);
        Assert.Equal(6 + (rows.Count - 1), legend.Lines.Count);
        Assert.Contains(legend.Texts, t => t.Text == "PLUMBING LEGEND");
        Assert.Contains(legend.Texts, t => t.Text == SymbolLibrary.DraftNote(ServiceDiscipline.Plumbing));
        var note = legend.Texts.Single(t => t.Text == SymbolLibrary.DraftNote(ServiceDiscipline.Plumbing));
        var tableBottom = legend.Lines.Min(l => Math.Min(l.Y1, l.Y2));
        Assert.True(note.Y < tableBottom);
        Assert.True(legend.MinY < note.Y);
        Assert.Equal(500, legend.MaxY);

        // Every symbol, scaled, fits inside its row and the symbol column.
        for (var i = 0; i < legend.Symbols.Count; i++)
        {
            var placed = legend.Symbols[i];
            var bounds = SymbolLibrary.Bounds(placed.Symbol);
            var top = 500 - 18 - 14 - (i * ServicesLegend.RowHeight);
            Assert.True(placed.Y + (bounds.MaxY * placed.Scale) <= top + 1e-6, placed.Symbol.BlockName);
            Assert.True(placed.Y + (bounds.MinY * placed.Scale) >= top - ServicesLegend.RowHeight - 1e-6, placed.Symbol.BlockName);
            Assert.True(placed.X + (bounds.MinX * placed.Scale) >= 100 - 1e-6 && placed.X + (bounds.MaxX * placed.Scale) <= 100 + ServicesLegend.SymbolColumn + 1e-6, placed.Symbol.BlockName);
        }

        var metric = ServicesLegend.Build(ServiceDiscipline.Electrical, [SymbolLibrary.Find("E-FAN")!], 0, 0, 25.4);
        Assert.Equal(-(18 + 14 + 24) * 25.4, metric.Lines.Min(l => l.Y1), 6);
        Assert.Contains(metric.Texts, t => t.Text == "ELECTRICAL LEGEND" && Math.Abs(t.Height - (9 * 25.4)) < 1e-9);
        Assert.Throws<ArgumentException>(() => ServicesLegend.Build(ServiceDiscipline.Electrical, [], 0, 0));
        Assert.Equal(new[] { "aaa bbb", "ccc" }, ServicesLegend.Wrap("aaa bbb ccc", 7));
    }

    [Fact]
    public void Tool_definitions_lint_and_validate_typical_arguments()
    {
        var insert = ServiceToolDefinitions.InsertSymbolLibraryDefinition();
        var legend = ServiceToolDefinitions.PlaceServicesLegendDefinition();
        Assert.Empty(insert.Lint());
        Assert.Empty(legend.Lint());
        Assert.Equal(ToolRisk.Write, insert.Risk);
        Assert.All(SymbolLibrary.All, s => Assert.Contains(s.BlockName, insert.Description, StringComparison.Ordinal));

        Assert.Empty(JsonSchemaValidator.Validate(insert.InputSchema, new JsonObject()));
        Assert.Empty(JsonSchemaValidator.Validate(insert.InputSchema, new JsonObject { ["discipline"] = "electrical", ["replace_existing"] = true }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(insert.InputSchema, new JsonObject { ["discipline"] = "hvac" }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(insert.InputSchema, new JsonObject { ["extra"] = 1 }));

        Assert.Empty(JsonSchemaValidator.Validate(legend.InputSchema, new JsonObject { ["discipline"] = "plumbing", ["at"] = new JsonArray(10, 20) }));
        Assert.Empty(JsonSchemaValidator.Validate(legend.InputSchema, new JsonObject { ["discipline"] = "electrical", ["at"] = new JsonArray(0, 0), ["symbols"] = new JsonArray("E-LIGHT", "E-FAN"), ["size"] = 2 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(legend.InputSchema, new JsonObject { ["discipline"] = "electrical" }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(legend.InputSchema, new JsonObject { ["discipline"] = "electrical", ["at"] = new JsonArray(1, 2, 3) }));
    }

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(index >= 0, "test pattern not found: " + find);
        return text[..index] + replacement + text[(index + find.Length)..];
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new FileInfo(sourceFile).Directory;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcadCopilot.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found from " + sourceFile);
    }
}
