using System.Text.Json.Nodes;
using AcadCopilot.Core.Presentation;
using AcadCopilot.Core.Tools;

namespace AcadCopilot.Core.Tests.Presentation;

public sealed class PresentationTests
{
    [Fact]
    public void Colours_parse_format_and_mix()
    {
        Assert.True(Rgb.TryParse(" 243, 233,212 ", out var cream));
        Assert.Equal(new Rgb(243, 233, 212), cream);
        Assert.Equal("243,233,212", cream.ToString());
        Assert.True(Rgb.TryParse("#F3E9D4", out var hex));
        Assert.Equal(cream, hex);
        Assert.False(Rgb.TryParse("256,0,0", out _));
        Assert.False(Rgb.TryParse("red", out _));
        Assert.False(Rgb.TryParse("1,2", out _));
        Assert.Equal(new Rgb(255, 255, 255), new Rgb(0, 0, 0).Lighter(1));
        Assert.Equal(new Rgb(100, 50, 0), new Rgb(200, 100, 0).Darker(0.5));
        Assert.Equal(new Rgb(150, 150, 150), new Rgb(100, 100, 100).Mix(new Rgb(200, 200, 200), 0.5));
    }

    [Fact]
    public void The_palette_has_unique_finishes_on_presentation_layers()
    {
        Assert.Equal(PresentationFinishes.All.Count, PresentationFinishes.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PresentationFinishes.All, f => Assert.StartsWith(PresentationFinishes.LayerPrefix, f.Layer, StringComparison.Ordinal));
        Assert.All(PresentationFinishes.All.Where(f => f.Kind == FillKind.Pattern), f => Assert.False(string.IsNullOrEmpty(f.Pattern)));
        Assert.All(PresentationFinishes.All.Where(f => f.Kind == FillKind.Gradient), f => Assert.NotNull(f.Color2));
        foreach (var name in new[] { "wall_paint", "glass", "stone_cladding", "slab_band", "roof", "sky", "ground", "shadow" })
        {
            Assert.NotNull(PresentationFinishes.Find(name));
        }

        Assert.True(PresentationFinishes.IsPresentationLayer("a-elev-pres-wall"));
        Assert.False(PresentationFinishes.IsPresentationLayer("A-ELEV"));
        Assert.Equal(0, PresentationFinishes.RankOfLayer("A-ELEV-PRES-SKY"));
        Assert.Equal(3, PresentationFinishes.RankOfLayer("A-ELEV-PRES-SHAD"));
        Assert.Equal(1, PresentationFinishes.RankOfLayer("A-ELEV-PRES-WALL"));
        Assert.True(PresentationFinishes.Find("shadow")!.TransparencyPercent > 0);
        Assert.Equal(PresentationFinishes.Layers.Count, PresentationFinishes.Layers.Select(l => l.Layer).Distinct().Count());
    }

    [Fact]
    public void Overrides_resolve_colours_gradients_and_angles()
    {
        var wall = PresentationFinishes.Find("wall_paint")!;
        var red = new Rgb(200, 30, 30);
        Assert.Equal(red, PresentationFinishes.Resolve(wall, red, null, null, null).Color);

        var gradient = PresentationFinishes.Resolve(wall, null, null, true, 90);
        Assert.Equal(FillKind.Gradient, gradient.Kind);
        Assert.Equal(wall.Color.Lighter(0.5), gradient.Color2);
        Assert.Equal(90, gradient.GradientAngle);

        var glass = PresentationFinishes.Find("glass")!;
        Assert.Equal(FillKind.Solid, PresentationFinishes.Resolve(glass, null, null, false, null).Kind);
        var recoloured = PresentationFinishes.Resolve(glass, red, null, null, null);
        Assert.Equal(red.Lighter(0.55), recoloured.Color2);

        var stone = PresentationFinishes.Find("stone_cladding")!;
        Assert.Equal(red.Darker(0.3), PresentationFinishes.Resolve(stone, red, null, null, null).Color2);
        Assert.Equal(new Rgb(1, 2, 3), PresentationFinishes.Resolve(stone, red, new Rgb(1, 2, 3), null, null).Color2);
    }

    [Fact]
    public void Backdrop_puts_the_sky_above_and_the_ground_below_the_ground_line()
    {
        var elevation = new PlanRect(0, 0, 400, 300);
        var (sky, ground) = PresentationGeometry.Backdrop(elevation, 18);
        Assert.Equal(18, sky.MinY);
        Assert.Equal(18, ground.MaxY);
        Assert.True(sky.MaxY > 300);
        Assert.Equal(-48, sky.MinX, 6);
        Assert.Equal(448, sky.MaxX, 6);
        Assert.Equal(18 - 18, ground.MinY, 6); // 6% of 300 = 18 deep
        Assert.Throws<ArgumentException>(() => PresentationGeometry.Backdrop(new PlanRect(0, 0, 0, 10), 0));
    }

