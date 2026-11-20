using AcadCopilot.Core.Accuracy;
using static AcadCopilot.Core.Tests.Accuracy.PlanBriefs;

namespace AcadCopilot.Core.Tests.Accuracy;

public sealed class DrawingCheckerTests
{
    private static readonly CheckLayers Layers = new();

    private static (HousePlan Plan, PlanFloor Floor, Pt Origin, DrawingSnapshot Drawing) Drawn(PlanJson json, int floorIndex = 0)
    {
        var plan = Parse(json);
        var floor = plan.Floors[floorIndex];
        var origin = floor.Origin ?? new Pt(0, 0);
        return (plan, floor, origin, SyntheticDrawing.Draw(plan, floor, origin));
    }

    private static DrawingCheckReport Check((HousePlan Plan, PlanFloor Floor, Pt Origin, DrawingSnapshot Drawing) d, string stage = "all") =>
        DrawingChecker.Check(d.Plan, d.Floor, d.Origin, d.Drawing, Layers, stage);

    private static string Describe(DrawingCheckReport r) => string.Join("\n", r.Findings.Select(f => f.Text()));

    private static bool Has(DrawingCheckReport r, string code, string? text = null) =>
        r.Findings.Any(f => f.Code == code && (text is null || f.Message.Contains(text, StringComparison.Ordinal)));

    [Fact]
    public void PerfectGroundFloor_HasNoFindings()
    {
        var report = Check(Drawn(ThirtyForty()));
        Assert.True(report.Errors == 0 && report.Warnings == 0, Describe(report));
        var living = report.Rooms.First(r => r!["room"]!.ToString() == "Living")!;
        Assert.Equal("13'-0\" x 15'-0\"", living["measured"]!.ToString());
    }

    [Fact]
    public void PerfectFirstFloor_DrawnElsewhere_IsCheckedAtItsOrigin()
    {
        var drawn = Drawn(ThirtyForty(), floorIndex: 1);
        Assert.Equal(new Pt(720, 0), drawn.Origin);
        var report = Check(drawn);
        Assert.True(report.Errors == 0 && report.Warnings == 0, Describe(report));

        // The same drawing checked at the wrong origin does not match.
        var wrong = DrawingChecker.Check(drawn.Plan, drawn.Floor, new Pt(0, 0), drawn.Drawing, Layers);
        Assert.True(Has(wrong, "wall_face_missing") && Has(wrong, "door_missing"), Describe(wrong));
    }

    [Fact]
    public void PerfectThirtyFifty_HasNoFindings()
    {
        var report = Check(Drawn(ThirtyFifty()));
        Assert.True(report.Errors == 0 && report.Warnings == 0, Describe(report));
    }

    [Fact]
    public void WallLineOutOfPlace_ChangesTheRoomSize()
    {
        var d = Drawn(ThirtyForty());
        // Move Living's east face (x = 201) 1" to the west.
        var moved = d.Drawing.Segments.Where(s => Math.Abs(s.A.X - 201) < 1e-6 && Math.Abs(s.B.X - 201) < 1e-6).ToList();
        Assert.NotEmpty(moved);
        foreach (var s in moved)
        {
            d.Drawing.Segments[d.Drawing.Segments.IndexOf(s)] = s with { A = s.A + new Pt(-1, 0), B = s.B + new Pt(-1, 0) };
        }

        var report = Check(d, "walls");
        Assert.True(Has(report, "room_size", "Living measures 12'-11\" x 15'-0\""), Describe(report));
        Assert.True(Has(report, "wall_face_missing", "Living"), Describe(report));
    }

