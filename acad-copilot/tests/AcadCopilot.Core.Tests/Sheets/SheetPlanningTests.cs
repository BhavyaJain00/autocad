using AcadCopilot.Core.Sheets;

namespace AcadCopilot.Core.Tests.Sheets;

/// <summary>Scale choice, paper and plotter media, title block, sheet numbering and packing, file names.</summary>
public sealed class SheetPlanningTests
{
    // ---- scales ------------------------------------------------------------------------------------------------

    [Fact]
    public void Plans_take_quarter_inch_when_they_fit_A3_else_eighth()
    {
        var a3 = TitleBlockDesign.For(PaperSizes.For("A3", PaperStandard.Iso)).Drawable;
        Assert.Equal(new SheetRect(25, 15, 335, 282), a3);

        // 25' x 33'4" fits at 1/4" (159 x 212 mm); 41'8" x 50' (265 x 318 mm) does not, so 1/8".
        Assert.Same(DrawingScales.QuarterInch, DrawingScales.Choose(DrawingKind.FloorPlan, 300, 400, 1, a3.Width, a3.Height, ScaleSystem.Imperial));
        Assert.Same(DrawingScales.EighthInch, DrawingScales.Choose(DrawingKind.FloorPlan, 500, 600, 1, a3.Width, a3.Height, ScaleSystem.Imperial));

        // A site plan never goes above 1/8"; a huge drawing gets the smallest scale.
        Assert.Same(DrawingScales.EighthInch, DrawingScales.Choose(DrawingKind.SitePlan, 50, 50, 1, a3.Width, a3.Height, ScaleSystem.Imperial));
        Assert.Equal(384, DrawingScales.Choose(DrawingKind.FloorPlan, 99_999, 99_999, 1, a3.Width, a3.Height, ScaleSystem.Imperial).Ratio);
    }

    [Fact]
    public void Metric_drawings_use_1_to_50_and_1_to_100()
    {
        const double mm = 1 / 25.4;
        Assert.Equal("1:50", DrawingScales.Choose(DrawingKind.Elevation, 9000, 7000, mm, 304, 261, ScaleSystem.Metric).Text);
        Assert.Equal("1:100", DrawingScales.Choose(DrawingKind.FloorPlan, 12000, 15000, mm, 304, 261, ScaleSystem.Metric).Text);
        Assert.Equal("1:100", DrawingScales.Preferred(DrawingKind.SitePlan, ScaleSystem.Metric).Text);
        Assert.Equal(0.02, DrawingScales.Metric[1].PaperMmPerUnit(mm), 9);
        Assert.Equal(25.4 / 48, DrawingScales.QuarterInch.PaperMmPerUnit(1), 9);
        Assert.Equal(2, DrawingScales.EighthInch.TextFactor);
    }

    [Theory]
    [InlineData("1/4", 48)]
    [InlineData("1/4\" = 1'-0\"", 48)]
    [InlineData("SCALE 1/8\" = 1'-0\"", 96)]
    [InlineData("1/8 inch", 96)]
    [InlineData("1:100", 100)]
    [InlineData("Scale: 1:50", 50)]
    [InlineData("200", 200)]
    [InlineData("1:48", 48)]
    public void Scales_are_parsed(string text, double ratio) => Assert.Equal(ratio, DrawingScales.Parse(text)!.Ratio);

    [Theory]
    [InlineData("3/16")]
    [InlineData("1:75")]
    [InlineData("abc")]
    [InlineData("")]
    public void Unknown_scales_are_refused(string text) => Assert.Null(DrawingScales.Parse(text));

    [Theory]
    [InlineData(1, ScaleSystem.Imperial)]
    [InlineData(2, ScaleSystem.Imperial)]
    [InlineData(0, ScaleSystem.Imperial)]
    [InlineData(4, ScaleSystem.Metric)]
    [InlineData(6, ScaleSystem.Metric)]
    public void Scale_system_follows_the_drawing_units(int insUnits, ScaleSystem expected) => Assert.Equal(expected, DrawingScales.SystemFor(insUnits));

    // ---- paper and media ----------------------------------------------------------------------------------------