    [Fact]
    public void Shadows_are_45_degree_parallelograms_clipped_at_the_ground()
    {
        var chajja = PlanRect.FromCorners(120, 96, 60, 100);
        var below = PresentationGeometry.Shadow(chajja, 18, ShadowKind.Below);
        Assert.Equal(new[] { new PlanPoint(60, 96), new PlanPoint(120, 96), new PlanPoint(138, 78), new PlanPoint(78, 78) }, below);
        Assert.Equal(60 * 18, PresentationGeometry.Area(below), 6);

        var fromRight = PresentationGeometry.Shadow(chajja, 18, ShadowKind.Below, sunFromLeft: false);
        Assert.Equal(42, fromRight[3].X, 6);

        var column = new PlanRect(200, 0, 212, 120);
        var side = PresentationGeometry.Shadow(column, 12, ShadowKind.Side, true, groundY: 0);
        Assert.Equal(212, side[0].X);
        Assert.All(side, p => Assert.True(p.Y >= 0));
        Assert.Equal((12 * 120) - (12 * 12 / 2.0), PresentationGeometry.Area(side), 6);

        var clipped = PresentationGeometry.Shadow(PlanRect.FromCorners(0, 10, 50, 12), 20, ShadowKind.Below, true, groundY: 0);
        Assert.All(clipped, p => Assert.True(p.Y >= -1e-9));
        Assert.Equal(50 * 10, PresentationGeometry.Area(clipped), 6);
        Assert.Throws<ArgumentException>(() => PresentationGeometry.Shadow(chajja, 0, ShadowKind.Below));
    }

    [Fact]
    public void Composite_puts_fills_on_white_paper_with_dark_ink_lines()
    {
        // Paper: nothing drawn.
        Assert.Equal(((byte)255, (byte)255, (byte)255), PresentationPixels.CompositePixel(0, 0, 0, 0, 0, 0));

        // A cream fill keeps its exact colour; a light grey (neutral) fill too.
        Assert.Equal(((byte)212, (byte)233, (byte)243), PresentationPixels.CompositePixel(212, 233, 243, 0, 0, 0));
        Assert.Equal(((byte)236, (byte)236, (byte)236), PresentationPixels.CompositePixel(236, 236, 236, 0, 0, 0));

        // A white (colour 7) line becomes black ink, over paper and over a fill.
        Assert.Equal(((byte)0, (byte)0, (byte)0), PresentationPixels.CompositePixel(0, 0, 0, 255, 255, 255));
        Assert.Equal(((byte)0, (byte)0, (byte)0), PresentationPixels.CompositePixel(212, 233, 243, 255, 255, 255));

        // Half coverage (anti-aliased edge) blends halfway.
        var (b, g, r) = PresentationPixels.CompositePixel(0, 0, 0, 128, 128, 128);
        Assert.InRange(r, 125, 129);
        Assert.Equal(b, r);

        // A pure red line keeps its hue, darkened: value 0.4.
        Assert.Equal(((byte)0, (byte)0, (byte)102), PresentationPixels.CompositePixel(0, 0, 0, 0, 0, 255));

        var fills = new byte[] { 0, 0, 0, 212, 233, 243, 0, 0, 0, 0 };
        var lines = new byte[] { 255, 255, 255, 0, 0, 0, 0, 0, 0, 0 };
        var output = new byte[10];
        PresentationPixels.Composite(fills, lines, output, 2, 1, 10, 3);
        Assert.Equal(new byte[] { 0, 0, 0, 212, 233, 243, 0, 0, 0, 0 }, output);
        Assert.Throws<ArgumentException>(() => PresentationPixels.Composite(fills, lines, new byte[4], 2, 1, 10, 3));
    }

