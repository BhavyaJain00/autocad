using AcadCopilot.Core.Sheets;

namespace AcadCopilot.Core.Tests.Sheets;

/// <summary>Lining up the drawings of model space: rows, spacing, alignment, titles, sketch.</summary>
public sealed class ModelArrangementTests
{
    private static DrawingCluster Cluster(int number, string? title, SheetRect box, SheetRect? anchor = null) => new()
    {
        Number = number,
        Items = [],
        Box = box,
        Anchor = anchor ?? box,
        Title = title is null ? null : DrawingTitles.Classify(title),
    };

    // Ground floor (walls 0,0-360,480 with dimensions and title around), the first floor somewhere else, a front
    // elevation, a section, a site plan and an untitled drawing, all scattered.
    private static List<DrawingCluster> Scattered() =>
    [
        Cluster(1, "GROUND FLOOR PLAN", new SheetRect(-100, -150, 460, 480), new SheetRect(0, 0, 360, 480)),
        Cluster(2, "FIRST FLOOR PLAN", new SheetRect(1400, 550, 1960, 1180), new SheetRect(1500, 700, 1860, 1180)),
        Cluster(3, "SECTION A-A", new SheetRect(3000, 3000, 3500, 3400), new SheetRect(3000, 3050, 3500, 3400)),
        Cluster(4, "FRONT ELEVATION", new SheetRect(-2000, 0, -1600, 400), new SheetRect(-2000, 30, -1600, 400)),
        Cluster(5, "SITE PLAN", new SheetRect(5000, 5000, 5600, 5700)),
        Cluster(6, null, new SheetRect(-500, 4000, -300, 4100)),
        Cluster(7, "TERRACE PLAN", new SheetRect(800, -3000, 1360, -2370), new SheetRect(900, -2850, 1260, -2370)),
    ];

    [Fact]
    public void Floor_plans_share_one_row_with_equal_spacing_of_their_walls_and_the_ground_floor_stays()
    {
        var result = ModelArrangement.Arrange(Scattered(), inchesPerUnit: 1);
        var plans = result.Drawings.Where(d => d.Row == 1).ToList();

        Assert.Equal(360, result.Gap); // one plan width (30') is more than the 20' minimum
        Assert.Equal(["GROUND FLOOR PLAN", "FIRST FLOOR PLAN", "TERRACE PLAN"], plans.Select(p => p.Name));
        Assert.False(plans[0].Moves);

        // pitch = widest (box right - wall left) 460 + widest (wall left - box left) 100 + gap 360 = 920
        var clusters = Scattered().ToDictionary(c => c.Number);
        for (var i = 0; i < plans.Count; i++)
        {
            var cluster = clusters[plans[i].Number];
            var wallLeft = cluster.Anchor.MinX + plans[i].Dx;
            var wallBottom = cluster.Anchor.MinY + plans[i].Dy;
            Assert.Equal(i * 920.0, wallLeft, 6);
            Assert.Equal(0, wallBottom, 6);
        }
    }

    [Fact]
    public void Elevations_sections_and_site_plan_go_below_the_plans_and_the_rest_below_them()
    {
        var result = ModelArrangement.Arrange(Scattered(), inchesPerUnit: 1);
        var row1 = result.Drawings.Where(d => d.Row == 1).ToList();
        var row2 = result.Drawings.Where(d => d.Row == 2).ToList();
        var row3 = result.Drawings.Where(d => d.Row == 3).ToList();

        Assert.Equal(["FRONT ELEVATION", "SECTION A-A", "SITE PLAN"], row2.Select(d => d.Name));
        Assert.Equal(["DRAWING 6"], row3.Select(d => d.Name));
        Assert.True(row2.Max(d => d.To.MaxY) <= row1.Min(d => d.To.MinY) - result.Gap + 1e-6);
        Assert.True(row3.Max(d => d.To.MaxY) <= row2.Min(d => d.To.MinY) - result.Gap + 1e-6);

        // The front elevation starts under the ground floor's walls (projection) and every row keeps the gap.
        var front = row2[0];
        Assert.Equal(0, front.To.MinX, 6); // its anchor = box in X here
        for (var i = 1; i < row2.Count; i++)
        {
            Assert.Equal(row2[i - 1].To.MaxX + result.Gap, row2[i].To.MinX, 6);
        }

        // Ground lines (anchor bottoms) of the second row line up.
        var clusters = Scattered().ToDictionary(c => c.Number);
        var baselines = row2.Select(d => clusters[d.Number].Anchor.MinY + d.Dy).Distinct().ToList();
        Assert.Single(baselines);
    }