    [Fact]
    public void Paper_sizes_are_landscape_iso_or_arch()
    {
        Assert.Equal((420.0, 297.0), (PaperSizes.For("A3", PaperStandard.Iso).WidthMm, PaperSizes.For("A3", PaperStandard.Iso).HeightMm));
        Assert.Equal("ARCH-C", PaperSizes.For("a2", PaperStandard.Arch).Code);
        Assert.Equal("ARCH-D", PaperSizes.For("ARCH D", PaperStandard.Arch).Code);
        Assert.Equal("ISO-A1", PaperSizes.For("A1", PaperStandard.Iso).Code);
        Assert.Equal("ISO-A3", PaperSizes.For("legal", PaperStandard.Iso).Code);
        Assert.All(PaperSizes.All, p => Assert.True(p.WidthMm > p.HeightMm));
    }

    private static readonly string[] PdfMedia =
    [
        "ISO_A4_(210.00_x_297.00_MM)",
        "ISO_A3_(297.00_x_420.00_MM)",
        "ISO_expand_A3_(420.00_x_297.00_MM)",
        "ISO_A3_(420.00_x_297.00_MM)",
        "ISO_full_bleed_A3_(420.00_x_297.00_MM)",
        "ISO_full_bleed_A2_(594.00_x_420.00_MM)",
        "ARCH_B_(18.00_x_12.00_Inches)",
        "ARCH_full_bleed_B_(18.00_x_12.00_Inches)",
        "ARCH_full_bleed_D_(24.00_x_36.00_Inches)",
    ];

    [Fact]
    public void Media_prefers_landscape_full_bleed_of_the_right_size()
    {
        Assert.Equal(("ISO_full_bleed_A3_(420.00_x_297.00_MM)", false), PaperSizes.PickMedia(PdfMedia, PaperSizes.For("A3", PaperStandard.Iso)));
        Assert.Equal(("ISO_full_bleed_A2_(594.00_x_420.00_MM)", false), PaperSizes.PickMedia(PdfMedia, PaperSizes.For("A2", PaperStandard.Iso)));
        Assert.Equal(("ARCH_full_bleed_B_(18.00_x_12.00_Inches)", false), PaperSizes.PickMedia(PdfMedia, PaperSizes.For("A3", PaperStandard.Arch)));
        Assert.Equal(("ARCH_full_bleed_D_(24.00_x_36.00_Inches)", true), PaperSizes.PickMedia(PdfMedia, PaperSizes.For("A1", PaperStandard.Arch)));
        Assert.Null(PaperSizes.PickMedia(PdfMedia, PaperSizes.For("A1", PaperStandard.Iso)));
        Assert.Equal(("ISO_A3_(420.00_x_297.00_MM)", false), PaperSizes.PickMedia(["ISO_expand_A3_(420.00_x_297.00_MM)", "ISO_A3_(420.00_x_297.00_MM)"], PaperSizes.For("A3", PaperStandard.Iso)));
    }

    [Fact]
    public void Media_sizes_are_read_from_their_names()
    {
        var arch = PaperSizes.MediaSizeMm("ARCH_B_(18.00_x_12.00_Inches)")!.Value;
        Assert.Equal(457.2, arch.Width, 6);
        Assert.Equal(304.8, arch.Height, 6);
        Assert.Equal((420.0, 297.0), PaperSizes.MediaSizeMm("ISO A3 (420.00 x 297.00 MM)")!.Value);
        Assert.Null(PaperSizes.MediaSizeMm("Letter"));
        Assert.Null(PaperSizes.MediaSizeMm(null));
    }

    // ---- title block --------------------------------------------------------------------------------------------

