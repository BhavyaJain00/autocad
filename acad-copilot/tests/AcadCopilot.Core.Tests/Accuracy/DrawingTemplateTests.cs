using System.Text.Json.Nodes;
using AcadCopilot.Core.Accuracy;

namespace AcadCopilot.Core.Tests.Accuracy;

public sealed class DrawingTemplateTests
{
    private static string BundleTemplate([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AcadCopilot.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "deploy", "AcadCopilot.bundle", "Contents", "Templates", DrawingTemplate.FileName);
    }

    [Fact]
    public void Default_HasTheStandardLayersWithColoursLinetypesAndLineweights()
    {
        var t = DrawingTemplate.Default;
        string[] required = ["A-WALL", "A-DOOR", "A-GLAZ", "A-FURN", "A-STRS", "A-ANNO-TEXT", "A-ANNO-DIMS", "A-SITE", "A-GRID", "A-HATCH", "A-TTLB"];
        foreach (var name in required)
        {
            Assert.Contains(t.Layers, l => l.Name == name);
        }

        Assert.Equal(new TemplateLayer("A-WALL", 7, "Continuous", 0.50, "Walls"), t.Layers.First(l => l.Name == "A-WALL"));
        Assert.Equal("CENTER", t.Layers.First(l => l.Name == "A-GRID").Linetype);
        Assert.Contains("CENTER", t.Linetypes);
        Assert.Empty(t.Problems());
        Assert.Equal(t.Layers.Count, t.Layers.Select(l => l.Name).Distinct().Count());
    }

    [Fact]
    public void Default_UnitsAreArchitecturalInches()
    {
        var u = DrawingTemplate.Default.Units;
        Assert.Equal(4, u["LUNITS"]);
        Assert.Equal(1, u["INSUNITS"]);
        Assert.Equal(0, u["MEASUREMENT"]);
        Assert.Equal(4, u["LUPREC"]);
    }

    [Fact]
    public void Default_DimensionStylesAreArch48AndArch96WithTicksAndTextAbove()
    {
        var styles = DrawingTemplate.Default.DimStyles;
        var a48 = styles.Single(s => s.Name == "ARCH-48");
        var a96 = styles.Single(s => s.Name == "ARCH-96");
        Assert.Equal(48, a48.Variables["DIMSCALE"]);
        Assert.Equal(96, a96.Variables["DIMSCALE"]);
        foreach (var s in new[] { a48, a96 })
        {
            Assert.Equal(4, s.Variables["DIMLUNIT"]);
            Assert.True(s.Variables["DIMTSZ"] > 0, "ticks");
            Assert.Equal(1, s.Variables["DIMTAD"]);
            Assert.Equal(0, s.Variables["DIMTIH"]);
            Assert.Equal("ARCH", s.TextStyle);
        }

        Assert.Equal("ARCH-48", DrawingTemplate.DimStyleFor("1/4"));
        Assert.Equal("ARCH-96", DrawingTemplate.DimStyleFor("1/8"));
        Assert.Equal(96, DrawingTemplate.FactorFor("1/8"));
    }

    [Fact]
    public void Default_TextStyleAndHeightsForBothScales()
    {
        var t = DrawingTemplate.Default;
        Assert.Equal(new TemplateTextStyle("ARCH", "arial.ttf", 0, 1), t.TextStyles.Single());
        var names = t.TextHeights.First(h => h.Use == "room names");
        Assert.Equal((9.0, 18.0), (names.QuarterInch, names.EighthInch));
        foreach (var h in t.TextHeights)
        {
            Assert.Equal(h.Paper * 48, h.QuarterInch, 6);
            Assert.Equal(h.Paper * 96, h.EighthInch, 6);
        }
    }

    [Fact]
    public void Default_TitleBlockIsAnA3SheetWithAttributes()
    {
        var tb = DrawingTemplate.Default.TitleBlock!;
        Assert.Equal("A-TTLB-A3", tb.Name);
        Assert.Equal(16.535, tb.Width, 3);
        Assert.Equal(11.693, tb.Height, 3);
        Assert.Equal(["PROJECT", "TITLE", "SCALE", "DATE", "DRAWN", "SHEET", "NOTE"], tb.Attributes.Select(a => a.Tag));
        Assert.All(tb.Lines, l => Assert.True(l.X1 >= 0 && l.X2 <= tb.Width + 1e-9 && l.Y1 >= 0 && l.Y2 <= tb.Height + 1e-9));
        Assert.Equal(tb.Attributes.Count, tb.Attributes.Select(a => a.Tag).Distinct().Count());
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var json = DrawingTemplate.Default.ToJson();
        var back = DrawingTemplate.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject());
        Assert.Equal(DrawingTemplate.Default.ToJsonText(), back.ToJsonText());
        Assert.Equal(DrawingTemplate.Default.Layers, back.Layers);
    }

    [Fact]
    public void Json_WithBadDataIsRejected()
    {
        var json = DrawingTemplate.Default.ToJson();
        json["layers"]![0]!["lineweight"] = 0.42;
        Assert.Throws<FormatException>(() => DrawingTemplate.FromJson(json));

        var bad = DrawingTemplate.Default.ToJson();
        bad["dimStyles"]![0]!["variables"]!["DIMFOO"] = 1;
        var ex = Assert.Throws<FormatException>(() => DrawingTemplate.FromJson(bad));
        Assert.Contains("DIMFOO", ex.Message, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DrawingTemplate.FromJson(new JsonObject { ["layers"] = new JsonArray(new JsonObject { ["name"] = "X" }) }));
    }

    [Fact]
    public void BundleTemplateFile_EqualsTheBuiltInData()
    {
        var file = BundleTemplate();
        Assert.True(File.Exists(file), file);
        Assert.Equal(DrawingTemplate.Default.ToJsonText(), File.ReadAllText(file).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Settings_RememberAndForgetTheUsersTemplate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AcadCopilotTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AccuracySettings(Path.Combine(dir, "accuracy.json"));
            Assert.Null(settings.TemplatePath);
            settings.SetTemplatePath(@"C:\Templates\Office.dwt");
            Assert.Equal(@"C:\Templates\Office.dwt", new AccuracySettings(settings.File).TemplatePath);
            settings.SetTemplatePath(null);
            Assert.Null(settings.TemplatePath);

            var dwt = Path.Combine(dir, "Office.dwt");
            File.WriteAllText(dwt, "x");
            Assert.Null(AccuracySettings.CheckTemplatePath(dwt));
            Assert.Contains(".dwt or .dwg", AccuracySettings.CheckTemplatePath("Office.dwt"), StringComparison.Ordinal);
            Assert.Contains(".dwt or .dwg", AccuracySettings.CheckTemplatePath(Path.Combine(dir, "x.dxf")), StringComparison.Ordinal);
            Assert.Contains("does not exist", AccuracySettings.CheckTemplatePath(Path.Combine(dir, "missing.dwg")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