    [Fact]
    public void A_single_black_render_keeps_colours_and_inverts_neutrals()
    {
        var pixels = new byte[] { 0, 0, 0, 255, 255, 255, 60, 120, 200, 0 };
        PresentationPixels.PaperFromBlackRender(pixels, 3, 1, 10, 3);
        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0, 60, 120, 200, 0 }, pixels);
    }

    [Fact]
    public void Export_names_are_safe_and_never_overwrite()
    {
        Assert.Equal("Front elevation", ExportFiles.SafeBaseName("Front elevation", "x.dwg"));
        Assert.Equal("House 30x40-1", ExportFiles.SafeBaseName(null, @"C:\Work\House 30x40#1.dwg"));
        Assert.Equal("a-b-c", ExportFiles.SafeBaseName("a/b\\c.png", null));
        Assert.Equal("drawing", ExportFiles.SafeBaseName("  ..  ", null));
        Assert.Equal("drawing", ExportFiles.SafeBaseName(null, null));
        Assert.Equal(ExportFiles.MaxBaseNameLength, ExportFiles.SafeBaseName(new string('x', 200), null).Length);
        Assert.DoesNotContain("..", ExportFiles.SafeBaseName("..\\..\\evil", null), StringComparison.Ordinal);

        var now = new DateTimeOffset(2026, 11, 19, 17, 5, 9, TimeSpan.FromHours(5.5));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("C:\\out", "Front_20261119-170509.png") };
        Assert.Equal(Path.Combine("C:\\out", "Front_20261119-170509-2.png"), ExportFiles.NewPath("C:\\out", "Front", now, taken.Contains));
        Assert.Equal(Path.Combine("C:\\out", "Front_20261119-170509.png"), ExportFiles.NewPath("C:\\out", "Front", now, _ => false));

        var previous = Environment.GetEnvironmentVariable(ExportFiles.FolderEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExportFiles.FolderEnvironmentVariable, null);
            Assert.EndsWith(ExportFiles.FolderName, ExportFiles.Folder, StringComparison.Ordinal);
            Environment.SetEnvironmentVariable(ExportFiles.FolderEnvironmentVariable, @"D:\Exports");
            Assert.Equal(@"D:\Exports", ExportFiles.Folder);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFiles.FolderEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Tool_definitions_lint_and_validate_typical_arguments()
    {
        var fill = PresentationToolDefinitions.FillRegionsDefinition();
        var backdrop = PresentationToolDefinitions.AddBackdropDefinition();
        var export = PresentationToolDefinitions.ExportViewPngDefinition();
        Assert.Empty(fill.Lint());
        Assert.Empty(backdrop.Lint());
        Assert.Empty(export.Lint());

        var fills = new JsonObject
        {
            ["fills"] = new JsonArray(
                new JsonObject { ["finish"] = "wall_paint", ["point"] = new JsonArray(120, 60) },
                new JsonObject { ["finish"] = "glass", ["handle"] = "2A3F", ["color"] = "90,140,180", ["color2"] = "#C8E1F0", ["angle"] = 45 },
                new JsonObject { ["finish"] = "slab_band", ["point"] = new JsonArray(10, 20), ["gradient"] = false }),
            ["islands"] = true,
        };
        Assert.Empty(JsonSchemaValidator.Validate(fill.InputSchema, fills));
        Assert.NotEmpty(JsonSchemaValidator.Validate(fill.InputSchema, new JsonObject { ["fills"] = new JsonArray(new JsonObject { ["finish"] = "chrome", ["point"] = new JsonArray(1, 2) }) }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(fill.InputSchema, new JsonObject { ["fills"] = new JsonArray(new JsonObject { ["finish"] = "glass", ["color"] = "blue" }) }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(fill.InputSchema, new JsonObject { ["fills"] = new JsonArray() }));

        Assert.Empty(JsonSchemaValidator.Validate(backdrop.InputSchema, new JsonObject
        {
            ["window"] = new JsonArray(0, 0, 480, 360),
            ["ground_y"] = 0,
            ["ground"] = "lawn",
            ["shadows"] = new JsonArray(new JsonObject { ["box"] = new JsonArray(60, 96, 120, 100), ["depth"] = 18 }, new JsonObject { ["box"] = new JsonArray(0, 0, 12, 120), ["depth"] = 9, ["kind"] = "side" }),
            ["sun"] = "right",
        }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(backdrop.InputSchema, new JsonObject { ["window"] = new JsonArray(0, 0, 1, 1) }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(backdrop.InputSchema, new JsonObject { ["window"] = new JsonArray(0, 0, 1, 1), ["ground_y"] = 0, ["shadows"] = new JsonArray(new JsonObject { ["box"] = new JsonArray(0, 0, 1, 1), ["depth"] = 0 }) }));

        Assert.Empty(JsonSchemaValidator.Validate(export.InputSchema, new JsonObject()));
        Assert.Empty(JsonSchemaValidator.Validate(export.InputSchema, new JsonObject { ["area"] = "window", ["window"] = new JsonArray(0, 0, 10, 10), ["width"] = 3000, ["name"] = "Front elevation", ["background"] = "white" }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(export.InputSchema, new JsonObject { ["width"] = 10000 }));
        Assert.NotEmpty(JsonSchemaValidator.Validate(export.InputSchema, new JsonObject { ["path"] = "C:\\x.png" }));
    }
}