    [Fact]
    public void Title_block_has_every_attribute_and_the_note_inside_its_strip()
    {
        var design = TitleBlockDesign.For(PaperSizes.For("A3", PaperStandard.Iso));
        Assert.Equal("A-TTLB-ISO-A3", design.BlockName);
        Assert.Equal(new SheetRect(20, 10, 410, 287), design.Border);
        Assert.Equal(70, design.Strip.Width, 6);
        Assert.Equal(
            ["DRAWN_BY", "DATE", "SHEET_NO", "SCALE", "TITLE", "CLIENT", "PROJECT"],
            design.Attributes.Select(a => a.Tag));
        Assert.All(design.Attributes, a => Assert.True(design.Strip.Contains(a.X, a.Y) && a.X + a.MaxWidth <= design.Strip.MaxX + 1e-6, a.Tag));
        Assert.All(design.Texts, t => Assert.True(design.Strip.Contains(t.X, t.Y), t.Text));
        Assert.Contains(design.Texts, t => t.Text == "NOT FOR CONSTRUCTION -");
        Assert.Contains(design.Texts, t => t.Text == "CHECK BY ARCHITECT/ENGINEER");
        Assert.True(design.Drawable.MaxX <= design.Strip.MinX - TitleBlockDesign.Inset + 1e-6);
        Assert.True(design.Border.Contains(design.Drawable));
        Assert.All(design.Lines, l => Assert.True(design.Border.Contains(l.X0, l.Y0) && design.Border.Contains(l.X1, l.Y1)));

        var a1 = TitleBlockDesign.For(PaperSizes.For("A1", PaperStandard.Iso));
        Assert.Equal(110, a1.Strip.Width, 6);
        Assert.True(a1.Attributes[0].Height > design.Attributes[0].Height);
    }

    [Fact]
    public void Long_values_get_smaller_text_but_never_below_the_minimum()
    {
        var title = TitleBlockDesign.For(PaperSizes.All[0]).Attributes.Single(a => a.Tag == TitleBlockDesign.TagTitle);
        Assert.Equal(title.Height, TitleBlockDesign.FitHeight(title, "SITE PLAN"));
        var fitted = TitleBlockDesign.FitHeight(title, "FRONT & REAR ELEVATIONS WITH DETAILS");
        Assert.True(fitted < title.Height);
        Assert.True(fitted * 0.8 * 36 <= title.MaxWidth + 0.01);
        Assert.Equal(TitleBlockDesign.MinTextHeight, TitleBlockDesign.FitHeight(title, new string('X', 200)));
    }

