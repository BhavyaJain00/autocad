using AcadCopilot.Core.Sheets;
using static AcadCopilot.Core.Tests.Sheets.SheetTestData;

namespace AcadCopilot.Core.Tests.Sheets;

/// <summary>Titles, scale lines and the separation of model space into drawings.</summary>
public sealed class DrawingClustersTests
{
    [Theory]
    [InlineData("%%uGROUND FLOOR PLAN", DrawingKind.FloorPlan, 0)]
    [InlineData("First Floor Plan", DrawingKind.FloorPlan, 1)]
    [InlineData("{\\fArial|b1;\\LSECOND FLOOR PLAN}", DrawingKind.FloorPlan, 2)]
    [InlineData("1ST FLOOR PLAN", DrawingKind.FloorPlan, 1)]
    [InlineData("G.F. PLAN", DrawingKind.FloorPlan, 0)]
    [InlineData("TERRACE PLAN", DrawingKind.FloorPlan, 100)]
    [InlineData("STILT FLOOR PLAN", DrawingKind.FloorPlan, -0.5)]
    [InlineData("FRONT ELEVATION", DrawingKind.Elevation, 0)]
    [InlineData("REAR ELEVATION", DrawingKind.Elevation, 1)]
    [InlineData("LEFT SIDE ELEVATION", DrawingKind.Elevation, 2)]
    [InlineData("SECTION A-A", DrawingKind.Section, 0)]
    [InlineData("SECTION B-B", DrawingKind.Section, 1)]
    [InlineData("SECTIONAL ELEVATION 2-2", DrawingKind.Section, 1)]
    [InlineData("%%USITE PLAN", DrawingKind.SitePlan, 0)]
    [InlineData("LOCATION PLAN", DrawingKind.SitePlan, 0)]
    [InlineData("DOOR & WINDOW SCHEDULE", DrawingKind.Schedule, 0)]
    [InlineData("AREA STATEMENT", DrawingKind.Schedule, 1)]
    [InlineData("GROUND FLOOR FURNITURE LAYOUT", DrawingKind.Layout, 0)]
    [InlineData("FIRST FLOOR ELECTRICAL LAYOUT", DrawingKind.Layout, 11)]
    public void Titles_are_recognised_with_kind_and_order(string text, DrawingKind kind, double order)
    {
        var info = DrawingTitles.Classify(text);
        Assert.NotNull(info);
        Assert.Equal(kind, info.Kind);
        Assert.Equal(order, info.Order);
    }

    [Theory]
    [InlineData("KITCHEN")]
    [InlineData("11'-0\" X 12'-0\"")]
    [InlineData("SCALE 1/4\" = 1'-0\"")]
    [InlineData("SEE SECTION A-A")]
    [InlineData("UP")]
    [InlineData("A")]
    [InlineData(null)]
    public void Other_texts_are_not_titles(string? text) => Assert.Null(DrawingTitles.Classify(text));

    [Fact]
    public void Clean_removes_control_codes_and_mtext_formatting()
    {
        Assert.Equal("GROUND FLOOR PLAN", DrawingTitles.Clean("%%uGround  Floor Plan"));
        Assert.Equal("FRONT ELEVATION", DrawingTitles.Clean("{\\H1.5x;\\C1;Front}\\PElevation"));
        Assert.Equal("Ø12 BAR", DrawingTitles.Clean("%%c12 bar"));
        Assert.Equal(string.Empty, DrawingTitles.Clean("   "));
    }

    [Theory]
    [InlineData("SCALE 1/4\" = 1'-0\"", true)]
    [InlineData("Scale: 1:100", true)]
    [InlineData("1/8\"=1'-0\"", true)]
    [InlineData("1:50", true)]
    [InlineData("GROUND FLOOR PLAN", false)]
    [InlineData("12'-0\"", false)]
    public void Scale_lines_are_recognised(string text, bool expected) => Assert.Equal(expected, DrawingTitles.IsScaleText(text));

    [Fact]
    public void Title_case_and_standard_floor_titles()
    {
        Assert.Equal("Ground Floor Plan", DrawingTitles.TitleCase("%%uGROUND FLOOR PLAN"));
        Assert.Equal("Section A-A", DrawingTitles.TitleCase("SECTION A-A"));
        Assert.Equal("Front & Rear Elevations", DrawingTitles.TitleCase("FRONT & REAR ELEVATIONS"));
        Assert.Equal("FIRST FLOOR PLAN", DrawingTitles.FloorPlanTitle(1));
        Assert.Equal("TERRACE PLAN", DrawingTitles.FloorPlanTitle(DrawingTitles.TerraceLevel));
        Assert.Equal("GROUND FLOOR PLAN", DrawingTitles.FloorPlanTitle(0));
    }