    [Fact]
    public void GapsDuplicatesAndDanglingEnds_AreFound()
    {
        var d = Drawn(ThirtyForty());
        // Shorten one outline segment by 2" (a gap), duplicate another, add a stray line.
        var outer = d.Drawing.Segments.First(s => Math.Abs(s.A.Y - 444) < 1e-6 && Math.Abs(s.B.Y - 444) < 1e-6 && s.Length > 100);
        var index = d.Drawing.Segments.IndexOf(outer);
        var dir = (outer.B - outer.A).Unit();
        d.Drawing.Segments[index] = outer with { B = outer.B - (dir * 2) };
        var inner = d.Drawing.Segments.First(s => Math.Abs(s.A.X - 45) < 1e-6 && Math.Abs(s.B.X - 45) < 1e-6);
        d.Drawing.Segments.Add(inner with { Handle = "DUP" });
        d.Drawing.Segments.Add(new DrawnSegment("STRAY", "A-WALL", new Pt(100, 150), new Pt(130, 150)));

        var report = Check(d, "walls");
        Assert.True(Has(report, "wall_gap", "Gap of 0'-2\""), Describe(report));
        Assert.True(Has(report, "wall_overlap", "DUP"), Describe(report));
        Assert.True(Has(report, "wall_dangling", "STRAY"), Describe(report));
    }

    [Fact]
    public void WallLineAcrossAnOpening_IsFound()
    {
        var d = Drawn(ThirtyForty());
        d.Drawing.Segments.Add(new DrawnSegment("ACROSS", "A-WALL", new Pt(49, 69), new Pt(91, 69)));
        var report = Check(d, "walls");
        Assert.True(Has(report, "wall_across_opening", "MD"), Describe(report));
    }

    [Fact]
    public void MissingWrongAndExtraDoors_AreFound()
    {
        var d = Drawn(ThirtyForty());
        var arcs = d.Drawing.Arcs;
        var d1 = arcs.First(a => a.Center.DistanceTo(new Pt(205.5, 181)) < 1e-6);
        arcs.Remove(d1);
        var md = arcs.First(a => a.Center.DistanceTo(new Pt(49, 69)) < 1e-6);
        // Main door swinging out (mirror the arc to the outside).
        arcs[arcs.IndexOf(md)] = md with { StartAngle = -Math.PI / 2, EndAngle = 0 };
        arcs.Add(new DrawnArc("EXTRA", "A-DOOR", new Pt(150, 300), 36, 0, Math.PI / 2));

        var report = Check(d, "openings");
        Assert.True(Has(report, "door_missing", "D1 (V1, 3'-0\")"), Describe(report));
        Assert.True(Has(report, "door_swing_side", "MD"), Describe(report));
        Assert.True(Has(report, "door_extra", "EXTRA"), Describe(report));
    }

    [Fact]
    public void DoorHingedAtTheOtherJamb_IsAWarning()
    {
        var plan = Parse(ThirtyForty());
        var floor = plan.Floors[0];
        var drawing = SyntheticDrawing.Draw(plan, floor, new Pt(0, 0));
        var d2 = drawing.Arcs.First(a => a.Center.DistanceTo(new Pt(196, 253.5)) < 1e-6);
        drawing.Arcs[drawing.Arcs.IndexOf(d2)] = d2 with { Center = new Pt(160, 253.5), StartAngle = 0, EndAngle = Math.PI / 2 };
        var report = DrawingChecker.Check(plan, floor, new Pt(0, 0), drawing, Layers, "openings");
        Assert.True(Has(report, "door_hinge", "D2 is hinged at"), Describe(report));
    }

    [Fact]
    public void MissingAndExtraWindows_AreFound()
    {
        var d = Drawn(ThirtyForty());
        var w1 = d.Drawing.Segments.First(s => s.Layer == "A-GLAZ" && Math.Abs(s.A.Y - 64.5) < 1e-6);
        d.Drawing.Segments.Remove(w1);
        d.Drawing.Segments.Add(new DrawnSegment("GLASS", "A-GLAZ", new Pt(40.5, 200), new Pt(40.5, 240)));
        var report = Check(d, "openings");
        Assert.True(Has(report, "window_missing", "W1 (OS, 5'-0\""), Describe(report));
        Assert.True(Has(report, "window_extra", "GLASS"), Describe(report));
    }

