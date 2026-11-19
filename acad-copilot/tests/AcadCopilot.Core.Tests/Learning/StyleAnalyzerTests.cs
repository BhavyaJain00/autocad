using AcadCopilot.Core.Learning;

namespace AcadCopilot.Core.Tests.Learning;

public sealed class StyleAnalyzerTests
{
    // A 26' x 34' plan in inches: 9" outer walls (outer and inner face rectangles) and one 4 1/2" inner wall at x = 144.
    private static List<SegmentSample> Plan()
    {
        var segments = new List<SegmentSample>();
        void Rect(double x1, double y1, double x2, double y2)
        {
            segments.Add(new SegmentSample(x1, y1, x2, y1, "A-WALL"));
            segments.Add(new SegmentSample(x2, y1, x2, y2, "A-WALL"));
            segments.Add(new SegmentSample(x2, y2, x1, y2, "A-WALL"));
            segments.Add(new SegmentSample(x1, y2, x1, y1, "A-WALL"));
        }

        Rect(0, 0, 312, 408);
        Rect(9, 9, 303, 399);
        segments.Add(new SegmentSample(144, 9, 144, 399, "A-WALL"));
        segments.Add(new SegmentSample(148.5, 9, 148.5, 399, "A-WALL"));
        segments.Add(new SegmentSample(0, -60, 312, -60, "A-ANNO-DIMS")); // far away: not a wall
        return segments;
    }

    [Fact]
    public void Parallel_line_spacing_finds_the_wall_thicknesses()
    {
        var spacings = StyleAnalyzer.ParallelSpacings(Plan(), 1);
        Assert.Equal(9, spacings[0].Spacing);
        Assert.Equal(8, spacings[0].Count);
        Assert.Contains(spacings, s => s.Spacing == 4.5 && s.Count == 2);
        Assert.DoesNotContain(spacings, s => s.Spacing > 24);
    }

    [Fact]
    public void Spacing_works_in_millimetre_drawings()
    {
        // The same plan in millimetres: the spacing is rounded to 1/4" and reported in drawing units (228.6 mm = 9").
        var mm = Plan().Select(s => s with { X1 = s.X1 * 25.4, Y1 = s.Y1 * 25.4, X2 = s.X2 * 25.4, Y2 = s.Y2 * 25.4 }).ToList();
        var spacings = StyleAnalyzer.ParallelSpacings(mm, 1 / 25.4);
        Assert.Equal(228.6, spacings[0].Spacing, 3);
    }

    [Fact]
    public void A_sample_becomes_readable_style_sections()
    {
        var sample = new StyleSample
        {
            DrawingName = "House-GF.dwg",
            InchesPerUnit = 1,
            UnitsSummary = "architectural, 1 unit = 1 inch",
            Ltscale = 24,
            Layers =
            [
                new LayerSample("A-WALL", "7", "Continuous", 0.5, 10),
                new LayerSample("A-DOOR", "4", "Continuous", null, 6),
                new LayerSample("A-GLAZ", "5", "Continuous", 0.25, 3),
                new LayerSample("EMPTY", "1", "Continuous", null, 0),
                new LayerSample("Defpoints", "7", "Continuous", null, 4),
            ],
            Texts = [new TextSample("ARCH", 9, "A-ANNO-TEXT"), new TextSample("ARCH", 9, "A-ANNO-TEXT"), new TextSample("ARCH", 6, "A-ANNO-TEXT")],
            TextStyles = [new TextStyleSample("ARCH", "arial.ttf", 0)],
            DimStyles = [new DimStyleSample("ARCH-48", 12, new Dictionary<string, string> { ["DIMSCALE"] = "48", ["DIMTSZ"] = "0.09375" })],
            CurrentDimStyle = "ARCH-48",
            Segments = Plan(),
            Arcs = [new ArcSample(36, 90, "A-DOOR"), new ArcSample(36, 90, "A-DOOR"), new ArcSample(30, 90, "A-DOOR"), new ArcSample(50, 180, "A-DOOR")],
            Blocks = [new BlockSample("DOOR-3FT", "A-DOOR", 2), new BlockSample("BED-DOUBLE", "A-FURN", 3), new BlockSample("NORTH", "A-ANNO", 1), new BlockSample("*U12", "0", 4)],
            Hatches = [new HatchSample("ANSI31", 24, "A-HATCH", 5)],
        };

        var profile = StyleAnalyzer.Analyze(sample);
        var sections = profile.Sections.ToDictionary(s => s.Title, s => s.Lines);
        Assert.Equal("House-GF.dwg", profile.Source);
        Assert.Equal("architectural, 1 unit = 1 inch, LTSCALE 24", Assert.Single(sections[StyleProfile.UnitsSection]));
        Assert.Equal("A-WALL: colour 7, Continuous, 0.5 mm (10 objects)", sections[StyleProfile.LayersSection][0]);
        Assert.DoesNotContain(sections[StyleProfile.LayersSection], l => l.StartsWith("EMPTY", StringComparison.Ordinal) || l.StartsWith("Defpoints", StringComparison.Ordinal));
        Assert.Contains("A-DOOR: colour 4, Continuous, default weight (6 objects)", sections[StyleProfile.LayersSection]);
        Assert.Equal("Style ARCH (arial.ttf, height 0): heights 9\" x2, 6\" x1", sections[StyleProfile.TextSection][0]);
        Assert.StartsWith("Style ARCH-48 (12 dimensions, current): DIMSCALE 48, DIMTSZ 0.09375", sections[StyleProfile.DimensionsSection][0], StringComparison.Ordinal);
        Assert.StartsWith("Wall thicknesses (parallel line spacing on A-WALL): 9\" (8 lines), 4-1/2\" (2 lines)", sections[StyleProfile.WallsSection][0], StringComparison.Ordinal);
        Assert.Equal("Door swings (90 degree arcs on A-DOOR): widths 3' x2, 2'6\" x1", sections[StyleProfile.OpeningsSection][0]);
        Assert.Contains("Windows on layer A-GLAZ", sections[StyleProfile.OpeningsSection]);
        var blocks = sections[StyleProfile.BlocksSection];
        Assert.Contains("Doors: DOOR-3FT x2 (A-DOOR)", blocks);
        Assert.Contains("Furniture and fixtures: BED-DOUBLE x3 (A-FURN)", blocks);
        Assert.Equal("Other blocks: NORTH x1 (A-ANNO)", blocks[^1]);
        Assert.DoesNotContain(blocks, b => b.Contains("*U12", StringComparison.Ordinal));
        Assert.Equal("ANSI31 scale 24 on A-HATCH x5", Assert.Single(sections[StyleProfile.HatchesSection]));
    }

    [Fact]
    public void An_empty_drawing_gives_no_sections()
    {
        var profile = StyleAnalyzer.Analyze(new StyleSample { DrawingName = "Drawing1.dwg" });
        Assert.Empty(profile.Sections);
    }
}