    [Fact]
    public void Drawings_separated_by_empty_space_are_found_with_title_scale_and_wall_anchor()
    {
        var items = Plan(0, 0, "GROUND FLOOR PLAN");
        items.AddRange(Plan(1000, 0, "FIRST FLOOR PLAN"));
        var result = DrawingClusters.Find(items, 120);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Clusters.Count);
        var ground = result.Clusters[0];
        Assert.Equal(1, ground.Number);
        Assert.Equal("GROUND FLOOR PLAN", ground.Name);
        Assert.Equal(DrawingKind.FloorPlan, ground.Kind);
        Assert.Equal(new SheetRect(0, 0, 360, 480), ground.Anchor);
        Assert.True(ground.Box.MinY < -100, "the title is part of the drawing");
        Assert.NotNull(ground.TitleItem);
        Assert.NotNull(ground.ScaleItem);
        Assert.Equal("SCALE 1/4\" = 1'-0\"", items[ground.ScaleItem.Value].Text);
        Assert.Equal(1, result.Clusters[1].Order);
        Assert.Equal(items.Count, result.Clusters.Sum(c => c.Items.Count));
    }

    [Fact]
    public void A_gap_wider_than_the_cluster_gap_separates_and_a_narrower_one_joins()
    {
        var items = new List<ClusterItem> { Line(0, 0, 100, 0), Line(150, 0, 250, 0), Line(400, 0, 500, 0) };
        var result = DrawingClusters.Find(items, 100);
        Assert.Equal(2, result.Clusters.Count);
        Assert.Equal(new SheetRect(0, 0, 250, 0), result.Clusters[0].Box);
        Assert.Throws<ArgumentOutOfRangeException>(() => DrawingClusters.Find(items, 0));
    }

    [Fact]
    public void Two_titled_drawings_too_close_together_are_split_with_a_smaller_gap()
    {
        var items = Plan(0, 0, "GROUND FLOOR PLAN");
        items.AddRange(Plan(460, 0, "FIRST FLOOR PLAN")); // 100" apart: closer than the 120" gap
        var result = DrawingClusters.Find(items, 120);

        Assert.Equal(2, result.Clusters.Count);
        Assert.Equal(["GROUND FLOOR PLAN", "FIRST FLOOR PLAN"], result.Clusters.Select(c => c.Name));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Overlapping_titled_drawings_are_reported()
    {
        var items = Plan(0, 0, "GROUND FLOOR PLAN");
        items.AddRange(Plan(100, 0, "FIRST FLOOR PLAN"));
        var result = DrawingClusters.Find(items, 120);
        Assert.Single(result.Clusters);
        Assert.Contains("touch or overlap", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_untitled_group_next_to_a_drawing_joins_it_without_moving_its_anchor()
    {
        var items = Plan(0, 0, "GROUND FLOOR PLAN");
        items.Add(Line(510, 200, 540, 230)); // north arrow 150" right of the walls
        items.Add(Text(525, 240, 6, "N"));
        var result = DrawingClusters.Find(items, 120);

        var plan = Assert.Single(result.Clusters);
        Assert.Equal(540, plan.Box.MaxX, 6);
        Assert.Equal(new SheetRect(0, 0, 360, 480), plan.Anchor);
        Assert.Equal("GROUND FLOOR PLAN", plan.Name);
    }

    [Fact]
    public void Drawings_are_numbered_in_reading_order()
    {
        var items = Plan(1000, 0, "FIRST FLOOR PLAN");
        items.AddRange(Plan(0, 0, "GROUND FLOOR PLAN"));
        items.Add(Line(0, -1500, 400, -1100));
        var result = DrawingClusters.Find(items, 120);

        Assert.Equal(["GROUND FLOOR PLAN", "FIRST FLOOR PLAN", "DRAWING 3"], result.Clusters.Select(c => c.Name));
        Assert.Equal([1, 2, 3], result.Clusters.Select(c => c.Number));
        Assert.Equal(DrawingKind.Other, result.Clusters[2].Kind);
    }

    [Fact]
    public void Labels_name_untitled_drawings_and_unmatched_labels_are_returned()
    {
        var items = Plan(0, 0, "GROUND FLOOR PLAN");
        items.Add(Line(0, -1500, 400, -1100));
        var found = DrawingClusters.Find(items, 120);
        var (clusters, unmatched) = DrawingClusters.ApplyLabels(
            found.Clusters,
            [new DrawingLabel(200, -1300, "front elevation"), new DrawingLabel(9000, 9000, "SITE PLAN"), new DrawingLabel(200, -1300, "Puja room detail")],
            120);

        Assert.Equal("PUJA ROOM DETAIL", clusters[1].Name);
        Assert.Equal(DrawingKind.Other, clusters[1].Kind);
        var relabelled = DrawingClusters.ApplyLabels(found.Clusters, [new DrawingLabel(200, -1300, "front elevation")], 120).Clusters;
        Assert.Equal(DrawingKind.Elevation, relabelled[1].Kind);
        Assert.Equal("FRONT ELEVATION", relabelled[1].Name);
        Assert.Equal("SITE PLAN", Assert.Single(unmatched).Title);
    }

    [Fact]
    public void Room_names_and_small_texts_do_not_become_titles()
    {
        var items = Plan(0, 0, null);
        items.Add(Text(270, 400, 8, "LIVING PLAN AREA")); // a plan-like word in a small text while the title is bigger
        items.Add(Text(180, -90, 12, "%%uGROUND FLOOR PLAN"));
        var result = DrawingClusters.Find(items, 120);
        var cluster = Assert.Single(result.Clusters);
        Assert.Equal("GROUND FLOOR PLAN", cluster.Name);
        Assert.Empty(result.Warnings);
    }
}
