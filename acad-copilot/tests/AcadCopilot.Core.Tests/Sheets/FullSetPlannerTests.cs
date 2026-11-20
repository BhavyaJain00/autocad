using System.Text.Json.Nodes;
using AcadCopilot.Core.Accuracy;
using AcadCopilot.Core.Agents;
using AcadCopilot.Core.Sheets;
using AcadCopilot.Core.Tools;
using static AcadCopilot.Core.Tests.Sheets.SheetTestData;

namespace AcadCopilot.Core.Tests.Sheets;

/// <summary>The full-set planner, reading the ground floor plan, the tool definitions and the guide.</summary>
public sealed class FullSetPlannerTests
{
    // A 30' x 40' ground floor at 0,0 with the stair at the back right, a toilet, a 40' x 55' plot.
    private static FullSetInput House() => new()
    {
        Outline = new SheetRect(0, 0, 360, 480),
        Stair = new SheetRect(240, 300, 336, 444),
        WetAreas = [new NamedRect("TOILET", new SheetRect(0, 0, 60, 96))],
        Plot = new SheetRect(-60, -60, 420, 600),
        Openings = [new OpeningCount("MD", 1), new OpeningCount("D", 3), new OpeningCount("W", 4, 54), new OpeningCount("V", 2)],
    };

    [Fact]
    public void A_g_plus_1_set_has_every_stage_in_drafting_order()
    {
        var plan = FullSetPlanner.Plan(House());
        Assert.Equal(
            ["first-floor", "terrace", "front-elevation", "rear-elevation", "side-elevation", "section", "site-plan", "furniture", "schedule", "area-statement", "sheets"],
            plan.Stages.Select(s => s.Id));
        Assert.Equal(Enumerable.Range(1, 11), plan.Stages.Select(s => s.Step));
        Assert.Equal("FIRST FLOOR PLAN", plan.Stages[0].Title);
        Assert.Equal("SECTION A-A", plan.Stages[5].Title);
        Assert.All(plan.Stages, s => Assert.NotEmpty(s.Tasks));
        Assert.Equal("elevation", plan.Stages[2].Guide);
        Assert.Equal("section", plan.Stages[5].Guide);

        var g2 = FullSetPlanner.Plan(House() with { UpperFloors = 2, Terrace = false });
        Assert.Equal(["first-floor", "second-floor", "front-elevation"], g2.Stages.Take(3).Select(s => s.Id));
        Assert.DoesNotContain(g2.Stages, s => s.Id == "terrace");
    }