    [Fact]
    public void No_two_drawings_overlap_after_arranging()
    {
        var result = ModelArrangement.Arrange(Scattered(), inchesPerUnit: 1);
        var boxes = result.Drawings.Select(d => d.To).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                Assert.False(boxes[i].Overlaps(boxes[j]), $"{result.Drawings[i].Name} overlaps {result.Drawings[j].Name}");
            }
        }

        Assert.All(result.Drawings, d => Assert.Equal(d.From.Width, d.To.Width, 6));
    }

    [Fact]
    public void Gap_is_one_plan_width_with_a_20_foot_minimum_and_can_be_given()
    {
        Assert.Equal(240, ModelArrangement.AutomaticGap([Cluster(1, "FRONT ELEVATION", new SheetRect(0, 0, 100, 100))], 1));
        Assert.Equal(20, ModelArrangement.AutomaticGap([Cluster(1, "FRONT ELEVATION", new SheetRect(0, 0, 10, 10))], 12)); // feet drawing
        Assert.Equal(500, ModelArrangement.AutomaticGap([Cluster(1, "GROUND FLOOR PLAN", new SheetRect(0, 0, 500, 600))], 1));
        Assert.Equal(100, ModelArrangement.Arrange(Scattered(), 1, gap: 100).Gap);
    }

    [Fact]
    public void A_drawing_set_without_plans_keeps_its_first_drawing_in_place()
    {
        var clusters = new List<DrawingCluster>
        {
            Cluster(1, "REAR ELEVATION", new SheetRect(500, 500, 900, 900)),
            Cluster(2, "FRONT ELEVATION", new SheetRect(-800, 0, -400, 400)),
        };
        var result = ModelArrangement.Arrange(clusters, 1);
        Assert.Equal(["FRONT ELEVATION", "REAR ELEVATION"], result.Drawings.Select(d => d.Name));
        Assert.False(result.Drawings[0].Moves);
        Assert.Equal(-400 + 240, result.Drawings[1].To.MinX, 6);
        Assert.Equal(0, result.Drawings[1].To.MinY, 6);
    }

    [Fact]
    public void Planned_titles_reserve_room_in_the_arrangement()
    {
        var clusters = new List<DrawingCluster>
        {
            Cluster(1, "GROUND FLOOR PLAN", new SheetRect(0, 0, 360, 480)),
            Cluster(2, "FRONT ELEVATION", new SheetRect(0, -2000, 360, -1700)),
        };
        var title = ModelArrangement.PlanTitle(clusters[0].Box, "GROUND FLOOR PLAN", DrawingScales.QuarterInch, 1);
        var result = ModelArrangement.Arrange(clusters, 1, boxes: new Dictionary<int, SheetRect> { [1] = title.Box });
        var plan = result.Drawings[0];
        Assert.Equal(title.Box.MinY, plan.From.MinY, 6);
        Assert.True(result.Drawings[1].To.MaxY <= plan.To.MinY - result.Gap + 1e-6);
    }

    [Fact]
    public void Titles_are_placed_under_the_drawing_and_scaled_with_the_drawing_scale()
    {
        var box = new SheetRect(0, 0, 360, 480);
        var quarter = ModelArrangement.PlanTitle(box, "ground floor plan", DrawingScales.QuarterInch, 1);
        Assert.Equal("%%uGROUND FLOOR PLAN", quarter.Title);
        Assert.Equal("SCALE 1/4\" = 1'-0\"", quarter.ScaleLine);
        Assert.Equal(180, quarter.X);
        Assert.Equal(12, quarter.TitleHeight);
        Assert.Equal(6, quarter.ScaleHeight);
        Assert.Equal(-54, quarter.TitleY, 6); // 4'-0" below, centre of a 12" text
        Assert.Equal(-69, quarter.ScaleY, 6); // 1'-3" lower

        var eighth = ModelArrangement.PlanTitle(box, "SITE PLAN", DrawingScales.EighthInch, 1);
        Assert.Equal(24, eighth.TitleHeight);
        Assert.Equal(-108, eighth.TitleY, 6);
        Assert.True(eighth.Box.MaxY <= box.MinY);

        var feet = ModelArrangement.PlanTitle(new SheetRect(0, 0, 30, 40), "X PLAN", DrawingScales.QuarterInch, 12);
        Assert.Equal(1, feet.TitleHeight, 6);

        var under = ModelArrangement.PlanTitle(box, null, DrawingScales.QuarterInch, 1, new SheetRect(100, -96, 260, -84));
        Assert.Null(under.Title);
        Assert.Equal(180, under.X);
        Assert.Equal(-96 - 6 - 3, under.ScaleY, 6);
    }

    [Fact]
    public void The_sketch_shows_every_drawing_as_a_numbered_box()
    {
        var result = ModelArrangement.Arrange(Scattered(), 1);
        var lines = result.Sketch.Split('\n');
        Assert.True(lines.Length is >= 2 and <= 20);
        Assert.All(lines, l => Assert.True(l.Length <= 64));
        foreach (var number in new[] { "1", "2", "3", "4", "5", "6", "7" })
        {
            Assert.Contains(number, result.Sketch, StringComparison.Ordinal);
        }

        Assert.Contains('+', result.Sketch);
        Assert.Equal(string.Empty, ModelArrangement.Sketch([]));
    }
}
