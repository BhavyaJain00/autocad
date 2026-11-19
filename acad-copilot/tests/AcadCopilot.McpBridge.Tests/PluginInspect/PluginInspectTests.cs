using System.Text.Json.Nodes;
using AcadCopilot.Core.Tools;
using AcadCopilot.Plugin.Inspect;

namespace AcadCopilot.McpBridge.Tests.PluginInspect;

/// <summary>
/// Unit tests of the plug-in's pure inspection helpers (src/AcadCopilot.Plugin/Inspect, no AutoCAD types), compiled
/// into this test project as linked files because the plug-in itself only runs inside AutoCAD.
/// </summary>
public sealed class InspectFilterTests
{
    [Theory]
    [InlineData("A-WALL*", "A-WALL", true)]
    [InlineData("A-WALL*", "a-wall-ext", true)]
    [InlineData("A-WALL*", "A-DOOR", false)]
    [InlineData("A-WALL*,A-DOOR", "A-DOOR", true)]
    [InlineData("A-WALL*, A-DOOR", "a-door", true)]
    [InlineData("LEVEL#", "LEVEL2", true)]
    [InlineData("LEVEL#", "LEVELX", false)]
    [InlineData("@-*", "A-X", true)]
    [InlineData("@-*", "1-X", false)]
    [InlineData("?", "0", true)]
    [InlineData("?", "00", false)]
    [InlineData("~A-*", "0", true)]
    [InlineData("~A-*", "A-WALL", false)]
    [InlineData("WALL.(2)", "wall.(2)", true)]
    [InlineData("LINE,LWPOLYLINE", "LWPOLYLINE", true)]
    [InlineData("LINE,LWPOLYLINE", "POLYLINE", false)]
    public void Name_patterns_work_like_autocad(string pattern, string name, bool expected) =>
        Assert.Equal(expected, NamePattern.Parse(pattern)!.IsMatch(name));

    [Fact]
    public void Blank_patterns_match_everything()
    {
        Assert.Null(NamePattern.Parse(null));
        Assert.Null(NamePattern.Parse("  "));
        Assert.Equal("A*", NamePattern.Parse(" A* ")!.Text);
    }

    [Fact]
    public void Plan_boxes_cross_contain_and_frame()
    {
        var window = PlanBox.FromCorners(100, 50, 0, 0);
        Assert.Equal(new PlanBox(0, 0, 100, 50), window);
        Assert.True(window.Intersects(new PlanBox(90, 40, 120, 60)));
        Assert.True(window.Intersects(new PlanBox(100, 50, 110, 60)));
        Assert.False(window.Intersects(new PlanBox(101, 0, 110, 10)));
        Assert.True(window.Contains(new PlanBox(10, 10, 20, 20)));
        Assert.False(window.Contains(new PlanBox(90, 40, 120, 60)));
        Assert.Equal(new PlanBox(-5, 0, 100, 60), window.Union(new PlanBox(-5, 10, 10, 60)));
        Assert.Equal((50.0, 25.0), (window.CenterX, window.CenterY));
        Assert.Equal(new PlanBox(-10, -10, 110, 60), window.Expand(0.1, 1));
        Assert.True(window.IsValid);
        Assert.False(new PlanBox(0, 0, double.NaN, 1).IsValid);
        Assert.False(new PlanBox(1e20, 1e20, -1e20, -1e20).IsValid);
    }
}

public sealed class CaptureMathTests
{
    [Theory]
    [InlineData(1200.0, 800.0, 1200, 1200, 1200, 800)]
    [InlineData(400.0, 800.0, 1200, 1200, 600, 1200)]
    [InlineData(3000.0, 100.0, 1600, 1200, 1600, 64)]
    [InlineData(0.0, 0.0, 1200, 1200, 1200, 800)]
    [InlineData(double.NaN, 5.0, 900, 1200, 900, 600)]
    public void Fit_size_keeps_proportions_within_limits(double w, double h, int maxW, int maxH, int expectedW, int expectedH) =>
        Assert.Equal((expectedW, expectedH), CaptureMath.FitSize(w, h, maxW, maxH));

    [Fact]
    public void Frame_for_picture_matches_the_picture_proportions_and_keeps_the_centre()
    {
        var framed = CaptureMath.FrameForPicture(new PlanBox(0, 0, 360, 480), 1200, 800, marginFraction: 0);
        Assert.Equal(180, framed.CenterX, 6);
        Assert.Equal(240, framed.CenterY, 6);
        Assert.Equal(480, framed.Height, 6);
        Assert.Equal(720, framed.Width, 6);

        var wide = CaptureMath.FrameForPicture(new PlanBox(0, 0, 1000, 10), 1200, 800);
        Assert.Equal(2.0 / 3.0, wide.Height / wide.Width, 6);
        Assert.True(wide.Width > 1000);

        var point = CaptureMath.FrameForPicture(new PlanBox(5, 5, 5, 5), 100, 100);
        Assert.True(point.Width > 0 && point.Height > 0);
    }