    [Fact]
    public void Upper_floors_stack_on_the_ground_floor_at_the_plan_spacing()
    {
        var plan = FullSetPlanner.Plan(House());

        // Plan extents = outline + 12' = 648" wide; gap = one plan width (360" > 240"); spacing 1008".
        Assert.Equal(360, plan.Gap);
        Assert.Equal(1008, plan.Pitch);
        Assert.Equal((1008.0, 0.0), plan.Stages[0].Origin);
        Assert.Equal((2016.0, 0.0), plan.Stages[1].Origin);
        var first = plan.Stages[0];
        Assert.Contains("second point 84',0\"", first.Tasks[0], StringComparison.Ordinal); // 1008" = 84'
        Assert.Contains("TOILET (84',0\")-(89',8')", first.Tasks[1], StringComparison.Ordinal);
        Assert.Contains("Stair at (104',25')-(112',37')", first.Tasks[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Levels_use_the_standard_heights_unless_given()
    {
        var plan = FullSetPlanner.Plan(House());
        var levels = plan.Levels.ToDictionary(l => l.Name, l => l.Inches);
        Assert.Equal(0, levels["GL"]);
        Assert.Equal(18, levels["GF FFL"]);
        Assert.Equal(54, levels["GF SILL"]);
        Assert.Equal(102, levels["GF LINTEL"]);
        Assert.Equal(144, levels["FF FFL"]);
        Assert.Equal(270, levels["TERRACE"]);
        Assert.Equal(306, levels["PARAPET"]);
        Assert.Equal(372, levels["CABIN"]);
        Assert.Equal("+12'-0\" FF FFL", plan.Levels.Single(l => l.Name == "FF FFL").Text);

        var custom = FullSetPlanner.Plan(House() with { FloorHeightInches = 120, PlinthInches = 24, ParapetInches = 42, Stair = null });
        var c = custom.Levels.ToDictionary(l => l.Name, l => l.Inches);
        Assert.Equal(144, c["FF FFL"]);
        Assert.Equal(264 + 42, c["PARAPET"]);
        Assert.False(c.ContainsKey("CABIN"));
    }

    [Fact]
    public void Elevations_and_section_sit_on_one_ground_line_below_the_plans_without_overlaps()
    {
        var plan = FullSetPlanner.Plan(House());
        var planBottom = -144.0; // outline - 12'
        Assert.Equal(planBottom - 360 - 780, plan.GroundY); // the site plan (55' + 10') needs the most headroom
        var row2 = plan.Stages.Where(s => s.Kind is DrawingKind.Elevation or DrawingKind.Section or DrawingKind.SitePlan).ToList();
        Assert.Equal(5, row2.Count);
        Assert.All(row2, s => Assert.True(s.Area.MaxY <= planBottom - plan.Gap + 1e-6, s.Id));
        Assert.All(row2.Where(s => s.Kind != DrawingKind.SitePlan), s => Assert.Equal(plan.GroundY, s.Origin.Y));
        Assert.Equal(0, row2[0].Origin.X); // front elevation under the ground floor plan
        for (var i = 1; i < row2.Count; i++)
        {
            Assert.True(row2[i].Area.MinX >= row2[i - 1].Area.MaxX + plan.Gap - 1e-6, row2[i].Id);
        }

        var all = plan.Stages.Where(s => s.Id != "sheets").Select(s => s.Area).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                Assert.False(all[i].Overlaps(all[j]), $"{plan.Stages[i].Id} overlaps {plan.Stages[j].Id}");
            }
        }
    }

    [Fact]
    public void The_section_cuts_through_the_stair_across_the_front()
    {
        var south = FullSetPlanner.Plan(House());
        Assert.Equal((288.0, -60.0, 288.0, 540.0), south.Cut);

        var east = FullSetPlanner.Plan(House() with { FrontSide = "east" });
        Assert.Equal((-60.0, 372.0, 420.0, 372.0), east.Cut);
        Assert.Equal(480 + 144, east.Stages.Single(s => s.Id == "front-elevation").Area.Width, 6); // the 40' side faces the road
        Assert.Equal(360 + 144, south.Stages.Single(s => s.Id == "front-elevation").Area.Width, 6);
    }

    [Fact]
    public void Area_statement_and_schedule()
    {
        var plan = FullSetPlanner.Plan(House());
        Assert.Equal(1200, plan.Areas.FloorSqFt, 6);
        Assert.Equal(2, plan.Areas.Floors);
        Assert.Equal(114.0 * 162 / 144, plan.Areas.CabinSqFt, 6); // stair + 9" walls
        Assert.Equal(2200, plan.Areas.PlotSqFt!.Value, 6);
        Assert.Equal(2400 + plan.Areas.CabinSqFt, plan.Areas.TotalSqFt, 6);
        Assert.Equal(1200 / 2200.0 * 100, plan.Areas.CoveragePercent!.Value, 6);
        Assert.Equal(plan.Areas.TotalSqFt / 2200, plan.Areas.Far!.Value, 6);

        Assert.Equal(["MD", "D", "W", "V"], plan.Schedule.Select(r => r.Tag));
        Assert.Equal(42, plan.Schedule[0].WidthInches);
        Assert.Equal(54, plan.Schedule[2].WidthInches); // measured width wins
        Assert.Equal(36, plan.Schedule[2].SillInches);
        Assert.Equal("Ventilator", plan.Schedule[3].Type);
        Assert.Contains("MD Door 3'-6\" x 7'-0\" x1", string.Join(" ", plan.Stages.Single(s => s.Id == "schedule").Tasks), StringComparison.Ordinal);

        var noPlot = FullSetPlanner.Plan(House() with { Plot = null, Openings = [] });
        Assert.Null(noPlot.Areas.CoveragePercent);
        Assert.Contains("ask the user for the plot size", noPlot.Stages.Single(s => s.Id == "site-plan").Tasks[0], StringComparison.Ordinal);
        Assert.Contains("count them on the plans", string.Join(" ", noPlot.Stages.Single(s => s.Id == "schedule").Tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_tags_are_counted_from_plan_texts()
    {
        var counts = FullSetPlanner.CountTags(["D", "D", "W1", "%%uGROUND FLOOR PLAN", "MD", "V", "KITCHEN", "D1", "W", null, "KW", "SD"]);
        Assert.Equal(["MD", "D", "D1", "SD", "W", "W1", "KW", "V"], counts.Select(c => c.Tag));
        Assert.Equal(2, counts.Single(c => c.Tag == "D").Count);
    }

    [Fact]
    public void Feet_drawings_and_bad_outlines()
    {
        // 1 unit = 1 foot: the same house in feet gives the same plan scaled down by 12.
        var feet = FullSetPlanner.Plan(new FullSetInput { Outline = new SheetRect(0, 0, 30, 40), InchesPerUnit = 12 });
        Assert.Equal(1008 / 12.0, feet.Pitch, 6);
        Assert.Equal(1200, feet.Areas.FloorSqFt, 6);
        Assert.Contains("84,0", feet.Stages[0].Tasks[0], StringComparison.Ordinal); // plain numbers when 1 unit is not 1 inch

        Assert.Throws<ArgumentException>(() => FullSetPlanner.Plan(new FullSetInput { Outline = new SheetRect(0, 0, 0, 40) }));
    }

    [Fact]
    public void The_plan_as_json_has_stages_levels_areas_and_assumptions()
    {
        var plan = FullSetPlanner.Plan(House());
        var json = FullSetPlanner.ToJson(plan, 1);
        Assert.Equal(11, json["stages"]!.AsArray().Count);
        Assert.Equal("84',0\"", json["stages"]![0]!["originText"]!.GetValue<string>());
        Assert.Equal(plan.GroundY + 144, json["levels"]!.AsArray().Single(l => l!["name"]!.GetValue<string>() == "FF FFL")!["elevationY"]!.GetValue<double>(), 6);
        Assert.Equal(2, json["areas"]!["floors"]!.GetValue<int>());
        Assert.Equal("30'-0\"", json["gap"]!["ftIn"]!.GetValue<string>());
        Assert.Equal(4, json["sectionCut"]!.AsArray().Count);
        Assert.Contains("10'-6\"", json["assumptions"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ---- reading the plan ---------------------------------------------------------------------------------------

    [Fact]
    public void A_room_is_measured_from_its_name_to_the_nearest_wall_lines()
    {
        var walls = new[] { new SheetRect(0, 100, 200, 100), new SheetRect(150, 0, 150, 300), new SheetRect(0, 300, 400, 300) };
        Assert.Equal(new SheetRect(0, 0, 150, 100), PlanReading.RoomAround(50, 50, walls, new SheetRect(0, 0, 400, 400)));
        Assert.Equal(new SheetRect(150, 0, 400, 300), PlanReading.RoomAround(300, 150, walls, new SheetRect(0, 0, 400, 400)));
    }

    [Fact]
    public void The_ground_floor_is_read_from_the_drawing()
    {
        var items = Plan(0, 0, "GROUND FLOOR PLAN");
        var layers = items.Select(i => i.IsWall ? "A-WALL" : i.Text is null ? "A-ANNO-DIMS" : "A-ANNO-TEXT").ToList();
        void Add(ClusterItem item, string layer)
        {
            items.Add(item);
            layers.Add(layer);
        }

        Add(Line(240, 300, 336, 444), "A-STRS");
        Add(Wall(0, 96, 180, 96), "A-WALL");
        Add(Text(60, 40, 9, "TOILET"), "A-ANNO-TEXT");
        Add(Text(100, 4, 4.5, "W"), "A-ANNO-TEXT");
        Add(Text(200, 4, 4.5, "D"), "A-ANNO-TEXT");
        Add(Line(-60, -160, 420, 600), "A-SITE");
        var cluster = Assert.Single(DrawingClusters.Find(items, 120).Clusters);

        var input = PlanReading.FromDrawing(items, layers, cluster, 1);
        Assert.Equal(new SheetRect(0, 0, 360, 480), input.Outline);
        Assert.Equal(new SheetRect(240, 300, 336, 444), input.Stair);
        Assert.Equal(new SheetRect(-60, -160, 420, 600), input.Plot);
        Assert.Equal(["KITCHEN", "TOILET"], input.WetAreas.Select(w => w.Name));
        Assert.Equal(new SheetRect(0, 96, 180, 480), input.WetAreas[0].Box);
        Assert.Equal(new SheetRect(0, 0, 180, 96), input.WetAreas[1].Box);
        Assert.Equal(["D", "W"], input.Openings.Select(o => o.Tag));
        Assert.Equal(cluster.Box, input.PlanBox);
    }

    [Fact]
    public void A_validated_plan_gives_the_ground_floor_in_drawing_coordinates()
    {
        var wall = new PlanWall { Id = "S", From = new Pt(0, 0), To = new Pt(360, 0) };
        var house = new HousePlan
        {
            PlotWidth = 480,
            PlotDepth = 660,
            RoadSide = "east",
            FloorHeight = 120,
            FloorHeightGiven = true,
            Floors =
            [
                new PlanFloor
                {
                    Index = 0,
                    Outline = Polygon.Rect(60, 60, 360, 480),
                    Origin = new Pt(100, 200),
                    Rooms = [new PlanRoom { Name = "Toilet", Type = "toilet", Shape = Polygon.Rect(60, 60, 60, 96) }, new PlanRoom { Name = "Bed", Type = "bedroom", Shape = Polygon.Rect(120, 60, 120, 144) }],
                    Doors = [new PlanOpening { Tag = "D", IsDoor = true, Wall = wall, Width = 36 }, new PlanOpening { Tag = "D", IsDoor = true, Wall = wall, Width = 30 }],
                    Windows = [new PlanOpening { Tag = "W", Wall = wall, Width = 48 }],
                    Stair = new PlanStair { Box = new Box(300, 360, 396, 504) },
                },
                new PlanFloor { Index = 1, Outline = Polygon.Rect(60, 60, 360, 480) },
            ],
        };

        var input = PlanReading.FromHousePlan(house, 1)!;
        Assert.Equal(new SheetRect(160, 260, 520, 740), input.Outline);
        Assert.Equal(new SheetRect(400, 560, 496, 704), input.Stair);
        Assert.Equal(new SheetRect(100, 200, 580, 860), input.Plot);
        Assert.Equal("TOILET", Assert.Single(input.WetAreas).Name);
        Assert.Equal(1, input.UpperFloors);
        Assert.Equal(120, input.FloorHeightInches);
        Assert.Equal("east", input.FrontSide);
        Assert.Equal("validated plan", input.Source);
        Assert.Equal(33, input.Openings.Single(o => o.Tag == "D").WidthInches);
        Assert.Null(PlanReading.FromHousePlan(new HousePlan(), 1));
    }

    // ---- definitions and guide ----------------------------------------------------------------------------------

    [Fact]
    public void Tool_definitions_are_strict_with_the_right_risks()
    {
        var all = SheetToolDefinitions.All();
        Assert.Equal(["arrange_drawings", "create_sheets", "plot_to_pdf", "plan_full_set"], all.Select(d => d.Name));
        Assert.All(all, d => Assert.Empty(d.Lint()));
        Assert.All(all, d => Assert.True(d.Description.Length < 1_500, d.Name));
        Assert.Equal(ToolRisk.Write, all[0].Risk);
        Assert.Equal(ToolRisk.Write, all[1].Risk);
        Assert.Equal(ToolRisk.Destructive, all[2].Risk);
        Assert.Equal(ToolRisk.Read, all[3].Risk);
    }

    [Fact]
    public void Typical_arguments_pass_and_wrong_ones_are_rejected()
    {
        var arrange = SheetToolDefinitions.ArrangeDrawingsDefinition().InputSchema;
        Assert.Empty(JsonSchemaValidator.Validate(arrange, new JsonObject()));
        Assert.Empty(JsonSchemaValidator.Validate(arrange, JsonNode.Parse("""{"labels":[{"point":[10,20],"title":"FRONT ELEVATION"}],"preview_only":true,"scale":"1/8"}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(arrange, JsonNode.Parse("""{"labels":[{"point":[10],"title":"X"}]}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(arrange, JsonNode.Parse("""{"scale":"3/16"}""")));

        var sheets = SheetToolDefinitions.CreateSheetsDefinition().InputSchema;
        Assert.Empty(JsonSchemaValidator.Validate(sheets, JsonNode.Parse("""{"paper":"A2","standard":"arch","project":"Sharma Residence","client":"Mr Sharma","drawings":[1,2,3],"combine":false,"show_sheet":true}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(sheets, JsonNode.Parse("""{"paper":"A4"}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(sheets, JsonNode.Parse("""{"layout":"x"}""")));

        var pdf = SheetToolDefinitions.PlotToPdfDefinition().InputSchema;
        Assert.Empty(JsonSchemaValidator.Validate(pdf, JsonNode.Parse("""{"sheets":["A-101 Ground Floor Plan"],"name":"Sharma set"}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(pdf, JsonNode.Parse("""{"sheets":[]}""")));

        var full = SheetToolDefinitions.PlanFullSetDefinition().InputSchema;
        Assert.Empty(JsonSchemaValidator.Validate(full, JsonNode.Parse("""{"upper_floors":2,"front":"east","floor_height":120,"outline":[0,0,360,480],"wet_areas":[{"name":"TOILET","box":[0,0,60,96]}]}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(full, JsonNode.Parse("""{"upper_floors":9}""")));
        Assert.NotEmpty(JsonSchemaValidator.Validate(full, JsonNode.Parse("""{"outline":[0,0,360]}""")));
    }

    [Fact]
    public void The_full_set_guide_comes_back_for_set_sheet_and_pdf_queries()
    {
        Assert.Equal("Full drawing set", DraftingGuides.Match("full set")[0].Name);
        Assert.Equal("Full drawing set", DraftingGuides.Match("make sheets and a pdf")[0].Name);
        Assert.Equal("Elevation", DraftingGuides.Match("front elevation")[0].Name);
        var guide = DraftingGuides.All.Single(g => g.Name == "Full drawing set");
        Assert.True(guide.Text.Length < 1_200, guide.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var tool in new[] { "plan_full_set", "arrange_drawings", "create_sheets", "plot_to_pdf" })
        {
            Assert.Contains(tool, guide.Text, StringComparison.Ordinal);
        }

        Assert.Contains("\"full set\"", CopilotInstructions.Default, StringComparison.Ordinal);
        Assert.True(CopilotInstructions.Default.Length < 15_500, $"{CopilotInstructions.Default.Length} characters");
    }
}