    [Fact]
    public void TextProblems_AreFound()
    {
        var d = Drawn(ThirtyForty());
        var texts = d.Drawing.Texts;
        texts.Remove(texts.First(t => t.Text == "KITCHEN"));
        var living = texts.First(t => t.Text == "LIVING");
        texts.Add(living with { Handle = "SIZE", Text = "13'-0\" X 15'-0\"", Bounds = living.Bounds });
        texts.Add(new DrawnText("ONWALL", "A-ANNO-TEXT", "NOTE", new Box(195, 100, 210, 106), 6));
        texts.Add(new DrawnText("INSWING", "A-ANNO-TEXT", "X", new Box(60, 75, 64, 79), 4));

        var report = Check(d, "annotation");
        Assert.True(Has(report, "label_missing", "Kitchen"), Describe(report));
        Assert.True(Has(report, "text_overlap", "SIZE"), Describe(report));
        Assert.True(Has(report, "text_on_line", "ONWALL"), Describe(report));
        Assert.True(Has(report, "text_on_swing", "INSWING"), Describe(report));
    }

    [Fact]
    public void DimensionProblems_AreFound()
    {
        var d = Drawn(ThirtyForty());
        var dims = d.Drawing.Dimensions;
        // Override one chain value, drop the last segment of the south chain, and the overall depth.
        var first = dims.First(x => x.Rotation == 0 && Math.Abs(x.LinePoint.Y - 18) < 1e-6);
        dims[dims.IndexOf(first)] = first with { TextOverride = "1'-0\"" };
        var last = dims.Last(x => x.Rotation == 0 && Math.Abs(x.LinePoint.Y - 18) < 1e-6);
        dims.Remove(last);
        dims.Remove(dims.First(x => x.Rotation is > 1 && Math.Abs(x.Measurement - 384) < 1e-6));
        dims.Add(new DrawnDimension("FLOAT", "A-ANNO-DIMS", new Pt(-300, -300), new Pt(-200.3, -300), new Pt(-200, -320), 0, 99.7, null));

        var report = Check(d, "annotation");
        Assert.True(Has(report, "dim_text", "shows \"1'-0\"\" but measures 0'-9\""), Describe(report));
        Assert.True(Has(report, "dim_sum", "a segment is missing at the east end"), Describe(report));
        Assert.True(Has(report, "dim_overall", "No overall depth dimension of 32'-0\""), Describe(report));
        Assert.True(Has(report, "dim_point", "FLOAT"), Describe(report));
        Assert.True(Has(report, "dim_fraction", "FLOAT"), Describe(report));
    }

    [Fact]
    public void WrongLayers_AreFound()
    {
        var d = Drawn(ThirtyForty());
        d.Drawing.Objects.Add(new DrawnObject("Z1", "0", "line"));
        d.Drawing.Objects.Add(new DrawnObject("T1", "A-WALL", "text"));
        d.Drawing.Objects.Add(new DrawnObject("DM", "A-WALL", "dimension"));
        d.Drawing.Objects.Add(new DrawnObject("HT", "A-WALL", "hatch"));
        d.Drawing.Arcs.Add(new DrawnArc("ARCW", "A-WALL", new Pt(100, 100), 36, 0, 1));

        var report = Check(d);
        Assert.True(Has(report, "layer_zero", "Z1"), Describe(report));
        Assert.True(Has(report, "layer_text", "T1"), Describe(report));
        Assert.True(Has(report, "layer_dims", "DM"), Describe(report));
        Assert.True(Has(report, "layer_hatch", "HT"), Describe(report));
        Assert.True(Has(report, "layer_door", "ARCW"), Describe(report));
    }

    [Fact]
    public void EmptyArea_SaysNothingIsDrawnYet()
    {
        var plan = Parse(ThirtyForty());
        var report = DrawingChecker.Check(plan, plan.Floors[0], new Pt(0, 0), new DrawingSnapshot(), Layers);
        Assert.True(Has(report, "no_walls"), Describe(report));
        Assert.True(Has(report, "no_dimensions"), Describe(report));
        Assert.True(Has(report, "door_missing", "MD"), Describe(report));
    }

    [Fact]
    public void Region_CoversTheFloorAndItsDimensions()
    {
        var plan = Parse(ThirtyFifty());
        var region = DrawingChecker.Region(plan.Floors[0], new Pt(1000, 0));
        Assert.True(region.MinX <= 1000 + 36 - 144 + 1e-6);
        Assert.True(region.MaxY >= 594 + 144 - 1e-6);
    }
}