    [Fact]
    public void Uniform_pictures_are_detected_including_row_padding()
    {
        // 3 x 2 BGR with stride 12 (3 padding bytes per row that must be ignored).
        var pixels = new byte[24];
        Array.Fill(pixels, (byte)7);
        pixels[9] = 99;
        pixels[21] = 42;
        Assert.True(CaptureMath.IsUniform(pixels, 3, 2, 12, 3));
        pixels[4] = 8;
        Assert.False(CaptureMath.IsUniform(pixels, 3, 2, 12, 3));

        var bgra = new byte[2 * 2 * 4];
        Assert.True(CaptureMath.IsUniform(bgra, 2, 2, 8, 4));
        bgra[15] = 255;
        Assert.False(CaptureMath.IsUniform(bgra, 2, 2, 8, 4));

        Assert.Throws<ArgumentException>(() => CaptureMath.IsUniform(new byte[5], 2, 2, 8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureMath.IsUniform(new byte[16], 2, 2, 8, 2));
    }

    [Fact]
    public void Ink_on_white_turns_a_black_render_into_a_plot_like_picture()
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), CaptureMath.InkOnWhite(0, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)0), CaptureMath.InkOnWhite(255, 255, 255));
        Assert.Equal(((byte)0, (byte)0, (byte)102), CaptureMath.InkOnWhite(0, 0, 255));
        Assert.Equal(((byte)0, (byte)102, (byte)102), CaptureMath.InkOnWhite(0, 255, 255));
        var grey = CaptureMath.InkOnWhite(128, 128, 128);
        Assert.InRange(grey.R, 125, 129);
        Assert.Equal(grey.R, grey.G);

        // In place over a BGRA buffer: alpha untouched.
        var pixels = new byte[] { 0, 0, 0, 255, 255, 255, 255, 128 };
        CaptureMath.InkOnWhite(pixels, 2, 1, 8, 4);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 128 }, pixels);
    }
}

public sealed class InspectToolDefinitionTests
{
    [Fact]
    public void Definitions_are_strict_and_lint_clean()
    {
        var all = InspectToolDefinitions.All();
        Assert.Equal(["capture_view", "list_layers", "list_entities", "get_entity", "get_sysvars"], all.Select(d => d.Name));
        foreach (var definition in all)
        {
            Assert.Empty(definition.Lint());
            Assert.Equal(ToolRisk.Read, definition.Risk);
            Assert.True(definition.Description.Length < 1200, definition.Name);
        }
    }

    [Theory]
    [InlineData("capture_view", "{}")]
    [InlineData("capture_view", """{"area":"window","window":[0,0,360,480],"width":1600,"background":"dark","method":"offscreen"}""")]
    [InlineData("capture_view", """{"area":"extents","width":400}""")]
    [InlineData("list_layers", """{"name":"A-*","counts":false}""")]
    [InlineData("list_entities", """{"layer":"A-WALL*","type":"LINE,LWPOLYLINE","window":[0,0,100,100],"inside":true,"space":"current","limit":200,"offset":50}""")]
    [InlineData("get_entity", """{"handle":"2A3"}""")]
    [InlineData("get_entity", """{"handles":["2A3","ff"]}""")]
    [InlineData("get_sysvars", """{"names":["LUNITS","INSUNITS","DIMSCALE"]}""")]
    public void Typical_arguments_are_valid(string tool, string arguments) =>
        Assert.Empty(JsonSchemaValidator.Validate(Find(tool).InputSchema, JsonNode.Parse(arguments)!.AsObject()));

    [Theory]
    [InlineData("capture_view", """{"width":4000}""")]
    [InlineData("capture_view", """{"window":[0,0,1]}""")]
    [InlineData("capture_view", """{"area":"layout"}""")]
    [InlineData("capture_view", """{"zoom":2}""")]
    [InlineData("list_entities", """{"limit":0}""")]
    [InlineData("list_entities", """{"limit":500}""")]
    [InlineData("get_entity", """{"handle":"xyz"}""")]
    [InlineData("get_entity", """{"handles":[]}""")]
    [InlineData("get_sysvars", "{}")]
    [InlineData("get_sysvars", """{"names":["(command)"]}""")]
    public void Bad_arguments_are_rejected(string tool, string arguments) =>
        Assert.NotEmpty(JsonSchemaValidator.Validate(Find(tool).InputSchema, JsonNode.Parse(arguments)!.AsObject()));

    private static ToolDefinition Find(string name) => InspectToolDefinitions.All().Single(d => d.Name == name);
}