    [Fact]
    public void Title_block_values_by_tag_and_drawn_by()
    {
        var values = new TitleBlockValues { Project = "SHARMA RESIDENCE", Title = "GROUND FLOOR PLAN", SheetNumber = "A-101", DrawnBy = TitleBlockValues.DrawnByFor("ravi") };
        Assert.Equal("AI Copilot / ravi", values.ByTag["DRAWN_BY"]);
        Assert.Equal("A-101", values.ByTag["SHEET_NO"]);
        Assert.Equal(10, values.ByTag.Count);
        Assert.Equal("A-101", values.ByTag["SHEET"]); // the accuracy pack's A3 title block tags
        Assert.Equal(TitleBlockDesign.Note, values.ByTag["NOTE"]);
        Assert.Equal("AI Copilot", TitleBlockValues.DrawnByFor(" "));
        Assert.Equal("20 Nov 2026", TitleBlockValues.DateText(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(5.5))));
    }

    [Fact]
    public void The_accuracy_packs_A3_title_block_is_used_above_its_title_box()
    {
        var design = TitleBlockDesign.FromTemplate(AcadCopilot.Core.Accuracy.DrawingTemplate.Default.TitleBlock!);
        Assert.NotNull(design);
        Assert.True(design.External);
        Assert.Equal(25.4, design.InsertScale);
        Assert.Equal("A-TTLB-A3", design.BlockName);
        Assert.Equal(410, design.Strip.MaxX, 0);
        Assert.True(design.Drawable.MinY >= design.Strip.MaxY + TitleBlockDesign.Inset - 1e-6);
        Assert.Equal(new SheetRect(25, design.Drawable.MinY, 405, 282), design.Drawable);
        Assert.Contains(design.Attributes, a => a.Tag == "SHEET");
        Assert.All(design.Attributes, a => Assert.True(a.MaxWidth >= 10, a.Tag));
        var scale = design.Attributes.Single(a => a.Tag == "SCALE");
        Assert.True(scale.X + scale.MaxWidth < design.Strip.MaxX - 50, "SCALE shares its row with DATE");

        var plan = SheetPlanner.Plan(
            [new SheetDrawing(1, DrawingKind.FloorPlan, 0, "GROUND FLOOR PLAN", new SheetRect(0, 0, 360, 480))],
            new SheetOptions { TitleBlock = design });
        Assert.Same(design, plan.TitleBlock);
        Assert.True(design.Drawable.Contains(plan.Sheets[0].Viewports[0].Paper));

        var odd = new AcadCopilot.Core.Accuracy.TemplateTitleBlock("X", 11, 8.5, [], [], []);
        Assert.Null(TitleBlockDesign.FromTemplate(odd));
    }

    // ---- sheet planning ------------------------------------------------------------------------------------------

    private static List<SheetDrawing> HouseSet() =>
    [
        new(1, DrawingKind.FloorPlan, 0, "GROUND FLOOR PLAN", new SheetRect(0, 0, 500, 600)),
        new(2, DrawingKind.FloorPlan, 1, "FIRST FLOOR PLAN", new SheetRect(1000, 0, 1300, 400)),
        new(3, DrawingKind.Elevation, 0, "FRONT ELEVATION", new SheetRect(0, -1000, 250, -800)),
        new(4, DrawingKind.Elevation, 1, "REAR ELEVATION", new SheetRect(400, -1000, 650, -800)),
        new(5, DrawingKind.Elevation, 4, "SIDE ELEVATION", new SheetRect(800, -1000, 1050, -800)),
        new(6, DrawingKind.Section, 0, "SECTION A-A", new SheetRect(1200, -1000, 1450, -800)),
        new(7, DrawingKind.SitePlan, 0, "SITE PLAN", new SheetRect(2000, -1000, 2600, -300)),
    ];

    [Fact]
    public void Sheets_are_numbered_by_series_with_one_plan_per_sheet()
    {
        var plan = SheetPlanner.Plan(HouseSet(), new SheetOptions());
        Assert.Empty(plan.Warnings);
        Assert.Equal(["A-001", "A-101", "A-102", "A-201", "A-301"], plan.Sheets.Select(s => s.Number));
        Assert.Equal(
            ["A-001 Site Plan", "A-101 Ground Floor Plan", "A-102 First Floor Plan", "A-201 Elevations", "A-301 Section A-A"],
            plan.Sheets.Select(s => s.LayoutName));
        Assert.All(plan.Sheets.Take(3), s => Assert.Single(s.Viewports));
        Assert.Equal([3, 4, 5], plan.Sheets[3].Viewports.Select(v => v.DrawingNumber));
    }

    [Fact]
    public void Floor_plans_share_the_smallest_scale_and_small_drawings_keep_quarter_inch()
    {
        var plan = SheetPlanner.Plan(HouseSet(), new SheetOptions());
        Assert.All(plan.Sheets.Where(s => s.Number.StartsWith("A-1", StringComparison.Ordinal)), s => Assert.Equal("1/8\" = 1'-0\"", s.ScaleText));
        Assert.Equal("1/4\" = 1'-0\"", plan.Sheets[3].ScaleText);
        Assert.Equal("1/8\" = 1'-0\"", plan.Sheets[0].ScaleText);

        var scales = SheetPlanner.ChooseScales(HouseSet(), new SheetOptions());
        Assert.Same(DrawingScales.EighthInch, scales[2]); // would fit at 1/4" alone
        Assert.Same(DrawingScales.QuarterInch, scales[6]);

        var forced = SheetPlanner.ChooseScales(HouseSet(), new SheetOptions { Scale = DrawingScales.Imperial[3] });
        Assert.All(forced.Values, s => Assert.Equal(192, s.Ratio));
    }

    [Fact]
    public void Viewports_sit_inside_the_drawable_area_without_overlapping_and_show_the_whole_drawing()
    {
        var plan = SheetPlanner.Plan(HouseSet(), new SheetOptions());
        var drawings = HouseSet().ToDictionary(d => d.Number);
        foreach (var sheet in plan.Sheets)
        {
            for (var i = 0; i < sheet.Viewports.Count; i++)
            {
                var view = sheet.Viewports[i];
                Assert.True(plan.TitleBlock.Drawable.Contains(view.Paper), $"{sheet.Number} viewport {view.DrawingNumber}");
                Assert.False(view.Cropped);
                Assert.True(view.Model.Contains(drawings[view.DrawingNumber].ModelBox));
                Assert.Equal(view.Paper.Width / view.Model.Width, view.Scale.PaperMmPerUnit(1), 9);
                for (var j = i + 1; j < sheet.Viewports.Count; j++)
                {
                    Assert.False(view.Paper.Overlaps(sheet.Viewports[j].Paper));
                }
            }
        }

        // A single plan is centred in the drawable area.
        var ground = plan.Sheets[1].Viewports[0].Paper;
        Assert.Equal(plan.TitleBlock.Drawable.CenterX, ground.CenterX, 6);
        Assert.Equal(plan.TitleBlock.Drawable.CenterY, ground.CenterY, 6);
    }

    [Fact]
    public void Two_elevations_share_a_titled_sheet_and_combine_false_gives_each_its_own()
    {
        var two = HouseSet().Where(d => d.Number is 3 or 4).ToList();
        var shared = Assert.Single(SheetPlanner.Plan(two, new SheetOptions()).Sheets);
        Assert.Equal("FRONT & REAR ELEVATIONS", shared.Title);
        Assert.Equal("A-201 Front & Rear Elevations", shared.LayoutName);

        var separate = SheetPlanner.Plan(two, new SheetOptions { Combine = false }).Sheets;
        Assert.Equal(["A-201", "A-202"], separate.Select(s => s.Number));
        Assert.Equal("FRONT ELEVATION", separate[0].Title);
    }

    [Fact]
    public void Mixed_scales_on_a_sheet_say_as_shown_and_big_drawings_move_to_a_new_sheet()
    {
        var mixed = new List<SheetDrawing>
        {
            new(1, DrawingKind.Schedule, 0, "DOOR & WINDOW SCHEDULE", new SheetRect(0, 0, 200, 100)),
            new(2, DrawingKind.Schedule, 1, "AREA STATEMENT", new SheetRect(0, 0, 900, 300)),
        };
        var sheet = Assert.Single(SheetPlanner.Plan(mixed, new SheetOptions()).Sheets);
        Assert.Equal("AS SHOWN", sheet.ScaleText);
        Assert.Equal("A-601", sheet.Number);

        var big = new List<SheetDrawing>
        {
            new(1, DrawingKind.Elevation, 0, "FRONT ELEVATION", new SheetRect(0, 0, 500, 450)),
            new(2, DrawingKind.Elevation, 1, "REAR ELEVATION", new SheetRect(0, 0, 500, 450)),
        };
        Assert.Equal(2, SheetPlanner.Plan(big, new SheetOptions()).Sheets.Count);
    }

    [Fact]
    public void A_drawing_too_big_for_the_paper_is_cropped_with_a_warning()
    {
        var huge = new List<SheetDrawing> { new(1, DrawingKind.SitePlan, 0, "SITE PLAN", new SheetRect(0, 0, 6000, 6000)) };
        var plan = SheetPlanner.Plan(huge, new SheetOptions());
        var view = Assert.Single(Assert.Single(plan.Sheets).Viewports);
        Assert.True(view.Cropped);
        Assert.Contains("does not fit", Assert.Single(plan.Warnings), StringComparison.Ordinal);
        Assert.True(plan.TitleBlock.Drawable.Contains(view.Paper));

        var a1 = SheetPlanner.Plan(huge, new SheetOptions { Paper = PaperSizes.For("A1", PaperStandard.Iso) });
        Assert.Empty(a1.Warnings);
    }

    [Fact]
    public void Sheet_titles_join_short_titles_and_fall_back_to_the_kind()
    {
        SheetDrawing D(DrawingKind kind, string title) => new(1, kind, 0, title, new SheetRect(0, 0, 1, 1));
        Assert.Equal("SECTION A-A & SECTION B-B", SheetPlanner.SheetTitle([D(DrawingKind.Section, "SECTION A-A"), D(DrawingKind.Section, "SECTION B-B")]));
        Assert.Equal("SECTIONS", SheetPlanner.SheetTitle([D(DrawingKind.Section, "SECTION A-A"), D(DrawingKind.Section, "SECTION B-B"), D(DrawingKind.Section, "SECTION C-C"), D(DrawingKind.Section, "SECTION D-D")]));
        Assert.Equal("DRAWINGS", SheetPlanner.SheetTitle([]));
        Assert.Equal("DRAWINGS", SheetPlanner.SheetTitle([D(DrawingKind.Schedule, "DOOR & WINDOW SCHEDULE"), D(DrawingKind.Other, "KEY NOTES FOR THE CONTRACTOR")]));
    }

    [Fact]
    public void Packing_fills_rows_then_refuses_what_does_not_fit()
    {
        var area = new SheetRect(0, 0, 300, 200);
        var three = SheetPlanner.Pack<string>([("a", 100, 80), ("b", 100, 60), ("c", 250, 50)], area)!;
        Assert.Equal(3, three.Count);
        Assert.Equal(three[0].MaxY, three[1].MaxY + (80 - 60), 6); // bottoms aligned in a row
        Assert.Equal(three[0].MinY, three[1].MinY, 6);
        Assert.True(three[2].MaxY <= three[0].MinY - SheetPlanner.ViewportGap + 1e-6);
        Assert.Equal(area.CenterX, (three[0].MinX + three[1].MaxX) / 2, 6);
        Assert.Null(SheetPlanner.Pack<string>([("a", 100, 150), ("b", 250, 100)], area));
        Assert.Null(SheetPlanner.Pack<string>([("a", 400, 10)], area));
    }

    // ---- names and files ----------------------------------------------------------------------------------------

    [Fact]
    public void Sheet_numbers_and_layout_names()
    {
        Assert.Equal("A-101", SheetNames.Number("A", 100, 0));
        Assert.Equal("A-001", SheetNames.Number(null, 0, 0));
        Assert.Equal("AR-202", SheetNames.Number("AR-", 200, 1));
        Assert.Equal("A-101 Ground Floor Plan", SheetNames.LayoutName("A-101", "%%uGROUND FLOOR PLAN"));
        var odd = SheetNames.LayoutName("A-901", "KEY: \"A\" / B = C; D?*|<x>,'q'`");
        Assert.All("<>/\\\":;?*|,=`'", ch => Assert.DoesNotContain(ch, odd));
        Assert.StartsWith("A-901 Key", odd, StringComparison.Ordinal);
        Assert.Equal(900, SheetNames.Series(DrawingKind.Other));
        Assert.Equal(500, SheetNames.Series(DrawingKind.Layout));
    }

    [Fact]
    public void Layout_names_are_made_unique_and_sheets_are_recognised()
    {
        Assert.Equal("A-101 Plan", SheetNames.Unique("A-101 Plan", ["Layout1"]));
        Assert.Equal("A-101 Plan (2)", SheetNames.Unique("A-101 Plan", ["a-101 plan"]));
        Assert.Equal("A-101 Plan (3)", SheetNames.Unique("A-101 Plan", ["A-101 Plan", "A-101 Plan (2)"]));
        Assert.True(SheetNames.IsSheetName("A-101 Ground Floor Plan"));
        Assert.True(SheetNames.IsSheetName("AR-201"));
        Assert.False(SheetNames.IsSheetName("Layout1"));
        Assert.False(SheetNames.IsSheetName(null));
    }

    [Fact]
    public void Pdf_names_carry_the_drawing_and_date_and_never_overwrite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exports");
        var now = new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(Path.Combine(folder, "House_2026-11-20.pdf"), SheetNames.PdfPath(folder, null, "House.dwg", now, _ => false));
        var taken = new HashSet<string> { Path.Combine(folder, "House_2026-11-20.pdf"), Path.Combine(folder, "House_2026-11-20-2.pdf") };
        Assert.Equal(Path.Combine(folder, "House_2026-11-20-3.pdf"), SheetNames.PdfPath(folder, null, "House.dwg", now, taken.Contains));
        Assert.Equal(Path.Combine(folder, "Sharma set_2026-11-20.pdf"), SheetNames.PdfPath(folder, "Sharma set.pdf", "House.dwg", now, _ => false));
        Assert.Equal(Path.Combine(folder, "a-b_2026-11-20.pdf"), SheetNames.PdfPath(folder, "a/b", null, now, _ => false));
    }
}
